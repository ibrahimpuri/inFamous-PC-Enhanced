//! `infamous-installer update`: bring an installed copy up to date with the GitHub repository that
//! holds the port's published tree (the output of `export`).
//!
//! 1. Ask GitHub for the branch's latest commit and that commit's full file list. GitHub gives
//!    every file's git blob hash, so comparing it with the hash of the local file
//!    (`sha1("blob <len>\0" + bytes)`) says exactly which files differ, without downloading them.
//! 2. Download only those files (raw.githubusercontent.com), or the commit's zip if many changed,
//!    and verify each one against its blob hash before anything on disk is touched.
//! 3. Move them into place and delete files that an earlier update installed but the repository
//!    no longer has. `.installer/update.state` remembers the commit and the file list.
//!
//! No extra crates: HTTP goes through `curl.exe` and zips through `tar.exe`, both part of
//! Windows 10/11 (and of every Linux/macOS). A private repository works with GITHUB_TOKEN set.

use crate::util::{self, Ctx, Error, Result};
use crate::{bail, hash, manifest::Manifest};
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

/// More changed files than this -> one zip download instead of one request per file.
const ZIP_THRESHOLD: usize = 150;

pub struct Remote {
    pub repo: String,
    pub branch: String,
    /// Folder inside the repository that holds the tree ("" = repository root).
    pub path: String,
    token: Option<String>,
}

impl Remote {
    pub fn new(m: &Manifest, repo: Option<&str>, branch: Option<&str>) -> Result<Remote> {
        let repo = repo
            .map(|r| r.to_string())
            .or_else(|| m.update_repo.clone())
            .ctx(|| "updating is not configured: installer/infamous.toml has no [update] repo (or pass --repo owner/name)")?;
        let repo = normalize_repo(&repo)?;
        let token = std::env::var("GITHUB_TOKEN")
            .ok()
            .filter(|t| !t.trim().is_empty());
        Ok(Remote {
            repo,
            branch: branch
                .map(|b| b.to_string())
                .unwrap_or_else(|| m.update_branch.clone()),
            path: m.update_path.clone(),
            token,
        })
    }
}

/// "owner/name", also accepting a pasted https://github.com/owner/name(.git) URL.
fn normalize_repo(r: &str) -> Result<String> {
    let mut s = r.trim().trim_end_matches('/').to_string();
    for p in [
        "https://github.com/",
        "http://github.com/",
        "github.com/",
        "git@github.com:",
    ] {
        if let Some(x) = s.strip_prefix(p) {
            s = x.to_string();
        }
    }
    let s = s.trim_end_matches(".git").to_string();
    let parts: Vec<&str> = s.split('/').collect();
    if parts.len() != 2
        || parts.iter().any(|p| {
            p.is_empty()
                || !p
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || "-_.".contains(c))
        })
    {
        bail!("'{r}' is not a GitHub repository (expected owner/name)");
    }
    Ok(s)
}

// ---------------------------------------------------------------------------------------------
// HTTP (curl)
// ---------------------------------------------------------------------------------------------

fn curl_cmd(remote: &Remote, url: &str, accept: Option<&str>, trusted_redirect: bool) -> Command {
    let prog = if cfg!(windows) {
        // The real curl.exe, never a PowerShell alias; it ships in System32 since Windows 10 1803.
        let sys = std::env::var("SystemRoot")
            .map(|r| PathBuf::from(r).join("System32").join("curl.exe"))
            .ok();
        match sys {
            Some(p) if p.is_file() => p,
            _ => PathBuf::from("curl.exe"),
        }
    } else {
        PathBuf::from("curl")
    };
    let mut c = Command::new(prog);
    c.args([
        "-fsSL",
        "--retry",
        "3",
        "--retry-delay",
        "2",
        "--connect-timeout",
        "20",
        "-A",
        "infamous-installer",
    ]);
    if let Some(a) = accept {
        c.arg("-H").arg(format!("Accept: {a}"));
    }
    if let Some(t) = &remote.token {
        c.arg("-H")
            .arg(format!("Authorization: Bearer {}", t.trim()));
        if trusted_redirect {
            c.arg("--location-trusted");
        }
    }
    c.arg(url);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        c.creation_flags(0x0800_0000); // no console window when started from the setup program
    }
    c.stdin(Stdio::null());
    c
}

