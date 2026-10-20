//! inFamous Setup: the point-and-click installer. Pages: Welcome, Requirements, Your game,
//! Install location, Installing, Done. The work itself is in `infamous_installer::setup`.
#![cfg_attr(windows, windows_subsystem = "windows")]

use eframe::egui::{self, Color32, RichText, Stroke};
use infamous_installer::{setup, util};
use setup::{Check, GameInfo, GameProblem, GameSource, Installer, UpdateInfo};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

// ---- look ------------------------------------------------------------------------------------

const BG: Color32 = Color32::from_rgb(0x10, 0x13, 0x19);
const SIDE: Color32 = Color32::from_rgb(0x0A, 0x0C, 0x10);
const CARD: Color32 = Color32::from_rgb(0x18, 0x1D, 0x25);
const CARD_EDGE: Color32 = Color32::from_rgb(0x26, 0x2D, 0x38);
const TEXT: Color32 = Color32::from_rgb(0xE8, 0xEC, 0xF1);
const MUTED: Color32 = Color32::from_rgb(0x8B, 0x95, 0xA3);
const ACCENT: Color32 = Color32::from_rgb(0x3D, 0xA9, 0xFC);
const ACCENT_HI: Color32 = Color32::from_rgb(0x6C, 0xC0, 0xFF);
const OK: Color32 = Color32::from_rgb(0x3F, 0xB9, 0x50);
const WARN: Color32 = Color32::from_rgb(0xE3, 0xA0, 0x08);
const BAD: Color32 = Color32::from_rgb(0xF8, 0x51, 0x49);

fn style(ctx: &egui::Context) {
    let mut s = (*ctx.style()).clone();
    use egui::{FontFamily::Proportional, FontId, TextStyle};
    s.text_styles = [
        (TextStyle::Heading, FontId::new(26.0, Proportional)),
        (TextStyle::Body, FontId::new(15.0, Proportional)),
        (TextStyle::Button, FontId::new(15.0, Proportional)),
        (TextStyle::Small, FontId::new(12.5, Proportional)),
        (
            TextStyle::Monospace,
            FontId::new(12.5, egui::FontFamily::Monospace),
        ),
    ]
    .into();
    s.spacing.item_spacing = egui::vec2(10.0, 10.0);
    s.spacing.button_padding = egui::vec2(16.0, 8.0);
    s.spacing.interact_size.y = 32.0;
    let mut v = egui::Visuals::dark();
    v.override_text_color = Some(TEXT);
    v.panel_fill = BG;
    v.window_fill = BG;
    v.extreme_bg_color = Color32::from_rgb(0x0C, 0x0F, 0x14);
    v.faint_bg_color = CARD;
    v.hyperlink_color = ACCENT_HI;
    v.selection.bg_fill = ACCENT.linear_multiply(0.6);
    v.selection.stroke = Stroke::new(1.0_f32, ACCENT_HI);
    let r = egui::Rounding::same(7.0);
    for w in [
        &mut v.widgets.noninteractive,
        &mut v.widgets.inactive,
        &mut v.widgets.hovered,
        &mut v.widgets.active,
        &mut v.widgets.open,
    ] {
        w.rounding = r;
    }
    v.widgets.inactive.weak_bg_fill = Color32::from_rgb(0x22, 0x28, 0x32);
    v.widgets.inactive.bg_fill = Color32::from_rgb(0x22, 0x28, 0x32);
    v.widgets.inactive.bg_stroke = Stroke::new(1.0_f32, CARD_EDGE);
    v.widgets.hovered.weak_bg_fill = Color32::from_rgb(0x2B, 0x33, 0x40);
    v.widgets.hovered.bg_fill = Color32::from_rgb(0x2B, 0x33, 0x40);
    v.widgets.hovered.bg_stroke = Stroke::new(1.0_f32, ACCENT.linear_multiply(0.7));
    v.widgets.active.weak_bg_fill = Color32::from_rgb(0x33, 0x3C, 0x4A);
    v.widgets.noninteractive.bg_stroke = Stroke::new(1.0_f32, CARD_EDGE);
    v.widgets.active.fg_stroke = Stroke::new(2.0_f32, Color32::WHITE);
    v.widgets.noninteractive.fg_stroke = Stroke::new(1.0_f32, TEXT);
    s.visuals = v;
    // Windows in light mode would otherwise swap in egui's light theme.
    ctx.set_theme(egui::ThemePreference::Dark);
    ctx.set_style_of(egui::Theme::Dark, s.clone());
    ctx.set_style_of(egui::Theme::Light, s);
}

fn primary(text: &str) -> egui::Button<'static> {
    egui::Button::new(
        RichText::new(text)
            .color(Color32::from_rgb(0x05, 0x10, 0x1C))
            .strong()
            .size(15.5),
    )
    .fill(ACCENT)
    .min_size(egui::vec2(120.0, 38.0))
    .rounding(8.0_f32)
}

fn secondary(text: &str) -> egui::Button<'static> {
    egui::Button::new(RichText::new(text).size(15.0))
        .min_size(egui::vec2(100.0, 38.0))
        .rounding(8.0_f32)
}

fn card<R>(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui) -> R) -> R {
    egui::Frame::none()
        .fill(CARD)
        .stroke(Stroke::new(1.0_f32, CARD_EDGE))
        .rounding(10.0_f32)
        .inner_margin(egui::Margin::symmetric(18.0, 14.0))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            add(ui)
        })
        .inner
}

#[derive(Clone, Copy, PartialEq)]
enum Icon {
    Ok,
    Bad,
    Warn,
    Busy,
    Dot,
}

