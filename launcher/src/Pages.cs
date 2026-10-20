// inFamous Launcher: the pages.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InfamousLauncher
{
    sealed partial class MainWindow
    {
        Button heroStop;

        // ------------------------------------------------------------------------------------------------
        // Setting rows
        // ------------------------------------------------------------------------------------------------
        static string Label(Def d, string v)
        {
            if (d.Kind == Kind.Slider) { double x; return Inv.TryNum(v, out x) ? d.Fmt(x) : (v ?? "not set"); }
            foreach (var o in d.Opts) if (Inv.Same(o.V, v)) return o.L;
            return v == null ? "not set" : "Custom (" + v + ")";
        }

        static string PageName(string p)
        {
            switch (p)
            {
                case "display": return "Display";
                case "graphics": return "Graphics";
                case "reshade": return "ReShade";
                case "performance": return "Performance";
                case "input": return "Controls";
                case "controller": return "Controller";
                case "audio": return "Audio";
                default: return "Advanced";
            }
        }

        bool Cond(string expr)   // KEY=value; & joins conditions that must all hold, | alternatives of which one must
        {
            if (string.IsNullOrEmpty(expr)) return true;
            foreach (var part in expr.Split('&'))
            {
                bool any = false;
                foreach (var alt in part.Split('|'))
                {
                    int eq = alt.IndexOf('=');
                    if (Inv.Same(M.Get(alt.Substring(0, eq)), alt.Substring(eq + 1))) { any = true; break; }
                }
                if (!any) return false;
            }
            return true;
        }

        void Change(Def d, string v)
        {
            M.Set(d, v);
            QueueSave();
            RefreshRows();
            UpdateBar();
        }

        ComboBox Combo(Def d, double width)
        {
            var cb = new ComboBox { Width = width };
            string cur = M.Get(d);
            foreach (var o in d.Opts)
            {
                var it = new ComboBoxItem { Content = o.L, Tag = o.V };
                cb.Items.Add(it);
                if (Inv.Same(o.V, cur)) cb.SelectedItem = it;
            }
            if (cb.SelectedItem == null && cur != null)
            {
                var it = new ComboBoxItem { Content = "Custom (" + cur + ")", Tag = cur };
                cb.Items.Add(it);
                cb.SelectedItem = it;
            }
            cb.SelectionChanged += (s, e) => { var it = cb.SelectedItem as ComboBoxItem; if (it != null) Change(d, (string)it.Tag); };
            return cb;
        }

        FrameworkElement Control(Def d)
        {
            string cur = M.Get(d);
            switch (d.Kind)
            {
                case Kind.Toggle:
                    {
                        var cb = new CheckBox { Style = S("Switch"), IsChecked = Inv.Same(cur, d.Opts[1].V) };
                        cb.Checked += (s, e) => Change(d, d.Opts[1].V);
                        cb.Unchecked += (s, e) => Change(d, d.Opts[0].V);
                        return cb;
                    }
                case Kind.Choice:
                    return Combo(d, 270);
                default:
                    {
                        var sl = new Slider { Minimum = d.Min, Maximum = d.Max, Width = 230, TickFrequency = d.Step, IsSnapToTickEnabled = true,
                                              SmallChange = d.Step, LargeChange = d.Step, VerticalAlignment = VerticalAlignment.Center };
                        double v;
                        if (!Inv.TryNum(cur, out v)) v = d.Min;
                        if (v < sl.Minimum) sl.Minimum = v;
                        if (v > sl.Maximum) sl.Maximum = v;
                        sl.Value = v;
                        var lbl = new TextBlock { Text = d.Fmt(v), Width = 150, FontSize = 13, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                        sl.ValueChanged += (s, e) =>
                        {
                            double x = Math.Round(e.NewValue / d.Step) * d.Step;
                            lbl.Text = d.Fmt(x);
                            Change(d, Inv.Num(x));
                        };
                        return HStack(sl, lbl);
                    }
            }
        }

        static UIElement Note(string glyph, string text, string fg, string bg, Thickness margin)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = Glyph(glyph, 13, fg);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 8, 0);
            g.Children.Add(icon);
            var t = T(text, 12.5, fg);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return new Border { Background = B(bg), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = margin, Child = g };
        }

        UIElement Row(Def d)
        {
            var g = new Grid { Margin = new Thickness(20, 14, 12, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            var title = HStack(new TextBlock { Text = d.Label, FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            if (d.Experimental) title.Children.Add(Chip("EXPERIMENTAL", "Warn", "WarnSoft"));
            var modChip = Chip("", "AccentHi", "AccentSoft");
            title.Children.Add(modChip);
            var left = VStack(title);
            if (d.Desc != null) { var desc = T(d.Desc, 12.5, "Dim"); desc.Margin = new Thickness(0, 4, 0, 0); left.Children.Add(desc); }
            if (d.Warn != null) left.Children.Add(Note("\uE7BA", d.Warn, "Warn", "WarnSoft", new Thickness(0, 8, 0, 0)));
            left.Margin = new Thickness(0, 0, 28, 0);
            left.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(left);

            var ctl = Control(d);
            ctl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ctl, 1);
            g.Children.Add(ctl);

            var reset = new Button { Style = S("Ghost"), Content = Glyph("\uE7A7", 14, null), Margin = new Thickness(6, 0, 0, 0),
                                     VerticalAlignment = VerticalAlignment.Center, ToolTip = "Reset to default: " + Label(d, M.Default(d)) };
            reset.Click += (s, e) => { M.Reset(d); QueueSave(); RebuildPage(); };
            Grid.SetColumn(reset, 2);
            g.Children.Add(reset);

            refreshers.Add(() =>
            {
                var owner = d.Store == Store.Env ? M.ModOwner(d.Key) : null;
                bool en = owner == null && Cond(d.EnabledIf);
                ctl.IsEnabled = en;
                left.Opacity = en ? 1 : 0.5;
                reset.Visibility = owner == null && M.Modified(d) ? Visibility.Visible : Visibility.Hidden;
                modChip.Visibility = owner != null ? Visibility.Visible : Visibility.Collapsed;
                if (owner != null) ((TextBlock)modChip.Child).Text = "SET BY MOD: " + owner.Name.ToUpperInvariant();
            });
            return g;
        }

        UIElement SettingsPage(string pg, string title, string subtitle, params UIElement[] top)
        {
            var s = VStack(Header(title, subtitle));
            foreach (var t in top) if (t != null) s.Children.Add(t);
            foreach (var grp in Catalog.All.Where(d => d.Page == pg).Select(d => d.Group).Distinct().ToList())
                s.Children.Add(Section(grp, null, Catalog.All.Where(d => d.Page == pg && d.Group == grp).Select(Row).ToList()));
            return s;
        }

        // ------------------------------------------------------------------------------------------------
        // Display & Graphics
        // ------------------------------------------------------------------------------------------------
        UIElement DisplayPage()
        {
            return SettingsPage("display", "Display", "Screen and interface are the same settings as the in-game menu (F10); changing them in either place updates the other. Language is set here.", DisplayInfo());
        }

        UIElement DisplayInfo()
        {
            if (SystemInfo.Width <= 0) return null;
            var res = Catalog.Find("RES_SCALE");
            string match = SystemInfo.MatchingScale();
            var g = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = B("AccentSoft"), Child = Glyph("\uE7F4", 18, "Accent") };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(icon);
            string hw = string.Join("  ·  ", new[] { SystemInfo.Gpu, SystemInfo.Cpu }.Where(x => !string.IsNullOrEmpty(x)));
            var text = VStack(T("Your display: " + SystemInfo.Width + " × " + SystemInfo.Height + (SystemInfo.Hz > 1 ? " at " + SystemInfo.Hz + " Hz" : ""), 14, "Text", FontWeights.SemiBold),
                              hw.Length > 0 ? T(hw, 12, "Dim") : null);
            text.Margin = new Thickness(14, 0, 14, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            if (match != null && !Inv.Same(M.Get(res), match))
            {
                var b = Btn("Use " + Label(res, match).Split('·')[0].Trim() + " (matches your display)", () => { Change(res, match); RebuildPage(); }, "Btn", "\uE73E");
                b.Margin = new Thickness(0);
                b.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(b, 2);
                g.Children.Add(b);
            }
            return Card(g);
        }

        // Performance page: suggests Low-end CPU mode on a CPU with 8 logical processors or fewer.
        UIElement LowCpuNote()
        {
            int n = Environment.ProcessorCount;
            if (n > 8 || M.Cfg.LowCpu == "1") return null;
            var g = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = VStack(T("This PC has " + n + " logical processors", 14, "Text", FontWeights.SemiBold),
                              T("The shipped settings are tuned for bigger CPUs and can make the game stall on this one. Low-end CPU mode " +
                                "(also part of the Potato PC preset on the Graphics page) stops the threads from spin-waiting and reserving cores.", 12.5, "Dim"));
            text.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(text);
            var b = Btn("Turn on Low-end CPU mode", () => { M.Set(Catalog.Find("launcher.lowcpu"), "1"); QueueSave(); RebuildPage(); }, "Primary", "");
            b.Margin = new Thickness(0);
            b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            return Card(g);
        }

        UIElement GraphicsPage()
        {
            return SettingsPage("graphics", "Graphics", "Image quality and how far the city is drawn. Draw distance has the biggest effect on frame rate.", GpuHint(), PresetSection());
        }

        // Graphics page, on a Radeon: suggest AMD FSR (FSR 4 on RX 7000 / 9000) with frame generation.
        UIElement GpuHint()
        {
            string gpu = SystemInfo.Gpu ?? "";
            if (gpu.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (Inv.Same(M.Get("FSR"), "1") && Inv.Same(M.Get("FSR_FG"), "1")) return null;
            bool fsr4 = Regex.IsMatch(gpu, @"RX\s*[79]\d{3}", RegexOptions.IgnoreCase);
            var g = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = VStack(T(gpu + (fsr4 ? ": FSR 4 available" : ": FSR 3.1 available"), 14, "Text", FontWeights.SemiBold),
                              T("AMD FSR (Quality) with frame generation gives the smoothest picture on this card, most of all in crowded " +
                                "streets, where the CPU sets the frame rate and frame generation doubles what you see.", 12.5, "Dim"));
            text.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(text);
            var b = Btn("Use FSR Quality + frame generation", () =>
            {
                M.Set("DLSS", "0"); M.Set("FSR", "1"); M.Set("FSR_MODE", "quality"); M.Set("FSR_FG", "1");
                lastMessage = "AMD FSR Quality with frame generation set.";
                QueueSave(); RebuildPage();
            }, "Primary", "");
            b.Margin = new Thickness(0);
            b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            return Card(g);
        }

        bool PresetActive(Catalog.Preset p)
        {
            return p.Values.All(kv => { int eq = kv.IndexOf('='); return Inv.Same(M.Get(kv.Substring(0, eq)), kv.Substring(eq + 1)); });
        }

        void ApplyPreset(Catalog.Preset p)
        {
            foreach (var kv in p.Values)
            {
                int eq = kv.IndexOf('=');
                string key = kv.Substring(0, eq);
                if (M.ModOwner(key) == null) M.Set(key, kv.Substring(eq + 1));
            }
            lastMessage = p.Name + " preset applied.";
            QueueSave();
            RebuildPage();
        }

        UIElement PresetSection()
        {
            return PresetSection(Catalog.Presets, "Presets", "Sets resolution, texture filtering, shadows and draw distance in one go. Potato PC also lowers CPU work for low-end PCs. Fine-tune anything below afterwards.");
        }

        UIElement PresetSection(Catalog.Preset[] list, string title, string subtitle)
        {
            var wrap = new UniformGrid { Columns = list.Length, Margin = new Thickness(20, 4, 10, 10) };
            foreach (var p in list)
            {
                bool on = PresetActive(p);
                var body = VStack(new TextBlock { Text = p.Name, FontSize = 14, FontWeight = FontWeights.SemiBold },
                                  new TextBlock { Text = p.Desc, FontSize = 11.5, Opacity = on ? 0.9 : 0.6, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
                var b = new Button { Style = S(on ? "Primary" : "Btn"), Content = body, Padding = new Thickness(14, 10, 12, 10),
                                     HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 10) };
                var preset = p;
                b.Click += (s, e) => ApplyPreset(preset);
                wrap.Children.Add(b);
            }
            return Section(title, subtitle, new UIElement[] { wrap });
        }

        // ------------------------------------------------------------------------------------------------
        // Home
        // ------------------------------------------------------------------------------------------------
        UIElement HomePage()
        {
            var s = VStack(Hero(), QuickSettings(), InfoTiles(), Hotkeys());
            return s;
        }

        UIElement Hero()
        {
            var grid = new Grid { Height = 360, Margin = new Thickness(0, 0, 0, 18) };
            Brush bg;
            if (heroImage != null) bg = new ImageBrush(heroImage) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right };
            else
            {
                var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                lg.GradientStops.Add(new GradientStop(((SolidColorBrush)Hex("#14294A")).Color, 0));
                lg.GradientStops.Add(new GradientStop(((SolidColorBrush)Hex("#0A0E16")).Color, 1));
                bg = lg;
            }
            var radius = new CornerRadius(16);
            grid.Children.Add(new Border { CornerRadius = radius, Background = bg });
            var side = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            foreach (var st in new[] { Tuple.Create("#F20A0E16", 0.0), Tuple.Create("#B00A0E16", 0.38), Tuple.Create("#300A0E16", 0.75), Tuple.Create("#000A0E16", 1.0) })
                side.GradientStops.Add(new GradientStop(((SolidColorBrush)Hex(st.Item1)).Color, st.Item2));
            grid.Children.Add(new Border { CornerRadius = radius, Background = side });
            var bottom = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            bottom.GradientStops.Add(new GradientStop(((SolidColorBrush)Hex("#000A0E16")).Color, 0.45));
            bottom.GradientStops.Add(new GradientStop(((SolidColorBrush)Hex("#D90A0E16")).Color, 1));
            grid.Children.Add(new Border { CornerRadius = radius, Background = bottom });
            grid.Children.Add(new Border { CornerRadius = radius, BorderBrush = B("Line"), BorderThickness = new Thickness(1) });

            var kicker = T("PLAYSTATION 3  ·  RECOMPILED FOR PC", 11.5, "AccentHi", FontWeights.SemiBold);
            var title = new TextBlock
            {
                Text = "inFAMOUS", FontSize = 66, FontWeight = FontWeights.Black, Foreground = Brushes.White, Margin = new Thickness(-2, 0, 0, 0),
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black }
            };
            var sub = T("Native Windows port, built from your own disc with PS3Recomp.", 14.5, "Dim");
            sub.MaxWidth = 470;
            sub.HorizontalAlignment = HorizontalAlignment.Left;

            heroPlay = new Button { Style = S("Play"), Content = IconText("\uE768", "PLAY", 20) };
            heroPlay.Click += (s, e) => Play();
            heroStop = new Button { Style = S("Danger"), Content = IconText("\uE71A", "Stop"), Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heroStop.Click += (s, e) => StopGame();
            heroDot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
            heroStatus = new TextBlock { FontSize = 12.5, Foreground = B("Text"), MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border { Background = Hex("#C00D121D"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
                                    Padding = new Thickness(14, 8, 16, 8), Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Child = HStack(heroDot, heroStatus) };
            var actions = HStack(heroPlay, heroStop, chip);
            actions.Margin = new Thickness(0, 24, 0, 0);
            var content = VStack(kicker, title, sub, actions);
            content.Margin = new Thickness(40, 0, 40, 34);
            content.VerticalAlignment = VerticalAlignment.Bottom;
            content.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(content);
            return grid;
        }

        UIElement QuickSettings()
        {
            var wrap = new UniformGrid { Columns = 4, Margin = new Thickness(20, 4, 6, 8) };
            foreach (var key in new[] { "FULLSCREEN", "RES_SCALE", "UI_FPS_CAP", "PRESENT_VSYNC" })
            {
                var d = Catalog.Find(key);
                var lbl = T(d.Label, 12, "Dim", FontWeights.SemiBold);
                lbl.Margin = new Thickness(0, 0, 0, 6);
                var combo = Combo(d, double.NaN);
                combo.MinWidth = 0;
                var item = VStack(lbl, combo);
                item.Margin = new Thickness(0, 0, 14, 10);
                wrap.Children.Add(item);
            }
            var presets = new WrapPanel { Margin = new Thickness(20, 12, 10, 6) };
            var pl = T("Graphics preset", 12, "Dim", FontWeights.SemiBold);
            pl.Width = 120; pl.VerticalAlignment = VerticalAlignment.Center; pl.Margin = new Thickness(0, 0, 0, 8);
            presets.Children.Add(pl);
            foreach (var p in Catalog.Presets)
            {
                var preset = p;
                var b = Btn(p.Name, () => ApplyPreset(preset), PresetActive(p) ? "Primary" : "Btn", null, p.Desc);
                presets.Children.Add(b);
            }
            return Section("Quick settings", "The essentials. Every option has its own page on the left.", new UIElement[] { wrap, presets });
        }

        UIElement Tile(string glyph, string title, string value, string sub, string tone, Thickness margin)
        {
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = B(tone + "Soft"), HorizontalAlignment = HorizontalAlignment.Left, Child = Glyph(glyph, 16, tone) };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var t = T(title.ToUpperInvariant(), 10.5, "Faint", FontWeights.SemiBold); t.Margin = new Thickness(0, 12, 0, 0);
            var v = T(value, 19, "Text", FontWeights.SemiBold); v.Margin = new Thickness(0, 2, 0, 0);
            var sb = T(sub, 12, "Dim"); sb.Margin = new Thickness(0, 2, 0, 0);
            return Card(Padded(VStack(icon, t, v, sb), 18, 16, 18, 16), margin);
        }

        UIElement InfoTiles()
        {
            var grid = new UniformGrid { Columns = 4 };

            // build
            string exe = M.ResolveExe();
            if (exe != null && File.Exists(exe))
            {
                var fi = new FileInfo(exe);
                string cache = System.IO.Path.Combine(fi.DirectoryName, "CMakeCache.txt");
                bool pgo = File.Exists(cache) && File.ReadAllText(cache).Contains("PORT_PGO:STRING=USE");
                grid.Children.Add(Tile("\uE7B8", "Game build", pgo ? "Ready · PGO" : "Ready",
                                       fi.Directory.Name + " · " + fi.LastWriteTime.ToString("d MMM yyyy", Inv.C), "Good", new Thickness(0, 0, 12, 16)));
            }
            else grid.Children.Add(Tile("\uE7B8", "Game build", "Missing", "Run Update or Setup to build", "Bad", new Thickness(0, 0, 12, 16)));

            // saves
            int slots = 0; DateTime last = DateTime.MinValue;
            try
            {
                if (Directory.Exists(M.SaveDir))
                    foreach (var d in Directory.GetDirectories(M.SaveDir))
                    {
                        slots++;
                        foreach (var f in Directory.GetFiles(d)) { var t = File.GetLastWriteTime(f); if (t > last) last = t; }
                    }
            }
            catch { }
            grid.Children.Add(Tile("\uE74E", "Save games", slots == 0 ? "None yet" : slots + (slots == 1 ? " slot" : " slots"),
                                   slots == 0 ? "Saves appear after you play" : "Last saved " + last.ToString("d MMM, HH:mm", Inv.C), "Accent", new Thickness(0, 0, 12, 16)));

            // trophies (click: the Trophies page)
            string troValue = "\u2014", troSub = "Unlock them in game";
            try
            {
                var tl = Trophies.Load(M);
                int tro = tl.Count(t => t.Unlocked);
                troValue = tl.Problem == null ? tro + " of " + tl.All.Count : tro + " unlocked";
                var newest = tl.All.Where(t => t.Unlocked && !t.Counted).OrderByDescending(t => t.UnlockedAt).FirstOrDefault();
                if (newest != null) troSub = "Latest " + new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(newest.UnlockedAt).ToLocalTime().ToString("d MMM yyyy", Inv.C);
            }
            catch { }
            var troTile = (Border)Tile("\uE734", "Trophies", troValue, troSub, "Warn", new Thickness(0, 0, 12, 16));
            troTile.Cursor = Cursors.Hand;
            troTile.ToolTip = "See every trophy";
            troTile.MouseLeftButtonUp += (s, e) => Navigate("trophies");
            grid.Children.Add(troTile);

            // play time
            string pt = M.Cfg.PlaySeconds > 0 ? Inv.Duration(TimeSpan.FromSeconds(M.Cfg.PlaySeconds)) : "—";
            string lp = string.IsNullOrEmpty(M.Cfg.LastPlayed) ? "Counted when you play from here" : "Last played " + M.Cfg.LastPlayed;
            grid.Children.Add(Tile("\uE916", "Play time", pt, lp, "Accent", new Thickness(0, 0, 0, 16)));
            return grid;
        }

        static UIElement KeyCap(string k)
        {
            return new Border
            {
                Background = Hex("#182134"), BorderBrush = B("LineHi"), BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3), MinWidth = 46, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = k, FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
            };
        }

        UIElement Hotkeys()
        {
            var wrap = new WrapPanel { Margin = new Thickness(20, 4, 20, 14) };
            var keys = new[]
            {
                Tuple.Create("F10", "Settings menu (also Back while paused)"),
                Tuple.Create("F11", "Fullscreen on / off"),
                Tuple.Create("F1", "FPS counter"),
                Tuple.Create("F5", "Draw distance boost on / off"),
                Tuple.Create("Esc", "Pause menu"),
                Tuple.Create("Alt+F4", "Quit the game"),
            };
            foreach (var k in keys)
            {
                var item = HStack(KeyCap(k.Item1), T(k.Item2, 13, "Dim"));
                item.Width = 320;
                item.Margin = new Thickness(0, 0, 0, 10);
                wrap.Children.Add(item);
            }
            return Section("In-game keys", null, new UIElement[] { wrap });
        }

        // ------------------------------------------------------------------------------------------------
        // Mods
        // ------------------------------------------------------------------------------------------------
        UIElement ModsPage()
        {
            M.ReloadMods();
            var s = VStack(Header("Mods", "Mods are folders in infamous-recomp\\mods with a mod.ini file. When the game starts, every enabled mod's settings are applied on top of yours."));
            var bar = HStack(Btn("Open mods folder", () => OpenFolder(M.ModsDir), "Btn", "\uE838"),
                             Btn("New mod", NewMod, "Btn", "\uE710"),
                             Btn("Refresh", RebuildPage, "Btn", "\uE72C"));
            bar.Margin = new Thickness(0, 0, 0, 10);
            s.Children.Add(bar);
            if (M.ModList.Count == 0) s.Children.Add(Card(Padded(T("No mods yet. Use New mod to make one.", 13, "Dim"))));
            foreach (var m in M.ModList) s.Children.Add(ModCard(m));
            s.Children.Add(HowModsWork());
            return s;
        }

        UIElement ModCard(ModInfo m)
        {
            bool on = M.ModEnabled(m);
            var g = new Grid { Margin = new Thickness(20, 18, 16, 18) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sw = new CheckBox { Style = S("Switch"), IsChecked = on, IsEnabled = m.Available, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 18, 0),
                                    ToolTip = m.Available ? (on ? "Disable this mod" : "Enable this mod") : "Not available" };
            sw.Checked += (s, e) => { M.Cfg.Mods[m.Id] = true; QueueSave(); RebuildPage(); };
            sw.Unchecked += (s, e) => { M.Cfg.Mods[m.Id] = false; QueueSave(); RebuildPage(); };
            g.Children.Add(sw);

            var title = HStack(new TextBlock { Text = m.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(m.Version)) title.Children.Add(Chip("v" + m.Version, "Dim", "CardHi"));
            if (on) title.Children.Add(Chip("ENABLED", "Good", "GoodSoft"));
            if (m.Experimental) title.Children.Add(Chip("EXPERIMENTAL", "Warn", "WarnSoft"));
            if (!m.Available) title.Children.Add(Chip("UNAVAILABLE", "Bad", "BadSoft"));
            var body = VStack(title);
            if (!string.IsNullOrEmpty(m.Author)) { var a = T("by " + m.Author, 12, "Faint"); a.Margin = new Thickness(0, 2, 0, 0); body.Children.Add(a); }
            if (!string.IsNullOrEmpty(m.Description)) { var d = T(m.Description, 13, "Dim"); d.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(d); }
            if (!m.Available) body.Children.Add(Note("\uE7BA", m.Unavailable, "Warn", "WarnSoft", new Thickness(0, 10, 0, 0)));
            if (m.Env.Count > 0)
            {
                var h = T("CHANGES", 10.5, "Faint", FontWeights.SemiBold); h.Margin = new Thickness(0, 12, 0, 4);
                body.Children.Add(h);
                foreach (var kv in m.Env)
                {
                    var def = Catalog.Find(kv.Key);
                    var line = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                    line.Inlines.Add(new System.Windows.Documents.Run(kv.Key) { Foreground = B("AccentHi") });
                    line.Inlines.Add(new System.Windows.Documents.Run(" = " + (kv.Value == LauncherConfig.Unset ? "(removed)" : kv.Value)) { Foreground = B("Text") });
                    if (def != null)
                        line.Inlines.Add(new System.Windows.Documents.Run("   " + def.Label + " on the " + PageName(def.Page) + " page") { Foreground = B("Faint"), FontFamily = FontFamily });
                    if (M.Cfg.Env.ContainsKey(kv.Key))
                        line.Inlines.Add(new System.Windows.Documents.Run(on ? "  · replaces your setting" : "  · would replace your setting") { Foreground = B("Warn"), FontFamily = FontFamily });
                    body.Children.Add(line);
                }
            }
            if (m.Ignored.Count > 0)
                body.Children.Add(Note("\uE946", "Ignored: " + string.Join(", ", m.Ignored) + ". In-game menu settings can't be set by a mod.", "Dim", "CardHi", new Thickness(0, 10, 0, 0)));
            Grid.SetColumn(body, 1);
            g.Children.Add(body);

            var actions = VStack(Btn("Folder", () => OpenFolder(m.Dir), "Btn", "\uE838"),
                                 m.Readme != null ? Btn("Read me", () => Open(m.Readme), "Btn", "\uE8A5") : null,
                                 m.IniPath != null ? Btn("Edit", () => Edit(m.IniPath), "Btn", "\uE70F") : null);
            actions.Margin = new Thickness(16, 0, 0, 0);
            foreach (FrameworkElement b in actions.Children) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);
            return Card(g);
        }

        UIElement HowModsWork()
        {
            var code = new Border
            {
                Background = B("Input"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    FontFamily = Mono, FontSize = 12, Foreground = B("Text"),
                    Text = "[mod]\nname=Longer Draw Distance\nversion=1.0\nauthor=you\ndescription=What the mod changes.\nexperimental=0\n\n[env]\nLOD_CLASS_SCALE=3\nCAR_DIST_SCALE=2"
                }
            };
            var text = T("A mod sets environment variables the game reads at startup: anything listed on the Advanced page, or other switches the port's source reads. " +
                         "While a mod is enabled, the settings it changes are locked on the other pages. Mods load in folder-name order; a later mod wins if two change the same value. " +
                         "<unset> as a value removes a variable.", 13, "Dim");
            return Section("Make your own", null, new UIElement[] { Padded(VStack(text, code), 20, 14, 20, 18) });
        }

        void NewMod()
        {
            string name = InputBox.Ask(this, "New mod", "Name of the new mod", "My mod");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) slug = "mod";
            string dir = System.IO.Path.Combine(M.ModsDir, slug);
            for (int n = 2; Directory.Exists(dir); n++) dir = System.IO.Path.Combine(M.ModsDir, slug + "-" + n);
            try
            {
                Directory.CreateDirectory(dir);
                string ini = System.IO.Path.Combine(dir, "mod.ini");
                File.WriteAllText(ini, string.Format(Mods.Template, name.Replace("\r", "").Replace("\n", " ")), new UTF8Encoding(false));
                Edit(ini);
            }
            catch (Exception e) { MessageBox.Show("Could not create the mod: " + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
            RebuildPage();
        }

        static void Edit(string file)
        {
            try { Process.Start("notepad.exe", "\"" + file + "\""); }
            catch { Open(file); }
        }

        // ------------------------------------------------------------------------------------------------
        // Advanced
        // ------------------------------------------------------------------------------------------------
        TextBox envSearch;
        CheckBox envChangedOnly;
        StackPanel envList;
        DispatcherTimer envTimer;

        UIElement AdvancedPage()
        {
            var s = VStack(Header("Advanced", "Which build to start, what the launcher does, and every switch the game gets at startup. The defaults come from installer\\launch.txt, which the installer writes; your changes are kept separately in launcher\\launcher.ini, so updates don't overwrite them."));
            s.Children.Add(ExeSection());
            s.Children.Add(Section("Launcher", null, Catalog.All.Where(d => d.Page == "advanced").Select(Row).ToList()));
            s.Children.Add(EnvSection());
            return s;
        }

        string Rel(string path)
        {
            string root = M.P.Root + "\\";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }

        UIElement ExeSection()
        {
            var combo = new ComboBox { Width = 420 };
            var def = new ComboBoxItem { Content = "Installed build (from launch.txt)", Tag = "" };
            combo.Items.Add(def);
            combo.SelectedItem = def;
            string cur = M.Cfg.Exe ?? "";
            try
            {
                if (Directory.Exists(M.Port))
                    foreach (var dir in Directory.GetDirectories(M.Port, "build*").OrderBy(x => x))
                    {
                        string exe = System.IO.Path.Combine(dir, "inFamousRecomp.exe");
                        if (!File.Exists(exe)) continue;
                        var it = new ComboBoxItem { Content = System.IO.Path.GetFileName(dir) + "  ·  " + File.GetLastWriteTime(exe).ToString("d MMM yyyy HH:mm", Inv.C), Tag = Rel(exe) };
                        combo.Items.Add(it);
                        if (cur.Length > 0 && Inv.Same(Rel(exe), cur)) combo.SelectedItem = it;
                    }
            }
            catch { }
            if (cur.Length > 0 && combo.SelectedItem == def)
            {
                var it = new ComboBoxItem { Content = cur, Tag = cur };
                combo.Items.Add(it);
                combo.SelectedItem = it;
            }
            combo.SelectionChanged += (s, e) => { var it = combo.SelectedItem as ComboBoxItem; if (it == null) return; M.Cfg.Exe = (string)it.Tag; QueueSave(); UpdateBar(); };
            var browse = Btn("Browse…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", InitialDirectory = M.Port, Title = "Choose the game executable" };
                if (dlg.ShowDialog(this) == true) { M.Cfg.Exe = Rel(dlg.FileName); QueueSave(); RebuildPage(); }
            }, "Btn", "\uE8B7");
            browse.Margin = new Thickness(10, 0, 0, 0);

            var g = new Grid { Margin = new Thickness(20, 14, 20, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = VStack(new TextBlock { Text = "Game executable", FontSize = 14, FontWeight = FontWeights.Medium },
                              T("The program the launcher starts with your game's EBOOT.elf. Other builds in infamous-recomp (build-fast, build-pgo, …) show up here too.", 12.5, "Dim"));
            ((TextBlock)left.Children[1]).Margin = new Thickness(0, 4, 0, 0);
            left.Margin = new Thickness(0, 0, 24, 0);
            if (M.EnabledMods.Any(x => x.Exe != null))
                left.Children.Add(Note("\uE946", "Skate Mode is enabled, so its own build is started instead.", "AccentHi", "AccentSoft", new Thickness(0, 8, 0, 0)));
            g.Children.Add(left);
            var right = HStack(combo, browse);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return Section("Game build", null, new UIElement[] { g });
        }

        UIElement EnvSection()
        {
            envSearch = new TextBox { Width = 250, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Filter by name, value or setting" };
            envChangedOnly = new CheckBox { Content = "Changed only", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            envTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            envTimer.Tick += (s, e) => { envTimer.Stop(); FillEnvList(); };
            envSearch.TextChanged += (s, e) => { envTimer.Stop(); envTimer.Start(); };
            envChangedOnly.Checked += (s, e) => FillEnvList();
            envChangedOnly.Unchecked += (s, e) => FillEnvList();

            var tools = new Grid();
            tools.ColumnDefinitions.Add(new ColumnDefinition());
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var search = HStack(Glyph("\uE721", 14, "Dim"), envSearch, envChangedOnly);
            ((FrameworkElement)search.Children[0]).Margin = new Thickness(0, 0, 10, 0);
            tools.Children.Add(search);
            var right = HStack(Btn("launch.txt", () => Edit(M.P.LaunchTxt), "Btn", "\uE8A5", "Open the installer's launch file (read-only for the launcher)"),
                               Btn("Reset all", ResetAllEnv, "Danger", "\uE7A7", "Drop every change you made to these variables"));
            foreach (FrameworkElement b in right.Children) b.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(right, 1);
            tools.Children.Add(right);

            envList = new StackPanel();
            FillEnvList();
            return Section("Environment variables",
                           "Everything passed to the game. ↺ restores the installer's value, ✕ removes a variable. Rows marked settings.ini belong to the in-game menu and are set on the Display, Graphics, Performance and Controls pages.",
                           new UIElement[] { Padded(tools, 20, 12, 20, 12), envList, Padded(AddVarRow(), 20, 12, 20, 16) });
        }

        void FillEnvList()
        {
            if (envList == null) return;
            envList.Children.Clear();
            string q = envSearch.Text.Trim();
            bool only = envChangedOnly.IsChecked == true;
            int shown = 0;
            foreach (var k in M.EnvKeys())
            {
                string v = M.EnvValue(k) ?? "";
                var d = Catalog.Find(k);
                if (q.Length > 0 && k.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && v.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && (d == null || d.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (only && !M.EnvChanged(k)) continue;
                envList.Children.Add(EnvRow(k));
                shown++;
            }
            if (shown == 0)
                envList.Children.Add(new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = Padded(T(only ? "You haven't changed any variables." : "No variables match.", 13, "Dim")) });
        }

        UIElement EnvRow(string k)
        {
            var d = Catalog.Find(k);
            bool ini = Catalog.IsIni(k);
            var owner = M.ModOwner(k);
            var g = new Grid { Margin = new Thickness(20, 5, 12, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var key = new TextBlock { Text = k, FontFamily = Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                      ToolTip = d != null ? k + "\n“" + d.Label + "” on the " + PageName(d.Page) + " page" : k };
            g.Children.Add(key);
            var tb = new TextBox { FontFamily = Mono, FontSize = 12.5, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 14, 0),
                                   IsReadOnly = ini || owner != null };
            if (tb.IsReadOnly) tb.Foreground = B("Dim");
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            var status = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(status, 2);
            g.Children.Add(status);
            var reset = new Button { Style = S("Ghost"), Content = Glyph("\uE7A7", 13, null), ToolTip = "Restore the installer's value" };
            var remove = new Button { Style = S("Ghost"), Content = Glyph("\uE711", 12, null), ToolTip = "Remove this variable" };
            var acts = HStack(reset, remove);
            Grid.SetColumn(acts, 3);
            g.Children.Add(acts);

            Action refresh = () =>
            {
                string v = M.EnvValue(k), b;
                bool changed = M.EnvChanged(k), inBase = M.Base.TryGetValue(k, out b);
                string st, br;
                if (ini) { st = "settings.ini"; br = "Faint"; }
                else if (owner != null) { st = "mod: " + owner.Name; br = "AccentHi"; }
                else if (!changed) { st = "default"; br = "Faint"; }
                else if (v == null) { st = "removed"; br = "Warn"; }
                else if (!inBase) { st = "added"; br = "Good"; }
                else { st = "changed (was " + b + ")"; br = "Accent"; }
                status.Text = st; status.ToolTip = st; status.Foreground = B(br);
                key.Foreground = changed && !ini ? B("AccentHi") : B("Text");
                reset.Visibility = changed && !ini && owner == null ? Visibility.Visible : Visibility.Hidden;
                remove.Visibility = v != null && !ini && owner == null ? Visibility.Visible : Visibility.Hidden;
                if (ini) v = M.Get(k);
                if (!tb.IsKeyboardFocused) tb.Text = v ?? "";
            };
            Action commit = () =>
            {
                if (tb.IsReadOnly) return;
                string nv = tb.Text, cur = M.EnvValue(k);
                if (nv == cur || (cur == null && nv.Length == 0)) return;
                M.SetEnv(k, nv);
                QueueSave();
                refresh();
                UpdateBar();
            };
            tb.LostKeyboardFocus += (s, e) => commit();
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { commit(); refresh(); }
                else if (e.Key == Key.Escape) tb.Text = M.EnvValue(k) ?? "";
            };
            reset.Click += (s, e) => { M.ResetEnv(k); QueueSave(); if (M.Base.ContainsKey(k)) refresh(); else FillEnvList(); UpdateBar(); };
            remove.Click += (s, e) => { M.SetEnv(k, null); QueueSave(); if (M.Base.ContainsKey(k)) refresh(); else FillEnvList(); UpdateBar(); };
            refresh();
            return new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
        }

        UIElement AddVarRow()
        {
            var name = new TextBox { Width = 260, FontFamily = Mono, FontSize = 12.5 };
            var val = new TextBox { Width = 200, FontFamily = Mono, FontSize = 12.5, Margin = new Thickness(0, 0, 12, 0) };
            var add = Btn("Add variable", () =>
            {
                string k = name.Text.Trim();
                if (k.Length == 0 || k.IndexOfAny(new[] { '=', ' ', '\t' }) >= 0)
                { MessageBox.Show("A variable name can't be empty or contain spaces or '='.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (Catalog.IsIni(k))
                { MessageBox.Show(k + " is an in-game menu setting (settings.ini). Change it on the Display, Graphics, Performance or Controls page.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                M.SetEnv(k, val.Text);
                QueueSave();
                name.Text = val.Text = "";
                FillEnvList();
                UpdateBar();
            }, "Btn", "\uE710");
            add.Margin = new Thickness(0);
            var l1 = T("Name", 12, "Dim"); l1.VerticalAlignment = VerticalAlignment.Center; l1.Margin = new Thickness(0, 0, 8, 0);
            var l2 = T("Value", 12, "Dim"); l2.VerticalAlignment = VerticalAlignment.Center; l2.Margin = new Thickness(16, 0, 8, 0);
            return HStack(l1, name, l2, val, add);
        }

        void ResetAllEnv()
        {
            if (M.Cfg.Env.Count == 0) return;
            if (MessageBox.Show("Undo all " + M.Cfg.Env.Count + " change(s) to the environment variables and go back to the installer's values?\n\nThis also resets the matching settings on the other pages (in-game menu settings are kept).",
                                "inFamous Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            M.Cfg.Env.Clear();
            M.Cfg.EnvOrder.Clear();
            QueueSave();
            RebuildPage();
        }

        // ------------------------------------------------------------------------------------------------
        // Tools
        // ------------------------------------------------------------------------------------------------
        TextBox toolOut, logView;

        UIElement ToolsPage()
        {
            var s = VStack(Header("Tools", "Save backups, the shader cache, logs and updates."));

            // saves
            int slots = 0;
            try { if (Directory.Exists(M.SaveDir)) slots = Directory.GetDirectories(M.SaveDir).Length; } catch { }
            s.Children.Add(Section("Save games & trophies",
                slots + (slots == 1 ? " save slot" : " save slots") + " in " + Rel(M.SaveDir) + ". Backups are zip files in " + Rel(M.P.BackupDir) + ".",
                new UIElement[] { Padded(HStack(Btn("Back up now", BackupSaves, "Primary", "\uE74E"),
                                                Btn("Open save folder", () => OpenFolder(M.SaveDir), "Btn", "\uE838"),
                                                Btn("Open backups", () => OpenFolder(M.P.BackupDir), "Btn", "\uE838")), 20, 14, 20, 6) }));

            // shader cache
            int count = 0; long size = 0;
            try
            {
                if (Directory.Exists(M.ShaderCache))
                    foreach (var f in Directory.GetFiles(M.ShaderCache, "*", SearchOption.AllDirectories)) { count++; size += new FileInfo(f).Length; }
            }
            catch { }
            s.Children.Add(Section("Shader cache",
                count + " files · " + Inv.Size(size) + ". The game compiles shaders as it meets them and keeps them here. Clearing the cache can fix broken graphics after an update, but you'll get stutter until it is rebuilt.",
                new UIElement[] { Padded(HStack(Btn("Open folder", () => OpenFolder(M.ShaderCache), "Btn", "\uE838"),
                                                Btn("Clear shader cache", ClearShaderCache, "Danger", "\uE74D")), 20, 14, 20, 6) }));

            // updates
            toolOut = OutputBox();
            string repo = null;
            try
            {
                var toml = System.IO.Path.Combine(M.P.Root, "installer", "infamous.toml");
                if (File.Exists(toml)) { var m = Regex.Match(File.ReadAllText(toml), "(?m)^repo\\s*=\\s*\"([^\"]+)\""); if (m.Success) repo = m.Groups[1].Value; }
            }
            catch { }
            string inst = M.P.InstallerExe;
            var gone = PortPatches.Missing(M);
            bool canPatch = File.Exists(PortPatches.ApplyCmd(M));
            s.Children.Add(Section("Updates & repair",
                "Updates come from GitHub" + (repo != null ? " (" + repo + ")" : "") + ". Only the files that changed are downloaded, then whatever they affect is rebuilt." +
                (canPatch ? " Update now also puts the launcher's patches (DLSS, controllers, cheat mods) back afterwards." : ""),
                new UIElement[]
                {
                    Padded(VStack(gone.Count > 0
                                      ? Note("\uE7BA", "An update or repair replaced the port files the launcher patches, so " + PortPatches.Describe(gone) + " missing from the game. Re-apply the patches to put them back (it rebuilds the game, a few minutes).",
                                             "Warn", "WarnSoft", new Thickness(0, 0, 0, 10))
                                      : null,
                                  HStack(Btn("Check for updates", () => RunTool(toolOut, inst, "update --check --root \"" + M.P.Root + "\""), "Btn", "\uE895"),
                                         Btn("Update now", UpdateNow, "Primary", "\uE896"),
                                         canPatch ? Btn("Re-apply launcher patches", ReapplyPatches, gone.Count > 0 ? "Primary" : "Btn", "\uE90F",
                                                        "Puts NVIDIA DLSS, PlayStation / Steam controller support and the cheat mods back into the port and rebuilds the game") : null,
                                         Btn("Check prerequisites", () => RunTool(toolOut, inst, "doctor --root \"" + M.P.Root + "\""), "Btn", "\uE9D9")),
                                  toolOut), 20, 14, 20, 6)
                }));

            // logs
            logView = OutputBox();
            s.Children.Add(Section("Logs",
                "The game writes " + System.IO.Path.GetFileName(M.StderrLog) + " and " + System.IO.Path.GetFileName(M.StdoutLog) + " in " + Rel(M.Port) + " every time it runs.",
                new UIElement[]
                {
                    Padded(VStack(HStack(Btn("Show error log", () => ShowLog(M.StderrLog), "Btn", "\uE7BA"),
                                         Btn("Show output log", () => ShowLog(M.StdoutLog), "Btn", "\uE8A5"),
                                         Btn("Open in Notepad", () => Edit(M.StderrLog), "Btn", "\uE70F"),
                                         Btn("Open folder", () => OpenFolder(M.Port), "Btn", "\uE838")),
                                  logView), 20, 14, 20, 6)
                }));

            // folders
            string dump = M.Ps3GameDir != null ? System.IO.Path.GetDirectoryName(M.Ps3GameDir) : null;
            s.Children.Add(Section("Folders", null, new UIElement[]
            {
                Padded(HStack(Btn("Game folder", () => OpenFolder(M.P.Root), "Btn", "\uE838"),
                              Btn("Port (infamous-recomp)", () => OpenFolder(M.Port), "Btn", "\uE838"),
                              dump != null ? Btn("Disc dump", () => OpenFolder(dump), "Btn", "\uE958") : null,
                              Btn("Launcher", () => OpenFolder(M.P.LauncherDir), "Btn", "\uE838")), 20, 14, 20, 6)
            }));

            s.Children.Add(Section("About", null, new UIElement[]
            {
                Padded(T("inFamous Launcher 1.0. It starts the game the same way inFamous.exe does (installer\\launch.txt), adds your changes from launcher\\launcher.ini and enabled mods, " +
                         "and keeps infamous-recomp\\settings.ini in step with the in-game menu. Removing the launcher folder and this exe leaves the game exactly as the installer made it.", 12.5, "Dim"))
            }));
            return s;
        }

        static TextBox OutputBox()
        {
            var tb = new TextBox
            {
                IsReadOnly = true, FontFamily = Mono, FontSize = 12, Height = 240, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true,
                VerticalContentAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Background = Hex("#080B12"),
                Margin = new Thickness(0, 4, 0, 10)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
            return tb;
        }

        static readonly Regex Ansi = new Regex("\x1B\\[[0-9;?]*[A-Za-z]");

        void RunTool(TextBox output, string exe, string args)
        {
            output.Visibility = Visibility.Visible;
            if (!File.Exists(exe)) { output.Text = exe + " was not found."; return; }
            output.Text = "> " + System.IO.Path.GetFileName(exe) + " " + args + "\n";
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = M.P.Root,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };
                DataReceivedEventHandler on = (s, e) =>
                {
                    if (e.Data == null) return;
                    string line = Ansi.Replace(e.Data, "");
                    Dispatcher.BeginInvoke(new Action(() => { output.AppendText(line + "\n"); output.ScrollToEnd(); }));
                };
                p.OutputDataReceived += on;
                p.ErrorDataReceived += on;
                p.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    output.AppendText("\n[finished" + (p.ExitCode != 0 ? ", exit code " + p.ExitCode : "") + "]\n");
                    output.ScrollToEnd();
                    p.Dispose();
                }));
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception e) { output.AppendText("Could not run it: " + e.Message + "\n"); }
        }

        void UpdateNow()
        {
            if (Running) { MessageBox.Show("Close the game before updating.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!File.Exists(M.P.UpdateCmd)) { MessageBox.Show(M.P.UpdateCmd + " was not found. Run inFamous Setup again.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            // An update restores the port's own copies of the files the launcher patches; update-and-patch.cmd puts the
            // patches (DLSS, controllers, cheats) back right after it.
            bool patch = File.Exists(PortPatches.UpdateCmd(M));
            if (MessageBox.Show("This opens a console window that downloads the latest version of the port from GitHub and rebuilds the game if needed" +
                                (patch ? ", puts the launcher's patches (DLSS / FSR, controllers, cheat mods, language) back and rebuilds this launcher" : "") +
                                ". That can take a while. Reopen the launcher when it's done.\n\nContinue?",
                                "inFamous Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { Process.Start(new ProcessStartInfo(patch ? PortPatches.UpdateCmd(M) : M.P.UpdateCmd) { UseShellExecute = true, WorkingDirectory = M.P.Root }); }
            catch (Exception e) { MessageBox.Show("Could not start the update: " + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        // Re-applies the launcher's port patches and rebuilds the game, in a console window (apply-dlss.cmd).
        void ReapplyPatches()
        {
            if (Running) { MessageBox.Show("Close the game first.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string cmd = PortPatches.ApplyCmd(M);
            if (!File.Exists(cmd)) { MessageBox.Show(cmd + " was not found.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try { Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = PortPatches.Dir(M) }); }
            catch (Exception e) { MessageBox.Show("Could not start it: " + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void BackupSaves()
        {
            string data = System.IO.Path.Combine(M.Port, "gamedata");
            if (!Directory.Exists(data) || Directory.GetFiles(data, "*", SearchOption.AllDirectories).Length == 0)
            { MessageBox.Show("There are no save games yet.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                Directory.CreateDirectory(M.P.BackupDir);
                string zip = System.IO.Path.Combine(M.P.BackupDir, "inFamous saves " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", Inv.C) + ".zip");
                ZipFile.CreateFromDirectory(data, zip, CompressionLevel.Optimal, true);
                lastMessage = "Saves backed up.";
                UpdateBar();
                MessageBox.Show("Saves and trophies backed up to\n" + zip, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception e) { MessageBox.Show("The backup failed: " + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void ClearShaderCache()
        {
            if (Running) { MessageBox.Show("Close the game first; it is using the cache.", "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!Directory.Exists(M.ShaderCache)) return;
            if (MessageBox.Show("Delete every file in " + M.ShaderCache + "?\n\nThe game rebuilds the cache while you play, with some stutter at first.",
                                "inFamous Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int done = 0, failed = 0;
            foreach (var f in Directory.GetFiles(M.ShaderCache, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); done++; } catch { failed++; }
            }
            lastMessage = "Shader cache cleared (" + done + " files" + (failed > 0 ? ", " + failed + " in use" : "") + ").";
            RebuildPage();
        }

        void ShowLog(string path)
        {
            logView.Visibility = Visibility.Visible;
            if (!File.Exists(path)) { logView.Text = path + " doesn't exist yet. It is written when the game runs."; return; }
            try
            {
                const int max = 128 * 1024;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - max);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int n = 0;
                    while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
                    string text = Encoding.UTF8.GetString(buf, 0, n);
                    if (start > 0)
                    {
                        int nl = text.IndexOf('\n');
                        if (nl >= 0) text = text.Substring(nl + 1);
                        text = "… showing the last " + Inv.Size(max) + " of " + Inv.Size(fs.Length) + " (" + System.IO.Path.GetFileName(path) + ")\n\n" + text;
                    }
                    logView.Text = text;
                    logView.ScrollToEnd();
                }
            }
            catch (Exception e) { logView.Text = "Could not read " + path + ": " + e.Message; }
        }
    }

    // A small dark text prompt.
    static class InputBox
    {
        public static string Ask(Window owner, string title, string prompt, string initial)
        {
            var w = new Window
            {
                Title = title, Owner = owner, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = (Brush)Application.Current.Resources["Card"], Foreground = (Brush)Application.Current.Resources["Text"],
                FontFamily = owner.FontFamily, FontSize = 13, Icon = owner.Icon
            };
            var tb = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 18) };
            string result = null;
            var ok = new Button { Style = (Style)Application.Current.Resources["Primary"], Content = "Create", IsDefault = true, MinWidth = 96 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => { result = tb.Text; w.DialogResult = true; };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            body.Children.Add(new TextBlock { Text = prompt, Foreground = (Brush)Application.Current.Resources["Dim"] });
            body.Children.Add(tb);
            body.Children.Add(buttons);
            w.Content = body;
            w.SourceInitialized += (s, e) => MainWindow.ApplyDarkTitleBar(w);
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? result : null;
        }
    }
}