fn http_err(url: &str, stderr: &[u8]) -> Error {
    let msg = String::from_utf8_lossy(stderr).trim().to_string();
    let hint = if msg.contains("404") {
        " (repository, branch or file not found -- a private repository needs GITHUB_TOKEN)"
    } else if msg.contains("403") || msg.contains("429") {
        " (GitHub refused or rate-limited the request; try again later)"
    } else {
        ""
    };
    Error(format!(
        "download failed: {url}: {}{hint}",
        if msg.is_empty() {
            "no response".into()
        } else {
            msg
        }
    ))
}

fn get_bytes(remote: &Remote, url: &str, accept: Option<&str>) -> Result<Vec<u8>> {
    let out = curl_cmd(remote, url, accept, false)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .ctx(|| "could not run curl (it is part of Windows 10 and 11)")?;
    if !out.status.success() {
        return Err(http_err(url, &out.stderr));
    }
    Ok(out.stdout)
}

fn get_file(remote: &Remote, url: &str, dest: &Path, trusted_redirect: bool) -> Result<()> {
    util::ensure_parent(dest)?;
    let out = curl_cmd(remote, url, None, trusted_redirect)
        .arg("-o")
        .arg(dest)
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .output()
        .ctx(|| "could not run curl (it is part of Windows 10 and 11)")?;
    if !out.status.success() {
        return Err(http_err(url, &out.stderr));
    }
    Ok(())
}

/// API / raw-file hosts; INFAMOUS_UPDATE_API / INFAMOUS_UPDATE_RAW override them (testing, mirrors).
fn api_base() -> String {
    std::env::var("INFAMOUS_UPDATE_API")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "https://api.github.com".into())
        .trim_end_matches('/')
        .to_string()
}
fn raw_base() -> String {
    std::env::var("INFAMOUS_UPDATE_RAW")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "https://raw.githubusercontent.com".into())
        .trim_end_matches('/')
        .to_string()
}

/// Percent-encode a repository path for a URL (keeps '/').
fn url_path(p: &str) -> String {
    let mut s = String::new();
    for b in p.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~/".contains(&b) {
            s.push(b as char);
        } else {
            s.push_str(&format!("%{b:02X}"));
        }
    }
    s
}

// ---------------------------------------------------------------------------------------------
// Minimal JSON (GitHub's tree listing)
// ---------------------------------------------------------------------------------------------

#[derive(Debug, Clone)]
pub enum Json {
    Null,
    Bool(bool),
    Num(f64),
    Str(String),
    Arr(Vec<Json>),
    Obj(Vec<(String, Json)>),
}

impl Json {
    pub fn get(&self, k: &str) -> Option<&Json> {
        match self {
            Json::Obj(v) => v.iter().find(|(a, _)| a == k).map(|(_, b)| b),
            _ => None,
        }
    }
    pub fn str(&self) -> Option<&str> {
        match self {
            Json::Str(s) => Some(s),
            _ => None,
        }
    }
}

pub fn parse_json(src: &[u8]) -> Result<Json> {
    let mut p = Parser { s: src, i: 0 };
    let v = p.value()?;
    p.ws();
    if p.i != p.s.len() {
        bail!("bad JSON: trailing data at byte {}", p.i);
    }
    Ok(v)
}

struct Parser<'a> {
    s: &'a [u8],
    i: usize,
}

