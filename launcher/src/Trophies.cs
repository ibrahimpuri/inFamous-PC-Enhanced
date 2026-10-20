// The Trophies page: every trophy in the game's trophy pack (PS3_GAME\TROPDIR\<id>\TROPHY.TRP in the player's dump)
// with which ones the port has recorded as unlocked (infamous-recomp\gamedata\trophies\<id>.json, written by
// ps3recomp's sceNpTrophy as { "trophies": [ { "id": N, "timestamp": unix seconds }, ... ] }).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace InfamousLauncher
{
    sealed class Trophy
    {
        public int Id;
        public string Name, Detail;
        public char Grade;           // P, G, S, B
        public bool Hidden;
        public long UnlockedAt;      // unix seconds, 0 = locked
        public bool Counted;         // the platinum: counted by the launcher (the port doesn't award it)
        public bool Unlocked { get { return UnlockedAt > 0; } }
    }

    sealed class TrophyList
    {
        public string Title, CommId;
        public List<Trophy> All = new List<Trophy>();
        public string Problem;       // why the trophy pack couldn't be read (the list then holds only recorded unlocks)
        public int Count(Func<Trophy, bool> f) { return All.Count(f); }
    }

    static class Trophies
    {
        // TRP container: big-endian header (magic DCA24D00, version, file size, entry count @0x10, entry size @0x14),
        // entries from 0x40: name[32], offset u64, size u64.
        static string trpPath; static DateTime trpStamp; static Dictionary<string, byte[]> trpFiles;
        static readonly Dictionary<string, BitmapSource> icons = new Dictionary<string, BitmapSource>();

        static Dictionary<string, byte[]> ReadTrp(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (path == trpPath && stamp == trpStamp && trpFiles != null) return trpFiles;
            byte[] d = File.ReadAllBytes(path);
            Func<int, ulong> be = (o) => { ulong v = 0; for (int i = 0; i < 8; i++) v = v << 8 | d[o + i]; return v; };
            Func<int, uint> be32 = (o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
            if (d.Length < 0x40 || be32(0) != 0xDCA24D00u) throw new InvalidDataException("not a trophy pack");
            uint count = be32(0x10), esz = be32(0x14);
            if (count > 512 || esz < 48) throw new InvalidDataException("unexpected trophy pack header");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++)
            {
                int e = (int)(0x40 + i * esz);
                if (e + 48 > d.Length) break;
                int n = 0; while (n < 32 && d[e + n] != 0) n++;
                string name = Encoding.ASCII.GetString(d, e, n);
                ulong off = be(e + 32), size = be(e + 40);
                if (off + size > (ulong)d.Length) continue;
                var b = new byte[size];
                Buffer.BlockCopy(d, (int)off, b, 0, (int)size);
                files[name] = b;
            }
            trpPath = path; trpStamp = stamp; trpFiles = files; icons.Clear();
            return files;
        }

        // The trophy pack whose folder matches a recorded set (NPWR00660 -> NPWR00660_00), else the first one.
        static string FindTrp(Model m, string commId)
        {
            string dir = m.Ps3GameDir != null ? Path.Combine(m.Ps3GameDir, "TROPDIR") : null;
            if (dir == null || !Directory.Exists(dir)) return null;
            string first = null;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                string f = Path.Combine(sub, "TROPHY.TRP");
                if (!File.Exists(f)) continue;
                if (commId != null && Path.GetFileName(sub).StartsWith(commId, StringComparison.OrdinalIgnoreCase)) return f;
                if (first == null) first = f;
            }
            return first;
        }

        // PS3_LANG (the launcher's Language) -> the CELL_SYSUTIL_LANG_ number the trophy texts are numbered by (TROP_xx.SFM)
        static readonly Dictionary<string, int> LangNumber = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "japanese", 0 }, { "english", 1 }, { "english_us", 1 }, { "french", 2 }, { "spanish", 3 }, { "german", 4 },
            { "italian", 5 }, { "dutch", 6 }, { "portuguese", 7 }, { "portuguese_pt", 7 }, { "russian", 8 }, { "korean", 9 },
            { "chinese_t", 10 }, { "chinese_s", 11 }, { "finnish", 12 }, { "swedish", 13 }, { "danish", 14 }, { "norwegian", 15 },
            { "polish", 16 }, { "portuguese_br", 17 }, { "english_gb", 18 }, { "british", 18 }, { "turkish", 19 }
        };
        static int Language(Model m)
        {
            string v = m.Get(Catalog.Find("PS3_LANG"));
            int n;
            if (string.IsNullOrEmpty(v)) return 1;
            if (int.TryParse(v, out n)) return n;
            return LangNumber.TryGetValue(v, out n) ? n : 1;
        }

        // The recorded unlocks: commId (file name) -> trophy id -> unix seconds
        static Dictionary<string, Dictionary<int, long>> ReadUnlocks(Model m)
        {
            var all = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(m.TrophyDir)) return all;
            foreach (var f in Directory.GetFiles(m.TrophyDir, "*.json"))
            {
                var set = new Dictionary<int, long>();
                foreach (Match x in Regex.Matches(File.ReadAllText(f), "\"id\"\\s*:\\s*(\\d+)\\s*,\\s*\"timestamp\"\\s*:\\s*(\\d+)"))
                    set[int.Parse(x.Groups[1].Value)] = long.Parse(x.Groups[2].Value);
                all[Path.GetFileNameWithoutExtension(f)] = set;
            }
            return all;
        }

        // Newest write time of the recorded unlocks (the page reloads when the game recorded a new one)
        public static DateTime Stamp(Model m)
        {
            try { return Directory.Exists(m.TrophyDir) ? Directory.GetFiles(m.TrophyDir, "*.json").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        public static TrophyList Load(Model m)
        {
            var list = new TrophyList();
            Dictionary<string, Dictionary<int, long>> unlocks;
            try { unlocks = ReadUnlocks(m); } catch (Exception e) { unlocks = new Dictionary<string, Dictionary<int, long>>(); list.Problem = "Couldn't read the unlocked trophies: " + e.Message; }
            list.CommId = unlocks.Keys.FirstOrDefault();
            try
            {
                string trp = FindTrp(m, list.CommId);
                if (trp == null) throw new FileNotFoundException("no PS3_GAME\\TROPDIR\\…\\TROPHY.TRP in your game dump");
                var files = ReadTrp(trp);
                byte[] sfm;
                if (!files.TryGetValue("TROP_" + Language(m).ToString("00") + ".SFM", out sfm) && !files.TryGetValue("TROP.SFM", out sfm))
                    throw new InvalidDataException("the trophy pack has no trophy texts");
                var x = new XmlDocument();
                x.LoadXml(Encoding.UTF8.GetString(sfm));
                var root = x.DocumentElement;
                list.Title = Inner(root, "title-name");
                string npc = Inner(root, "npcommid");
                if (npc != null && npc.Length > 3 && npc[npc.Length - 3] == '_') npc = npc.Substring(0, npc.Length - 3);   // NPWR00660_00 -> NPWR00660
                if (npc != null && unlocks.ContainsKey(npc)) list.CommId = npc;
                else if (list.CommId == null) list.CommId = npc;
                foreach (XmlElement t in root.GetElementsByTagName("trophy"))
                {
                    int id;
                    if (!int.TryParse(t.GetAttribute("id"), out id)) continue;
                    string g = t.GetAttribute("ttype");
                    list.All.Add(new Trophy { Id = id, Name = Inner(t, "name") ?? "Trophy " + id, Detail = Inner(t, "detail") ?? "",
                                              Grade = g.Length > 0 ? char.ToUpperInvariant(g[0]) : 'B', Hidden = t.GetAttribute("hidden") == "yes" });
                }
            }
            catch (Exception e)
            {
                list.Problem = "The trophy names come from your game dump, and it couldn't be read (" + e.Message + "). Only the recorded unlocks are shown.";
                list.All.Clear();
            }
            Dictionary<int, long> mine;
            if (list.CommId == null || !unlocks.TryGetValue(list.CommId, out mine)) mine = new Dictionary<int, long>();
            if (list.All.Count == 0)
                foreach (var kv in mine.OrderBy(k => k.Key)) list.All.Add(new Trophy { Id = kv.Key, Name = "Trophy " + kv.Key, Detail = "", Grade = '?' });
            foreach (var t in list.All) { long ts; if (mine.TryGetValue(t.Id, out ts)) t.UnlockedAt = Math.Max(ts, 1); }
            // The platinum is earned with every other trophy; the port doesn't award it, so it's counted here.
            var plat = list.All.FirstOrDefault(t => t.Grade == 'P');
            var rest = list.All.Where(t => t.Grade != 'P').ToList();
            if (plat != null && !plat.Unlocked && rest.Count > 0 && rest.All(t => t.Unlocked)) { plat.UnlockedAt = rest.Max(t => t.UnlockedAt); plat.Counted = true; }
            return list;
        }

        static string Inner(XmlElement e, string tag)
        {
            var n = e.GetElementsByTagName(tag);
            return n.Count > 0 ? n[0].InnerText.Trim() : null;
        }

        // TROPnnn.PNG (or ICON0.PNG), decoded small; locked trophies in grey
        public static BitmapSource Icon(string file, int width, bool grey)
        {
            string key = file + "|" + width + "|" + grey;
            BitmapSource b;
            if (icons.TryGetValue(key, out b)) return b;
            byte[] png;
            if (trpFiles == null || !trpFiles.TryGetValue(file, out png)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(png);
                bi.DecodePixelWidth = width;
                bi.EndInit();
                bi.Freeze();
                b = bi;
                if (grey) { var g = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0); g.Freeze(); b = g; }
            }
            catch { b = null; }
            icons[key] = b;
            return b;
        }

        public static string When(long unix)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime().ToString("d MMM yyyy, HH:mm", Inv.C);
        }
    }

    sealed partial class MainWindow
    {
        string trophyFilter = "all";     // all, unlocked, locked
        bool trophyReveal;               // show the hidden trophies' names
        DateTime trophyStamp;

        static readonly Dictionary<char, string> GradeColour = new Dictionary<char, string> { { 'P', "#B8D4EC" }, { 'G', "#F2C14E" }, { 'S', "#C3CAD4" }, { 'B', "#D58C58" } };
        static readonly Dictionary<char, string> GradeName = new Dictionary<char, string> { { 'P', "Platinum" }, { 'G', "Gold" }, { 'S', "Silver" }, { 'B', "Bronze" } };

        static Border GradeChip(char g, bool on)
        {
            string c; if (!GradeColour.TryGetValue(g, out c)) c = "#8E9AB1";
            string n; if (!GradeName.TryGetValue(g, out n)) n = "Trophy";
            var col = (Color)ColorConverter.ConvertFromString(c);
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(on ? (byte)0x2E : (byte)0x18, col.R, col.G, col.B)), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = n.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(on ? col : Color.FromArgb(0xA0, col.R, col.G, col.B)) }
            };
        }

        UIElement TrophiesPage()
        {
            trophyStamp = Trophies.Stamp(M);
            var list = Trophies.Load(M);
            var s = VStack(Header("Trophies", "Every trophy in inFamous, and which ones you've unlocked. The game unlocks them as you play; this page reads the port's record of them and updates when you come back to it."));
            if (list.Problem != null) s.Children.Add(Note("", list.Problem, "Warn", "WarnSoft", new Thickness(0, 0, 0, 16)));
            s.Children.Add(TrophySummary(list));
            if (list.All.Count == 0) { s.Children.Add(Card(Padded(T("No trophies yet. They show up here as you unlock them in game.", 13, "Dim")))); return s; }

            int on = list.Count(t => t.Unlocked), off = list.All.Count - on;
            var bar = HStack(FilterBtn("all", "All " + list.All.Count), FilterBtn("unlocked", "Unlocked " + on), FilterBtn("locked", "Locked " + off));
            int hiddenLocked = list.Count(t => t.Hidden && !t.Unlocked);
            if (hiddenLocked > 0)
            {
                var sw = new CheckBox { Style = S("Switch"), IsChecked = trophyReveal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 8) };
                sw.Checked += (o, e) => { trophyReveal = true; RebuildPage(); };
                sw.Unchecked += (o, e) => { trophyReveal = false; RebuildPage(); };
                var lbl = T("Show the " + hiddenLocked + " hidden " + (hiddenLocked == 1 ? "trophy" : "trophies") + " (spoilers)", 13, "Dim");
                lbl.VerticalAlignment = VerticalAlignment.Center; lbl.Margin = new Thickness(0, 0, 0, 8);
                bar.Children.Add(sw); bar.Children.Add(lbl);
            }
            bar.Margin = new Thickness(0, 0, 0, 8);
            s.Children.Add(bar);

            var shown = list.All.Where(t => trophyFilter == "all" || (trophyFilter == "unlocked") == t.Unlocked).ToList();
            if (shown.Count == 0) s.Children.Add(Card(Padded(T(trophyFilter == "unlocked" ? "None unlocked yet." : "Every trophy is unlocked. Well done.", 13, "Dim"))));
            else s.Children.Add(Section(null, null, shown.Select(TrophyRow).ToList()));
            return s;
        }

        Button FilterBtn(string key, string text)
        {
            var b = Btn(text, () => { trophyFilter = key; RebuildPage(); }, trophyFilter == key ? "Primary" : "Btn");
            return b;
        }

        UIElement TrophySummary(TrophyList list)
        {
            int total = list.All.Count, on = list.Count(t => t.Unlocked);
            var g = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var art = Trophies.Icon("ICON0.PNG", 320, false);
            if (art != null)
            {
                var img = new Image { Source = art, Width = 160, Height = 88, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top,
                                      Clip = new RectangleGeometry(new Rect(0, 0, 160, 88), 8, 8) };
                g.Children.Add(img);
            }
            int pct = total > 0 ? (int)Math.Round(on * 100.0 / total) : 0;
            var head = HStack(T(on + " of " + total + " unlocked", 22, "Text", FontWeights.SemiBold), Chip(pct + " %", "AccentHi", "AccentSoft"));
            var track = new Grid { Height = 8, Margin = new Thickness(0, 10, 0, 12) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(on, 0), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(total - on, 0), GridUnitType.Star) });
            track.Children.Add(new Border { Background = B("Line"), CornerRadius = new CornerRadius(4) });
            Grid.SetColumnSpan(track.Children[0], 2);
            if (on > 0) track.Children.Add(new Border { Background = B("Accent"), CornerRadius = new CornerRadius(4) });
            var grades = new WrapPanel();
            foreach (char gr in new[] { 'P', 'G', 'S', 'B' })
            {
                int all = list.Count(t => t.Grade == gr);
                if (all == 0) continue;
                var c = GradeChip(gr, true); c.Margin = new Thickness(0, 0, 6, 6);
                var n = T(list.Count(t => t.Grade == gr && t.Unlocked) + " / " + all, 13, "Dim"); n.Margin = new Thickness(0, 0, 16, 6); n.VerticalAlignment = VerticalAlignment.Center;
                grades.Children.Add(c); grades.Children.Add(n);
            }
            var body = VStack(head, track, grades);
            var latest = list.All.Where(t => t.Unlocked && !t.Counted).OrderByDescending(t => t.UnlockedAt).FirstOrDefault();
            string sub = latest != null ? "Latest: " + TrophyName(latest) + " · " + Trophies.When(latest.UnlockedAt) : "Nothing unlocked yet. Trophies unlock as you play.";
            var st = T(sub, 12.5, "Dim"); st.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(st);
            Grid.SetColumn(body, 1);
            g.Children.Add(body);
            return Card(g);
        }

        string TrophyName(Trophy t) { return t.Hidden && !t.Unlocked && !trophyReveal ? "Hidden trophy" : t.Name; }

        UIElement TrophyRow(Trophy t)
        {
            bool masked = t.Hidden && !t.Unlocked && !trophyReveal;
            var g = new Grid { Margin = new Thickness(18, 12, 20, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = masked ? null : Trophies.Icon("TROP" + t.Id.ToString("000") + ".PNG", 112, !t.Unlocked);
            FrameworkElement pic;
            if (icon != null)
                pic = new Image { Source = icon, Width = 56, Height = 56, Opacity = t.Unlocked ? 1 : 0.45, Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 8, 8) };
            else
            {
                var gl = Glyph(masked ? "" : "", 22, "Faint"); gl.HorizontalAlignment = HorizontalAlignment.Center;
                pic = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), Background = B("CardHi"), Child = gl };
            }
            pic.VerticalAlignment = VerticalAlignment.Center;
            pic.Margin = new Thickness(0, 0, 16, 0);
            g.Children.Add(pic);

            var name = new TextBlock { Text = TrophyName(t), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B(t.Unlocked ? "Text" : "Dim"),
                                       VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var title = HStack(name);
            if (t.Grade != '?') title.Children.Add(GradeChip(t.Grade, t.Unlocked));
            var detail = T(masked ? "Keep playing to reveal it." : t.Detail, 13, t.Unlocked ? "Dim" : "Faint");
            detail.Margin = new Thickness(0, 3, 0, 0);
            var body = VStack(title, detail);
            body.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(body, 1);
            g.Children.Add(body);

            UIElement status;
            if (t.Unlocked)
            {
                var top = HStack(Glyph("", 13, "Good"), T(t.Counted ? "Earned" : "Unlocked", 13, "Good", FontWeights.SemiBold));
                ((FrameworkElement)top.Children[1]).Margin = new Thickness(6, 0, 0, 0);
                top.HorizontalAlignment = HorizontalAlignment.Right;
                var when = T(t.Counted ? "every other trophy unlocked" : Trophies.When(t.UnlockedAt), 12, "Faint");
                when.HorizontalAlignment = HorizontalAlignment.Right; when.Margin = new Thickness(0, 2, 0, 0);
                status = VStack(top, when);
            }
            else
            {
                var top = HStack(Glyph("", 13, "Faint"), T("Locked", 13, "Faint", FontWeights.SemiBold));
                ((FrameworkElement)top.Children[1]).Margin = new Thickness(6, 0, 0, 0);
                status = top;
            }
            ((FrameworkElement)status).VerticalAlignment = VerticalAlignment.Center;
            ((FrameworkElement)status).Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(status, 2);
            g.Children.Add(status);
            return g;
        }
    }
}
