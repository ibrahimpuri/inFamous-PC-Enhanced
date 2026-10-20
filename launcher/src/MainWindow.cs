// inFamous Launcher: window shell, navigation, the play bar and the game process.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InfamousLauncher
{
    sealed partial class MainWindow : Window
    {
        readonly Model M;
        readonly Dictionary<string, RadioButton> nav = new Dictionary<string, RadioButton>();
        readonly ContentControl host = new ContentControl();
        readonly List<Action> refreshers = new List<Action>();   // per-row state for the page on screen
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        string page;
        ScrollViewer pageScroll;
        RunningGame game;
        bool weKilled;
        string lastMessage;
        DateTime launchStamp;
        ImageSource heroImage, logoImage;

        // play bar + the hero's copy of it
        Border bar;
        Ellipse barDot;
        TextBlock barText;
        Button barPlay, barStop, heroPlay;
        TextBlock heroStatus;
        Ellipse heroDot;

        public MainWindow(Model model)
        {
            M = model;
            Title = "inFamous Launcher";
            Width = 1220; Height = 800; MinWidth = 1000; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = B("Bg");
            Foreground = B("Text");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Icon = LoadIcon();
            LoadArt();
            launchStamp = Stamp(M.P.LaunchTxt);

            Content = BuildShell();
            SourceInitialized += (s, e) => ApplyDarkTitleBar(this);
            Activated += (s, e) => OnActivated();
            Closing += (s, e) => { saveTimer.Stop(); TrySave(); Sdl.Stop(); };   // Sdl.Stop: gives a Steam Controller back its mouse mode

            saveTimer.Tick += (s, e) => { saveTimer.Stop(); TrySave(); };
            tick.Tick += (s, e) => OnTick();
            tick.Start();

            if (M.Spec != null) game = RunningGame.FindExisting(M.ResolveExe() ?? "inFamousRecomp.exe");
            Navigate(nav.ContainsKey(M.Cfg.LastPage ?? "") ? M.Cfg.LastPage : "home");
            UpdateBar();
        }

        // ------------------------------------------------------------------------------------------------
        // Resources and small UI factories
        // ------------------------------------------------------------------------------------------------
        static Brush B(string k) { return (Brush)Application.Current.Resources[k]; }
        static Style S(string k) { return (Style)Application.Current.Resources[k]; }
        static FontFamily IconFont { get { return (FontFamily)Application.Current.Resources["Icons"]; } }
        static FontFamily Mono { get { return (FontFamily)Application.Current.Resources["Mono"]; } }
        static SolidColorBrush Hex(string h) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); }

        static TextBlock T(string text, double size = 13, string brush = "Text", FontWeight? weight = null)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        }
        static TextBlock Glyph(string g, double size = 16, string brush = "Text")
        {
            var t = new TextBlock { Text = g, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            if (brush != null) t.Foreground = B(brush);   // null: inherit (button / nav text colour)
            return t;
        }
        static StackPanel HStack(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }
        static StackPanel VStack(params UIElement[] items)
        {
            var p = new StackPanel();
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }
        static UIElement IconText(string glyph, string text, double size = 13)
        {
            return HStack(Glyph(glyph, size + 1, null), new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        static Button Btn(string text, Action click, string style = "Btn", string glyph = null, string tip = null)
        {
            var b = new Button { Style = S(style), Content = glyph != null ? IconText(glyph, text) : (object)text, Margin = new Thickness(0, 0, 8, 8) };
            if (tip != null) b.ToolTip = tip;
            b.Click += (s, e) => click();
            return b;
        }
        static Border Chip(string text, string fg, string bg)
        {
            return new Border
            {
                Background = B(bg), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(fg) }
            };
        }
        static Border Card(UIElement body, Thickness? margin = null)
        {
            return new Border
            {
                Background = B("Card"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Margin = margin ?? new Thickness(0, 0, 0, 16), Child = body
            };
        }
        static Border Section(string title, string subtitle, IEnumerable<UIElement> rows)
        {
            var stack = new StackPanel();
            if (title != null)
            {
                var head = VStack(T(title, 15, "Text", FontWeights.SemiBold));
                if (subtitle != null) { var st = T(subtitle, 12.5, "Dim"); st.Margin = new Thickness(0, 3, 0, 0); head.Children.Add(st); }
                head.Margin = new Thickness(20, 16, 20, 10);
                stack.Children.Add(head);
            }
            bool first = true;
            foreach (var r in rows)
            {
                stack.Children.Add(new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(0, first && title == null ? 0 : 1, 0, 0), Child = r });
                first = false;
            }
            return Card(stack);
        }
        static UIElement Padded(UIElement e, double l = 20, double t = 14, double r = 20, double b = 14)
        {
            return new Border { Padding = new Thickness(l, t, r, b), Child = e };
        }
        static UIElement Header(string title, string subtitle)
        {
            var h = VStack(T(title, 28, "Text", FontWeights.SemiBold));
            if (subtitle != null) { var s = T(subtitle, 13.5, "Dim"); s.Margin = new Thickness(0, 4, 0, 0); s.MaxWidth = 760; s.HorizontalAlignment = HorizontalAlignment.Left; h.Children.Add(s); }
            h.Margin = new Thickness(0, 0, 0, 22);
            return h;
        }

        static void Open(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show("Could not open " + path + "\n\n" + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        static void OpenFolder(string dir)
        {
            if (dir != null && !Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
            Open(dir);
        }
        static DateTime Stamp(string f) { try { return File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

        // ------------------------------------------------------------------------------------------------
        // Art: the game's own PS3 menu art from the player's dump (PIC1 background, ICON0 logo), and the exe icon
        // ------------------------------------------------------------------------------------------------
        void LoadArt()
        {
            string dir = M.Ps3GameDir;
            if (dir == null) return;
            heroImage = LoadBitmap(System.IO.Path.Combine(dir, "PIC1.PNG"), 1600);
            logoImage = LoadBitmap(System.IO.Path.Combine(dir, "ICON0.PNG"), 0);
        }
        static BitmapImage LoadBitmap(string path, int decodeWidth)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path);
                if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
        static ImageSource LoadIcon()
        {
            try
            {
                var s = typeof(MainWindow).Assembly.GetManifestResourceStream("InfamousLauncher.icon.ico");
                if (s == null) return null;
                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            catch { return null; }
        }

        public static void ApplyDarkTitleBar(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1, caption = 0x001D120D, text = 0x00F8EEE8, border = 0x00402B21;   // COLORREF = 0x00BBGGRR
                Native.DwmSetWindowAttribute(h, 20, ref on, 4);        // DWMWA_USE_IMMERSIVE_DARK_MODE
                Native.DwmSetWindowAttribute(h, 35, ref caption, 4);   // DWMWA_CAPTION_COLOR (Windows 11)
                Native.DwmSetWindowAttribute(h, 36, ref text, 4);      // DWMWA_TEXT_COLOR
                Native.DwmSetWindowAttribute(h, 34, ref border, 4);    // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        // ------------------------------------------------------------------------------------------------
        // Shell
        // ------------------------------------------------------------------------------------------------
        UIElement BuildShell()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            root.ColumnDefinitions.Add(new ColumnDefinition());

            // ---- sidebar ----
            var side = new DockPanel { LastChildFill = true };
            var sideBorder = new Border { Background = B("Sidebar"), BorderBrush = B("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
            root.Children.Add(sideBorder);

            UIElement brand;
            if (logoImage != null)
            {
                brand = new Border
                {
                    CornerRadius = new CornerRadius(10), Height = 112, Margin = new Thickness(18, 20, 18, 6), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
                    Background = new ImageBrush(logoImage) { Stretch = Stretch.UniformToFill }
                };
            }
            else
            {
                var bolt = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = B("AccentSoft"), Child = Glyph("", 20, "Accent") };
                ((TextBlock)bolt.Child).HorizontalAlignment = HorizontalAlignment.Center;
                var words = VStack(T("inFAMOUS", 19, "Text", FontWeights.Black), T("PC LAUNCHER", 10.5, "Dim", FontWeights.SemiBold));
                words.Margin = new Thickness(12, 0, 0, 0);
                brand = new Border { Margin = new Thickness(22, 26, 22, 18), Child = HStack(bolt, words) };
            }
            var tag = T("PC LAUNCHER  ·  BCES00220", 10.5, "Faint", FontWeights.SemiBold);
            tag.Margin = new Thickness(22, 0, 22, 14);
            var top = VStack(brand, logoImage != null ? tag : null);
            DockPanel.SetDock(top, Dock.Top);
            side.Children.Add(top);

            var foot = T("Changes save automatically and apply the next time the game starts.", 11.5, "Faint");
            foot.Margin = new Thickness(22, 10, 22, 18);
            DockPanel.SetDock(foot, Dock.Bottom);
            side.Children.Add(foot);

            var navStack = new StackPanel();
            AddNav(navStack, "home", "", "Home");
            NavLabel(navStack, "SETTINGS");
            AddNav(navStack, "display", "", "Display");
            AddNav(navStack, "graphics", "", "Graphics");
            AddNav(navStack, "reshade", "", "ReShade");
            AddNav(navStack, "performance", "", "Performance");
            AddNav(navStack, "input", "", "Controls");
            AddNav(navStack, "controller", "\uE7FC", "Controller");
            AddNav(navStack, "audio", "", "Audio");
            NavLabel(navStack, "MORE");
            AddNav(navStack, "mods", "", "Mods");
            AddNav(navStack, "advanced", "", "Advanced");
            AddNav(navStack, "tools", "", "Tools");
            side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = navStack });

            // ---- content + play bar ----
            var main = new Grid();
            main.RowDefinitions.Add(new RowDefinition());
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(main, 1);
            root.Children.Add(main);
            main.Children.Add(host);

            barDot = new Ellipse { Width = 9, Height = 9, Fill = B("Accent"), VerticalAlignment = VerticalAlignment.Center };
            barText = new TextBlock { Foreground = B("Dim"), FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            barStop = Btn("Stop game", StopGame, "Danger", "");
            barStop.Margin = new Thickness(0, 0, 10, 0);
            barPlay = new Button { Style = S("Primary"), FontSize = 15, Padding = new Thickness(34, 10, 34, 10), Content = IconText("", "PLAY", 15) };
            barPlay.Click += (s, e) => Play();
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition());
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = HStack(barDot, barText);
            left.VerticalAlignment = VerticalAlignment.Center;
            barGrid.Children.Add(left);
            Grid.SetColumn(barStop, 1); barGrid.Children.Add(barStop);
            Grid.SetColumn(barPlay, 2); barGrid.Children.Add(barPlay);
            bar = new Border { Background = B("Sidebar"), BorderBrush = B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(32, 12, 32, 12), Child = barGrid };
            Grid.SetRow(bar, 1);
            main.Children.Add(bar);
            return root;
        }

        void AddNav(Panel p, string key, string glyph, string label)
        {
            var rb = new RadioButton { Style = S("Nav"), GroupName = "nav", Content = IconText(glyph, label, 14) };
            rb.Checked += (s, e) => { if (page != key) Navigate(key); };
            nav[key] = rb;
            p.Children.Add(rb);
        }
        static void NavLabel(Panel p, string text)
        {
            var t = T(text, 10.5, "Faint", FontWeights.SemiBold);
            t.Margin = new Thickness(26, 16, 0, 6);
            p.Children.Add(t);
        }

        void Navigate(string key)
        {
            page = key;
            M.Cfg.LastPage = key;
            QueueSave();
            if (!nav[key].IsChecked.GetValueOrDefault()) nav[key].IsChecked = true;
            BuildPage(0);
        }

        // Rebuild the page on screen, keeping its scroll position (after a reset, a preset or a reload).
        void RebuildPage() { BuildPage(pageScroll != null ? pageScroll.VerticalOffset : 0); }

        List<string> patchesGone = new List<string>();   // PortPatches.Missing, refreshed on page builds and activation
        bool patchAsked;                                  // Play asked about missing patches this session

        void BuildPage(double scroll)
        {
            refreshers.Clear();
            try { patchesGone = PortPatches.Missing(M); } catch { patchesGone = new List<string>(); }
            heroPlay = null; heroStatus = null; heroDot = null;
            UIElement content;
            switch (page)
            {
                case "home": content = HomePage(); break;
                case "display": content = DisplayPage(); break;
                case "graphics": content = GraphicsPage(); break;
                case "reshade": content = ReShadePage(); break;
                case "performance": content = SettingsPage("performance", "Performance", "Frame rate and CPU settings. The CPU defaults come from the port's benchmarks; on a CPU with few cores, turn on Low-end CPU mode.", LowCpuNote()); break;
                case "input": content = SettingsPage("input", "Controls", "Keyboard and mouse. Controllers have their own page."); break;
                case "controller": content = ControllerPage(); break;
                case "audio": content = SettingsPage("audio", "Audio", null); break;
                case "mods": content = ModsPage(); break;
                case "advanced": content = AdvancedPage(); break;
                default: content = ToolsPage(); break;
            }
            var inner = new Border { Padding = new Thickness(40, 34, 40, 30), MaxWidth = 1160, Child = content };
            pageScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = inner, Focusable = false };
            host.Content = pageScroll;
            if (scroll > 0) pageScroll.Dispatcher.BeginInvoke(new Action(() => pageScroll.ScrollToVerticalOffset(scroll)), DispatcherPriority.Loaded);
            bar.Visibility = page == "home" ? Visibility.Collapsed : Visibility.Visible;
            RefreshRows();
            UpdateBar();
        }

        void RefreshRows() { foreach (var r in refreshers) r(); }

        // ------------------------------------------------------------------------------------------------
        // Saving
        // ------------------------------------------------------------------------------------------------
        void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }
        void TrySave()
        {
            try { M.Save(); }
            catch (Exception e) { lastMessage = "Could not save settings: " + e.Message; UpdateBar(); }
        }

        void OnActivated()
        {
            // The in-game menu (F10) or an update may have changed the files while we were in the background.
            bool reload = false;
            if (Stamp(M.P.LaunchTxt) != launchStamp)
            {
                launchStamp = Stamp(M.P.LaunchTxt);
                saveTimer.Stop();
                TrySave();
                M.Spec = null; M.SpecError = null; M.Base.Clear();
                M.Load();
                reload = true;
            }
            else if (M.IniChangedOnDisk()) { M.ReloadIni(); reload = true; }
            try { var g = PortPatches.Missing(M); if (g.Count != patchesGone.Count) { patchesGone = g; reload = true; } } catch { }
            if (reload && page != null && !IsTyping()) RebuildPage();
        }
        bool IsTyping() { return Keyboard.FocusedElement is TextBox; }

        // ------------------------------------------------------------------------------------------------
        // Play / stop
        // ------------------------------------------------------------------------------------------------
        bool Running { get { return game != null && !game.HasExited; } }

        string BlockReason()
        {
            if (M.SpecError != null) return M.SpecError;
            string exe = M.ResolveExe();
            if (exe == null || !File.Exists(exe)) return "The game build is missing (" + exe + "). Run Update inFamous or inFamous Setup to rebuild it.";
            if (M.Elf != null && !File.Exists(M.Elf)) return "The game files are missing (" + M.Elf + "). The installer links your disc dump here; run inFamous Setup again.";
            return null;
        }

        void Play()
        {
            if (Running) return;
            string why = BlockReason();
            if (why != null) { MessageBox.Show(why, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (patchesGone.Count > 0 && !patchAsked)
            {
                patchAsked = true;
                var r = MessageBox.Show("An update or repair replaced the port files the launcher patches, so " + PortPatches.Describe(patchesGone) + " missing from the game.\n\nPut them back now? That rebuilds the game in a console window " +
                                        "(a few minutes); press Play again when it has finished.\n\nNo starts the game without them.",
                                        "inFamous Launcher", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes) { ReapplyPatches(); return; }
                if (r != MessageBoxResult.No) { patchAsked = false; return; }
            }
            try
            {
                saveTimer.Stop();
                M.Save();
                M.WriteSettingsIni();
                if (game != null) game.Dispose();
                weKilled = false;
                string nr = Proxies.Sync(M);   // DLSS 5 (OptiScaler) and ReShade
                if (nr != null) MessageBox.Show(nr, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                game = RunningGame.Start(M.ResolveExe(), M.Spec.Args, M.Spec.Cwd, M.BuildEnvironment(), M.StdoutLog, M.StderrLog, M.Cfg.Priority);
                M.Cfg.LastPlayed = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv.C);
                M.Save();
                lastMessage = null;
            }
            catch (Exception e)
            {
                MessageBox.Show("The game could not be started.\n\n" + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            UpdateBar();
            if (M.Cfg.AfterLaunch == "minimize") WindowState = WindowState.Minimized;
            else if (M.Cfg.AfterLaunch == "close") Close();
        }

        void StopGame()
        {
            if (!Running) return;
            if (MessageBox.Show("Force-close the game? Progress since your last save will be lost.", "inFamous Launcher",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            weKilled = true;
            game.Kill();
        }

        void OnTick()
        {
            if (game != null && game.HasExited)
            {
                var g = game; game = null;
                int code = g.ExitCode;
                var played = DateTime.Now - g.Started;
                if (played.TotalSeconds > 0 && played.TotalDays < 2) { M.Cfg.PlaySeconds += (long)played.TotalSeconds; TrySave(); }
                g.Dispose();
                if (M.IniChangedOnDisk()) { M.ReloadIni(); if (!IsTyping()) RebuildPage(); }
                if (weKilled) lastMessage = "Game stopped.";
                else if (code != 0 && !g.Attached)
                {
                    lastMessage = "The game exited with an error (code " + code + ").";
                    if (MessageBox.Show("The game exited with an error (code " + code + ").\n\nOpen the log file?", "inFamous Launcher",
                                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Open(M.StderrLog);
                }
                else lastMessage = "Game closed after " + Inv.Duration(played) + ".";
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
            UpdateBar();
        }

        string Summary()
        {
            var parts = new List<string>();
            var res = Catalog.Find("RES_SCALE"); var cap = Catalog.Find("UI_FPS_CAP"); var fs = Catalog.Find("FULLSCREEN");
            parts.Add(Label(res, M.Get(res)).Split('·')[0].Trim());
            parts.Add(Label(fs, M.Get(fs)).Replace(" (borderless)", ""));
            string c = M.Get(cap);
            parts.Add(c == "0" ? "uncapped" : Label(cap, c).Replace(" (PS3)", ""));
            var lang = Catalog.Find("PS3_LANG");
            if (!string.IsNullOrEmpty(M.Get(lang))) parts.Add(Label(lang, M.Get(lang)).Split('·')[0].Trim());
            if (Inv.Same(M.Get("FSR"), "1")) { var fm = Catalog.Find("FSR_MODE"); parts.Add("FSR " + Label(fm, M.Get(fm)).Split('·')[0].Trim()); }
            else if (Inv.Same(M.Get("DLSS"), "1")) { var dm = Catalog.Find("DLSS_MODE"); parts.Add("DLSS " + Label(dm, M.Get(dm)).Split('·')[0].Trim()); }
            if (Inv.Same(M.Get("FSR_FG"), "1") && (Inv.Same(M.Get("FSR"), "1") || Inv.Same(M.Get("DLSS"), "1"))) parts.Add("frame generation");
            if (M.Cfg.LowCpu == "1") parts.Add("low-end CPU mode");
            if (ReShadeSetup.Wanted(M)) parts.Add(ReShadeSetup.OwnPreset(M) ? "ReShade" : "ReShade " + ReShadeSetup.LookName(M));
            int mods = M.EnabledMods.Count();
            if (mods > 0) parts.Add(mods + (mods == 1 ? " mod" : " mods"));
            return string.Join(" · ", parts);
        }

        void UpdateBar()
        {
            if (barText == null) return;
            string text, dot; bool canPlay;
            string why = BlockReason();
            if (Running)
            {
                var t = DateTime.Now - game.Started;
                text = "Running · " + ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + " · PID " + game.Pid;
                dot = "Good"; canPlay = false;
            }
            else if (why != null) { text = why; dot = "Bad"; canPlay = false; }
            else { text = (lastMessage != null ? lastMessage + "   " : "") + "Ready · " + Summary(); dot = "Accent"; canPlay = true; }
            if (!Running && why == null && patchesGone.Count > 0) { text = "Launcher patches missing after an update (Tools › Re-apply launcher patches) · " + text; dot = "Warn"; }

            barText.Text = text; barText.ToolTip = text; barDot.Fill = B(dot);
            barPlay.IsEnabled = canPlay;
            barPlay.Content = IconText(Running ? "" : "", Running ? "RUNNING" : "PLAY", 15);
            barStop.Visibility = Running ? Visibility.Visible : Visibility.Collapsed;
            if (heroPlay != null)
            {
                heroPlay.IsEnabled = canPlay;
                heroPlay.Content = IconText(Running ? "" : "", Running ? "RUNNING" : "PLAY", 20);
                heroStop.Visibility = Running ? Visibility.Visible : Visibility.Collapsed;
                heroStatus.Text = text; heroStatus.ToolTip = text; heroDot.Fill = B(dot);
            }
        }
    }
}