impl Parser<'_> {
    fn ws(&mut self) {
        while self.i < self.s.len() && matches!(self.s[self.i], b' ' | b'\t' | b'\n' | b'\r') {
            self.i += 1;
        }
    }
    fn err<T>(&self, what: &str) -> Result<T> {
        Err(Error(format!("bad JSON: {what} at byte {}", self.i)))
    }
    fn lit(&mut self, w: &str, v: Json) -> Result<Json> {
        if self.s[self.i..].starts_with(w.as_bytes()) {
            self.i += w.len();
            Ok(v)
        } else {
            self.err("unexpected word")
        }
    }
    fn value(&mut self) -> Result<Json> {
        self.ws();
        let Some(&c) = self.s.get(self.i) else {
            return self.err("unexpected end");
        };
        match c {
            b'{' => {
                self.i += 1;
                let mut v = Vec::new();
                self.ws();
                if self.s.get(self.i) == Some(&b'}') {
                    self.i += 1;
                    return Ok(Json::Obj(v));
                }
                loop {
                    self.ws();
                    if self.s.get(self.i) != Some(&b'"') {
                        return self.err("expected a key");
                    }
                    let k = self.string()?;
                    self.ws();
                    if self.s.get(self.i) != Some(&b':') {
                        return self.err("expected ':'");
                    }
                    self.i += 1;
                    let x = self.value()?;
                    v.push((k, x));
                    self.ws();
                    match self.s.get(self.i) {
                        Some(b',') => self.i += 1,
                        Some(b'}') => {
                            self.i += 1;
                            return Ok(Json::Obj(v));
                        }
                        _ => return self.err("expected ',' or '}'"),
                    }
                }
            }
            b'[' => {
                self.i += 1;
                let mut v = Vec::new();
                self.ws();
                if self.s.get(self.i) == Some(&b']') {
                    self.i += 1;
                    return Ok(Json::Arr(v));
                }
                loop {
                    v.push(self.value()?);
                    self.ws();
                    match self.s.get(self.i) {
                        Some(b',') => self.i += 1,
                        Some(b']') => {
                            self.i += 1;
                            return Ok(Json::Arr(v));
                        }
                        _ => return self.err("expected ',' or ']'"),
                    }
                }
            }
            b'"' => Ok(Json::Str(self.string()?)),
            b't' => self.lit("true", Json::Bool(true)),
            b'f' => self.lit("false", Json::Bool(false)),
            b'n' => self.lit("null", Json::Null),
            _ => {
                let st = self.i;
                while self.i < self.s.len()
                    && matches!(
                        self.s[self.i],
                        b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9'
                    )
                {
                    self.i += 1;
                }
                let t = std::str::from_utf8(&self.s[st..self.i]).unwrap_or("");
                match t.parse::<f64>() {
                    Ok(n) if !t.is_empty() => Ok(Json::Num(n)),
                    _ => self.err("unexpected character"),
                }
            }
        }
    }
    fn hex4(&mut self) -> Result<u32> {
        let Some(h) = self.s.get(self.i..self.i + 4) else {
            return self.err("short \\u escape");
        };
        let v = u32::from_str_radix(std::str::from_utf8(h).unwrap_or("x"), 16)
            .map_err(|_| Error(format!("bad JSON: bad \\u escape at byte {}", self.i)))?;
        self.i += 4;
        Ok(v)
    }
    fn string(&mut self) -> Result<String> {
        self.i += 1; // opening quote
        let mut out: Vec<u8> = Vec::new();
        loop {
            let Some(&c) = self.s.get(self.i) else {
                return self.err("unterminated string");
            };
            self.i += 1;
            match c {
                b'"' => break,
                b'\\' => {
                    let Some(&e) = self.s.get(self.i) else {
                        return self.err("bad escape");
                    };
                    self.i += 1;
                    match e {
                        b'"' => out.push(b'"'),
                        b'\\' => out.push(b'\\'),
                        b'/' => out.push(b'/'),
                        b'b' => out.push(8),
                        b'f' => out.push(12),
                        b'n' => out.push(b'\n'),
                        b'r' => out.push(b'\r'),
                        b't' => out.push(b'\t'),
                        b'u' => {
                            let mut cp = self.hex4()?;
                            if (0xD800..0xDC00).contains(&cp)
                                && self.s.get(self.i..self.i + 2) == Some(b"\\u")
                            {
                                self.i += 2;
                                let lo = self.hex4()?;
                                cp = 0x10000
                                    + ((cp - 0xD800) << 10)
                                    + (lo.wrapping_sub(0xDC00) & 0x3FF);
                            }
                            let ch = char::from_u32(cp).unwrap_or('\u{FFFD}');
                            let mut b = [0u8; 4];
                            out.extend_from_slice(ch.encode_utf8(&mut b).as_bytes());
                        }
                        _ => return self.err("bad escape"),
                    }
                }
                _ => out.push(c),
            }
        }
        String::from_utf8(out).map_err(|_| Error("bad JSON: invalid UTF-8 in a string".into()))
    }
}