fn icon(ui: &mut egui::Ui, kind: Icon) {
    let (rect, _) = ui.allocate_exact_size(egui::vec2(22.0, 22.0), egui::Sense::hover());
    let c = rect.center();
    let p = ui.painter();
    match kind {
        Icon::Busy => {
            egui::Spinner::new()
                .size(18.0)
                .color(ACCENT)
                .paint_at(ui, rect.shrink(2.0));
        }
        Icon::Ok => {
            p.circle_filled(c, 10.0, OK);
            let s = Stroke::new(2.4_f32, Color32::WHITE);
            p.line_segment([c + egui::vec2(-4.5, 0.5), c + egui::vec2(-1.2, 3.8)], s);
            p.line_segment([c + egui::vec2(-1.2, 3.8), c + egui::vec2(5.0, -3.5)], s);
        }
        Icon::Bad => {
            p.circle_filled(c, 10.0, BAD);
            let s = Stroke::new(2.4_f32, Color32::WHITE);
            p.line_segment([c + egui::vec2(-4.0, -4.0), c + egui::vec2(4.0, 4.0)], s);
            p.line_segment([c + egui::vec2(4.0, -4.0), c + egui::vec2(-4.0, 4.0)], s);
        }
        Icon::Warn => {
            p.circle_filled(c, 10.0, WARN);
            let s = Stroke::new(2.6_f32, Color32::from_rgb(0x20, 0x16, 0x00));
            p.line_segment([c + egui::vec2(0.0, -5.0), c + egui::vec2(0.0, 1.5)], s);
            p.circle_filled(
                c + egui::vec2(0.0, 5.0),
                1.5,
                Color32::from_rgb(0x20, 0x16, 0x00),
            );
        }
        Icon::Dot => {
            p.circle_stroke(c, 9.0, Stroke::new(1.5_f32, MUTED));
        }
    }
}

/// The zig-zag bolt, in a `size` box at `origin`.
fn bolt_points(origin: egui::Pos2, size: f32) -> Vec<egui::Pos2> {
    [(0.64, 0.04), (0.28, 0.52), (0.62, 0.47), (0.34, 0.96)]
        .iter()
        .map(|&(x, y)| origin + egui::vec2(x * size, y * size))
        .collect()
}

fn window_icon() -> egui::IconData {
    let n = 64usize;
    let pts: Vec<(f32, f32)> = [(0.64, 0.10), (0.30, 0.52), (0.62, 0.48), (0.36, 0.92)]
        .iter()
        .map(|&(x, y)| (x * n as f32, y * n as f32))
        .collect();
    let dist = |px: f32, py: f32| -> f32 {
        pts.windows(2)
            .map(|w| {
                let ((ax, ay), (bx, by)) = (w[0], w[1]);
                let (dx, dy) = (bx - ax, by - ay);
                let t = (((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)).clamp(0.0, 1.0);
                ((px - ax - t * dx).powi(2) + (py - ay - t * dy).powi(2)).sqrt()
            })
            .fold(f32::MAX, f32::min)
    };
    let mut rgba = vec![0u8; n * n * 4];
    for y in 0..n {
        for x in 0..n {
            let (fx, fy) = (x as f32 + 0.5, y as f32 + 0.5);
            // rounded square background
            let r = 12.0;
            let (cx, cy) = (fx.clamp(r, n as f32 - r), fy.clamp(r, n as f32 - r));
            let inside_bg = ((fx - cx).powi(2) + (fy - cy).powi(2)).sqrt() <= r;
            let d = dist(fx, fy);
            let i = (y * n + x) * 4;
            if d < 5.0 {
                let a = (5.0 - d).clamp(0.0, 1.0);
                let (r0, g0, b0) = (0x10 as f32, 0x13 as f32, 0x19 as f32);
                rgba[i] = (r0 + (0x6C as f32 - r0) * a) as u8;
                rgba[i + 1] = (g0 + (0xC0 as f32 - g0) * a) as u8;
                rgba[i + 2] = (b0 + (0xFF as f32 - b0) * a) as u8;
                rgba[i + 3] = 255;
            } else if inside_bg {
                rgba[i..i + 4].copy_from_slice(&[0x10, 0x13, 0x19, 255]);
            }
        }
    }
    egui::IconData {
        rgba,
        width: n as u32,
        height: n as u32,
    }
}

// ---- state -----------------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum Page {
    Welcome,
    Requirements,
    Game,
    Location,
    Installing,
    Done,
}

const PAGES: [(Page, &str); 6] = [
    (Page::Welcome, "Welcome"),
    (Page::Requirements, "Requirements"),
    (Page::Game, "Your game"),
    (Page::Location, "Install location"),
    (Page::Installing, "Installing"),
    (Page::Done, "Play"),
];

#[derive(Clone)]
enum GameState {
    None,
    Checking,
    Ok(GameInfo),
    Problem(GameProblem),
}

#[derive(Clone)]
enum UpdState {
    Checking,
    Done(Result<UpdateInfo, String>),
}

struct Reqs {
    python: Check,
    tools: Check,
    log: Vec<String>,
}

struct App {
    page: Page,
    setup_exe: PathBuf,
    source: Option<setup::Source>,
    payload_bytes: u64,
    load_error: Option<String>,
    title: String,
    title_ids: Vec<String>,
    link_name: String,
    repaint: Arc<dyn Fn() + Send + Sync>,

    reqs: Arc<Mutex<Reqs>>,
    show_req_log: bool,

    game: Option<GameSource>,
    game_state: Arc<Mutex<(u64, GameState)>>,
    game_gen: u64,
    rpcs3: Option<PathBuf>,
    rpcs3_searched: bool,

    location: String,
    desktop: bool,
    start_menu: bool,

    installer: Option<Installer>,
    show_log: bool,
    confirm_cancel: bool,

    /// An existing installation found at the default location (or picked), offered for updating.
    existing: Option<PathBuf>,
    upd: Arc<Mutex<(u64, UpdState)>>,
    upd_gen: u64,
    updating: bool,
}

