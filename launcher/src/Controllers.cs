// inFamous Launcher: the Controller page -- every connected controller, how the game will read it, and a live view of
// what it sends (with the game's own PS3 mapping), plus vibration / light bar tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InfamousLauncher
{
    // The SDL3.dll the game loads (sdl3\lib\x64). Used on the UI thread only, while the Controller page is open and
    // the game is not running.
    static class Sdl
    {
        const string D = "SDL3.dll";
        const CallingConvention C = CallingConvention.Cdecl;
        public const uint INIT_GAMEPAD = 0x00002000;
        public const int TYPE_PS3 = 4, TYPE_PS4 = 5, TYPE_PS5 = 6, TYPE_SWITCH_PRO = 7, TYPE_XBOX360 = 2, TYPE_XBOXONE = 3, TYPE_STEAM = 12;
        public const int SOUTH = 0, EAST = 1, WEST = 2, NORTH = 3, BACK = 4, GUIDE = 5, START = 6, LSTICK = 7, RSTICK = 8, LSHOULDER = 9,
                         RSHOULDER = 10, UP = 11, DOWN = 12, LEFT = 13, RIGHT = 14, MISC1 = 15, RPADDLE1 = 16, LPADDLE1 = 17, TOUCHPAD = 20;
        public const int AX_LX = 0, AX_LY = 1, AX_RX = 2, AX_RY = 3, AX_LT = 4, AX_RT = 5;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport(D, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
        [DllImport(D, CallingConvention = C)] static extern void SDL_QuitSubSystem(uint flags);
        [DllImport(D, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
        [DllImport(D, CallingConvention = C)] static extern IntPtr SDL_GetGamepads(out int count);
        [DllImport(D, CallingConvention = C)] static extern void SDL_free(IntPtr p);
        [DllImport(D, CallingConvention = C)] static extern IntPtr SDL_OpenGamepad(uint id);
        [DllImport(D, CallingConvention = C)] static extern void SDL_CloseGamepad(IntPtr gp);
        [DllImport(D, CallingConvention = C)] static extern void SDL_UpdateGamepads();
        [DllImport(D, CallingConvention = C)] static extern IntPtr SDL_GetGamepadName(IntPtr gp);
        [DllImport(D, CallingConvention = C)] public static extern int SDL_GetGamepadType(IntPtr gp);
        [DllImport(D, CallingConvention = C)] public static extern int SDL_GetGamepadConnectionState(IntPtr gp);
        [DllImport(D, CallingConvention = C)] public static extern int SDL_GetGamepadPowerInfo(IntPtr gp, out int percent);
        [DllImport(D, CallingConvention = C)] public static extern ushort SDL_GetGamepadVendor(IntPtr gp);
        [DllImport(D, CallingConvention = C)] public static extern ushort SDL_GetGamepadProduct(IntPtr gp);
        [DllImport(D, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SDL_GetGamepadButton(IntPtr gp, int button);
        [DllImport(D, CallingConvention = C)] public static extern short SDL_GetGamepadAxis(IntPtr gp, int axis);
        [DllImport(D, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SDL_RumbleGamepad(IntPtr gp, ushort low, ushort high, uint ms);
        [DllImport(D, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SDL_SetGamepadLED(IntPtr gp, byte r, byte g, byte b);

        static int loaded;   // 0 not tried, 1 loaded, -1 missing
        static bool running;
        static readonly Dictionary<uint, IntPtr> open = new Dictionary<uint, IntPtr>();

        public static string DllPath(GamePaths p) { return System.IO.Path.Combine(p.Root, "sdl3", "lib", "x64", "SDL3.dll"); }
        public static bool Available(GamePaths p)
        {
            if (loaded == 0) loaded = File.Exists(DllPath(p)) && LoadLibraryW(DllPath(p)) != IntPtr.Zero ? 1 : -1;
            return loaded > 0;
        }

        public static bool Start(GamePaths p)
        {
            if (running) return true;
            if (!Available(p)) return false;
            SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");   // the game reads Steam Controllers too (unless switched off)
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");        // Xbox pads once, through XInput, as in the game
            SDL_SetHint("SDL_JOYSTICK_WGI", "0");
            running = SDL_Init(INIT_GAMEPAD);
            return running;
        }

        public static void Stop()
        {
            if (!running) return;
            foreach (var gp in open.Values) SDL_CloseGamepad(gp);
            open.Clear();
            SDL_QuitSubSystem(INIT_GAMEPAD);
            running = false;
        }

        // Pumps SDL and returns the connected gamepads (opened), in connection order.
        public static List<KeyValuePair<uint, IntPtr>> Poll()
        {
            var list = new List<KeyValuePair<uint, IntPtr>>();
            if (!running) return list;
            SDL_UpdateGamepads();
            int n;
            IntPtr ids = SDL_GetGamepads(out n);
            var seen = new HashSet<uint>();
            for (int i = 0; i < n && ids != IntPtr.Zero; i++)
            {
                uint id = (uint)Marshal.ReadInt32(ids, i * 4);
                seen.Add(id);
                IntPtr gp;
                if (!open.TryGetValue(id, out gp)) { gp = SDL_OpenGamepad(id); if (gp == IntPtr.Zero) continue; open[id] = gp; }
                list.Add(new KeyValuePair<uint, IntPtr>(id, gp));
            }
            if (ids != IntPtr.Zero) SDL_free(ids);
            foreach (var gone in open.Keys.Where(k => !seen.Contains(k)).ToList()) { SDL_CloseGamepad(open[gone]); open.Remove(gone); }
            return list;
        }

        public static string Name(IntPtr gp)
        {
            IntPtr s = SDL_GetGamepadName(gp);
            if (s == IntPtr.Zero) return "Controller";
            int len = 0;
            while (Marshal.ReadByte(s, len) != 0) len++;
            var b = new byte[len];
            Marshal.Copy(s, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        public static bool IsXbox(int type) { return type == TYPE_XBOX360 || type == TYPE_XBOXONE; }
        public static bool IsSony(int type) { return type == TYPE_PS3 || type == TYPE_PS4 || type == TYPE_PS5; }
        public static string TypeName(IntPtr gp, int type)
        {
            ushort vid = SDL_GetGamepadVendor(gp), pid = SDL_GetGamepadProduct(gp);
            switch (type)
            {
                case TYPE_PS5: return vid == 0x054C && pid == 0x0DF2 ? "DualSense Edge" : "DualSense";
                case TYPE_PS4: return "DualShock 4";
                case TYPE_PS3: return "DualShock 3";
                case TYPE_STEAM: return "Steam Controller";
                case TYPE_SWITCH_PRO: return "Switch Pro Controller";
                case TYPE_XBOX360: case TYPE_XBOXONE: return "Xbox controller";
                default: return "Gamepad";
            }
        }
    }

    sealed partial class MainWindow
    {
        // One controller on the page: its card and the shapes the live view updates.
        sealed class PadPanel
        {
            public uint Id;
            public IntPtr Gp;
            public int Type;
            public Border Card;
            public TextBlock Status, Battery;
            public readonly Dictionary<int, Shape> Buttons = new Dictionary<int, Shape>();   // CELL_PAD-style keys below
            public Rectangle L2Fill, R2Fill;
            public Ellipse LDot, RDot, LRing, RRing, LDead, RDead;
            public int LedStep;
        }

        // keys of PadPanel.Buttons (the PS3 buttons the game receives)
        const int PB_CROSS = 0, PB_CIRCLE = 1, PB_SQUARE = 2, PB_TRIANGLE = 3, PB_SELECT = 4, PB_START = 5, PB_L1 = 6, PB_R1 = 7, PB_L2 = 8, PB_R2 = 9,
                  PB_UP = 10, PB_DOWN = 11, PB_LEFT = 12, PB_RIGHT = 13, PB_PS = 14, PB_TOUCH = 15;
        const double StickR = 26;

        DispatcherTimer padTimer;
        readonly List<PadPanel> padPanels = new List<PadPanel>();
        StackPanel padList;
        TextBlock padEmpty;
        StackPanel padTips;
        DateTime padTipsAt;

        UIElement ControllerPage()
        {
            padPanels.Clear();
            padList = new StackPanel();
            padEmpty = T("", 13, "Dim");
            padEmpty.Margin = new Thickness(20, 4, 20, 16);
            padTips = new StackPanel { Margin = new Thickness(20, 0, 20, 8) };
            var live = Section("Connected controllers",
                               "Live: press buttons and move the sticks to see exactly what the game will receive (shown with the PS3 buttons it gets). " +
                               "PlayStation and Steam controllers are read through SDL3, Xbox controllers through XInput.",
                               new UIElement[] { padList, padEmpty, padTips });
            if (padTimer == null)
            {
                padTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                padTimer.Tick += (s, e) => PadTick();
            }
            padTipsAt = DateTime.MinValue;
            padTimer.Start();
            PadTick();
            return SettingsPage("controller", "Controller",
                                "DualSense, DualSense Edge, DualShock 4, Steam Controller and Xbox controllers work by USB or Bluetooth. " +
                                "The game shows PlayStation button prompts.", live);
        }

        void PadTick()
        {
            if (page != "controller" || padList == null)
            {
                padTimer.Stop();
                Sdl.Stop();
                return;
            }
            if (Running)
            {
                Sdl.Stop();
                if (padPanels.Count > 0) { padPanels.Clear(); padList.Children.Clear(); }
                padEmpty.Text = "The game is running and reading the controllers. The live view comes back when it closes.";
                padEmpty.Visibility = Visibility.Visible;
                return;
            }
            if (!Sdl.Start(M.P))
            {
                padEmpty.Text = Sdl.Available(M.P)
                    ? "SDL3 could not start, so controllers can't be shown here."
                    : "SDL3 is missing (" + Sdl.DllPath(M.P) + "). Without it the game reads Xbox controllers only.";
                padEmpty.Visibility = Visibility.Visible;
                return;
            }
            var pads = Sdl.Poll();
            foreach (var gone in padPanels.Where(p => !pads.Any(x => x.Key == p.Id)).ToList()) { padList.Children.Remove(gone.Card); padPanels.Remove(gone); }
            foreach (var kv in pads)
                if (!padPanels.Any(p => p.Id == kv.Key)) { var p = NewPadPanel(kv.Key, kv.Value); padPanels.Add(p); padList.Children.Add(p.Card); }
            padEmpty.Text = pads.Count == 0 ? "No controller found. Connect a DualSense, DualShock 4, Steam Controller or Xbox controller by USB or Bluetooth." : "";
            padEmpty.Visibility = pads.Count == 0 || padEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // which player each one is in the game: XInput pads take the first ports, the others follow in order
            bool sdlOn = !Inv.Same(M.Get("PAD_SDL"), "0"), steamOn = !Inv.Same(M.Get("PAD_STEAM_CONTROLLER"), "0");
            int player = 0;
            foreach (var p in padPanels.Where(x => Sdl.IsXbox(x.Type)).Concat(padPanels.Where(x => !Sdl.IsXbox(x.Type))))
            {
                string how;
                if (Sdl.IsXbox(p.Type)) how = "Player " + (++player) + " in the game · read through XInput";
                else if (!sdlOn) how = "Not used by the game: PlayStation and Steam controllers are switched off below";
                else if (p.Type == Sdl.TYPE_STEAM && !steamOn) how = "Not read directly: Steam Controller reading is off below (use it through Steam Input)";
                else how = "Player " + (++player) + " in the game · read through SDL3";
                if (p.Status.Text != how) p.Status.Text = how;
                UpdatePadPanel(p);
            }
            if (DateTime.Now - padTipsAt > TimeSpan.FromSeconds(3)) { padTipsAt = DateTime.Now; UpdatePadTips(); }
        }

        void UpdatePadTips()
        {
            padTips.Children.Clear();
            var procs = new HashSet<string>(Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }));
            bool steam = procs.Contains("steam");
            if (steam && padPanels.Any(p => p.Type == Sdl.TYPE_STEAM))
                padTips.Children.Add(Note("", "Steam is running, and Steam reads the Steam Controller too: outside a Steam game it sends mouse and keyboard input. " +
                    "Either close Steam before playing, or add the game to Steam as a non-Steam game and play it through Steam Input " +
                    "(it then appears as an Xbox controller; switch 'Read Steam Controllers directly' off).", "Warn", "WarnSoft", new Thickness(0, 0, 0, 8)));
            if (procs.Contains("ds4windows") || procs.Contains("dsx") || procs.Contains("dualsensex"))
                padTips.Children.Add(Note("", "DS4Windows / DSX is running. It adds a virtual Xbox controller on top of your PlayStation controller, so the game " +
                    "would see two. Close it while playing, or turn on its option to hide the real controller.", "Warn", "WarnSoft", new Thickness(0, 0, 0, 8)));
        }

        PadPanel NewPadPanel(uint id, IntPtr gp)
        {
            var p = new PadPanel { Id = id, Gp = gp, Type = Sdl.SDL_GetGamepadType(gp) };
            string type = Sdl.TypeName(gp, p.Type), name = Sdl.Name(gp);
            int conn = Sdl.SDL_GetGamepadConnectionState(gp);
            string link = conn == 1 ? "USB" : conn == 2 ? (Sdl.IsSony(p.Type) ? "Bluetooth" : "Wireless") : null;

            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = B("AccentSoft"), Child = Glyph("", 20, "Accent"),
                                    VerticalAlignment = VerticalAlignment.Top };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var title = HStack(T(name, 15, "Text", FontWeights.SemiBold), Chip(type.ToUpperInvariant(), "AccentHi", "AccentSoft"));
            if (link != null) title.Children.Add(Chip(link.ToUpperInvariant(), "Dim", "CardHi"));
            p.Battery = T("", 12.5, "Dim");
            p.Status = T("", 12.5, "Dim");
            var head = VStack(title, p.Status, p.Battery);
            head.Margin = new Thickness(14, 0, 0, 0);
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.Children.Add(icon);
            Grid.SetColumn(head, 1);
            top.Children.Add(head);

            var buttons = HStack(Btn("Vibrate", () => Sdl.SDL_RumbleGamepad(p.Gp, 0xC000, 0xC000, 450), "Btn", ""));
            if (Sdl.IsSony(p.Type))
                buttons.Children.Add(Btn("Light bar", () =>
                {
                    var cols = new[] { new byte[] { 0, 110, 255 }, new byte[] { 255, 10, 0 }, new byte[] { 160, 32, 240 }, new byte[] { 255, 255, 255 }, new byte[] { 0, 0, 0 } };
                    var c = cols[p.LedStep++ % cols.Length];
                    Sdl.SDL_SetGamepadLED(p.Gp, c[0], c[1], c[2]);
                }, "Btn", "", "Cycles blue, red, purple, white and off. The game sets its own colour when it starts."));
            buttons.Margin = new Thickness(0, 12, 0, 0);

            var body = new Grid { Margin = new Thickness(18, 14, 18, 10) };
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = VStack(top, buttons);
            body.Children.Add(left);
            var diagram = PadDiagram(p);
            Grid.SetColumn(diagram, 1);
            body.Children.Add(diagram);
            p.Card = new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = body };
            return p;
        }

        // A small controller drawn with shapes; PadPanel keeps the ones that light up.
        UIElement PadDiagram(PadPanel p)
        {
            var c = new Canvas { Width = 440, Height = 190 };
            Func<double, double, double, double, double, Rectangle> rect = (x, y, w, h, r) =>
            {
                var s = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = B("CardHi"), Stroke = B("LineHi"), StrokeThickness = 1 };
                Canvas.SetLeft(s, x); Canvas.SetTop(s, y); c.Children.Add(s); return s;
            };
            Func<double, double, double, Ellipse> circle = (cx, cy, r) =>
            {
                var s = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = B("CardHi"), Stroke = B("LineHi"), StrokeThickness = 1 };
                Canvas.SetLeft(s, cx - r); Canvas.SetTop(s, cy - r); c.Children.Add(s); return s;
            };
            Action<string, double, double, double> label = (t, cx, cy, size) =>
            {
                var tb = new TextBlock { Text = t, FontSize = size, Foreground = B("Text"), IsHitTestVisible = false };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2); Canvas.SetTop(tb, cy - tb.DesiredSize.Height / 2); c.Children.Add(tb);
            };
            // body outline
            var outline = new Rectangle { Width = 420, Height = 150, RadiusX = 60, RadiusY = 60, Stroke = B("Line"), StrokeThickness = 1.5, Fill = Brushes.Transparent };
            Canvas.SetLeft(outline, 10); Canvas.SetTop(outline, 36); c.Children.Add(outline);
            // shoulders and triggers
            rect(40, 2, 80, 12, 4); p.L2Fill = rect(40, 2, 0, 12, 4); p.L2Fill.Stroke = null; p.Buttons[PB_L2] = p.L2Fill; label("L2", 80, 8, 9);
            p.Buttons[PB_L1] = rect(40, 18, 80, 14, 5); label("L1", 80, 25, 10);
            rect(320, 2, 80, 12, 4); p.R2Fill = rect(320, 2, 0, 12, 4); p.R2Fill.Stroke = null; p.Buttons[PB_R2] = p.R2Fill; label("R2", 360, 8, 9);
            p.Buttons[PB_R1] = rect(320, 18, 80, 14, 5); label("R1", 360, 25, 10);
            // d-pad
            p.Buttons[PB_UP] = rect(77, 62, 18, 22, 3); p.Buttons[PB_DOWN] = rect(77, 102, 18, 22, 3);
            p.Buttons[PB_LEFT] = rect(53, 84, 24, 18, 3); p.Buttons[PB_RIGHT] = rect(95, 84, 24, 18, 3);
            // touchpad, select / start, PS
            p.Buttons[PB_TOUCH] = rect(170, 44, 100, 40, 8);
            p.Buttons[PB_SELECT] = rect(146, 52, 18, 12, 6); label("SEL", 155, 72, 8);
            p.Buttons[PB_START] = rect(276, 52, 18, 12, 6); label("START", 285, 72, 8);
            p.Buttons[PB_PS] = circle(220, 106, 9);
            // face buttons (PS3 symbols: what the game gets)
            p.Buttons[PB_TRIANGLE] = circle(360, 66, 13); label("△", 360, 66, 13);
            p.Buttons[PB_CIRCLE] = circle(388, 93, 13); label("○", 388, 93, 14);
            p.Buttons[PB_CROSS] = circle(360, 120, 13); label("✕", 360, 120, 12);
            p.Buttons[PB_SQUARE] = circle(332, 93, 13); label("□", 332, 93, 14);
            // sticks: ring, deadzone ring, dot
            p.LRing = circle(165, 148, StickR); p.LDead = circle(165, 148, 4); p.LDead.Fill = Brushes.Transparent; p.LDead.StrokeDashArray = new DoubleCollection { 2, 2 };
            p.LDot = circle(165, 148, 9); p.LDot.Fill = B("Dim"); p.LDot.Stroke = null;
            p.RRing = circle(275, 148, StickR); p.RDead = circle(275, 148, 4); p.RDead.Fill = Brushes.Transparent; p.RDead.StrokeDashArray = new DoubleCollection { 2, 2 };
            p.RDot = circle(275, 148, 9); p.RDot.Fill = B("Dim"); p.RDot.Stroke = null;
            return c;
        }

        void UpdatePadPanel(PadPanel p)
        {
            IntPtr g = p.Gp;
            Func<int, bool> b = k => Sdl.SDL_GetGamepadButton(g, k);
            bool touchSel = !Inv.Same(M.Get("PAD_TOUCHPAD"), "0") && !Sdl.IsXbox(p.Type);
            int lt = Math.Max((int)Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LT), 0), rt = Math.Max((int)Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RT), 0);
            var on = new Dictionary<int, bool>
            {
                { PB_CROSS, b(Sdl.SOUTH) }, { PB_CIRCLE, b(Sdl.EAST) }, { PB_SQUARE, b(Sdl.WEST) }, { PB_TRIANGLE, b(Sdl.NORTH) },
                { PB_SELECT, b(Sdl.BACK) || (touchSel && b(Sdl.TOUCHPAD)) }, { PB_START, b(Sdl.START) },
                { PB_L1, b(Sdl.LSHOULDER) }, { PB_R1, b(Sdl.RSHOULDER) }, { PB_L2, lt * 255 / 32767 > 30 }, { PB_R2, rt * 255 / 32767 > 30 },
                { PB_UP, b(Sdl.UP) }, { PB_DOWN, b(Sdl.DOWN) }, { PB_LEFT, b(Sdl.LEFT) }, { PB_RIGHT, b(Sdl.RIGHT) },
                { PB_PS, b(Sdl.GUIDE) }, { PB_TOUCH, b(Sdl.TOUCHPAD) },
            };
            foreach (var kv in on)
            {
                if (kv.Key == PB_L2 || kv.Key == PB_R2) continue;
                var want = kv.Value ? B("Accent") : B("CardHi");
                if (p.Buttons[kv.Key].Fill != want) p.Buttons[kv.Key].Fill = want;
            }
            p.L2Fill.Width = 80.0 * lt / 32767.0; p.L2Fill.Fill = on[PB_L2] ? B("Accent") : B("AccentSoft");
            p.R2Fill.Width = 80.0 * rt / 32767.0; p.R2Fill.Fill = on[PB_R2] ? B("Accent") : B("AccentSoft");

            double dz = 0;
            if (!Sdl.IsXbox(p.Type)) { double v; if (Inv.TryNum(M.Get("PAD_DEADZONE"), out v)) dz = v / 100.0; }
            Stick(p.LDot, p.LDead, 165, 148, Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LX), Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LY), dz,
                  b(Sdl.LSTICK) || (!Sdl.IsXbox(p.Type) && b(Sdl.LPADDLE1)));
            Stick(p.RDot, p.RDead, 275, 148, Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RX), Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RY), dz,
                  b(Sdl.RSTICK) || (!Sdl.IsXbox(p.Type) && b(Sdl.RPADDLE1)));

            int pct;
            int ps = Sdl.SDL_GetGamepadPowerInfo(g, out pct);   // 1 battery, 2 none, 3 charging, 4 charged
            string bat = ps == 1 ? "Battery " + (pct >= 0 ? pct + " %" : "in use") : ps == 3 ? "Charging" + (pct >= 0 ? ", " + pct + " %" : "") : ps == 4 ? "Charged" : "";
            if (p.Battery.Text != bat) { p.Battery.Text = bat; p.Battery.Visibility = bat.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        static void Stick(Ellipse dot, Ellipse dead, double cx, double cy, short ax, short ay, double dz, bool click)
        {
            double x = ax / 32767.0, y = ay / 32767.0, m = Math.Sqrt(x * x + y * y);
            if (m > 1) { x /= m; y /= m; }
            Canvas.SetLeft(dot, cx + x * (StickR - 6) - 9);
            Canvas.SetTop(dot, cy + y * (StickR - 6) - 9);
            dot.Fill = click ? B("Accent") : m > dz && m > 0.02 ? B("AccentHi") : B("Dim");
            double r = Math.Max(2, StickR * dz);
            dead.Width = dead.Height = 2 * r;
            Canvas.SetLeft(dead, cx - r); Canvas.SetTop(dead, cy - r);
            dead.Visibility = dz > 0 ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