// ---------------------------------------------------------------------------------------------
// Plan
// ---------------------------------------------------------------------------------------------

#[derive(Debug, Clone)]
pub struct Entry {
    /// Path relative to the install folder (forward slashes).
    pub path: String,
    /// Path inside the repository.
    pub repo_path: String,
    /// git blob hash (hex).
    pub sha: String,
    pub size: u64,
}

#[derive(Debug)]
pub struct Plan {
    pub commit: String,
    pub fetch: Vec<Entry>,
    pub delete: Vec<String>,
    /// Every file the repository has (local paths), for the state file.
    pub all: Vec<String>,
    pub previous_commit: Option<String>,
}

impl Plan {
    pub fn is_empty(&self) -> bool {
        self.fetch.is_empty() && self.delete.is_empty()
    }
    pub fn download_bytes(&self) -> u64 {
        self.fetch.iter().map(|e| e.size).sum()
    }
}

/// `.installer/update.state`: the commit this folder was last updated to and the files it got.
struct UpdateState {
    commit: Option<String>,
    files: Vec<String>,
}

fn state_path(root: &Path) -> PathBuf {
    root.join(".installer").join("update.state")
}

impl UpdateState {
    fn load(root: &Path) -> UpdateState {
        let mut st = UpdateState {
            commit: None,
            files: Vec::new(),
        };
        if let Ok(t) = fs::read_to_string(state_path(root)) {
            for l in t.lines() {
                if let Some(c) = l.strip_prefix("commit ") {
                    st.commit = Some(c.trim().to_string());
                } else if let Some(f) = l.strip_prefix("file ") {
                    st.files.push(f.to_string());
                }
            }
        }
        st
    }
}

fn save_state(root: &Path, commit: &str, files: &[String]) -> Result<()> {
    let mut s = format!("# Written by `infamous-installer update`.\ncommit {commit}\n");
    for f in files {
        s.push_str(&format!("file {f}\n"));
    }
    util::write_file(&state_path(root), s.as_bytes())
}

fn blob_sha(data: &[u8]) -> String {
    let mut buf = format!("blob {}\0", data.len()).into_bytes();
    buf.extend_from_slice(data);
    hash::hex(&hash::sha1(&buf))
}

/// The git blob hash of a local file, or None if it does not exist / can't be read.
pub fn git_blob_sha(p: &Path) -> Option<String> {
    Some(blob_sha(&fs::read(p).ok()?))
}

fn crlf_to_lf(d: &[u8]) -> Vec<u8> {
    let mut o = Vec::with_capacity(d.len());
    let mut i = 0;
    while i < d.len() {
        if d[i] == b'\r' && d.get(i + 1) == Some(&b'\n') {
            i += 1;
            continue;
        }
        o.push(d[i]);
        i += 1;
    }
    o
}

/// Does the local file already hold this blob? A repository with `text=auto` stores text files
/// with LF line ends, while a Windows copy may have CRLF; both count as the same file.
pub fn local_matches(p: &Path, sha: &str) -> bool {
    let Ok(d) = fs::read(p) else { return false };
    if blob_sha(&d) == sha {
        return true;
    }
    d.windows(2).any(|w| w == b"\r\n") && blob_sha(&crlf_to_lf(&d)) == sha
}