impl App {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        style(&cc.egui_ctx);
        let ctx = cc.egui_ctx.clone();
        let repaint: Arc<dyn Fn() + Send + Sync> = Arc::new(move || ctx.request_repaint());
        let setup_exe = std::env::current_exe().unwrap_or_default();
        let mut app = App {
            page: Page::Welcome,
            setup_exe: setup_exe.clone(),
            source: None,
            payload_bytes: 0,
            load_error: None,
            title: "inFamous".into(),
            title_ids: vec!["BCES00220".into()],
            link_name: "Infamous (Europe) (Fr,De,Es,It,Nl)".into(),
            repaint,
            reqs: Arc::new(Mutex::new(Reqs {
                python: Check::Unknown,
                tools: Check::Unknown,
                log: Vec::new(),
            })),
            show_req_log: false,
            game: None,
            game_state: Arc::new(Mutex::new((0, GameState::None))),
            game_gen: 0,
            rpcs3: None,
            rpcs3_searched: false,
            location: String::new(),
            desktop: true,
            start_menu: true,
            installer: None,
            show_log: false,
            confirm_cancel: false,
            existing: None,
            upd: Arc::new(Mutex::new((0, UpdState::Checking))),
            upd_gen: 0,
            updating: false,
        };
        match setup::find_source(&setup_exe) {
            Ok(info) => {
                app.payload_bytes = info.bytes;
                app.title = info.manifest.game_title.clone();
                app.title_ids = info.manifest.title_ids.clone();
                if let Some(l) = info.manifest.link_name.clone() {
                    app.link_name = l;
                }
                app.source = Some(info.source);
            }
            Err(e) => app.load_error = Some(e),
        }
        app.location = util::path_str(&setup::default_location(&app.title));
        app.check_reqs();
        let here = PathBuf::from(app.location.clone());
        app.find_existing(here);
        // For screenshots / testing: open on a given page.
        if let Ok(pg) = std::env::var("INFAMOUS_SETUP_PAGE") {
            app.page = match pg.as_str() {
                "requirements" => Page::Requirements,
                "game" => Page::Game,
                "location" => Page::Location,
                "done" => Page::Done,
                _ => Page::Welcome,
            };
            if let Ok(g) = std::env::var("INFAMOUS_SETUP_GAME") {
                let p = PathBuf::from(g);
                app.set_game(if p.is_dir() {
                    GameSource::Folder(p)
                } else {
                    GameSource::Iso { path: p, key: None }
                });
            }
        }
        app
    }

    /// Remember `dir` if it holds an installation and ask its installer whether GitHub has an update.
    fn find_existing(&mut self, dir: PathBuf) -> bool {
        if !setup::is_installed(&dir) {
            return false;
        }
        self.existing = Some(dir.clone());
        self.upd_gen += 1;
        let gen = self.upd_gen;
        *self.upd.lock().unwrap() = (gen, UpdState::Checking);
        let (st, repaint) = (self.upd.clone(), self.repaint.clone());
        std::thread::spawn(move || {
            let r = setup::check_update(&dir);
            let mut g = st.lock().unwrap();
            if g.0 == gen {
                g.1 = UpdState::Done(r);
            }
            drop(g);
            repaint();
        });
        true
    }

    fn start_update(&mut self) {
        let Some(dir) = self.existing.clone() else {
            return;
        };
        self.location = util::path_str(&dir);
        self.installer = Some(Installer::start_update(
            dir,
            self.title.clone(),
            self.start_menu,
            self.repaint.clone(),
        ));
        self.updating = true;
        self.confirm_cancel = false;
        self.show_log = false;
        self.page = Page::Installing;
    }

    fn update_card(&mut self, ui: &mut egui::Ui) {
        let Some(dir) = self.existing.clone() else {
            return;
        };
        let st = self.upd.lock().unwrap().1.clone();
        let mut do_update = false;
        let mut recheck = false;
        let mut other = false;
        card(ui, |ui| {
            ui.horizontal(|ui| {
                icon(
                    ui,
                    match &st {
                        UpdState::Checking => Icon::Busy,
                        UpdState::Done(Ok(i)) if i.available => Icon::Warn,
                        UpdState::Done(Ok(_)) => Icon::Ok,
                        UpdState::Done(Err(_)) => Icon::Bad,
                    },
                );
                ui.vertical(|ui| {
                    ui.spacing_mut().item_spacing.y = 3.0;
                    ui.label(
                        RichText::new(format!(
                            "{} is installed in {}",
                            self.title,
                            util::path_str(&dir)
                        ))
                        .strong(),
                    );
                    let (text, color) = match &st {
                        UpdState::Checking => {
                            ("Checking GitHub for a newer version...".to_string(), MUTED)
                        }
                        UpdState::Done(Ok(i)) if i.available && i.files > 0 => (
                            format!(
                                "An update is available (version {}): {} changed file(s), {}.",
                                i.version,
                                i.files,
                                util::human_bytes(i.bytes)
                            ),
                            ACCENT_HI,
                        ),
                        UpdState::Done(Ok(i)) if i.available => (
                            "The last update did not finish; Update finishes it.".to_string(),
                            ACCENT_HI,
                        ),
                        UpdState::Done(Ok(i)) => {
                            (format!("Up to date (version {}).", i.version), OK)
                        }
                        UpdState::Done(Err(e)) => (e.clone(), BAD),
                    };
                    ui.label(RichText::new(text).small().color(color));
                });
            });
            ui.add_space(6.0);
            ui.horizontal(|ui| {
                let can = !matches!(st, UpdState::Checking);
                if ui.add_enabled(can, primary("Update now")).on_hover_text("Download only the files that changed on GitHub, then rebuild what they affect.").clicked() {
                    do_update = true;
                }
                if ui.add_enabled(can, secondary("Check again")).clicked() {
                    recheck = true;
                }
                if ui.add(secondary("Another folder...")).clicked() {
                    other = true;
                }
            });
        });
        if do_update {
            self.start_update();
        } else if recheck {
            self.find_existing(dir);
        } else if other {
            if let Some(p) = rfd::FileDialog::new()
                .set_title("Choose the folder where the game is installed")
                .pick_folder()
            {
                if !self.find_existing(p.clone()) {
                    *self.upd.lock().unwrap() = (
                        self.upd_gen,
                        UpdState::Done(Err(format!(
                            "{} is not an installation of {}.",
                            util::path_str(&p),
                            self.title
                        ))),
                    );
                }
            }
        }
        ui.add_space(8.0);
        ui.label(
            RichText::new("To install a fresh copy instead, click Next.")
                .small()
                .color(MUTED),
        );
        ui.add_space(8.0);
    }

    fn check_reqs(&self) {
        {
            let mut r = self.reqs.lock().unwrap();
            r.python = Check::Checking;
            r.tools = Check::Checking;
        }
        let (reqs, rp) = (self.reqs.clone(), self.repaint.clone());
        std::thread::spawn(move || {
            setup::refresh_path();
            let py = setup::check_python();
            reqs.lock().unwrap().python = py;
            rp();
            let t = setup::check_build_tools();
            reqs.lock().unwrap().tools = t;
            rp();
        });
    }

    fn install_req(&self, python: bool) {
        {
            let mut r = self.reqs.lock().unwrap();
            let msg = if python {
                "Installing Python..."
            } else {
                "Installing the C++ build tools (a few GB; this can take a while)..."
            };
            if python {
                r.python = Check::Installing(msg.into());
            } else {
                r.tools = Check::Installing(msg.into());
            }
        }
        let (reqs, rp) = (self.reqs.clone(), self.repaint.clone());
        std::thread::spawn(move || {
            let log = {
                let reqs = reqs.clone();
                let rp = rp.clone();
                move |l: String| {
                    reqs.lock().unwrap().log.push(l);
                    rp();
                }
            };
            let r = if python {
                setup::install_python(&log)
            } else {
                setup::install_build_tools(&log)
            };
            let state = match r {
                Ok(()) => {
                    if python {
                        setup::check_python()
                    } else {
                        setup::check_build_tools()
                    }
                }
                Err(e) => Check::Missing(e),
            };
            let mut g = reqs.lock().unwrap();
            if python {
                g.python = state;
            } else {
                g.tools = state;
            }
            drop(g);
            rp();
        });
    }

    fn set_game(&mut self, src: GameSource) {
        self.game = Some(src.clone());
        self.game_gen += 1;
        let gen = self.game_gen;
        *self.game_state.lock().unwrap() = (gen, GameState::Checking);
        let (st, rp, ids) = (
            self.game_state.clone(),
            self.repaint.clone(),
            self.title_ids.clone(),
        );
        std::thread::spawn(move || {
            let r = match setup::check_game(&src, &ids) {
                Ok(i) => GameState::Ok(i),
                Err(p) => GameState::Problem(p),
            };
            let mut g = st.lock().unwrap();
            if g.0 == gen {
                g.1 = r;
            }
            drop(g);
            rp();
        });
    }

    fn game_info(&self) -> Option<GameInfo> {
        match &self.game_state.lock().unwrap().1 {
            GameState::Ok(i) => Some(i.clone()),
            _ => None,
        }
    }

    fn can_continue(&self) -> bool {
        match self.page {
            Page::Welcome => self.load_error.is_none(),
            Page::Requirements => {
                let r = self.reqs.lock().unwrap();
                matches!(r.python, Check::Ok(_)) && matches!(r.tools, Check::Ok(_))
            }
            Page::Game => match self.game_info() {
                Some(i) => {
                    !i.needs_rpcs3 || self.rpcs3.as_ref().map(|p| p.is_file()).unwrap_or(false)
                }
                None => false,
            },
            Page::Location => !self.location.trim().is_empty(),
            _ => false,
        }
    }

    fn start_install(&mut self) {
        let (Some(game), Some(source)) = (self.game.clone(), self.source.clone()) else {
            return;
        };
        let needs = self.game_info().map(|i| i.needs_rpcs3).unwrap_or(true);
        let opts = setup::InstallOpts {
            source,
            location: PathBuf::from(self.location.trim()),
            game,
            rpcs3: if needs { self.rpcs3.clone() } else { None },
            desktop_shortcut: self.desktop,
            start_menu: self.start_menu,
            game_title: self.title.clone(),
            link_name: self.link_name.clone(),
        };
        self.installer = Some(Installer::start(opts, self.repaint.clone()));
        self.confirm_cancel = false;
        self.page = Page::Installing;
    }

    // ---- pages ---------------------------------------------------------------------------

    fn heading(ui: &mut egui::Ui, title: &str, sub: &str) {
        ui.scope(|ui| {
            ui.spacing_mut().item_spacing.y = 4.0;
            ui.label(RichText::new(title).heading().strong());
            if !sub.is_empty() {
                ui.label(RichText::new(sub).color(MUTED));
            }
        });
        ui.add_space(12.0);
    }

    fn page_welcome(&mut self, ui: &mut egui::Ui) {
        Self::heading(
            ui,
            &format!("Welcome to {} for PC", self.title),
            "This program builds the PC version of the game from your own copy of it.",
        );
        if let Some(e) = &self.load_error {
            card(ui, |ui| {
                ui.horizontal(|ui| {
                    icon(ui, Icon::Bad);
                    ui.vertical(|ui| ui.label(RichText::new(e).color(BAD)));
                });
            });
            return;
        }
        if self.existing.is_some() {
            self.update_card(ui);
        }
        card(ui, |ui| {
            ui.label(RichText::new("You will need").strong());
            for (t, d) in [
                ("Your game", format!("A disc image (.iso) or game folder of {}, European release ({}).", self.title, self.title_ids.join(", "))),
                ("Disk space", format!("About {} free.", util::human_bytes(setup::space_needed(self.payload_bytes, 12 << 30)))),
                ("Time", "The first build takes roughly 20 to 60 minutes, depending on your PC. It runs on its own.".into()),
            ] {
                ui.horizontal(|ui| {
                    icon(ui, Icon::Dot);
                    ui.vertical(|ui| {
                        ui.spacing_mut().item_spacing.y = 2.0;
                        ui.label(RichText::new(t).strong());
                        ui.label(RichText::new(d).color(MUTED));
                    });
                });
            }
        });
        ui.add_space(6.0);
        ui.label(RichText::new("No game files are included in this program. Everything is made from your disc on your PC.").small().color(MUTED));
    }

    fn page_requirements(&mut self, ui: &mut egui::Ui) {
        Self::heading(
            ui,
            "Requirements",
            "The game is compiled on your PC, so it needs these free tools.",
        );
        let (python, tools, nlog) = {
            let r = self.reqs.lock().unwrap();
            (r.python.clone(), r.tools.clone(), r.log.len())
        };
        let busy = matches!(python, Check::Installing(_)) || matches!(tools, Check::Installing(_));
        let mut install = None;
        for (i, (name, what, c)) in [
            (
                "Python 3",
                "Runs the tools that rebuild the game code.",
                &python,
            ),
            (
                "Visual Studio Build Tools + Clang",
                "The C++ compiler. A free download from Microsoft (several GB).",
                &tools,
            ),
        ]
        .into_iter()
        .enumerate()
        {
            card(ui, |ui| {
                ui.horizontal(|ui| {
                    icon(
                        ui,
                        match c {
                            Check::Ok(_) => Icon::Ok,
                            Check::Missing(_) => Icon::Bad,
                            _ => Icon::Busy,
                        },
                    );
                    ui.vertical(|ui| {
                        ui.spacing_mut().item_spacing.y = 3.0;
                        ui.horizontal(|ui| {
                            ui.label(RichText::new(name).strong());
                            if let Check::Missing(_) = c {
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        if ui.add_enabled(!busy, primary("Install")).clicked() {
                                            install = Some(i == 0);
                                        }
                                    },
                                );
                            }
                        });
                        let (txt, col) = match c {
                            Check::Ok(s) => (s.clone(), MUTED),
                            Check::Missing(s) => (format!("Not found. {}\n{s}", what), WARN),
                            Check::Installing(s) => (s.clone(), ACCENT_HI),
                            Check::Checking | Check::Unknown => ("Checking...".into(), MUTED),
                        };
                        ui.label(RichText::new(txt).small().color(col));
                    });
                });
            });
        }
        if let Some(py) = install {
            self.install_req(py);
        }
        ui.horizontal(|ui| {
            if ui.add_enabled(!busy, secondary("Check again")).clicked() {
                self.check_reqs();
            }
            if nlog > 0
                && ui
                    .add(secondary(if self.show_req_log {
                        "Hide details"
                    } else {
                        "Show details"
                    }))
                    .clicked()
            {
                self.show_req_log = !self.show_req_log;
            }
        });
        if self.show_req_log {
            let log = self.reqs.lock().unwrap().log.join("\n");
            log_box(ui, &log, 150.0);
        }
    }

    fn page_game(&mut self, ui: &mut egui::Ui) {
        Self::heading(
            ui,
            "Your game",
            &format!(
                "Choose your {} disc image, or a folder with the game's files.",
                self.title
            ),
        );
        ui.horizontal(|ui| {
            if ui.add(primary("Choose disc image (.iso)...")).clicked() {
                if let Some(p) = rfd::FileDialog::new()
                    .set_title("Choose your disc image")
                    .add_filter("PS3 disc image", &["iso", "ISO"])
                    .pick_file()
                {
                    self.set_game(GameSource::Iso { path: p, key: None });
                }
            }
            if ui.add(secondary("Use a game folder...")).clicked() {
                if let Some(p) = rfd::FileDialog::new()
                    .set_title("Choose the folder that contains PS3_GAME")
                    .pick_folder()
                {
                    self.set_game(GameSource::Folder(p));
                }
            }
        });
        ui.add_space(4.0);
        let state = self.game_state.lock().unwrap().1.clone();
        let Some(src) = self.game.clone() else {
            ui.label(RichText::new("Disc images from Redump that come with a .dkey file work too: keep the .dkey next to the .iso.").small().color(MUTED));
            return;
        };
        let shown = match &src {
            GameSource::Iso { path, .. } => path.clone(),
            GameSource::Folder(p) => p.clone(),
        };
        card(ui, |ui| {
            ui.horizontal(|ui| {
                icon(
                    ui,
                    match &state {
                        GameState::Ok(_) => Icon::Ok,
                        GameState::Checking | GameState::None => Icon::Busy,
                        GameState::Problem(GameProblem::NeedsKey) => Icon::Warn,
                        GameState::Problem(_) => Icon::Bad,
                    },
                );
                ui.vertical(|ui| {
                    ui.spacing_mut().item_spacing.y = 3.0;
                    match &state {
                        GameState::Ok(i) => {
                            ui.label(
                                RichText::new(format!("{}  ·  {}", i.title, i.title_id)).strong(),
                            );
                        }
                        GameState::Checking | GameState::None => {
                            ui.label(RichText::new("Checking...").strong());
                        }
                        GameState::Problem(p) => {
                            ui.label(RichText::new(p.to_string()).strong().color(
                                if matches!(p, GameProblem::NeedsKey) {
                                    WARN
                                } else {
                                    BAD
                                },
                            ));
                        }
                    }
                    let mut sub = util::path_str(&shown);
                    if let GameState::Ok(i) = &state {
                        if i.copy_bytes > 0 {
                            sub.push_str(&format!(
                                "\n{} of game files",
                                util::human_bytes(i.copy_bytes)
                            ));
                        }
                        if i.encrypted {
                            sub.push_str("  ·  encrypted disc image, disc key OK");
                        }
                    }
                    ui.label(RichText::new(sub).small().color(MUTED));
                });
            });
            if let (
                GameState::Problem(GameProblem::NeedsKey | GameProblem::BadKey),
                GameSource::Iso { path, .. },
            ) = (&state, &src)
            {
                ui.add_space(4.0);
                if ui.add(primary("Choose disc key (.dkey)...")).clicked() {
                    let mut d = rfd::FileDialog::new()
                        .set_title("Choose the disc key for this image")
                        .add_filter("Disc key", &["dkey", "key", "txt"]);
                    if let Some(dir) = path.parent() {
                        d = d.set_directory(dir);
                    }
                    if let Some(k) = d.pick_file() {
                        self.set_game(GameSource::Iso {
                            path: path.clone(),
                            key: Some(k),
                        });
                    }
                }
            }
        });

        if let GameState::Ok(i) = &state {
            if i.needs_rpcs3 {
                if !self.rpcs3_searched {
                    self.rpcs3_searched = true;
                    if self.rpcs3.is_none() {
                        self.rpcs3 = setup::find_rpcs3();
                    }
                }
                ui.add_space(4.0);
                card(ui, |ui| {
                    ui.horizontal(|ui| {
                        let found = self.rpcs3.as_ref().map(|p| p.is_file()).unwrap_or(false);
                        icon(ui, if found { Icon::Ok } else { Icon::Warn });
                        ui.vertical(|ui| {
                            ui.spacing_mut().item_spacing.y = 3.0;
                            ui.horizontal(|ui| {
                                ui.label(RichText::new("RPCS3").strong());
                                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                    if !found && ui.add(secondary("Download RPCS3")).clicked() {
                                        setup::open_url("https://rpcs3.net/download");
                                    }
                                    if ui.add(if found { secondary("Change...") } else { primary("Locate rpcs3.exe...") }).clicked() {
                                        if let Some(p) = rfd::FileDialog::new().set_title("Find rpcs3.exe").add_filter("RPCS3", &["exe"]).pick_file() {
                                            self.rpcs3 = Some(p);
                                        }
                                    }
                                });
                            });
                            let t = match (&self.rpcs3, found) {
                                (Some(p), true) => format!("Found: {}\nUsed once to unlock the game's program file (EBOOT.BIN).", p.display()),
                                _ => "The PS3 emulator RPCS3 is used once to unlock the game's program file (EBOOT.BIN). Install it (free), then locate rpcs3.exe.".into(),
                            };
                            ui.label(RichText::new(t).small().color(if found { MUTED } else { WARN }));
                        });
                    });
                });
            }
        }
    }

    fn page_location(&mut self, ui: &mut egui::Ui) {
        Self::heading(
            ui,
            "Install location",
            "Where should the game be built? Pick a drive with plenty of space.",
        );
        card(ui, |ui| {
            ui.label(RichText::new("Folder").strong());
            ui.horizontal(|ui| {
                ui.add(
                    egui::TextEdit::singleline(&mut self.location)
                        .desired_width(ui.available_width() - 120.0),
                );
                if ui.add(secondary("Browse...")).clicked() {
                    let mut d = rfd::FileDialog::new().set_title("Choose where to install");
                    if let Some(p) = Path::new(self.location.trim())
                        .parent()
                        .filter(|p| p.is_dir())
                    {
                        d = d.set_directory(p);
                    }
                    if let Some(p) = d.pick_folder() {
                        // An empty folder or an earlier install is used as is; otherwise a subfolder.
                        let empty = std::fs::read_dir(&p)
                            .map(|mut r| r.next().is_none())
                            .unwrap_or(true);
                        let ours = p.join("installer").join("infamous.toml").is_file();
                        self.location = util::path_str(&if empty || ours {
                            p
                        } else {
                            p.join(format!("{} PC", self.title))
                        });
                    }
                }
            });
            let game_bytes = self.game_info().map(|i| i.copy_bytes).unwrap_or(0);
            let need = setup::space_needed(self.payload_bytes, game_bytes);
            let free = util::free_space(Path::new(self.location.trim()));
            let (txt, col) = match free {
                Some(f) if f < need => (
                    format!(
                        "Needs about {}, but only {} is free on that drive.",
                        util::human_bytes(need),
                        util::human_bytes(f)
                    ),
                    BAD,
                ),
                Some(f) => (
                    format!(
                        "Needs about {}  ·  {} free",
                        util::human_bytes(need),
                        util::human_bytes(f)
                    ),
                    MUTED,
                ),
                None => (format!("Needs about {}", util::human_bytes(need)), MUTED),
            };
            ui.label(RichText::new(txt).small().color(col));
            if self.location.contains(['(', ')', '&', '%', '!', '^']) {
                ui.label(
                    RichText::new(
                        "Tip: a path without ( ) & % ! ^ avoids trouble with the build tools.",
                    )
                    .small()
                    .color(WARN),
                );
            }
        });
        card(ui, |ui| {
            ui.checkbox(&mut self.desktop, "Create a desktop shortcut");
            ui.checkbox(&mut self.start_menu, "Add to the Start menu");
        });
        ui.label(RichText::new("When you click Install, the game is copied and compiled. It runs by itself; you can leave it alone.").small().color(MUTED));
    }

    fn page_installing(&mut self, ui: &mut egui::Ui) {
        let Some(inst) = &self.installer else { return };
        let (fraction, phase, detail, done, log, secs) = {
            let p = inst.progress.lock().unwrap();
            (
                p.fraction,
                p.phase.clone(),
                p.detail.clone(),
                p.done.clone(),
                if self.show_log {
                    p.log.join("\n")
                } else {
                    String::new()
                },
                p.started.map(|s| s.elapsed().as_secs()).unwrap_or(0),
            )
        };
        match &done {
            Some(Ok(())) => {
                self.page = Page::Done;
                return;
            }
            Some(Err(e)) => {
                Self::heading(
                    ui,
                    "Something went wrong",
                    if self.updating {
                        "The update stopped. Nothing is lost: run the update again and it continues where it stopped."
                    } else {
                        "The installation stopped. Nothing is lost: Try again continues where it stopped."
                    },
                );
                card(ui, |ui| {
                    ui.horizontal(|ui| {
                        icon(ui, Icon::Bad);
                        ui.vertical(|ui| ui.label(RichText::new(e).color(BAD)));
                    });
                });
            }
            None => {
                Self::heading(
                    ui,
                    if self.updating {
                        "Updating"
                    } else {
                        "Installing"
                    },
                    &format!("Elapsed {}:{:02}", secs / 60, secs % 60),
                );
                card(ui, |ui| {
                    ui.label(
                        RichText::new(if phase.is_empty() {
                            "Starting..."
                        } else {
                            &phase
                        })
                        .strong()
                        .size(16.5),
                    );
                    ui.add(
                        egui::ProgressBar::new(fraction)
                            .fill(ACCENT)
                            .show_percentage()
                            .animate(true),
                    );
                    ui.label(
                        RichText::new(if detail.is_empty() { " " } else { &detail })
                            .small()
                            .color(MUTED),
                    );
                });
            }
        }
        ui.horizontal(|ui| {
            if ui
                .add(secondary(if self.show_log {
                    "Hide details"
                } else {
                    "Show details"
                }))
                .clicked()
            {
                self.show_log = !self.show_log;
            }
            if done.is_some() {
                if ui.add(secondary("Copy details")).clicked() {
                    let all = inst.progress.lock().unwrap().log.join("\n");
                    ui.output_mut(|o| o.copied_text = all);
                }
                if ui.add(secondary("Open folder")).clicked() {
                    setup::open_folder(Path::new(self.location.trim()));
                }
            }
        });
        if self.show_log {
            log_box(ui, &log, 230.0);
        }
        if done.is_none() {
            ui.ctx()
                .request_repaint_after(std::time::Duration::from_millis(500));
        }
    }

    fn page_done(&mut self, ui: &mut egui::Ui) {
        if self.updating {
            Self::heading(
                ui,
                &format!("{} is up to date", self.title),
                "The latest version from GitHub is installed.",
            );
        } else {
            Self::heading(
                ui,
                &format!("{} is ready", self.title),
                "The game was built successfully.",
            );
        }
        card(ui, |ui| {
            ui.horizontal(|ui| {
                icon(ui, Icon::Ok);
                ui.vertical(|ui| {
                    ui.spacing_mut().item_spacing.y = 3.0;
                    ui.label(
                        RichText::new(format!("Installed to {}", self.location.trim())).strong(),
                    );
                    let s = match (self.desktop, self.start_menu) {
                        (true, true) => {
                            "Start it any time from the desktop shortcut or the Start menu."
                                .to_string()
                        }
                        (true, false) => "Start it any time from the desktop shortcut.".to_string(),
                        (false, true) => "Start it any time from the Start menu.".to_string(),
                        (false, false) => {
                            format!(
                                "Start it any time with {} in that folder.",
                                setup::front_door(Path::new(self.location.trim()), &self.title)
                                    .file_name()
                                    .map(|n| n.to_string_lossy().to_string())
                                    .unwrap_or_default()
                            )
                        }
                    };
                    ui.label(RichText::new(s).small().color(MUTED));
                });
            });
        });
        ui.add_space(10.0);
        ui.horizontal(|ui| {
            if ui
                .add(primary(&format!("Start {}", self.title)).min_size(egui::vec2(200.0, 46.0)))
                .clicked()
            {
                let exe = setup::front_door(Path::new(self.location.trim()), &self.title);
                let _ = std::process::Command::new(exe)
                    .current_dir(self.location.trim())
                    .spawn();
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
            if ui.add(secondary("Open install folder")).clicked() {
                setup::open_folder(Path::new(self.location.trim()));
            }
        });
        ui.add_space(6.0);
        ui.label(RichText::new(format!("To update later, use \"Update {}\" in the Start menu (or run this setup again): it downloads only what changed.", self.title)).small().color(MUTED));
    }
}

