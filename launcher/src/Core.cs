// inFamous Launcher: everything that is not UI. Reads installer\launch.txt (what inFamous.exe runs), the in-game
// menu's settings.ini, and the launcher's own launcher\launcher.ini; starts the game the same way inFamous.exe does.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace InfamousLauncher
{
    static class Inv
    {
        public static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static string Num(double v) { return v.ToString("0.###", C); }
        public static bool TryNum(string s, out double v)
        {
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, C, out v);
        }
        // Values match when they are the same string, or the same number ("1.5" == "1.50", "2" == "2.0").
        public static bool Same(string a, string b)
        {
            if (a == null || b == null) return a == b;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            double x, y;
            return TryNum(a, out x) && TryNum(b, out y) && Math.Abs(x - y) < 1e-6;
        }
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#", C) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#", C) + " MB";
            return (bytes / 1073741824.0).ToString("0.##", C) + " GB";
        }
        public static string Duration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " min";
            return (int)t.TotalSeconds + " s";
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Locations
    // ---------------------------------------------------------------------------------------------------------
    sealed class GamePaths
    {
        public readonly string Root;
        public GamePaths(string root) { Root = Path.GetFullPath(root).TrimEnd('\\'); }

        public string LaunchTxt { get { return Path.Combine(Root, "installer", "launch.txt"); } }
        public string InstallerExe { get { return Path.Combine(Root, "installer", "infamous-installer.exe"); } }
        public string UpdateCmd { get { return Path.Combine(Root, "Update inFamous.cmd"); } }
        public string LauncherDir { get { return Path.Combine(Root, "launcher"); } }
        public string LauncherIni { get { return Path.Combine(LauncherDir, "launcher.ini"); } }
        public string DefaultPort { get { return Path.Combine(Root, "infamous-recomp"); } }
        public string BackupDir { get { return Path.Combine(Root, "save-backups"); } }

        // The checkout root holds installer\ and infamous-recomp\. The launcher normally sits in the root, but
        // also works from launcher\ or a build folder below it.
        public static GamePaths Find()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "installer", "launch.txt")) || Directory.Exists(Path.Combine(dir, "infamous-recomp")))
                    return new GamePaths(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return new GamePaths(AppDomain.CurrentDomain.BaseDirectory);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // installer\launch.txt -- the same format and path rules as installer/src/bin/infamous-launch.rs
    // ---------------------------------------------------------------------------------------------------------
    sealed class LaunchSpec
    {
        public string Exe, Cwd, Stdout, Stderr;
        public readonly List<string> Args = new List<string>();
        public readonly List<KeyValuePair<string, string>> Env = new List<KeyValuePair<string, string>>();

        public static string Resolve(string root, string p)
        {
            if (Path.IsPathRooted(p)) return p;
            string r = root;
            foreach (var part in p.Split('/'))
                if (part.Length > 0) r = Path.Combine(r, part);
            return r;
        }

        public static LaunchSpec Load(string file, string root)
        {
            var s = new LaunchSpec();
            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                switch (k)
                {
                    case "exe": s.Exe = Resolve(root, v); break;
                    case "cwd": s.Cwd = Resolve(root, v); break;
                    case "arg": s.Args.Add(Resolve(root, v)); break;
                    case "stdout": s.Stdout = v; break;
                    case "stderr": s.Stderr = v; break;
                    case "env":
                        int e2 = v.IndexOf('=');
                        if (e2 > 0) s.Env.Add(new KeyValuePair<string, string>(v.Substring(0, e2), v.Substring(e2 + 1)));
                        break;
                }
            }
            if (s.Exe == null) throw new InvalidDataException("launch.txt names no game executable");
            if (s.Cwd == null) s.Cwd = root;
            return s;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // launcher\launcher.ini
    // ---------------------------------------------------------------------------------------------------------
    sealed class LauncherConfig
    {
        public const string Unset = "<unset>";
        public readonly Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> EnvOrder = new List<string>();   // keys the player added, in order
        public readonly Dictionary<string, bool> Mods = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ReShade = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // [reshade]: the ReShade page
        public string Priority = "normal";     // normal | above | high
        public string AfterLaunch = "stay";    // stay | minimize | close
        public string Exe = "";                // game executable override, relative to the root or absolute
        public string Dlss5 = "0";             // 1 = install OptiScaler (launcher\dlss5) for DLSS 5 Neural Rendering
        public string LowCpu = "0";            // 1 = Low-end CPU mode (Catalog.LowCpuEnv)
        public string LastPage = "home";
        public string LastPlayed = "";
        public long PlaySeconds;

        public static LauncherConfig Load(string path)
        {
            var c = new LauncherConfig();
            if (!File.Exists(path)) return c;
            string sec = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]")) { sec = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (sec == "env") { c.Env[k] = v; c.EnvOrder.Add(k); }
                else if (sec == "mods") c.Mods[k] = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (sec == "reshade") c.ReShade[k] = v;
                else if (sec == "launcher")
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "priority": c.Priority = v; break;
                        case "after_launch": c.AfterLaunch = v; break;
                        case "exe": c.Exe = v; break;
                        case "dlss5": c.Dlss5 = v; break;
                        case "low_cpu": c.LowCpu = v; break;
                        case "last_page": c.LastPage = v; break;
                        case "last_played": c.LastPlayed = v; break;
                        case "play_seconds": long.TryParse(v, out c.PlaySeconds); break;
                    }
                }
            }
            return c;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; inFamous Launcher settings (written by inFamous Launcher.exe).");
            sb.AppendLine("; [env] overrides the variables in installer\\launch.txt; <unset> removes one. The in-game menu's");
            sb.AppendLine("; settings (display mode, resolution, vsync, ...) live in infamous-recomp\\settings.ini instead.");
            sb.AppendLine();
            sb.AppendLine("[launcher]");
            sb.AppendLine("priority=" + Priority);
            sb.AppendLine("after_launch=" + AfterLaunch);
            sb.AppendLine("exe=" + Exe);
            sb.AppendLine("dlss5=" + Dlss5);
            sb.AppendLine("low_cpu=" + LowCpu);
            sb.AppendLine("last_page=" + LastPage);
            sb.AppendLine("last_played=" + LastPlayed);
            sb.AppendLine("play_seconds=" + PlaySeconds.ToString(Inv.C));
            sb.AppendLine();
            sb.AppendLine("[reshade]");
            foreach (var k in ReShade.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(k + "=" + ReShade[k]);
            sb.AppendLine();
            sb.AppendLine("[env]");
            foreach (var k in Env.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(k + "=" + Env[k]);
            sb.AppendLine();
            sb.AppendLine("[mods]");
            foreach (var kv in Mods.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(kv.Key + "=" + (kv.Value ? "1" : "0"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Settings catalog. "Ini" settings are the in-game menu's (F10) keys: main.cpp loads them from settings.ini
    // and they override the environment, so the launcher writes them there. Everything else is an environment
    // variable for the game process.
    // ---------------------------------------------------------------------------------------------------------
    enum Kind { Toggle, Choice, Slider }
    enum Store { Ini, Env, Launcher }

    sealed class Opt
    {
        public readonly string V, L;
        public Opt(string v, string l) { V = v; L = l; }
    }

    sealed class Def
    {
        public string Key, Page, Group, Label, Desc, Warn, EnabledIf;
        public Kind Kind;
        public Store Store;
        public Opt[] Opts;             // Choice; Toggle = { off, on } (a null value removes the variable)
        public double Min, Max, Step;  // Slider
        public Func<double, string> Fmt;
        public string Fallback;        // what the game uses when the variable is absent
        public bool Experimental;
    }

    static class Catalog
    {
        // Same order as the in-game menu (ps3recomp/libs/video/rsx_ui_overlay.c s_mi[]), which is also the order
        // the game writes settings.ini in.
        public static readonly string[] IniKeys = { "FULLSCREEN", "PRESENT_VSYNC", "UI_FPS_CAP", "KBM_GLYPHS", "PAD_MOUSE_SPEED",
                                                    "RES_SCALE", "SHADOWS", "TEX_ANISO", "TEX_MIPS" };

        public static readonly List<Def> All = new List<Def>();
        static string page, group;

        static Def Add(Def d) { d.Page = page; d.Group = group; All.Add(d); return d; }
        static Def Toggle(Store st, string key, string label, string desc, string fallback, string off = "0", string on = "1")
        {
            return Add(new Def { Store = st, Key = key, Label = label, Desc = desc, Kind = Kind.Toggle, Fallback = fallback,
                                 Opts = new[] { new Opt(off, "Off"), new Opt(on, "On") } });
        }
        static Def Choice(Store st, string key, string label, string desc, string fallback, params Opt[] opts)
        {
            return Add(new Def { Store = st, Key = key, Label = label, Desc = desc, Kind = Kind.Choice, Fallback = fallback, Opts = opts });
        }
        static Def Slider(string key, string label, string desc, string fallback, double min, double max, double step, Func<double, string> fmt)
        {
            return Add(new Def { Store = Store.Env, Key = key, Label = label, Desc = desc, Kind = Kind.Slider, Fallback = fallback,
                                 Min = min, Max = max, Step = step, Fmt = fmt });
        }
        static Def LSlider(string key, string label, string desc, string fallback, double min, double max, double step, Func<double, string> fmt)
        {
            var d = Slider(key, label, desc, fallback, min, max, step, fmt);
            d.Store = Store.Launcher;
            return d;
        }
        static Func<double, string> Times(string one)
        {
            return x => Math.Abs(x - 1) < 1e-6 && one != null ? "1× (" + one + ")" : x.ToString("0.##", Inv.C) + "×";
        }
        static string Pct(double x) { return Math.Abs(x) < 1e-6 ? "Off" : (x * 100).ToString("0", Inv.C) + " %"; }
        static string Signed(double x) { return Math.Abs(x) < 1e-6 ? "0 (as the game)" : (x > 0 ? "+" : "−") + Math.Abs(x * 100).ToString("0", Inv.C); }

        static Catalog()
        {
            // ---------------- Display ----------------
            page = "display"; group = "Screen";
            Choice(Store.Ini, "FULLSCREEN", "Display mode", "Borderless fullscreen covers the monitor the game window is on. F11 switches in game.", "0",
                   new Opt("0", "Windowed"), new Opt("1", "Fullscreen (borderless)"));
            Choice(Store.Ini, "RES_SCALE", "Resolution", "Internal render resolution. The game draws at this size and scales it to the window, so higher values are sharper and cost GPU time. The PS3 rendered 720p.", "1",
                   new Opt("1", "720p · 1280×720 (PS3)"), new Opt("1.5", "1080p · 1920×1080"), new Opt("2", "1440p · 2560×1440"),
                   new Opt("2.5", "1800p · 3200×1800"), new Opt("3", "2160p · 3840×2160 (4K)"));
            Toggle(Store.Ini, "PRESENT_VSYNC", "VSync", "Waits for the display's refresh before showing a frame. Off gives the lowest input latency; the frame rate limiter keeps pacing steady.", "0");
            var vrr = Toggle(Store.Env, "PRESENT_TEARING", "Variable refresh (G-Sync / FreeSync)", "With VSync off, frames are shown immediately so a variable-refresh display can follow the game's frame rate in fullscreen. Without such a display you may see tearing.", "0");
            vrr.EnabledIf = "PRESENT_VSYNC=0";
            group = "Interface";
            Choice(Store.Ini, "KBM_GLYPHS", "Button prompts", "Which button icons the game shows. Automatic follows the device you used last.", "1",
                   new Opt("1", "Automatic"), new Opt("0", "Controller"), new Opt("2", "Keyboard & mouse"));

            // ---------------- Graphics ----------------
            page = "graphics"; group = "Quality";
            Toggle(Store.Ini, "SHADOWS", "Shadows", "Sun and character shadows.", "1");
            Choice(Store.Ini, "TEX_ANISO", "Texture filtering", "Anisotropic filtering keeps textures sharp at grazing angles (streets, rooftops). Cheap on modern GPUs.", "-1",
                   new Opt("-1", "Game default"), new Opt("0", "Bilinear"), new Opt("4", "4× anisotropic"), new Opt("8", "8× anisotropic"), new Opt("16", "16× anisotropic"));
            Toggle(Store.Ini, "TEX_MIPS", "Mipmaps", "Smaller texture levels for distant surfaces. Turning them off shimmers badly; keep on.", "1");
            group = "Anti-aliasing";
            var dlss = Toggle(Store.Env, "DLSS", "NVIDIA DLSS", "DLSS anti-aliasing (DLAA) or upscaling (Quality / Balanced / Performance, below): smooth, stable edges instead of jaggies and shimmer. Cole can soften slightly while he animates. Needs an NVIDIA RTX graphics card; on other cards the game runs without it.", "0");
            var dlssMode = Choice(Store.Env, "DLSS_MODE", "DLSS mode", "DLAA anti-aliases at the resolution on the Display page. The upscaling modes render the 3D scene at a lower resolution and DLSS rebuilds it at full size: less GPU work, the HUD enlarged smoothly. The game is mostly CPU-limited, so they lower GPU load and heat more than they raise FPS. It never renders below the PS3's own 720p.", null,
                   new Opt(null, "DLAA · native resolution, best image"), new Opt("quality", "Quality · renders at 67 %"),
                   new Opt("balanced", "Balanced · renders at 58 %"), new Opt("performance", "Performance · renders at 50 %"));
            dlssMode.EnabledIf = "DLSS=1";
            dlss.Warn = "Uses the DLSS files in the dlss-sdk folder next to the game. The game's own updates replace the renderer: re-apply DLSS with launcher\\dlss\\apply-dlss.cmd afterwards.";
            var dlssModel = Choice(Store.Env, "DLSS_PRESET", "DLSS model", "The AI model DLSS uses. L and M are the newer DLSS 4.5 models: sharper, steadier and less ghosting, at a little more GPU time (made for RTX 40 and 50 series).", null,
                   new Opt(null, "Automatic (K, DLSS 4)"), new Opt("L", "L · DLSS 4.5, best quality"), new Opt("M", "M · DLSS 4.5, faster"),
                   new Opt("K", "K · DLSS 4"), new Opt("J", "J · DLSS 4, less ghosting, more flicker"));
            dlssModel.EnabledIf = "DLSS=1";
            dlss.EnabledIf = "FSR=0";
            var nr = Toggle(Store.Launcher, "launcher.dlss5", "DLSS 5 Neural Rendering", "NVIDIA's DLSS 5 model run over the DLSS image: synthesised lighting and material detail, HUD untouched. Driven through OptiScaler (a community mod, not NVIDIA's own integration); its overlay opens with the Insert key, under \"DLSS Neural Rendering\". Needs an RTX 50 series card. Applied when you press Play.", "0");
            nr.Warn = "Needs OptiScaler-DLSSNR extracted into launcher\\dlss5, plus NVIDIA's nvngx_dlssnr.dll in the same folder. Turn it off here if the game misbehaves.";
            nr.EnabledIf = "DLSS=1&FSR_FG=0";
            nr.Experimental = true;
            var fsr = Toggle(Store.Env, "FSR", "AMD FSR", "AMD's upscaler, for every graphics card: FSR 4 (machine learning) on Radeon RX 7000 / 9000, FSR 3.1 on other GPUs, chosen automatically. Native AA anti-aliases at the resolution on the Display page; the other modes render the 3D scene smaller and FSR rebuilds it at full size. Use it instead of DLSS on AMD and Intel cards.", "0");
            fsr.EnabledIf = "DLSS=0";
            fsr.Warn = "Uses the AMD FSR SDK files in the fsr-sdk folder next to the game. The game's own updates replace the renderer: re-apply with launcher\\dlss\\apply-dlss.cmd afterwards (Tools page).";
            Choice(Store.Env, "FSR_MODE", "FSR mode", "Like the DLSS modes, with AMD's ratios. It never renders below the PS3's own 720p.", null,
                   new Opt(null, "Native AA · native resolution, best image"), new Opt("quality", "Quality · renders at 67 %"),
                   new Opt("balanced", "Balanced · renders at 59 %"), new Opt("performance", "Performance · renders at 50 %")).EnabledIf = "FSR=1";
            Slider("FSR_SHARPNESS", "FSR sharpening", "FSR's own sharpening pass on the upscaled image.", "0", 0, 1, 0.05,
                   x => Math.Abs(x) < 1e-6 ? "Off" : (x * 100).ToString("0", Inv.C) + " %").EnabledIf = "FSR=1";
            var fg = Toggle(Store.Env, "FSR_FG", "Frame generation (AMD FSR)", "Shows a generated frame between every two the game renders, so motion looks about twice as smooth. Most useful where the CPU limits the frame rate, such as crowded streets. Works with DLSS or FSR on, on any graphics card (FSR 3.1; FSR 4 frame generation on Radeon RX 9000). Adds a little input latency; best with a variable-refresh display and at least ~60 FPS before generation. The FPS counter in the port shows the rendered frames.", "0");
            fg.EnabledIf = "DLSS=1|FSR=1";
            fg.Warn = "While it is on, DLSS 5 Neural Rendering stays off (OptiScaler has its own swap chain handling).";
            fg.Experimental = true;
            group = "World detail";
            Slider("LOD_CLASS_SCALE", "Draw distance", "Scales the city's level-of-detail switch distances: buildings, props and people keep their detail and appear farther away. Costs CPU and GPU time. Below 1× objects simplify and fade sooner than on the PS3, which helps slow PCs. F5 toggles it in game to compare.",
                   "1", 0.5, 4, 0.25, Times("PS3"));
            Slider("LOD_DIST_SCALE", "Object fade distance", "Pushes out the distance where individual objects fade between detail levels.",
                   "2", 1, 4, 0.25, Times("off"));
            var car = Slider("CAR_DIST_SCALE", "Vehicle draw distance", "Traffic and parked cars are drawn, and switch detail, this many times farther away, so fewer cars pop in. Below 1× fewer distant cars are drawn.",
                   "1", 0.5, 4, 0.25, Times("PS3"));
            car.Experimental = true;
            var anim = Toggle(Store.Env, "ANIM_LOD", "Distant animation LOD", "Skips small animation jobs for far-away characters for about +1 FPS in the city. Cole is never affected. Off in the shipped profile.", "0");
            anim.Experimental = true;

            // ---------------- ReShade (launcher\reshade, applied at Play by ReShadeSetup) ----------------
            // The fallbacks are the Remastered look.
            page = "reshade"; group = "In the game";
            Toggle(Store.Launcher, "reshade.on", "ReShade", "Post-processing on the finished image: sharper texture detail, contact shadows, less haze and richer colour. Costs a little GPU time; leave it off on a weak graphics card. Applied when you press Play.", "0");
            var src = Choice(Store.Launcher, "reshade.source", "Effects", "The look set on this page, or a preset you build yourself in ReShade's overlay (Home key in game) from any shaders in launcher\\reshade\\shaders.", "launcher",
                   new Opt("launcher", "This page's look"), new Opt("own", "My own ReShade preset"));
            src.EnabledIf = "reshade.on=1";
            const string mine = "reshade.on=1&reshade.source=launcher";
            group = "Detail";
            LSlider("reshade.sharpen", "Sharpening", "Contrast-adaptive sharpening: crisper textures and edges without halos.", "0.4", 0, 1, 0.05, Pct).EnabledIf = mine;
            LSlider("reshade.clarity", "Clarity", "Local contrast: brick, concrete and cloth detail stand out more, overall contrast stays the same.", "0.3", 0, 1, 0.05, Pct).EnabledIf = mine;
            LSlider("reshade.ao", "Ambient occlusion", "Soft contact shadows where surfaces meet: corners, ledges, under objects. Uses the game's depth; the most expensive effect here.", "0.5", 0, 1, 0.05, Pct).EnabledIf = mine;
            Toggle(Store.Launcher, "reshade.deband", "Deband", "Smooths the colour banding in the sky, fog and smoke.", "1").EnabledIf = mine;
            group = "Colour";
            LSlider("reshade.dehaze", "Haze removal", "Pulls down the washed-out black level of the game's haze.", "0.3", 0, 1, 0.05, Pct).EnabledIf = mine;
            LSlider("reshade.contrast", "Contrast", null, "0.15", -0.5, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.vibrance", "Vibrance", "Saturates dull colours more than vivid ones, so skin tones stay natural.", "0.25", -1, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.saturation", "Saturation", null, "1", 0, 2, 0.05, x => (x * 100).ToString("0", Inv.C) + " %").EnabledIf = mine;
            LSlider("reshade.exposure", "Exposure", null, "0", -1, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.shadows", "Shadows", "Lifts (+) or deepens (−) the dark tones.", "0", -1, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.highlights", "Highlights", "Brightens (+) or recovers (−) the bright tones.", "-0.1", -1, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.warmth", "Warmth", "Cooler (−) or warmer (+). inFamous is graded quite yellow.", "-0.1", -1, 1, 0.05, Signed).EnabledIf = mine;
            LSlider("reshade.tint", "Tint", "Magenta (−) or green (+).", "0", -1, 1, 0.05, Signed).EnabledIf = mine;
            group = "Lens";
            LSlider("reshade.vignette", "Vignette", "Darkens the edges of the picture.", "0", 0, 1, 0.05, Pct).EnabledIf = mine;

            // ---------------- Performance ----------------
            page = "performance"; group = "Frame rate";
            Choice(Store.Ini, "UI_FPS_CAP", "Frame rate limit", "Caps the frame rate for steadier pacing, lower heat and less noise. Game speed does not depend on the frame rate. These are the steps the game's limiter supports.", "0",
                   new Opt("0", "Off (uncapped)"), new Opt("60", "60 FPS"), new Opt("50", "50 FPS"), new Opt("45", "45 FPS"), new Opt("40", "40 FPS"), new Opt("30", "30 FPS (PS3)"));
            group = "Adaptive draw distance";
            Toggle(Store.Env, "ADAPT_LOD", "Adaptive draw distance", "Lowers the draw distance when the frame rate drops and raises it again when there is headroom. Only works with Draw distance above 1×.", "0");
            var lo = Slider("ADAPT_LOD_LO_FPS", "Lower detail below", "Below this frame rate the draw distance slides toward the minimum.", "58", 25, 140, 1, x => x.ToString("0", Inv.C) + " FPS");
            var hi = Slider("ADAPT_LOD_HI_FPS", "Restore detail above", "Above this frame rate it slides back to the full draw distance. Keep it a few FPS above the lower limit.", "64", 26, 160, 1, x => x.ToString("0", Inv.C) + " FPS");
            var mn = Slider("ADAPT_LOD_MIN", "Lowest draw distance", "How far down the adaptive mode may go.", "1", 1, 3, 0.25, Times("PS3"));
            lo.EnabledIf = hi.EnabledIf = mn.EnabledIf = "ADAPT_LOD=1";
            group = "CPU";
            Toggle(Store.Launcher, "launcher.lowcpu", "Low-end CPU mode", "For CPUs with 4 cores / 8 threads or fewer. The shipped profile is tuned for big CPUs: its SPU threads spin-wait and it reserves cores, and on a small CPU those threads starve each other until the game stalls. This mode stops the spinning and the core reservations. On a 4-core CPU it takes the game from stalling to about 49 FPS; on a big CPU it costs about 2 %. The Potato PC preset turns it on.", "0");
            Toggle(Store.Env, "PS3_PCORE", "Prefer performance cores", "On Intel hybrid CPUs, keeps the busy emulation threads on P-cores instead of E-cores. No effect on other CPUs.", "1");
            var mainCore = Toggle(Store.Env, "PS3_MAIN_CORE", "Reserve a core for the main thread", "Gives the game's main PPU thread a physical core of its own (+0.7 FPS in the port's benchmarks).", "1");
            var fifo = Toggle(Store.Env, "PS3_FIFO_CORE", "Reserve a core for the GPU command thread", "Gives the RSX FIFO thread its own physical core (+1.9 to +2.6 FPS in the port's benchmarks).", "0");
            fifo.Warn = "On CPUs with 4 cores or fewer, reserving cores can make things slower.";
            var audioCore = Toggle(Store.Env, "PS3_AUDIO_CORE", "Reserve a core for the audio mixer", "Keeps the SPU audio mixer on its own core so music and effects don't stutter under load.", "1");
            Toggle(Store.Env, "PS3_FAST_HOST", "Boost render threads", "Runs the RSX, FIFO and render threads at above-normal priority.", null, null, "1");
            mainCore.EnabledIf = fifo.EnabledIf = audioCore.EnabledIf = "launcher.lowcpu=0";   // Low-end CPU mode turns them off
            group = "Process";
            Choice(Store.Launcher, "launcher.priority", "Process priority", "Windows priority class for the game. High can make the rest of the PC sluggish while you play.", "normal",
                   new Opt("normal", "Normal"), new Opt("above", "Above normal"), new Opt("high", "High"));

            // ---------------- Input ----------------
            page = "input"; group = "Keyboard & mouse";
            Toggle(Store.Env, "PAD_KBM", "Keyboard & mouse controls", "Play with keyboard and mouse. A controller keeps working either way.", "1");
            Choice(Store.Ini, "PAD_MOUSE_SPEED", "Mouse speed", "Camera speed for mouse look.", "450",
                   new Opt("900", "1 (slowest)"), new Opt("650", "2"), new Opt("450", "3 (default)"), new Opt("325", "4"), new Opt("225", "5 (fastest)"));
            Toggle(Store.Env, "PAD_MOUSE_INVERT_Y", "Invert mouse Y", "Moving the mouse up looks down.", "0");
            Toggle(Store.Env, "PAD_RAWINPUT", "Raw mouse input", "Reads the mouse directly, without Windows pointer acceleration.", "1");
            Toggle(Store.Env, "PAD_MOUSE_RAWTURN", "1:1 camera turning", "Removes the camera's turn acceleration and axis snapping while the mouse moves, so the view turns exactly with the mouse.", "1");
            Toggle(Store.Env, "PAD_MOUSE_NOSPRING", "Stop where the mouse stops", "Removes the camera's ease-out after a turn.", "1");
            Toggle(Store.Env, "PAD_MOUSE_LINEAR", "Linear mouse response", "Undoes the game's squared stick curve so camera speed follows mouse speed.", "1");
            Slider("PAD_MOUSE_MAX", "Fast flick limit", "How far past a full stick a fast mouse flick may turn the camera.", "8", 1, 16, 1, x => x.ToString("0", Inv.C) + "× stick");
            Toggle(Store.Env, "PAD_MOUSE_LSTICK", "Mouse aims left-stick moments", "Lets the mouse drive left-stick-only aiming (such as the Sasha struggle) when the camera is scripted.", "1");
            group = "Keyboard";
            Toggle(Store.Env, "ESC_QUITS", "Esc closes the game", "Off: Esc opens the pause menu. On: Esc quits at once.", "0");
            Toggle(Store.Env, "PAD_NO_KEYBOARD", "Ignore the keyboard", "Controller only. Useful when something else is typing while you play.", null, null, "1");

            // ---------------- Controller ----------------
            page = "controller"; group = "PlayStation and Steam controllers";
            Toggle(Store.Env, "PAD_SDL", "Read PlayStation and Steam controllers", "DualSense, DualSense Edge, DualShock 4, Steam Controller and Switch Pro controllers are read directly through SDL3, by USB or Bluetooth. Xbox controllers always work, through XInput.", "1");
            var steamPad = Toggle(Store.Env, "PAD_STEAM_CONTROLLER", "Read Steam Controllers directly", "Off: leave the Steam Controller to Steam, for playing the game through Steam Input (where it acts as an Xbox controller).", "1");
            var deadzone = Slider("PAD_DEADZONE", "Stick deadzone", "How far a stick has to move before the game reacts. Raise it if Cole or the camera creeps with the sticks at rest.", "10", 0, 30, 1, x => x.ToString("0", Inv.C) + " %");
            var touchpad = Toggle(Store.Env, "PAD_TOUCHPAD", "Touchpad click is SELECT", "Pressing the DualSense or DualShock 4 touchpad works like the Create / Share button, the PS3's SELECT.", "1");
            var lightbar = Choice(Store.Env, "PAD_LIGHTBAR", "Light bar", "The DualSense / DualShock 4 light bar while you play.", null,
                   new Opt(null, "Controller default (player colour)"), new Opt("karma", "Follows your karma (blue good, red evil)"), new Opt("0050FF", "Blue"),
                   new Opt("FF1E00", "Red"), new Opt("A020F0", "Purple"), new Opt("FFFFFF", "White"), new Opt("off", "Off"));
            steamPad.EnabledIf = deadzone.EnabledIf = touchpad.EnabledIf = lightbar.EnabledIf = "PAD_SDL=1";
            group = "All controllers";
            Toggle(Store.Env, "PAD_RUMBLE", "Vibration", "Controller rumble when the game asks for it.", "1");

            // ---------------- Audio ----------------
            page = "audio"; group = "Sound";
            Toggle(Store.Env, "AUDIO_MUTE", "Mute game audio", "Silences all game sound.", "0");
            Slider("AUDIO_AHEAD_BLOCKS", "Audio buffer", "Sound queued ahead of playback. Raise it if you hear crackling; lower it for less delay.", "16", 0, 40, 1,
                   x => x.ToString("0", Inv.C) + " blocks · " + (x * 5.333).ToString("0", Inv.C) + " ms");
            Toggle(Store.Env, "AUDIO_PLC", "Dropout concealment", "Fills short gaps in the game's audio stream so a stall doesn't click.", "1");
            Toggle(Store.Env, "AUDIO_STRETCH", "Stretch audio during stalls", "Slows the sound slightly instead of leaving gaps when the game falls behind. Off in the shipped profile.", "1");
            Toggle(Store.Env, "AUDIO_THREAD_BOOST", "High-priority audio threads", "Runs the game's audio threads at the highest priority so busy emulation threads can't delay them.", "1");

            // ---------------- Launcher (shown on the Advanced page) ----------------
            page = "advanced"; group = "Launcher";
            Choice(Store.Launcher, "launcher.after", "When the game starts", "What the launcher does after starting the game. Staying open shows play time and lets you stop the game.", "stay",
                   new Opt("stay", "Keep the launcher open"), new Opt("minimize", "Minimize the launcher"), new Opt("close", "Close the launcher"));
        }

        public static Def Find(string key) { return All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); }
        public static bool IsIni(string key) { return IniKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)); }

        public sealed class Preset
        {
            public string Name, Desc; public string[] Values;
            public Preset(string n, string d, params string[] v) { Name = n; Desc = d; Values = v; }
        }
        public static readonly Preset[] Presets =
        {
            // Low-end PCs: the game is CPU-bound, so this mostly cuts CPU work (shorter draw distances, fewer distant
            // cars, simplified far animation, 30 FPS) and keeps the GPU at the PS3's 720p with no extras.
            new Preset("Potato PC", "720p · 30 FPS · shortest draw distance, for low-end PCs",
                       "RES_SCALE=1", "TEX_ANISO=-1", "TEX_MIPS=1", "SHADOWS=0", "LOD_CLASS_SCALE=0.75", "LOD_DIST_SCALE=1", "CAR_DIST_SCALE=0.75",
                       "UI_FPS_CAP=30", "ADAPT_LOD=0", "ANIM_LOD=1", "DLSS=0", "FSR=0", "FSR_FG=0", "reshade.on=0", "launcher.lowcpu=1", "AUDIO_AHEAD_BLOCKS=24", "AUDIO_STRETCH=1"),
            new Preset("PS3 Original", "720p · 30 FPS · original draw distance",
                       "RES_SCALE=1", "TEX_ANISO=-1", "TEX_MIPS=1", "SHADOWS=1", "LOD_CLASS_SCALE=1", "LOD_DIST_SCALE=1", "CAR_DIST_SCALE=1", "UI_FPS_CAP=30", "ADAPT_LOD=0"),
            new Preset("Performance", "720p · no shadows · original draw distance",
                       "RES_SCALE=1", "TEX_ANISO=4", "TEX_MIPS=1", "SHADOWS=0", "LOD_CLASS_SCALE=1", "LOD_DIST_SCALE=1", "CAR_DIST_SCALE=1", "ADAPT_LOD=0"),
            new Preset("Balanced", "1080p · 16× AF · 1.25× draw distance",
                       "RES_SCALE=1.5", "TEX_ANISO=16", "TEX_MIPS=1", "SHADOWS=1", "LOD_CLASS_SCALE=1.25", "LOD_DIST_SCALE=1", "ADAPT_LOD=1"),
            new Preset("High", "1440p · 16× AF · 1.5× draw distance",
                       "RES_SCALE=2", "TEX_ANISO=16", "TEX_MIPS=1", "SHADOWS=1", "LOD_CLASS_SCALE=1.5", "LOD_DIST_SCALE=1", "ADAPT_LOD=1"),
            new Preset("Ultra", "4K · 16× AF · 2.5× draw distance",
                       "RES_SCALE=3", "TEX_ANISO=16", "TEX_MIPS=1", "SHADOWS=1", "LOD_CLASS_SCALE=2.5", "LOD_DIST_SCALE=1.5", "ADAPT_LOD=1"),
        };

        // Low-end CPU mode: what it changes on top of installer\launch.txt. On four cores the shipped profile livelocks:
        // while the PPU waits on a workload, its lane threads stay hot (spin) and so does the PPU, and the kernel thread
        // that would start the next pass never gets a core (no world after 8 min on 4 E-cores). Here:
        //  - runtime ABX bit 0x04 ("lanes / kernels stay hot while the PPU waits") is cleared: 0x83F -> 0x83B. It can
        //    only be set through the A/B machinery, so that is given one fixed value (no cycling, phase 0 throughout);
        //  - the SPU kernel, lane pool and event-flag threads stop spin-waiting, hot-poll lanes and the always-hot
        //    audio kernel are off, no cores are reserved, light workloads run their lanes serially (V222T).
        // 4 E-cores, Potato graphics, uncapped: 48.9 FPS, 1 % low 32.6. All 24 cores, High: 90.5 vs 92.3 FPS shipped,
        // 1 % low 48.9 vs 41.8.
        public static readonly string[] LowCpuEnv =
        {
            "PS3_AB=1", "PS3_AB_SEC=3600", "PS3_AB_SEQ=0,0", "PS3_ABX_SEQ=0x83B,0x83B",
            "SPURS_LANE_SPIN_US=0", "SPURS_SPIN_US=0", "SPURS_EF_SPIN_US=0", "SPURS_HOT_WIDS=-", "SPURS_INST1_HOT=0", "SPURS_INST1_NAP=1",
            "PS3_MAIN_CORE=0", "PS3_FIFO_CORE=0", "PS3_AUDIO_CORE=0", "V222T=1",
        };

        // ReShade page looks (they also switch ReShade on, with this page's effects)
        static string[] Look(double sharpen, double clarity, double ao, double dehaze, double contrast, double vibrance, double saturation,
                             double exposure, double shadows, double highlights, double warmth, double vignette)
        {
            Func<double, string> n = x => Inv.Num(x);
            return new[] { "reshade.on=1", "reshade.source=launcher", "reshade.deband=1", "reshade.tint=0",
                           "reshade.sharpen=" + n(sharpen), "reshade.clarity=" + n(clarity), "reshade.ao=" + n(ao), "reshade.dehaze=" + n(dehaze),
                           "reshade.contrast=" + n(contrast), "reshade.vibrance=" + n(vibrance), "reshade.saturation=" + n(saturation),
                           "reshade.exposure=" + n(exposure), "reshade.shadows=" + n(shadows), "reshade.highlights=" + n(highlights),
                           "reshade.warmth=" + n(warmth), "reshade.vignette=" + n(vignette) };
        }
        public static readonly Preset[] Looks =
        {
            new Preset("Natural", "Crisper and cleaner, colours as the game made them",
                       Look(0.35, 0.15, 0.35, 0.15, 0.05, 0.1, 1, 0, 0, 0, 0, 0)),
            new Preset("Remastered", "Sharper detail, contact shadows, less haze, richer colour",
                       Look(0.4, 0.3, 0.5, 0.3, 0.15, 0.25, 1, 0, 0, -0.1, -0.1, 0)),
            new Preset("Vivid", "Punchy colour and contrast, strong detail",
                       Look(0.5, 0.45, 0.6, 0.45, 0.25, 0.5, 1.1, 0, 0.1, -0.15, 0, 0)),
            new Preset("Cinematic", "Moody film look: deep shadows, warm grade, vignette",
                       Look(0.3, 0.25, 0.6, 0.35, 0.3, 0.1, 0.85, -0.05, -0.15, -0.25, 0.15, 0.4)),
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Mods: folders in infamous-recomp\mods with a mod.ini ([mod] name/version/author/description, [env] KEY=VALUE).
    // The Skate mod (mods\skate) is source for a separate build and is recognised specially.
    // ---------------------------------------------------------------------------------------------------------
    sealed class ModInfo
    {
        public string Id, Dir, Name, Version, Author, Description, Readme, IniPath, Exe;
        public bool Experimental, Builtin, Available = true;
        public string Unavailable;
        public readonly List<KeyValuePair<string, string>> Env = new List<KeyValuePair<string, string>>();
        public readonly List<string> Ignored = new List<string>();
    }

    static class Mods
    {
        public const string Template =
            "; inFamous Launcher mod. Enable it on the launcher's Mods page; its [env] values are applied on top of\r\n" +
            "; your settings when the game starts. Any variable from installer\\launch.txt (or one the game reads) works.\r\n" +
            "; The in-game menu settings (resolution, vsync, frame rate limit, ...) can't be set by a mod.\r\n" +
            "[mod]\r\nname={0}\r\nversion=1.0\r\nauthor=\r\ndescription=Describe what this mod changes.\r\nexperimental=0\r\n\r\n[env]\r\n; LOD_CLASS_SCALE=2\r\n";

        public static List<ModInfo> Scan(string modsDir, string port)
        {
            var list = new List<ModInfo>();
            if (!Directory.Exists(modsDir)) return list;
            foreach (var dir in Directory.GetDirectories(modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileName(dir);
                string ini = Path.Combine(dir, "mod.ini");
                string readme = Path.Combine(dir, "README.md");
                if (File.Exists(ini)) list.Add(Parse(id, dir, ini, File.Exists(readme) ? readme : null));
                else if (id.Equals("skate", StringComparison.OrdinalIgnoreCase)) list.Add(Skate(dir, port, File.Exists(readme) ? readme : null));
            }
            return list;
        }

        static ModInfo Parse(string id, string dir, string ini, string readme)
        {
            var m = new ModInfo { Id = id, Dir = dir, IniPath = ini, Readme = readme, Name = id };
            string sec = "";
            foreach (var raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]")) { sec = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (sec == "mod")
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "name": if (v.Length > 0) m.Name = v; break;
                        case "version": m.Version = v; break;
                        case "author": m.Author = v; break;
                        case "description": m.Description = v; break;
                        case "experimental": m.Experimental = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                else if (sec == "env")
                {
                    if (Catalog.IsIni(k)) m.Ignored.Add(k);
                    else m.Env.Add(new KeyValuePair<string, string>(k, v));
                }
            }
            return m;
        }

        static ModInfo Skate(string dir, string port, string readme)
        {
            var m = new ModInfo
            {
                Id = "skate", Dir = dir, Readme = readme, Builtin = true, Experimental = true, Name = "Skate Mode",
                Author = "inFamous port (GPL-3.0, adapted from Skate 3 Rust Engine)",
                Description = "Ride a skateboard through Empire City: L3 + R3 gets on and off the board, with Skate 3 animations retargeted onto Cole."
            };
            // It needs its own build (CMake PORT_SKATE_MOD=ON). Look for one next to the installed build.
            string build = null;
            if (Directory.Exists(port))
                foreach (var b in Directory.GetDirectories(port, "build*"))
                {
                    string cache = Path.Combine(b, "CMakeCache.txt"), exe = Path.Combine(b, "inFamousRecomp.exe");
                    if (File.Exists(cache) && File.Exists(exe) && File.ReadAllText(cache).Contains("PORT_SKATE_MOD:BOOL=ON")) { build = b; m.Exe = exe; break; }
                }
            if (build == null)
            {
                m.Available = false;
                m.Unavailable = "Needs a Skate-enabled build of the game (CMake option PORT_SKATE_MOD=ON) and the Skate 3 Rust Engine source. The installed build was made without it. See the README for how it is built.";
                return m;
            }
            string pack = Path.Combine(build, "skate_actor_v3.pack");
            m.Env.Add(new KeyValuePair<string, string>("INFAMOUS_SKATE_MOD", "1"));
            if (File.Exists(pack)) m.Env.Add(new KeyValuePair<string, string>("INFAMOUS_SKATE_PACK", pack));
            else { m.Available = false; m.Unavailable = "The Skate build was found, but its actor pack (" + pack + ") is missing. play_skate.cmd builds it."; }
            return m;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The model the UI edits
    // ---------------------------------------------------------------------------------------------------------
    sealed class Model
    {
        public readonly GamePaths P;
        public LaunchSpec Spec;
        public string SpecError;
        public LauncherConfig Cfg;
        public List<ModInfo> ModList = new List<ModInfo>();
        public readonly Dictionary<string, string> Base = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Ini = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTime iniStamp;
        bool iniDirty;

        public Model(GamePaths p) { P = p; }

        public string Port { get { return Spec != null ? Spec.Cwd : P.DefaultPort; } }
        public string SettingsIni { get { return Path.Combine(Port, "settings.ini"); } }
        public string ModsDir { get { return Path.Combine(Port, "mods"); } }
        public string SaveDir { get { return Path.Combine(Port, "gamedata", "dev_hdd0", "home", "00000001", "savedata"); } }
        public string TrophyDir { get { return Path.Combine(Port, "gamedata", "trophies"); } }
        public string ShaderCache { get { return Path.Combine(Port, "shadercache"); } }
        public string StdoutLog { get { return Spec != null && Spec.Stdout != null ? LaunchSpec.Resolve(Spec.Cwd, Spec.Stdout) : Path.Combine(Port, "play.out.log"); } }
        public string StderrLog { get { return Spec != null && Spec.Stderr != null ? LaunchSpec.Resolve(Spec.Cwd, Spec.Stderr) : Path.Combine(Port, "play.err.log"); } }
        // launch.txt's argument is <dump>\PS3_GAME\USRDIR\EBOOT.elf
        public string Ps3GameDir { get { return Spec != null && Spec.Args.Count > 0 ? Path.GetDirectoryName(Path.GetDirectoryName(Spec.Args[0])) : null; } }
        public string Elf { get { return Spec != null && Spec.Args.Count > 0 ? Spec.Args[0] : null; } }

        public void Load()
        {
            Cfg = LauncherConfig.Load(P.LauncherIni);
            try
            {
                Spec = LaunchSpec.Load(P.LaunchTxt, P.Root);
                foreach (var kv in Spec.Env) Base[kv.Key] = kv.Value;
            }
            catch (FileNotFoundException) { SpecError = "installer\\launch.txt was not found. Run inFamous Setup to install the game first."; }
            catch (DirectoryNotFoundException) { SpecError = "installer\\launch.txt was not found. Run inFamous Setup to install the game first."; }
            catch (Exception e) { SpecError = "installer\\launch.txt could not be read: " + e.Message; }
            ReloadIni();
            ReloadMods();
        }

        public void ReloadMods() { ModList = Mods.Scan(ModsDir, Port); }

        public bool IniChangedOnDisk()
        {
            try { return File.Exists(SettingsIni) && File.GetLastWriteTimeUtc(SettingsIni) != iniStamp; }
            catch { return false; }
        }

        public void ReloadIni()
        {
            Ini.Clear();
            iniDirty = false;
            if (!File.Exists(SettingsIni)) { iniStamp = DateTime.MinValue; return; }
            foreach (var raw in File.ReadAllLines(SettingsIni))
            {
                if (raw.StartsWith(";") || raw.StartsWith("#")) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq), v = raw.Substring(eq + 1).Trim();
                if (Catalog.IsIni(k)) Ini[k] = v;
            }
            iniStamp = File.GetLastWriteTimeUtc(SettingsIni);
        }

        // ---- values ----
        public string Default(Def d)
        {
            if (d.Store == Store.Launcher) return d.Fallback;
            string b;
            return Base.TryGetValue(d.Key, out b) ? b : d.Fallback;
        }

        public string Get(Def d)
        {
            switch (d.Store)
            {
                case Store.Launcher:
                    if (d.Key == "launcher.priority") return Cfg.Priority;
                    if (d.Key == "launcher.after") return Cfg.AfterLaunch;
                    if (d.Key == "launcher.dlss5") return Cfg.Dlss5;
                    if (d.Key == "launcher.lowcpu") return Cfg.LowCpu;
                    if (d.Key.StartsWith("reshade.", StringComparison.OrdinalIgnoreCase))
                    {
                        string r;
                        return Cfg.ReShade.TryGetValue(d.Key.Substring(8), out r) ? r : d.Fallback;
                    }
                    return d.Fallback;
                case Store.Ini:
                    string v;
                    return Ini.TryGetValue(d.Key, out v) ? v : Default(d);
                default:
                    var mod = ModOwner(d.Key);
                    if (mod != null) return ModValue(mod, d.Key) ?? d.Fallback;
                    return EnvValue(d.Key) ?? d.Fallback;
            }
        }

        public string Get(string key)
        {
            var d = Catalog.Find(key);
            return d != null ? Get(d) : EnvValue(key);
        }

        public void Set(Def d, string v)
        {
            switch (d.Store)
            {
                case Store.Launcher:
                    if (d.Key == "launcher.priority") Cfg.Priority = v;
                    else if (d.Key == "launcher.after") Cfg.AfterLaunch = v;
                    else if (d.Key == "launcher.dlss5") Cfg.Dlss5 = v;
                    else if (d.Key == "launcher.lowcpu") Cfg.LowCpu = v;
                    else if (d.Key.StartsWith("reshade.", StringComparison.OrdinalIgnoreCase))
                    {
                        // Store only real changes, like the environment settings.
                        if (v == null || Inv.Same(v, d.Fallback)) Cfg.ReShade.Remove(d.Key.Substring(8));
                        else Cfg.ReShade[d.Key.Substring(8)] = v;
                    }
                    break;
                case Store.Ini:
                    Ini[d.Key] = v;
                    iniDirty = true;
                    break;
                default:
                    // Store only real changes: a value equal to the default removes the override.
                    if (Inv.Same(v, Default(d))) Cfg.Env.Remove(d.Key);
                    else SetEnvOverride(d.Key, v);
                    break;
            }
        }

        public void Set(string key, string v)
        {
            var d = Catalog.Find(key);
            if (d != null) Set(d, v); else SetEnv(key, v);
        }

        public bool Modified(Def d) { return !Inv.Same(Get(d), Default(d)); }
        public void Reset(Def d) { Set(d, Default(d)); }

        // ---- raw environment (Advanced page) ----
        public string EnvValue(string key)
        {
            string o;
            if (Cfg.Env.TryGetValue(key, out o)) return o == LauncherConfig.Unset ? null : o;
            string b;
            return Base.TryGetValue(key, out b) ? b : null;
        }
        public bool EnvChanged(string key) { return Cfg.Env.ContainsKey(key); }
        public void SetEnv(string key, string v)
        {
            string b;
            bool inBase = Base.TryGetValue(key, out b);
            if (v == null ? !inBase : (inBase && b == v)) Cfg.Env.Remove(key);
            else SetEnvOverride(key, v);
        }
        void SetEnvOverride(string key, string v)
        {
            Cfg.Env[key] = v ?? LauncherConfig.Unset;
            if (!Cfg.EnvOrder.Contains(key)) Cfg.EnvOrder.Add(key);
        }
        public void ResetEnv(string key) { Cfg.Env.Remove(key); }

        // All variable names the Advanced page lists: launch.txt order first, then the player's additions.
        public List<string> EnvKeys()
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Spec != null) foreach (var kv in Spec.Env) if (seen.Add(kv.Key)) keys.Add(kv.Key);
            foreach (var k in Cfg.EnvOrder) if (Cfg.Env.ContainsKey(k) && seen.Add(k)) keys.Add(k);
            foreach (var k in Cfg.Env.Keys) if (seen.Add(k)) keys.Add(k);
            return keys;
        }

        // ---- mods ----
        public bool ModEnabled(ModInfo m) { bool on; return m.Available && Cfg.Mods.TryGetValue(m.Id, out on) && on; }
        public IEnumerable<ModInfo> EnabledMods { get { return ModList.Where(ModEnabled); } }
        public ModInfo ModOwner(string key)
        {
            ModInfo owner = null;
            foreach (var m in EnabledMods)
                if (m.Env.Any(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) owner = m;   // later mods win
            return owner;
        }
        static string ModValue(ModInfo m, string key)
        {
            string v = null;
            foreach (var kv in m.Env) if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) v = kv.Value;
            return v == LauncherConfig.Unset ? null : v;
        }

        // ---- persistence ----
        public void Save()
        {
            Cfg.Save(P.LauncherIni);
            if (iniDirty) WriteSettingsIni();
        }

        public void WriteSettingsIni()
        {
            // Same bytes the in-game menu writes (rsx_ui_overlay.c menu_save, text-mode fopen: CRLF).
            var sb = new StringBuilder();
            sb.Append("; inFamous recomp settings (written by the in-game Settings menu, F10). Loaded at startup; overrides the launcher.\r\n");
            foreach (var k in Catalog.IniKeys) sb.Append(k).Append('=').Append(Get(Catalog.Find(k))).Append("\r\n");
            File.WriteAllText(SettingsIni, sb.ToString(), new UTF8Encoding(false));
            iniStamp = File.GetLastWriteTimeUtc(SettingsIni);
            iniDirty = false;
        }

        // ---- launching ----
        public string ResolveExe()
        {
            var skate = EnabledMods.FirstOrDefault(m => m.Exe != null);
            if (skate != null) return skate.Exe;
            if (!string.IsNullOrWhiteSpace(Cfg.Exe))
            {
                string e = Path.IsPathRooted(Cfg.Exe) ? Cfg.Exe : Path.Combine(P.Root, Cfg.Exe);
                if (File.Exists(e)) return e;
            }
            return Spec != null ? Spec.Exe : null;
        }

        public Dictionary<string, string> BuildEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string)e.Value;
            foreach (var kv in Spec.Env) env[kv.Key] = kv.Value;
            if (Cfg.LowCpu == "1")   // Low-end CPU mode; the player's own overrides below still win
                foreach (var kv in Catalog.LowCpuEnv) { int eq = kv.IndexOf('='); env[kv.Substring(0, eq)] = kv.Substring(eq + 1); }
            foreach (var kv in Cfg.Env)
                if (kv.Value == LauncherConfig.Unset) env.Remove(kv.Key); else env[kv.Key] = kv.Value;
            foreach (var m in EnabledMods)
                foreach (var kv in m.Env)
                    if (kv.Value == LauncherConfig.Unset) env.Remove(kv.Key); else env[kv.Key] = kv.Value;
            // settings.ini wins over the environment for these anyway; keep both in agreement.
            foreach (var k in Catalog.IniKeys) { var v = Get(Catalog.Find(k)); if (v != null) env[k] = v; }
            return env;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The launcher's port patches (launcher\dlss\apply-dlss.ps1): NVIDIA DLSS, PlayStation / Steam controllers and
    // the cheat mods hook into four port files. "Update inFamous" and a repair restore the port's own copies of those
    // files, which drops the hooks; Missing() tells which features are gone so the launcher can offer to re-apply.
    // ---------------------------------------------------------------------------------------------------------
    static class PortPatches
    {
        public static string Dir(Model m) { return Path.Combine(m.P.LauncherDir, "dlss"); }
        public static string ApplyCmd(Model m) { return Path.Combine(Dir(m), "apply-dlss.cmd"); }
        public static string UpdateCmd(Model m) { return Path.Combine(Dir(m), "update-and-patch.cmd"); }

        // "A", "A and B", "A, B and C" + " is" / " are"
        public static string Describe(List<string> gone)
        {
            string list = gone.Count == 1 ? gone[0] : string.Join(", ", gone.Take(gone.Count - 1)) + " and " + gone[gone.Count - 1];
            return list + (gone.Count == 1 ? " is" : " are");
        }

        // Feature names whose hooks are missing (empty when all are in place, or when the patch scripts aren't here).
        public static List<string> Missing(Model m)
        {
            var gone = new List<string>();
            if (!File.Exists(Path.Combine(Dir(m), "apply-dlss.ps1"))) return gone;
            string ps3 = Path.Combine(m.P.Root, "ps3recomp");
            Func<string, string, bool> has = (file, marker) =>
            {
                try { return File.Exists(file) && File.ReadAllText(file).Contains(marker); }
                catch { return true; }   // unreadable: don't nag
            };
            if (!has(Path.Combine(ps3, "CMakeLists.txt"), "_ps3recomp_dlss") || !has(Path.Combine(ps3, "libs", "video", "rsx_d3d12_backend.c"), "rsx_dlss_"))
                gone.Add("NVIDIA DLSS");
            if (!has(Path.Combine(ps3, "CMakeLists.txt"), "_ps3recomp_sdl3") || !has(Path.Combine(ps3, "libs", "input", "cellPad.c"), "pad_sdl3"))
                gone.Add("PlayStation / Steam controllers");
            if (!has(Path.Combine(m.Port, "stubs.cpp"), "infamous_cheats"))
                gone.Add("the cheat mods");
            return gone;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // DLSS 5 Neural Rendering through OptiScaler. The player extracts OptiScaler-DLSSNR (and NVIDIA's
    // nvngx_dlssnr.dll) into launcher\dlss5. On Play the launcher installs it beside the game exe with
    // OptiScaler.dll named dxgi.dll -- the exe imports dxgi.dll, so Windows loads that copy first -- and removes
    // the proxy again when the switch is off. OptiScaler then intercepts the port's own DLSS (DLAA) calls and runs
    // the model right after them, before the port puts the HUD back.
    // ---------------------------------------------------------------------------------------------------------
    static class Dlss5
    {
        public const string ModelDll = "nvngx_dlssnr.dll";
        public static string Source(Model m) { return Path.Combine(m.P.LauncherDir, "dlss5"); }
        static string DlssRuntime(Model m) { return Path.Combine(m.P.Root, "dlss-sdk", "lib", "Windows_x86_64", "rel"); }
        // Off while FSR frame generation is on: its swap chain proxy and OptiScaler's would both take over presenting.
        public static bool Wanted(Model m) { return m.Cfg.Dlss5 == "1" && Inv.Same(m.Get("DLSS"), "1") && !Inv.Same(m.Get("FSR_FG"), "1"); }

        public static bool IsOpti(string dll)
        {
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
                return string.Equals(vi.OriginalFilename, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(vi.ProductName, "OptiScaler", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // What gets installed (paths relative to launcher\dlss5): everything but the package's setup scripts,
        // its notes in the top folder and the extension-less marker file.
        static IEnumerable<string> Files(string src)
        {
            if (!Directory.Exists(src)) yield break;
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length + 1), ext = Path.GetExtension(rel).ToLowerInvariant();
                bool top = rel.IndexOf('\\') < 0;
                if (ext == ".bat" || ext == ".sh" || ext == "" || (top && (ext == ".txt" || ext == ".md" || ext == ".log"))) continue;
                yield return rel;
            }
        }

        // For --dry-run and the Advanced page: no side effects.
        public static string Status(Model m)
        {
            string exe = m.ResolveExe(), src = Source(m);
            if (exe == null) return "no game build";
            string dir = Path.GetDirectoryName(exe), proxy = Path.Combine(dir, "dxgi.dll");
            bool installed = File.Exists(proxy) && IsOpti(proxy);
            if (!Wanted(m)) return installed ? "off (OptiScaler is removed at the next Play)" : "off";
            if (!File.Exists(Path.Combine(src, "OptiScaler.dll"))) return "on, but OptiScaler is not in " + src;
            bool model = File.Exists(Path.Combine(src, ModelDll)) || File.Exists(Path.Combine(dir, ModelDll));
            return "on" + (model ? "" : ", but " + ModelDll + " is missing") + (installed ? ", installed" : ", installed at the next Play");
        }

        // Brings the exe folder in line with the switch. Returns null, or a problem to show; the game starts anyway.
        public static string Sync(Model m)
        {
            string exe = m.ResolveExe();
            if (exe == null) return null;
            string dir = Path.GetDirectoryName(exe), src = Source(m), proxy = Path.Combine(dir, "dxgi.dll");
            bool ours = File.Exists(proxy) && IsOpti(proxy);
            if (!Wanted(m))
            {
                if (ours) File.Delete(proxy);   // the launcher's copy; launcher\dlss5 keeps the original
                return null;
            }
            if (!File.Exists(Path.Combine(src, "OptiScaler.dll")))
                return "DLSS 5 Neural Rendering is on, but OptiScaler is not in " + src + ".\n\nExtract OptiScaler-DLSSNR (all of it) into that folder. The game starts without it for now.";
            if (File.Exists(proxy) && !ours)
                return proxy + " belongs to another program, so OptiScaler was not installed. The game starts without DLSS 5.";
            foreach (var rel in Files(src))
            {
                string dst = rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : Path.Combine(dir, rel);
                if (rel.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dst)) continue;   // keep what the overlay saved
                CopyIfChanged(Path.Combine(src, rel), dst);
            }
            // OptiScaler's own DLSS pass uses the same DLSS runtime as the port.
            string rt = DlssRuntime(m);
            if (File.Exists(Path.Combine(rt, "nvngx_dlss.dll")))
                CopyIfChanged(Path.Combine(rt, "nvngx_dlss.dll"), Path.Combine(dir, "OptiScaler", "nvngx_dlss.dll"));
            string ini = Path.Combine(dir, "OptiScaler.ini");
            if (File.Exists(ini))
            {
                SetIni(ini, "Upscalers", "Dx12Upscaler", "dlss");   // keep DLSS (the port's DLAA) as the upscaler
                SetIni(ini, "DlssNr", "Enabled", "true");
                SetIni(ini, "Libraries", "NvngxFeaturePath", rt);
            }
            if (!File.Exists(Path.Combine(dir, ModelDll)))
                return "OptiScaler is installed, but DLSS 5 Neural Rendering needs NVIDIA's " + ModelDll + " in " + src + ". The game starts without it.";
            return null;
        }

        public static void CopyIfChanged(string from, string to)
        {
            var a = new FileInfo(from);
            var b = new FileInfo(to);
            if (b.Exists && b.Length == a.Length && b.LastWriteTimeUtc == a.LastWriteTimeUtc) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
        }

        // Sets key=value in [section], keeping everything else in the file (comments, encoding, line endings).
        public static void SetIni(string path, string section, string key, string value)
        {
            byte[] raw = File.ReadAllBytes(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();
            string want = key + "=" + value, sec = null;
            int header = -1, at = -1;
            for (int i = 0; i < lines.Count && at < 0; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    sec = t.Substring(1, t.Length - 2).Trim();
                    if (sec.Equals(section, StringComparison.OrdinalIgnoreCase)) header = i;
                    continue;
                }
                int eq = t.IndexOf('=');
                if (header >= 0 && sec.Equals(section, StringComparison.OrdinalIgnoreCase) && eq > 0 && !t.StartsWith(";") &&
                    t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) at = i;
            }
            if (at >= 0) { if (lines[at] == want) return; lines[at] = want; }
            else if (header >= 0) lines.Insert(header + 1, want);
            else { lines.Add("[" + section + "]"); lines.Add(want); }
            File.WriteAllText(path, string.Join(nl, lines), new UTF8Encoding(bom));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Game process (CreateProcess with log files as stdout/stderr, like inFamous.exe, so the game keeps logging
    // after the launcher closes)
    // ---------------------------------------------------------------------------------------------------------
    sealed class RunningGame : IDisposable
    {
        public readonly int Pid;
        public readonly DateTime Started;
        public readonly bool Attached;
        IntPtr handle;

        RunningGame(int pid, IntPtr h, DateTime started, bool attached) { Pid = pid; handle = h; Started = started; Attached = attached; }

        public bool HasExited { get { return handle == IntPtr.Zero || Native.WaitForSingleObject(handle, 0) == 0; } }
        public int ExitCode { get { uint c; return handle != IntPtr.Zero && Native.GetExitCodeProcess(handle, out c) ? unchecked((int)c) : 0; } }
        public void Kill() { if (handle != IntPtr.Zero) Native.TerminateProcess(handle, 1); }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }

        public static RunningGame FindExisting(string exeName)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                IntPtr h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                DateTime t;
                try { t = p.StartTime; } catch { t = DateTime.Now; }
                return new RunningGame(p.Id, h, t, true);
            }
            return null;
        }

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            return sb.Append('"').ToString();
        }

        static IntPtr OpenInheritable(string path, bool write)
        {
            var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)), bInheritHandle = true };
            IntPtr h = Native.CreateFileW(path, write ? Native.GENERIC_WRITE : Native.GENERIC_READ, 3 /* share read|write */, ref sa,
                                          write ? 2u /* CREATE_ALWAYS */ : 3u /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
            return h == new IntPtr(-1) ? IntPtr.Zero : h;
        }

        public static RunningGame Start(string exe, IList<string> args, string cwd, IDictionary<string, string> env,
                                        string stdoutPath, string stderrPath, string priority)
        {
            var cmd = new StringBuilder(Quote(exe));
            foreach (var a in args) cmd.Append(' ').Append(Quote(a));

            var block = new StringBuilder();
            foreach (var k in env.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                block.Append(k).Append('=').Append(env[k]).Append('\0');
            block.Append('\0');

            IntPtr hin = OpenInheritable("NUL", false);
            IntPtr hout = stdoutPath != null ? OpenInheritable(stdoutPath, true) : IntPtr.Zero;
            IntPtr herr = stderrPath != null ? OpenInheritable(stderrPath, true) : IntPtr.Zero;
            if (hout == IntPtr.Zero) hout = OpenInheritable("NUL", true);
            if (herr == IntPtr.Zero) herr = OpenInheritable("NUL", true);
            IntPtr envp = Marshal.StringToHGlobalUni(block.ToString());
            try
            {
                var si = new Native.STARTUPINFO { cb = Marshal.SizeOf(typeof(Native.STARTUPINFO)), dwFlags = 0x100 /* STARTF_USESTDHANDLES */,
                                                  hStdInput = hin, hStdOutput = hout, hStdError = herr };
                uint flags = 0x400 /* CREATE_UNICODE_ENVIRONMENT */ | 0x08000000 /* CREATE_NO_WINDOW */;
                flags |= priority == "high" ? 0x80u : priority == "above" ? 0x8000u : 0x20u;
                Native.PROCESS_INFORMATION pi;
                if (!Native.CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, envp, cwd, ref si, out pi))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Native.CloseHandle(pi.hThread);
                return new RunningGame(pi.dwProcessId, pi.hProcess, DateTime.Now, false);
            }
            finally
            {
                Marshal.FreeHGlobal(envp);
                foreach (var h in new[] { hin, hout, herr }) if (h != IntPtr.Zero) Native.CloseHandle(h);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Machine facts shown in the UI
    // ---------------------------------------------------------------------------------------------------------
    static class SystemInfo
    {
        public static int Width, Height, Hz;
        public static string Gpu = "", Cpu = "";

        public static void Probe()
        {
            try
            {
                var dm = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE)) };
                if (Native.EnumDisplaySettingsW(null, -1, ref dm)) { Width = dm.dmPelsWidth; Height = dm.dmPelsHeight; Hz = dm.dmDisplayFrequency; }
            }
            catch { }
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE)) };
                    if (!Native.EnumDisplayDevicesW(null, (uint)i, ref dd, 0)) break;
                    if ((dd.StateFlags & 4) != 0) { Gpu = dd.DeviceString; break; }   // DISPLAY_DEVICE_PRIMARY_DEVICE
                }
            }
            catch { }
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null) Cpu = ((k.GetValue("ProcessorNameString") as string) ?? "").Trim();
            }
            catch { }
        }

        // The resolution option closest to the monitor's height (720p steps of the game's RES_SCALE).
        public static string MatchingScale()
        {
            if (Height <= 0) return null;
            double want = Height / 720.0;
            string best = "1"; double bd = double.MaxValue;
            foreach (var s in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
                if (Math.Abs(s - want) < bd) { bd = Math.Abs(s - want); best = Inv.Num(s); }
            return best;
        }
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const int SYNCHRONIZE = 0x00100000, PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                                 IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevicesW(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    }
}