/// Windows batch/PowerShell scripts are written with CRLF (cmd.exe misreads labels in LF files).
fn wants_crlf(path: &str) -> bool {
    let l = path.to_ascii_lowercase();
    cfg!(windows) && (l.ends_with(".cmd") || l.ends_with(".bat") || l.ends_with(".ps1"))
}

fn lf_to_crlf(d: &[u8]) -> Vec<u8> {
    let mut o = Vec::with_capacity(d.len() + d.len() / 16);
    for (i, &c) in d.iter().enumerate() {
        if c == b'\n' && (i == 0 || d[i - 1] != b'\r') {
            o.push(b'\r');
        }
        o.push(c);
    }
    o
}

/// Files an update must never write or delete, whatever the repository contains.
fn protected(m: &Manifest, p: &str) -> bool {
    let l = p.to_ascii_lowercase();
    let top = l.split('/').next().unwrap_or("");
    if [".installer", ".git", ".github"].contains(&top) {
        return true;
    }
    if let Some(link) = &m.link_name {
        if top == link.to_ascii_lowercase() {
            return true;
        }
    }
    // The binaries the setup program places next to the tree (not in the repository).
    l == "installer/infamous-installer.exe"
        || l == "installer/infamous-installer"
        || l == format!("{}.exe", m.game_title.to_ascii_lowercase())
        || l.starts_with("installer/target/")
        || m.is_generated(p)
}

pub fn plan(root: &Path, m: &Manifest, r: &Remote) -> Result<Plan> {
    let api = format!("{}/repos/{}", api_base(), r.repo);
    let commit = get_bytes(
        r,
        &format!("{api}/commits/{}", url_path(&r.branch)),
        Some("application/vnd.github.sha"),
    )?;
    let commit = String::from_utf8_lossy(&commit).trim().to_string();
    if commit.len() != 40 || !commit.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!(
            "GitHub did not return a commit for {} branch {} (got '{}')",
            r.repo,
            r.branch,
            commit.chars().take(60).collect::<String>()
        );
    }
    let tree = get_bytes(
        r,
        &format!("{api}/git/trees/{commit}?recursive=1"),
        Some("application/vnd.github+json"),
    )?;
    let tree = parse_json(&tree)?;
    if matches!(tree.get("truncated"), Some(Json::Bool(true))) {
        bail!("the repository has too many files for GitHub's file list; updating needs a smaller tree");
    }
    let Some(Json::Arr(items)) = tree.get("tree") else {
        bail!("unexpected answer from GitHub (no file list)")
    };
    let prefix = if r.path.is_empty() {
        String::new()
    } else {
        format!("{}/", r.path)
    };

    let mut remote: BTreeMap<String, Entry> = BTreeMap::new();
    for it in items {
        if it.get("type").and_then(Json::str) != Some("blob")
            || it.get("mode").and_then(Json::str) == Some("120000")
        {
            continue; // folders, submodules, symlinks
        }
        let (Some(rp), Some(sha)) = (
            it.get("path").and_then(Json::str),
            it.get("sha").and_then(Json::str),
        ) else {
            continue;
        };
        let Some(local) = rp.strip_prefix(prefix.as_str()) else {
            continue;
        };
        // Prebuilt binaries the repository may carry: they replace the installer and the game's
        // launcher (which are not source files, so they are otherwise never touched).
        let local = match local.to_ascii_lowercase().as_str() {
            "installer/bin/infamous-installer.exe" if cfg!(windows) => {
                "installer/infamous-installer.exe".to_string()
            }
            "installer/bin/infamous-launch.exe" if cfg!(windows) => format!("{}.exe", m.game_title),
            l if l.starts_with("installer/bin/") => continue,
            _ => local.to_string(),
        };
        let binary = local.eq_ignore_ascii_case("installer/infamous-installer.exe")
            || local.eq_ignore_ascii_case(&format!("{}.exe", m.game_title));
        let local = local.as_str();
        if local.is_empty()
            || local.split('/').any(|c| c == ".." || c.is_empty())
            || (!binary && protected(m, local))
        {
            continue;
        }
        let size = match it.get("size") {
            Some(Json::Num(n)) => *n as u64,
            _ => 0,
        };
        remote.insert(
            local.to_string(),
            Entry {
                path: local.to_string(),
                repo_path: rp.to_string(),
                sha: sha.to_ascii_lowercase(),
                size,
            },
        );
    }
    if remote.is_empty() {
        bail!(
            "{} (branch {}{}) has no files to install",
            r.repo,
            r.branch,
            if r.path.is_empty() {
                String::new()
            } else {
                format!(", folder {}", r.path)
            }
        );
    }
    if !remote.contains_key("installer/infamous.toml") {
        bail!(
            "{} does not look like this port's published tree (no installer/infamous.toml{}). Check [update] repo/path in installer/infamous.toml.",
            r.repo,
            if r.path.is_empty() { " at the repository root".to_string() } else { format!(" under {}", r.path) }
        );
    }

    let fetch: Vec<Entry> = remote
        .values()
        .filter(|e| !local_matches(&util::join_slash(root, &e.path), &e.sha))
        .cloned()
        .collect();
    let st = UpdateState::load(root);
    let delete: Vec<String> = st
        .files
        .iter()
        .filter(|f| {
            !remote.contains_key(*f) && !protected(m, f) && util::join_slash(root, f).is_file()
        })
        .cloned()
        .collect();
    Ok(Plan {
        commit,
        fetch,
        delete,
        all: remote.into_keys().collect(),
        previous_commit: st.commit,
    })
}