fn log_box(ui: &mut egui::Ui, text: &str, h: f32) {
    egui::Frame::none()
        .fill(Color32::from_rgb(0x0B, 0x0D, 0x12))
        .stroke(Stroke::new(1.0_f32, CARD_EDGE))
        .rounding(8.0_f32)
        .inner_margin(10.0_f32)
        .show(ui, |ui| {
            egui::ScrollArea::vertical()
                .max_height(h)
                .stick_to_bottom(true)
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.label(
                        RichText::new(text)
                            .monospace()
                            .color(Color32::from_rgb(0xB8, 0xC2, 0xCF)),
                    );
                });
        });
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // ---- sidebar ----
        egui::SidePanel::left("side")
            .exact_width(232.0)
            .resizable(false)
            .frame(egui::Frame::none().fill(SIDE).inner_margin(egui::Margin {
                left: 26.0,
                right: 18.0,
                top: 30.0,
                bottom: 20.0,
            }))
            .show(ctx, |ui| {
                let (rect, _) =
                    ui.allocate_exact_size(egui::vec2(44.0, 44.0), egui::Sense::hover());
                ui.painter()
                    .rect_filled(rect, 10.0_f32, Color32::from_rgb(0x14, 0x1A, 0x24));
                ui.painter().add(egui::Shape::line(
                    bolt_points(rect.min + egui::vec2(6.0, 6.0), 32.0),
                    Stroke::new(4.5_f32, ACCENT_HI),
                ));
                ui.add_space(4.0);
                ui.scope(|ui| {
                    ui.spacing_mut().item_spacing.y = 0.0;
                    ui.label(RichText::new(&self.title).size(28.0).strong());
                    ui.label(
                        RichText::new("PC SETUP")
                            .size(12.0)
                            .color(ACCENT_HI)
                            .strong(),
                    );
                });
                ui.add_space(28.0);
                for (p, name) in PAGES {
                    ui.horizontal(|ui| {
                        let (r, _) =
                            ui.allocate_exact_size(egui::vec2(18.0, 18.0), egui::Sense::hover());
                        let c = r.center();
                        let pt = ui.painter();
                        if p < self.page {
                            pt.circle_filled(c, 8.0, ACCENT.linear_multiply(0.35));
                            let s = Stroke::new(2.0_f32, ACCENT_HI);
                            pt.line_segment(
                                [c + egui::vec2(-3.5, 0.3), c + egui::vec2(-1.0, 2.8)],
                                s,
                            );
                            pt.line_segment(
                                [c + egui::vec2(-1.0, 2.8), c + egui::vec2(3.8, -2.6)],
                                s,
                            );
                        } else if p == self.page {
                            pt.circle_filled(c, 8.0, ACCENT);
                            pt.circle_filled(c, 3.0, SIDE);
                        } else {
                            pt.circle_stroke(
                                c,
                                7.0,
                                Stroke::new(1.5_f32, Color32::from_rgb(0x3A, 0x42, 0x4E)),
                            );
                        }
                        let t = RichText::new(name).size(15.0);
                        ui.label(if p == self.page {
                            t.strong().color(TEXT)
                        } else if p < self.page {
                            t.color(Color32::from_rgb(0xB6, 0xC0, 0xCC))
                        } else {
                            t.color(MUTED)
                        });
                    });
                    ui.add_space(2.0);
                }
                ui.with_layout(egui::Layout::bottom_up(egui::Align::Min), |ui| {
                    ui.label(
                        RichText::new(
                            "Fan-made PC port. Not affiliated with Sony or Sucker Punch.",
                        )
                        .size(11.0)
                        .color(Color32::from_rgb(0x5C, 0x66, 0x73)),
                    );
                });
            });

        // ---- navigation bar ----
        egui::TopBottomPanel::bottom("nav")
            .exact_height(70.0)
            .frame(
                egui::Frame::none()
                    .fill(BG)
                    .inner_margin(egui::Margin {
                        left: 30.0,
                        right: 30.0,
                        top: 14.0,
                        bottom: 16.0,
                    })
                    .stroke(Stroke::new(1.0_f32, Color32::from_rgb(0x1C, 0x22, 0x2B))),
            )
            .show(ctx, |ui| {
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    match self.page {
                        Page::Welcome | Page::Requirements | Page::Game => {
                            if ui
                                .add_enabled(self.can_continue(), primary("Next"))
                                .clicked()
                            {
                                self.page = match self.page {
                                    Page::Welcome => Page::Requirements,
                                    Page::Requirements => Page::Game,
                                    _ => Page::Location,
                                };
                            }
                        }
                        Page::Location => {
                            if ui
                                .add_enabled(self.can_continue(), primary("Install"))
                                .clicked()
                            {
                                self.start_install();
                            }
                        }
                        Page::Installing => {
                            let done = self
                                .installer
                                .as_ref()
                                .and_then(|i| i.progress.lock().unwrap().done.clone());
                            match done {
                                Some(Err(_)) => {
                                    if ui.add(primary("Try again")).clicked() {
                                        if self.updating {
                                            self.start_update();
                                        } else {
                                            self.start_install();
                                        }
                                    }
                                    if ui.add(secondary("Back")).clicked() {
                                        if self.updating {
                                            self.updating = false;
                                            self.page = Page::Welcome;
                                            if let Some(d) = self.existing.clone() {
                                                self.find_existing(d);
                                            }
                                        } else {
                                            self.page = Page::Location;
                                        }
                                    }
                                }
                                _ => {
                                    let label = if self.confirm_cancel {
                                        "Click again to stop"
                                    } else {
                                        "Cancel"
                                    };
                                    if ui.add(secondary(label)).clicked() {
                                        if self.confirm_cancel {
                                            if let Some(i) = &self.installer {
                                                i.stop();
                                            }
                                        } else {
                                            self.confirm_cancel = true;
                                        }
                                    }
                                }
                            }
                        }
                        Page::Done => {
                            if ui.add(secondary("Close")).clicked() {
                                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                            }
                        }
                    }
                    if matches!(self.page, Page::Requirements | Page::Game | Page::Location)
                        && ui.add(secondary("Back")).clicked()
                    {
                        self.page = match self.page {
                            Page::Requirements => Page::Welcome,
                            Page::Game => Page::Requirements,
                            _ => Page::Game,
                        };
                    }
                });
            });

        // ---- page ----
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(BG).inner_margin(egui::Margin {
                left: 34.0,
                right: 34.0,
                top: 30.0,
                bottom: 10.0,
            }))
            .show(ctx, |ui| {
                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show(ui, |ui| match self.page {
                        Page::Welcome => self.page_welcome(ui),
                        Page::Requirements => self.page_requirements(ui),
                        Page::Game => self.page_game(ui),
                        Page::Location => self.page_location(ui),
                        Page::Installing => self.page_installing(ui),
                        Page::Done => self.page_done(ui),
                    });
            });

        // Keep spinners moving while something runs in the background.
        let busy = {
            let r = self.reqs.lock().unwrap();
            matches!(r.python, Check::Checking | Check::Installing(_))
                || matches!(r.tools, Check::Checking | Check::Installing(_))
        } || matches!(self.game_state.lock().unwrap().1, GameState::Checking);
        if busy {
            ctx.request_repaint_after(std::time::Duration::from_millis(60));
        }
    }
}

impl Drop for App {
    fn drop(&mut self) {
        if let Some(i) = &self.installer {
            if i.progress.lock().unwrap().done.is_none() {
                i.stop();
            }
        }
    }
}

fn main() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([900.0, 600.0])
            .with_min_inner_size([820.0, 560.0])
            .with_title("inFamous Setup")
            .with_icon(window_icon()),
        centered: true,
        ..Default::default()
    };
    eframe::run_native(
        "inFamous Setup",
        options,
        Box::new(|cc| Ok(Box::new(App::new(cc)))),
    )
}
