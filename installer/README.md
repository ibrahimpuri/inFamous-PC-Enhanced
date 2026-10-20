# infamous-installer

Builds the inFamous (BCES00220) PC port from the player's **own** copy of the game. The port's
repository holds no game code and no game data. The installer regenerates all of it from the
player's dump, applies the port's patches, compiles it and writes a launcher.

It's written in Rust with no dependencies, so it builds offline with a plain Rust toolchain:

```bat
cd installer
cargo build --release
:: -> installer\target\release\infamous-installer.exe
```

## For players

You need:

* **Your inFamous disc dump**: the folder that contains `PS3_GAME` (European release, BCES00220).
* **Python 3.10+** from python.org, with "Add python.exe to PATH" ticked.
* **Visual Studio 2022 Build Tools** with the *Desktop development with C++* workload and the
  *C++ Clang Compiler for Windows* component.
* **RPCS3**, if your `PS3_GAME\USRDIR` has only `EBOOT.BIN` and no decrypted `EBOOT.elf`.
* Roughly 10 GB of free disk space and 16 GB of RAM. The generated C++ files are large.

Double-click `infamous-installer.exe`, or run it from a terminal:

```bat
infamous-installer --game "D:\Dumps\inFamous" --rpcs3 "C:\RPCS3\rpcs3.exe"
infamous-installer doctor          :: check the prerequisites only
```

The installer runs these steps:

1. It reads `PARAM.SFO` and refuses any game that isn't BCES00220.
2. It decrypts `EBOOT.BIN` to `EBOOT.elf` with `rpcs3 --decrypt` if needed. You can also do this
   yourself in RPCS3 with *Utilities → Decrypt PS3 Binaries*.
3. It checks the SHA-256 of `EBOOT.elf` against the version the port was made from.
4. It links your dump into the checkout (a directory junction, so nothing is copied).
5. It creates a private Python environment in `.installer\venv` and installs the toolkit's packages.
6. It runs the generation pipeline: PPU lift, SPU images, job manager, the 21 WWS job modules, the
   plugin blobs, the SPU post-passes and the UI assets.
7. It checks every generated file against the hash recorded by the maintainer, then applies
   `installer/patches/` and checks again. The result is byte-identical to the maintainer's tree.
8. It runs CMake and Ninja with clang-cl, using the same settings as `build.cmd`.
9. It writes `Play inFamous.cmd`.
10. It builds the settings launcher (`launcher\build.cmd` -> `inFamous Launcher.exe`) when the tree has one and its
    sources are newer than the exe. A failure there is only a warning: the game runs without it.

Finished steps are skipped when you run the installer again, so an interrupted install continues
where it stopped. `--regen` starts the pipeline over.

### Updating an installation

```bat
infamous-installer update            :: download what changed on GitHub, rebuild what that needs
infamous-installer update --check    :: only report whether an update is available
```

The repository is set in `infamous.toml` (`[update] repo / branch / path`). The updater asks
GitHub for the branch's latest commit and its file list (two API requests), compares each file's
git blob hash with the local file (a CRLF copy of an LF file counts as equal), downloads only the
differing files from raw.githubusercontent.com (or the commit's zip when more than 150 changed),
verifies every download against its blob hash, then moves them into place. Files an earlier update
installed that the repository no longer has are removed; `.installer/update.state` records the
commit and file list. If `generated.lock` no longer matches what is on disk, the game code is
regenerated from the player's files and re-patched; generated files that come out identical keep
their timestamps, so only changed translation units recompile. HTTP goes through `curl.exe` and
zips through `tar.exe` (both part of Windows 10/11); a private repository needs `GITHUB_TOKEN`.
If the repository carries `installer/bin/infamous-installer.exe` or `installer/bin/infamous-launch.exe`,
those replace the installer and the game's launcher too (a running copy is renamed to `.old`).

## The setup program (what players download)

`inFamous Setup.exe` is a normal Windows installer window: Welcome → Requirements (Python and
the C++ build tools, with **Install** buttons that use winget) → Your game (pick the **.iso** or a
game folder; encrypted Redump images work with their `.dkey` next to them) → Install location →
progress bar → **Play**. It carries the whole publishable tree inside itself, extracts it to the
chosen folder, copies the game out of the disc image, runs `infamous-installer install` there and
creates `inFamous.exe` (starts the game without a console) plus desktop/Start-menu shortcuts. The shortcuts
and the final button open `inFamous Launcher.exe` when the install built it, otherwise `inFamous.exe`.
Running it again on the same folder repairs/updates and only redoes what changed.

Build it (maintainer, after `make-patches`):

```bat
cd installer && cargo build --release
cd .. && installer\target\release\infamous-installer package --out "dist\inFamous Setup.exe"
```

Disc images: `infamous-installer extract-iso --iso game.iso --out DIR [--dkey game.dkey]` is the
same extractor on the command line. RPCS3 (`rpcs3 --decrypt`) unlocks EBOOT.BIN; the setup finds
rpcs3.exe in the usual places or asks for it.

## For the maintainer

The patch set has to be created once on the development tree, and again whenever the
maintained generated files change:

```bat
infamous-installer make-patches --game "D:\Dumps\inFamous"
infamous-installer export --out D:\publish\infamous-port
```

* `make-patches` exports a clean tree to `.installer\fresh`, which is exactly what a player
  downloads. It runs the pipeline there, compares the output with your `infamous-recomp`, and
  writes `installer/generated.lock` and `installer/patches/`. It doesn't change any of your files.
* It refuses to finish if publishing a patch would amount to publishing generated content:
  * A binary file differs from the pipeline's output.
  * The pipeline doesn't produce a file at all.
  * A patch carries more than half of a file.

  Each of these means a pipeline step is missing or uses different arguments than you used.
  Fix that step in `infamous.toml` and run `make-patches` again. `--keep-fresh` skips the
  re-export and `--regen` re-runs every step.
* `export` copies the publishable tree: ps3recomp, infamous-recomp and the installer, without
  `[generated]` files and without anything in `[export].exclude` (backups, dumps, logs, the game
  folder).

Everything about the game and the port is in `infamous.toml`: pipeline steps, which files count
as generated, build commands, launcher switches and export rules. You can change it without
rebuilding the installer.

### Line endings

Python's text mode writes CRLF on Windows and LF elsewhere, and the tree mixes both: the lifted PPU
units are CRLF except `ppu_recomp_000.cpp`, and the SPU sources are LF while `spu_v200.py`,
`spu_lifter.py` and `plug_lift.py` write CRLF on Windows. When a pipeline file and the maintained
file use different endings throughout, the patch starts with `# eol lf` (or `# eol crlf`): the
installer converts the file's endings first and then applies the hunks, so the patch holds only the
real edits instead of every line. Run `make-patches` on Windows, because that is what players run.