// ---------------------------------------------------------------------------------------------
// Apply
// ---------------------------------------------------------------------------------------------

/// A download holds the blob, byte for byte or as a CRLF copy: GitHub's zips apply the repository's
/// `eol=crlf` attributes, so a script can arrive with CRLF although the blob has LF.
fn check_blob(p: &Path, e: &Entry) -> Result<()> {
    if local_matches(p, &e.sha) {
        return Ok(());
    }
    match git_blob_sha(p) {
        Some(_) => bail!(
            "{} arrived damaged or changed on GitHub during the update; run the update again",
            e.path
        ),
        None => bail!("{} was not downloaded", e.path),
    }
}

fn find_zip_top(dir: &Path) -> Result<PathBuf> {
    let mut dirs = Vec::new();
    for d in fs::read_dir(dir).ctx(|| format!("reading {}", dir.display()))? {
        let d = d?;
        if d.file_type()?.is_dir() {
            dirs.push(d.path());
        }
    }
    if dirs.len() != 1 {
        bail!(
            "unexpected layout of the downloaded zip ({} top folders)",
            dirs.len()
        );
    }
    Ok(dirs.remove(0))
}

/// Download, verify and install the plan's files. `log` gets one line per step.
pub fn apply(root: &Path, r: &Remote, plan: &Plan, log: &mut dyn FnMut(String)) -> Result<()> {
    let work = root.join(".installer").join("update");
    let _ = fs::remove_dir_all(&work);
    // Old copies of programs that were running during the previous update (X.old next to X.exe).
    for d in [root.to_path_buf(), root.join("installer")] {
        for f in fs::read_dir(&d).into_iter().flatten().flatten() {
            let p = f.path();
            if p.extension().map(|e| e == "old").unwrap_or(false)
                && p.with_extension("exe").is_file()
            {
                let _ = fs::remove_file(&p);
            }
        }
    }
    fs::create_dir_all(&work).ctx(|| format!("creating {}", work.display()))?;

    // 1. Everything downloaded and verified before the install folder is touched.
    let mut staged: Vec<(PathBuf, &Entry)> = Vec::new();
    if plan.fetch.len() > ZIP_THRESHOLD {
        let zip = work.join("update.zip");
        log(format!(
            "Downloading the whole update as one zip ({} files changed)",
            plan.fetch.len()
        ));
        get_file(
            r,
            &format!("{}/repos/{}/zipball/{}", api_base(), r.repo, plan.commit),
            &zip,
            true,
        )?;
        let x = work.join("x");
        fs::create_dir_all(&x)?;
        log("Unpacking".into());
        let tar = if cfg!(windows) {
            std::env::var("SystemRoot")
                .map(|s| PathBuf::from(s).join("System32").join("tar.exe"))
                .ok()
                .filter(|p| p.is_file())
                .unwrap_or_else(|| PathBuf::from("tar.exe"))
        } else {
            PathBuf::from("tar")
        };
        let mut c = if cfg!(windows) {
            let mut c = Command::new(tar); // bsdtar: reads zips
            c.arg("-xf").arg(&zip).arg("-C").arg(&x);
            c
        } else {
            let _ = tar;
            let mut c = Command::new("unzip"); // GNU tar can't read zips
            c.arg("-q").arg("-o").arg(&zip).arg("-d").arg(&x);
            c
        };
        c.stdin(Stdio::null()).stdout(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            c.creation_flags(0x0800_0000);
        }
        let out = c
            .stderr(Stdio::piped())
            .output()
            .ctx(|| "could not run tar (it is part of Windows 10 and 11)")?;
        if !out.status.success() {
            bail!(
                "unpacking the update failed: {}",
                String::from_utf8_lossy(&out.stderr).trim()
            );
        }
        let top = find_zip_top(&x)?;
        for e in &plan.fetch {
            let p = util::join_slash(&top, &e.repo_path);
            check_blob(&p, e)?;
            staged.push((p, e));
        }
    } else {
        let n = plan.fetch.len();
        for (i, e) in plan.fetch.iter().enumerate() {
            log(format!("[{}/{n}] {}", i + 1, e.path));
            let p = work.join("f").join(format!("{i}"));
            get_file(
                r,
                &format!(
                    "{}/{}/{}/{}",
                    raw_base(),
                    r.repo,
                    plan.commit,
                    url_path(&e.repo_path)
                ),
                &p,
                false,
            )?;
            check_blob(&p, e)?;
            staged.push((p, e));
        }
    }

    // 2. Into place.
    for (src, e) in &staged {
        let dst = util::join_slash(root, &e.path);
        util::ensure_parent(&dst)?;
        if dst.exists() && fs::remove_file(&dst).is_err() {
            // A running program (this installer, or the game's launcher) can't be deleted on
            // Windows but can be renamed; the old copy is cleaned up by the next update.
            let old = dst.with_extension("old");
            let _ = fs::remove_file(&old);
            fs::rename(&dst, &old)
                .ctx(|| format!("replacing {} (is the game still running?)", dst.display()))?;
        }
        if wants_crlf(&e.path) {
            let d = fs::read(src)?;
            fs::write(&dst, lf_to_crlf(&d)).ctx(|| format!("writing {}", dst.display()))?;
        } else if fs::rename(src, &dst).is_err() {
            fs::copy(src, &dst).ctx(|| format!("writing {}", dst.display()))?;
        }
    }
    for f in &plan.delete {
        let p = util::join_slash(root, f);
        match fs::remove_file(&p) {
            Ok(()) => log(format!("removed {f}")),
            Err(e) => log(format!("could not remove {f}: {e}")),
        }
    }
    save_state(root, &plan.commit, &plan.all)?;
    let _ = fs::remove_dir_all(&work);
    Ok(())
}

