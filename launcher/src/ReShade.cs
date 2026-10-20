// inFamous Launcher: ReShade. At Play it installs ReShade (launcher\reshade\ReShade64.dll) beside the game exe, writes its
// ReShade.ini and the ReShade page's preset; plus the ReShade page itself.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace InfamousLauncher
{
    // Without DLSS 5, ReShade is loaded as dxgi.dll (the exe imports dxgi.dll, so Windows loads that copy first). While
    // DLSS 5 Neural Rendering is on, OptiScaler owns dxgi.dll, so ReShade goes in as ReShade64.dll and OptiScaler loads it
    // ([Plugins] LoadReshade=true). The effects (inFamous.fx and any shaders the player adds) stay in launcher\reshade\shaders.
    static class ReShadeSetup
    {
        public const string Dll = "ReShade64.dll";
        const string Fx = "inFamous.fx";
        const string PresetFile = "ReShade-inFamous-Launcher.ini";
        // inFamous.fx techniques in the order they run; the debug ones are listed so the overlay sorts them sensibly
        static readonly string[] Order = { "CompareBegin", "Deband", "AO", "AOView", "Clarity", "Color", "Sharpen", "Vignette", "DepthView", "CompareEnd" };

        public static string Source(Model m) { return Path.Combine(m.P.LauncherDir, "reshade"); }
        public static string Shaders(Model m) { return Path.Combine(Source(m), "shaders"); }
        public static string SourceDll(Model m) { return Path.Combine(Source(m), Dll); }
        public static bool Wanted(Model m) { return Inv.Same(m.Get("reshade.on"), "1"); }
        public static bool OwnPreset(Model m) { return Inv.Same(m.Get("reshade.source"), "own"); }
        public static bool ThroughOpti(Model m) { return Dlss5.Wanted(m) && File.Exists(Path.Combine(Dlss5.Source(m), "OptiScaler.dll")); }

        public static bool IsReShade(string dll)
        {
            try { return File.Exists(dll) && string.Equals(FileVersionInfo.GetVersionInfo(dll).ProductName, "ReShade", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        public static string Version(string dll)
        {
            if (!IsReShade(dll)) return null;
            var vi = FileVersionInfo.GetVersionInfo(dll);
            return vi.FileMajorPart + "." + vi.FileMinorPart + "." + vi.FileBuildPart;
        }

        public static string LookName(Model m)
        {
            foreach (var p in Catalog.Looks)
                if (p.Values.All(kv => { int eq = kv.IndexOf('='); return Inv.Same(m.Get(kv.Substring(0, eq)), kv.Substring(eq + 1)); })) return p.Name;
            return "Custom";
        }

        // For --dry-run: no side effects.
        public static string Status(Model m)
        {
            string exe = m.ResolveExe();
            if (exe == null) return "no game build";
            string dir = Path.GetDirectoryName(exe);
            bool asProxy = IsReShade(Path.Combine(dir, "dxgi.dll")), asSide = IsReShade(Path.Combine(dir, Dll));
            if (!Wanted(m)) return asProxy || asSide ? "off (ReShade is removed at the next Play)" : "off";
            string v = Version(SourceDll(m));
            if (v == null) return "on, but " + SourceDll(m) + " is missing";
            bool opti = ThroughOpti(m);
            return "on, ReShade " + v + (opti ? " loaded by OptiScaler" : " as dxgi.dll") + ", " + (OwnPreset(m) ? "own preset" : LookName(m) + " look") +
                   ((opti ? asSide : asProxy) ? ", installed" : ", installed at the next Play");
        }

        // Before Dlss5.Sync: frees dxgi.dll for OptiScaler, or takes ReShade out when it is off.
        public static void Prepare(Model m)
        {
            string exe = m.ResolveExe();
            if (exe == null) return;
            string proxy = Path.Combine(Path.GetDirectoryName(exe), "dxgi.dll");
            if (IsReShade(proxy) && (!Wanted(m) || ThroughOpti(m))) File.Delete(proxy);
        }

        // After Dlss5.Sync: brings the exe folder in line with the page. Returns null, or a problem to show; the game
        // starts anyway.
        public static string Sync(Model m)
        {
            string exe = m.ResolveExe();
            if (exe == null) return null;
            string dir = Path.GetDirectoryName(exe), proxy = Path.Combine(dir, "dxgi.dll"), side = Path.Combine(dir, Dll);
            string optiIni = Path.Combine(dir, "OptiScaler.ini");
            if (!Wanted(m))
            {
                if (IsReShade(side)) File.Delete(side);   // only the launcher's copies; launcher\reshade keeps the original
                if (File.Exists(optiIni)) Dlss5.SetIni(optiIni, "Plugins", "LoadReshade", "false");
                return null;
            }
            if (!IsReShade(SourceDll(m)))
                return "ReShade is on, but " + SourceDll(m) + " is missing.\n\nAdd it on the launcher's ReShade page (Add ReShade…). The game starts without ReShade for now.";
            if (File.Exists(proxy) && Dlss5.IsOpti(proxy))
            {
                Dlss5.CopyIfChanged(SourceDll(m), side);
                if (File.Exists(optiIni)) Dlss5.SetIni(optiIni, "Plugins", "LoadReshade", "true");
            }
            else if (File.Exists(proxy) && !IsReShade(proxy))
                return proxy + " belongs to another program, so ReShade was not installed. The game starts without it.";
            else
            {
                Dlss5.CopyIfChanged(SourceDll(m), proxy);
                if (IsReShade(side)) File.Delete(side);
            }
            WriteIni(m, dir);
            if (!OwnPreset(m)) WritePreset(m, Path.Combine(dir, PresetFile));
            return null;
        }

        // ReShade.ini beside the exe. Only what the game needs is forced; anything else the player sets in the overlay stays.
        static void WriteIni(Model m, string dir)
        {
            string ini = Path.Combine(dir, "ReShade.ini");
            if (!File.Exists(ini)) File.WriteAllText(ini, "", new UTF8Encoding(false));
            Dlss5.SetIni(ini, "GENERAL", "EffectSearchPaths", WithPath(GetIni(ini, "GENERAL", "EffectSearchPaths"), Shaders(m) + "\\**"));
            Dlss5.SetIni(ini, "GENERAL", "TextureSearchPaths", WithPath(GetIni(ini, "GENERAL", "TextureSearchPaths"), Path.Combine(Source(m), "textures") + "\\**"));
            string preset = Path.Combine(dir, PresetFile), cur = GetIni(ini, "GENERAL", "PresetPath");
            if (!OwnPreset(m)) Dlss5.SetIni(ini, "GENERAL", "PresetPath", preset);
            else if (string.IsNullOrEmpty(cur) || SamePath(cur, preset, dir)) Dlss5.SetIni(ini, "GENERAL", "PresetPath", Path.Combine(dir, "ReShadePreset.ini"));
            // The game's depth is 24-bit and not reversed (for shaders built on ReShade.fxh).
            if (GetIni(ini, "GENERAL", "PreprocessorDefinitions") == null)
                Dlss5.SetIni(ini, "GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_REVERSED=0,RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0");
            // The HUD clears the depth buffer, so ReShade has to copy it before that.
            Dlss5.SetIni(ini, "DEPTH", "DepthCopyBeforeClears", "1");
            if (GetIni(ini, "DEPTH", "UseAspectRatioHeuristics") == null) Dlss5.SetIni(ini, "DEPTH", "UseAspectRatioHeuristics", "1");
            if (GetIni(ini, "INPUT", "KeyOverlay") == null) Dlss5.SetIni(ini, "INPUT", "KeyOverlay", "36,0,0,0");   // Home
            if (GetIni(ini, "INPUT", "KeyEffects") == null) Dlss5.SetIni(ini, "INPUT", "KeyEffects", "35,0,0,0");   // End
            if (GetIni(ini, "OVERLAY", "TutorialProgress") == null) Dlss5.SetIni(ini, "OVERLAY", "TutorialProgress", "4");
        }

        // The page's look as a ReShade preset (rewritten at every Play; "My own ReShade preset" leaves ReShade alone).
        static void WritePreset(Model m, string path)
        {
            Func<string, double> v = k => { double x; return Inv.TryNum(m.Get("reshade." + k), out x) ? x : 0; };
            Func<double, bool> nz = x => Math.Abs(x) > 1e-6;
            var on = new List<string>();
            if (Inv.Same(m.Get("reshade.deband"), "1")) on.Add("Deband");
            if (nz(v("ao"))) on.Add("AO");
            if (nz(v("clarity"))) on.Add("Clarity");
            if (new[] { "dehaze", "contrast", "vibrance", "exposure", "shadows", "highlights", "warmth", "tint" }.Any(k => nz(v(k))) || nz(v("saturation") - 1)) on.Add("Color");
            if (nz(v("sharpen"))) on.Add("Sharpen");
            if (nz(v("vignette"))) on.Add("Vignette");
            Func<string, string> t = n => "inFamous_" + n + "@" + Fx;
            var sb = new StringBuilder();
            sb.Append("Techniques=").Append(string.Join(",", Order.Where(on.Contains).Select(t))).Append("\r\n");
            sb.Append("TechniqueSorting=").Append(string.Join(",", Order.Select(t))).Append("\r\n\r\n");
            sb.Append('[').Append(Fx).Append("]\r\n");
            var u = new SortedDictionary<string, double>(StringComparer.Ordinal)
            {
                { "AORadius", 1 }, { "AOStrength", v("ao") }, { "Clarity", v("clarity") }, { "Contrast", v("contrast") },
                { "DebandStrength", 0.5 }, { "Dehaze", v("dehaze") }, { "Exposure", v("exposure") }, { "Highlights", v("highlights") },
                { "Saturation", v("saturation") }, { "Shadows", v("shadows") }, { "Sharpen", v("sharpen") }, { "Tint", v("tint") },
                { "Vibrance", v("vibrance") }, { "Vignette", v("vignette") }, { "Warmth", v("warmth") },
            };
            foreach (var kv in u) sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("0.000000", Inv.C)).Append("\r\n");
            string text = sb.ToString();
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        static string WithPath(string list, string path)   // ReShade lists are comma-separated; ours goes first
        {
            if (string.IsNullOrWhiteSpace(list)) return path;
            if (list.Split(',').Any(p => p.Trim().Equals(path, StringComparison.OrdinalIgnoreCase))) return list;
            return path + "," + list;
        }
        static bool SamePath(string a, string b, string dir)
        {
            try { return string.Equals(Path.GetFullPath(Path.Combine(dir, a)), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        static string GetIni(string path, string section, string key)
        {
            string sec = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                string t = raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { sec = t.Substring(1, t.Length - 2).Trim(); continue; }
                int eq = t.IndexOf('=');
                if (sec != null && eq > 0 && !t.StartsWith(";") && sec.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                    t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return t.Substring(eq + 1).Trim();
            }
            return null;
        }

        // The page's "Add ReShade…": a ReShade_Setup_*.exe (ReShade64.dll is read out of the archive inside it; the setup
        // itself never runs) or a ReShade64.dll. Returns the version.
        public static string InstallFrom(string file, Model m)
        {
            byte[] dll = Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(file) : ZipEntry(file, Dll);
            Directory.CreateDirectory(Source(m));
            string dst = SourceDll(m), tmp = dst + ".new";
            File.WriteAllBytes(tmp, dll);
            var vi = FileVersionInfo.GetVersionInfo(tmp);
            string why = null;
            if (!string.Equals(vi.ProductName, "ReShade", StringComparison.OrdinalIgnoreCase)) why = Path.GetFileName(file) + " is not ReShade.";
            else if (!string.Equals(vi.OriginalFilename, Dll, StringComparison.OrdinalIgnoreCase)) why = "That is the 32-bit ReShade. The game needs ReShade64.dll, which is inside ReShade_Setup_*.exe.";
            if (why != null) { File.Delete(tmp); throw new InvalidDataException(why); }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
            return Version(dst);
        }

        // One file out of a zip archive appended to a program (the ReShade setup). .NET's ZipArchive can't open it: the
        // archive's offsets don't count the program in front of it.
        static byte[] ZipEntry(string path, string name)
        {
            byte[] f = File.ReadAllBytes(path);
            Func<long, int> u16 = i => BitConverter.ToUInt16(f, (int)i);
            Func<long, long> u32 = i => BitConverter.ToUInt32(f, (int)i);
            long eocd = -1;
            for (long i = f.Length - 22; i >= Math.Max(0, f.Length - 22 - 65535); i--)
                if (u32(i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException(Path.GetFileName(path) + " is not a ReShade setup (there is no archive in it).");
            long count = u16(eocd + 10), cdSize = u32(eocd + 12), cdOff = u32(eocd + 16);
            long shift = eocd - cdSize - cdOff, p = cdOff + shift;
            for (long n = 0; n < count; n++)
            {
                if (p < 0 || p + 46 > f.Length || u32(p) != 0x02014b50) break;
                int method = u16(p + 10), nl = u16(p + 28), xl = u16(p + 30), cl = u16(p + 32);
                long csize = u32(p + 20), usize = u32(p + 24), local = u32(p + 42) + shift;
                if (Encoding.UTF8.GetString(f, (int)p + 46, nl).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (u32(local) != 0x04034b50) break;
                    long data = local + 30 + u16(local + 26) + u16(local + 28);
                    if (method == 0) { var b = new byte[csize]; Array.Copy(f, data, b, 0, csize); return b; }
                    if (method != 8) throw new InvalidDataException(name + " is packed in a way the launcher can't read (method " + method + ").");
                    using (var ms = new MemoryStream(f, (int)data, (int)csize))
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var o = new MemoryStream())
                    {
                        ds.CopyTo(o);
                        if (o.Length != usize) throw new InvalidDataException(name + " is damaged in " + Path.GetFileName(path) + ".");
                        return o.ToArray();
                    }
                }
                p += 46 + nl + xl + cl;
            }
            throw new InvalidDataException(name + " was not found in " + Path.GetFileName(path) + ". Pick ReShade_Setup_<version>.exe from reshade.me.");
        }
    }

    // DLSS 5 (OptiScaler) and ReShade share the dxgi.dll slot beside the exe, so they are set up together, in this order.
    static class Proxies
    {
        public static string Sync(Model m)
        {
            var msgs = new List<string>();
            try { ReShadeSetup.Prepare(m); }
            catch (Exception e) { msgs.Add("ReShade could not be taken out: " + e.Message); }
            try { string r = Dlss5.Sync(m); if (r != null) msgs.Add(r); }
            catch (Exception e) { msgs.Add("DLSS 5 Neural Rendering could not be set up: " + e.Message + "\n\nThe game starts without it."); }
            try { string r = ReShadeSetup.Sync(m); if (r != null) msgs.Add(r); }
            catch (Exception e) { msgs.Add("ReShade could not be set up: " + e.Message + "\n\nThe game starts without it."); }
            return msgs.Count > 0 ? string.Join("\n\n", msgs) : null;
        }
    }

    sealed partial class MainWindow
    {
        UIElement ReShadePage()
        {
            // status, the switch, the looks, then the effects
            var s = VStack(Header("ReShade", "Post-processing on top of the finished picture, with effects written for inFamous: sharper texture detail, " +
                                  "soft contact shadows from the game's depth, less haze and richer colour. Pick a look, then fine-tune it below."),
                           ReShadeStatus());
            foreach (var grp in Catalog.All.Where(d => d.Page == "reshade").Select(d => d.Group).Distinct().ToList())
            {
                if (grp == "Detail")
                    s.Children.Add(PresetSection(Catalog.Looks, "Looks", "Each look switches ReShade on with this page's effects. Fine-tune anything below afterwards."));
                s.Children.Add(Section(grp, null, Catalog.All.Where(d => d.Page == "reshade" && d.Group == grp).Select(Row).ToList()));
            }
            return s;
        }

        UIElement ReShadeStatus()
        {
            string v = ReShadeSetup.Version(ReShadeSetup.SourceDll(M));
            var g = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string tone = v != null ? "Accent" : "Warn";
            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = B(tone + "Soft"), Child = Glyph("", 18, tone),
                                    VerticalAlignment = VerticalAlignment.Top };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(icon);
            string how;
            if (v == null) how = "Download the ReShade setup from reshade.me, then press Add ReShade and pick it. Only ReShade64.dll is taken out of it; the setup itself doesn't run.";
            else if (ReShadeSetup.ThroughOpti(M)) how = "DLSS 5 Neural Rendering is on, so OptiScaler loads ReShade. Installed beside the game when you press Play.";
            else how = "Installed beside the game (as dxgi.dll) when you press Play, and taken out again when ReShade is off.";
            var text = VStack(T(v != null ? "ReShade " + v : "ReShade is not added yet", 14, "Text", FontWeights.SemiBold), T(how, 12, "Dim"),
                              v != null ? T("In game: Home opens ReShade's overlay · End switches the effects off and on, to compare.", 12, "Dim") : null);
            text.Margin = new Thickness(14, 0, 14, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var buttons = VStack(Btn("Add ReShade…", AddReShade, v == null ? "Primary" : "Btn", "", "Pick ReShade_Setup_<version>.exe (or ReShade64.dll)"),
                                 Btn("reshade.me", () => Open("https://reshade.me"), "Btn", "", "ReShade's website, to download the setup"),
                                 Btn("Shaders folder", () => OpenFolder(ReShadeSetup.Shaders(M)), "Btn", "", "Shader packs put here show up in ReShade's overlay"));
            buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons, 2);
            g.Children.Add(buttons);
            return Card(g);
        }

        void AddReShade()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Pick the ReShade setup (or ReShade64.dll)", Filter = "ReShade setup or DLL|ReShade_Setup*.exe;ReShade64.dll|All files|*.*" };
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(dl)) dlg.InitialDirectory = dl;
            if (dlg.ShowDialog(this) != true) return;
            try { lastMessage = "ReShade " + ReShadeSetup.InstallFrom(dlg.FileName, M) + " added."; }
            catch (Exception e)
            {
                MessageBox.Show("ReShade could not be added.\n\n" + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RebuildPage();
        }
    }
}