/// Which of the plan's files are worth naming in a summary.
pub fn summary(plan: &Plan) -> String {
    let mut groups: BTreeMap<&str, usize> = BTreeMap::new();
    for e in &plan.fetch {
        *groups
            .entry(e.path.split('/').next().unwrap_or(""))
            .or_default() += 1;
    }
    let mut parts: Vec<String> = groups.iter().map(|(g, n)| format!("{g} {n}")).collect();
    if !plan.delete.is_empty() {
        parts.push(format!("{} removed", plan.delete.len()));
    }
    parts.join(", ")
}

/// Distinct top-level folders touched (for deciding what needs rebuilding).
pub fn touched(plan: &Plan) -> BTreeSet<String> {
    plan.fetch
        .iter()
        .map(|e| e.path.clone())
        .chain(plan.delete.iter().cloned())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_tree() {
        let j = parse_json(br#"{"sha":"ab","tree":[{"path":"a b/\u00e9.txt","mode":"100644","type":"blob","sha":"x","size":12},{"path":"d","type":"tree"}],"truncated":false}"#).unwrap();
        let Some(Json::Arr(t)) = j.get("tree") else {
            panic!()
        };
        assert_eq!(t[0].get("path").and_then(Json::str), Some("a b/\u{e9}.txt"));
        assert!(matches!(t[0].get("size"), Some(Json::Num(n)) if *n == 12.0));
        assert!(matches!(j.get("truncated"), Some(Json::Bool(false))));
    }

    #[test]
    fn blob_hash_matches_git() {
        // `printf 'hello\n' | git hash-object --stdin` = ce013625030ba8dba906f756967f9e9ca394464a
        let d = std::env::temp_dir().join("ifupd_blob_test.txt");
        fs::write(&d, b"hello\n").unwrap();
        assert_eq!(
            git_blob_sha(&d).as_deref(),
            Some("ce013625030ba8dba906f756967f9e9ca394464a")
        );
        let _ = fs::remove_file(&d);
    }

    #[test]
    fn zip_script_with_crlf_passes() {
        // GitHub's zip of a repository with `*.cmd text eol=crlf` holds the LF blob as CRLF.
        let d = std::env::temp_dir().join("ifupd_zip_crlf_test.cmd");
        fs::write(&d, b"hello\r\n").unwrap();
        let e = Entry {
            path: "build.cmd".into(),
            repo_path: "build.cmd".into(),
            sha: "ce013625030ba8dba906f756967f9e9ca394464a".into(), // blob of "hello\n"
            size: 6,
        };
        assert!(check_blob(&d, &e).is_ok());
        fs::write(&d, b"hellO\r\n").unwrap();
        assert!(check_blob(&d, &e).is_err());
        let _ = fs::remove_file(&d);
    }

    #[test]
    fn line_ends() {
        let d = std::env::temp_dir().join("ifupd_eol_test.txt");
        fs::write(&d, b"hello\r\n").unwrap();
        assert!(local_matches(
            &d,
            "ce013625030ba8dba906f756967f9e9ca394464a"
        )); // blob of "hello\n"
        assert!(!local_matches(
            &d,
            "0000000000000000000000000000000000000000"
        ));
        let _ = fs::remove_file(&d);
        assert_eq!(lf_to_crlf(b"a\nb\r\nc\n"), b"a\r\nb\r\nc\r\n");
        assert_eq!(crlf_to_lf(b"a\r\nb\rc"), b"a\nb\rc");
    }

    #[test]
    fn repo_names() {
        assert_eq!(
            normalize_repo("https://github.com/whompay/inFAMOUSreborn").unwrap(),
            "whompay/inFAMOUSreborn"
        );
        assert_eq!(
            normalize_repo("whompay/inFAMOUSreborn.git").unwrap(),
            "whompay/inFAMOUSreborn"
        );
        assert!(normalize_repo("nope").is_err());
        assert_eq!(url_path("a b/c+d.txt"), "a%20b/c%2Bd.txt");
    }
}

#[cfg(test)]
mod manifest_tests {
    #[test]
    fn shipped_manifest_has_update_section() {
        let m = crate::manifest::Manifest::load(std::path::Path::new(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/infamous.toml"
        )))
        .unwrap();
        assert_eq!(m.update_repo.as_deref(), Some("ibrahimpuri/inFamous-PC-Enhanced"));
        assert_eq!(m.update_branch, "main");
        assert_eq!(m.update_path, "");
    }
}
