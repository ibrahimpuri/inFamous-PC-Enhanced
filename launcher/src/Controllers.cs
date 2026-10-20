// inFamous Launcher: the Controller page -- every connected controller, how the game will read it, and a live view of
// what it sends (with the game's own PS3 mapping), plus vibration / light bar tests, what to do when Windows sees a
// controller the game can't read, and a report to copy when asking for help.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InfamousLauncher
{
    // The SDL3.dll the game loads (sdl3\lib\x64), with the game's own driver set (pad_sdl3.c): PlayStation, Steam and
    // Switch pads only -- Xbox and other XInput pads are read through XInput, as in the game. Used on the UI thread
    // only, while the Controller page is open and the game is not running.
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
        [DllImport(D, CallingConvention = C)] static extern IntPtr SDL_GetGamepadNameForID(uint id);
        [DllImport(D, CallingConvention = C)] static extern IntPtr SDL_GetError();
        [DllImport(D, CallingConvention = C)] public static extern int SDL_GetGamepadType(IntPtr gp);
        [DllImport(D, CallingConvention = C)] public static extern int SDL_GetGamepadTypeForID(uint id);
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
        public static readonly Dictionary<uint, string> Failed = new Dictionary<uint, string>();   // pads SDL sees but can't open: name + reason

        public static string DllPath(GamePaths p) { return System.IO.Path.Combine(p.Root, "sdl3", "lib", "x64", "SDL3.dll"); }
        public static bool Available(GamePaths p)
        {
            if (loaded == 0) loaded = File.Exists(DllPath(p)) && LoadLibraryW(DllPath(p)) != IntPtr.Zero ? 1 : -1;
            return loaded > 0;
        }

        public static bool Start(GamePaths p, bool steam)
        {
            if (running) return true;
            if (!Available(p)) return false;
            // exactly the game's drivers (pad_sdl3.c start())
            SDL_SetHint("SDL_XINPUT_ENABLED", "0");
            SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            SDL_SetHint("SDL_JOYSTICK_WGI", "0");
            SDL_SetHint("SDL_JOYSTICK_GAMEINPUT", "0");
            SDL_SetHint("SDL_JOYSTICK_HIDAPI_XBOX", "0");
            SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
            SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5", "1");
            SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", steam ? "1" : "0");
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            running = SDL_Init(INIT_GAMEPAD);
            return running;
        }

        public static void Stop()
        {
            if (!running) return;
            foreach (var gp in open.Values) SDL_CloseGamepad(gp);
            open.Clear();
            Failed.Clear();
            SDL_QuitSubSystem(INIT_GAMEPAD);
            running = false;
        }

        // Pumps SDL and returns the connected gamepads the game reads through SDL (opened), in connection order.
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
                if (IsXbox(SDL_GetGamepadTypeForID(id))) continue;   // the game leaves those to XInput
                IntPtr gp;
                if (!open.TryGetValue(id, out gp))
                {
                    gp = SDL_OpenGamepad(id);
                    if (gp == IntPtr.Zero) { if (!Failed.ContainsKey(id)) Failed[id] = Str(SDL_GetGamepadNameForID(id)) + ": " + Str(SDL_GetError()); continue; }
                    open[id] = gp;
                    Failed.Remove(id);
                }
                list.Add(new KeyValuePair<uint, IntPtr>(id, gp));
            }
            if (ids != IntPtr.Zero) SDL_free(ids);
            foreach (var gone in open.Keys.Where(k => !seen.Contains(k)).ToList()) { SDL_CloseGamepad(open[gone]); open.Remove(gone); }
            foreach (var gone in Failed.Keys.Where(k => !seen.Contains(k)).ToList()) Failed.Remove(gone);
            return list;
        }

        static string Str(IntPtr s)
        {
            if (s == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(s, len) != 0) len++;
            var b = new byte[len];
            Marshal.Copy(s, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }
        public static string Name(IntPtr gp) { string n = Str(SDL_GetGamepadName(gp)); return n.Length > 0 ? n : "Controller"; }

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
                default: return "Gamepad";
            }
        }
    }

    // XInput, the way the game reads Xbox controllers (cellPad: XInput slot N is host port N). Also every virtual pad
    // that presents itself as one: Steam Input's, DS4Windows' / DSX's (ViGEm), reWASD's.
    static class XInput
    {
        [StructLayout(LayoutKind.Sequential)] public struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)] public struct State { public uint Packet; public Pad Gamepad; }
        [StructLayout(LayoutKind.Sequential)] public struct Vibration { public ushort Left, Right; }
        [StructLayout(LayoutKind.Sequential)] public struct Caps { public byte Type, SubType; public ushort Flags; public Pad Gamepad; public Vibration Vib; }
        [StructLayout(LayoutKind.Sequential)] public struct CapsEx { public Caps Caps; public ushort VendorId, ProductId, ProductVersion, Unknown1; public uint Unknown2; }
        [StructLayout(LayoutKind.Sequential)] public struct Battery { public byte Type, Level; }
        const string D = "xinput1_4.dll";
        [DllImport(D)] public static extern uint XInputGetState(uint slot, out State s);
        [DllImport(D)] public static extern uint XInputSetState(uint slot, ref Vibration v);
        [DllImport(D)] static extern uint XInputGetCapabilities(uint slot, uint flags, out Caps c);
        [DllImport(D, EntryPoint = "#108")] static extern uint XInputGetCapabilitiesEx(uint one, uint slot, uint flags, out CapsEx c);   // undocumented: adds VID / PID
        [DllImport(D)] public static extern uint XInputGetBatteryInformation(uint slot, byte devType, out Battery b);
        public const ushort UP = 0x1, DOWN = 0x2, LEFT = 0x4, RIGHT = 0x8, START = 0x10, BACK = 0x20, LTHUMB = 0x40, RTHUMB = 0x80,
                            LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
        public const short Deadzone = 7849;   // the game's XInput stick deadzone (cellPad PAD_STICK_DEADZONE)

        static readonly DateTime[] nextProbe = new DateTime[4];
        static readonly bool[] connected = new bool[4];

        // Connected slots; an empty slot is probed again only once a second (probing one is slow, as in the game).
        public static List<int> Poll()
        {
            var list = new List<int>();
            for (uint i = 0; i < 4; i++)
            {
                if (!connected[i] && DateTime.Now < nextProbe[i]) continue;
                State s;
                bool ok;
                try { ok = XInputGetState(i, out s) == 0; } catch { return list; }   // no xinput1_4.dll
                connected[i] = ok;
                if (ok) list.Add((int)i); else nextProbe[i] = DateTime.Now.AddSeconds(1);
            }
            return list;
        }

        public static bool Read(int slot, out Pad p)
        {
            State s;
            bool ok = XInputGetState((uint)slot, out s) == 0;
            p = s.Gamepad;
            return ok;
        }

        // (name, wireless) from the capabilities; VID / PID tell Xbox pads from Steam Input's virtual one
        public static void Describe(int slot, out string name, out string chip, out bool wireless, out ushort vid, out ushort pid)
        {
            name = "XInput controller"; chip = "XINPUT"; wireless = false; vid = pid = 0;
            try
            {
                CapsEx cx;
                if (XInputGetCapabilitiesEx(1, (uint)slot, 0, out cx) == 0)
                {
                    vid = cx.VendorId; pid = cx.ProductId; wireless = (cx.Caps.Flags & 0x0002) != 0;
                    if (vid == 0x28DE) { name = "Steam Input controller"; chip = "VIRTUAL · STEAM"; return; }
                    if (vid == 0x045E) { name = pid == 0x028E || pid == 0x0719 ? "Xbox 360 Controller" : "Xbox Controller"; chip = "XBOX"; }
                    if (cx.Caps.SubType != 1) { var sub = SubType(cx.Caps.SubType); if (sub != null) { name = sub; chip = "XINPUT"; } }
                    return;
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { return; }
            Caps c;
            if (XInputGetCapabilities((uint)slot, 0, out c) == 0)
            {
                wireless = (c.Flags & 0x0002) != 0;
                var sub = SubType(c.SubType);
                if (sub != null && c.SubType != 1) name = sub; else { name = "Xbox Controller"; chip = "XBOX"; }
            }
        }
        static string SubType(byte t)
        {
            switch (t)
            {
                case 1: return "Gamepad";
                case 2: return "Racing wheel";
                case 3: return "Arcade stick";
                case 4: return "Flight stick";
                case 5: return "Dance pad";
                case 6: case 7: case 0x0B: return "Guitar";
                case 8: return "Drum kit";
                case 0x13: return "Arcade pad";
                default: return null;
            }
        }

        public static string BatteryText(int slot)
        {
            Battery b;
            try { if (XInputGetBatteryInformation((uint)slot, 0, out b) != 0) return ""; } catch { return ""; }
            if (b.Type == 1) return "";             // wired
            if (b.Type == 0 || b.Type == 0xFF) return "";
            return "Battery " + new[] { "empty", "low", "medium", "full" }[Math.Min((int)b.Level, 3)];
        }
    }

    // Controllers Windows itself sees (present HID devices), to tell "not connected" from "connected but unreadable".
    static class HidScan
    {
        [StructLayout(LayoutKind.Sequential)] struct DevInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo data, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid Fmtid; public uint Pid; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DevInfo data, ref PropKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DevInfo data, uint property, out uint type, byte[] buffer, uint size, out uint required);
        static Guid HidClass = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        static Guid BluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        static readonly Regex PadName = new Regex(@"DualSense|DualShock|Wireless Controller|Pro Controller|Steam Controller", RegexOptions.IgnoreCase);

        // Controllers paired over Bluetooth but not connected right now (Windows' Bluetooth list says "Paired"), by name
        public static List<string> PairedNotConnected()
        {
            var list = new List<string>();
            IntPtr set = SetupDiGetClassDevsW(ref BluetoothClass, IntPtr.Zero, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                var d = new DevInfo { Size = (uint)Marshal.SizeOf(typeof(DevInfo)) };
                var sb = new StringBuilder(512);
                var connectedKey = new PropKey { Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 15 };   // DEVPKEY_Device_IsConnected
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    int req;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out req) || !sb.ToString().StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
                    uint type, need;
                    var nameBuf = new byte[512];
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, 0x0C /* SPDRP_FRIENDLYNAME */, out type, nameBuf, (uint)nameBuf.Length, out need)) continue;
                    string name = Encoding.Unicode.GetString(nameBuf, 0, (int)Math.Min(need, (uint)nameBuf.Length)).TrimEnd('\0');
                    if (!PadName.IsMatch(name)) continue;
                    var val = new byte[4];
                    if (SetupDiGetDevicePropertyW(set, ref d, ref connectedKey, out type, val, (uint)val.Length, out need, 0) && val[0] == 0)
                        list.Add(name);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public sealed class Found { public ushort Vid, Pid; public bool Bluetooth; public string Name; public string Id; }

        static readonly Dictionary<uint, string> Known = new Dictionary<uint, string>
        {
            { 0x054C0CE6, "DualSense" }, { 0x054C0DF2, "DualSense Edge" }, { 0x054C05C4, "DualShock 4" }, { 0x054C09CC, "DualShock 4" },
            { 0x054C0BA0, "DualShock 4 USB wireless adapter" }, { 0x054C0268, "DualShock 3" },
            { 0x28DE1102, "Steam Controller" }, { 0x28DE1142, "Steam Controller (wireless receiver)" }, { 0x057E2009, "Switch Pro Controller" },
        };
        // USB: HID\VID_054C&PID_0CE6...; Bluetooth: HID\{00001124-...}_VID&0002054C_PID&0CE6...; Bluetooth LE: ..._VID&02045E_PID&0B13...
        static readonly Regex VidPid = new Regex(@"VID(?:_|&)(?:0002|02)?([0-9A-F]{4})[&_]PID(?:_|&)([0-9A-F]{4})", RegexOptions.IgnoreCase);

        // PlayStation / Steam / Switch controllers present right now (one entry per controller and connection)
        public static List<Found> Controllers()
        {
            var list = new List<Found>();
            IntPtr set = SetupDiGetClassDevsW(ref HidClass, IntPtr.Zero, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                var d = new DevInfo { Size = (uint)Marshal.SizeOf(typeof(DevInfo)) };
                var sb = new StringBuilder(512);
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    int req;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out req)) continue;
                    string id = sb.ToString();
                    var m = VidPid.Match(id);
                    if (!m.Success) continue;
                    ushort vid = Convert.ToUInt16(m.Groups[1].Value, 16), pid = Convert.ToUInt16(m.Groups[2].Value, 16);
                    string name;
                    if (!Known.TryGetValue((uint)vid << 16 | pid, out name)) continue;
                    bool bt = id.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("{00001812", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (list.Any(f => f.Vid == vid && f.Pid == pid && f.Bluetooth == bt)) continue;
                    list.Add(new Found { Vid = vid, Pid = pid, Bluetooth = bt, Name = name, Id = id });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }
    }

    sealed partial class MainWindow
    {
        // One controller on the page: its card and the shapes the live view updates. Either an SDL3 pad (Gp) or an
        // XInput slot (XiSlot >= 0).
        sealed class PadPanel
        {
            public string Key;
            public IntPtr Gp;
            public int XiSlot = -1;
            public int Type;
            public string Label;          // for the report
            public Border Card;
            public TextBlock Status, Battery;
            public readonly Dictionary<int, Shape> Buttons = new Dictionary<int, Shape>();   // CELL_PAD-style keys below
            public Rectangle L2Fill, R2Fill;
            public Ellipse LDot, RDot, LRing, RRing, LDead, RDead;
            public int LedStep;
        }

        // keys of PadPanel.Buttons (the PS3 buttons the game receives)
        const int PB_CROSS = 0, PB_CIRCLE = 1, PB_SQUARE = 2, PB_TRIANGLE = 3, PB_SELECT = 4, PB_START = 5, PB_L1 = 6, PB_R1 = 7, PB_L2 = 8, PB_R2 = 9,
                  PB_UP = 10, PB_DOWN = 11, PB_LEFT = 12, PB_RIGHT = 13, PB_PS = 14, PB_TOUCH = 15, PB_L3 = 16, PB_R3 = 17;
        const double StickR = 26;

        DispatcherTimer padTimer;
        readonly List<PadPanel> padPanels = new List<PadPanel>();
        StackPanel padList;
        TextBlock padEmpty;
        StackPanel padTips;
        DateTime padTipsAt;
        bool padSdlOk;
        List<HidScan.Found> padHid = new List<HidScan.Found>();

        UIElement ControllerPage()
        {
            padPanels.Clear();
            padList = new StackPanel();
            padEmpty = T("", 13, "Dim");
            padEmpty.Margin = new Thickness(20, 4, 20, 16);
            padTips = new StackPanel { Margin = new Thickness(20, 0, 20, 8) };
            var report = Btn("Copy controller report", CopyPadReport, "Btn", "",
                             "Copies what the launcher and Windows see (controllers, programs that interfere, your controller settings) to paste when asking for help.");
            report.Margin = new Thickness(20, 0, 8, 16);
            var live = Section("Connected controllers",
                               "Live: press buttons and move the sticks to see exactly what the game will receive (shown with the PS3 buttons it gets). " +
                               "Xbox controllers are read through XInput, PlayStation, Steam and Switch Pro controllers through SDL3. " +
                               "The controller you use is player 1 in the game.",
                               new UIElement[] { padList, padEmpty, padTips, report });
            if (padTimer == null)
            {
                padTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                padTimer.Tick += (s, e) => PadTick();
            }
            padTipsAt = DateTime.MinValue;
            padTimer.Start();
            PadTick();
            return SettingsPage("controller", "Controller",
                                "DualSense, DualSense Edge, DualShock 4, Steam Controller, Switch Pro and Xbox controllers work by USB or Bluetooth. " +
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
                padTips.Children.Clear();
                padEmpty.Text = "The game is running and reading the controllers. The live view comes back when it closes.";
                padEmpty.Visibility = Visibility.Visible;
                return;
            }
            bool sdlOn = !Inv.Same(M.Get("PAD_SDL"), "0"), steamOn = !Inv.Same(M.Get("PAD_STEAM_CONTROLLER"), "0");
            padSdlOk = Sdl.Start(M.P, steamOn);
            var want = new List<string>();
            foreach (int slot in XInput.Poll()) want.Add("xi:" + slot);
            var sdl = padSdlOk ? Sdl.Poll() : new List<KeyValuePair<uint, IntPtr>>();
            foreach (var kv in sdl) want.Add("sdl:" + kv.Key);

            foreach (var gone in padPanels.Where(p => !want.Contains(p.Key)).ToList()) { padList.Children.Remove(gone.Card); padPanels.Remove(gone); }
            foreach (var key in want)
            {
                if (padPanels.Any(p => p.Key == key)) continue;
                PadPanel p = key.StartsWith("xi:") ? NewXiPanel(int.Parse(key.Substring(3))) : NewSdlPanel(sdl.First(kv => "sdl:" + kv.Key == key));
                padPanels.Add(p);
            }
            // the game's port order: XInput slots first, then the SDL3 pads in connection order
            var order = padPanels.OrderBy(p => p.XiSlot >= 0 ? p.XiSlot : 10 + want.IndexOf(p.Key)).ToList();
            if (!order.Select(p => p.Card).SequenceEqual(padList.Children.Cast<UIElement>()))
            {
                padList.Children.Clear();
                foreach (var p in order) padList.Children.Add(p.Card);
            }
            padEmpty.Text = padPanels.Count == 0 ? "No controller found. Connect a DualSense, DualShock 4, Steam Controller, Switch Pro or Xbox controller by USB or Bluetooth." : "";
            padEmpty.Visibility = padEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var p in order)
            {
                string how;
                if (p.XiSlot >= 0) how = "Read by the game through XInput (slot " + (p.XiSlot + 1) + ")";
                else if (!sdlOn) how = "Not used by the game: PlayStation and Steam controllers are switched off below";
                else if (p.Type == Sdl.TYPE_STEAM && !steamOn) how = "Not read directly: Steam Controller reading is off below (use it through Steam Input)";
                else how = "Read by the game through SDL3";
                if (p.Status.Text != how) p.Status.Text = how;
                UpdatePadPanel(p);
            }
            if (DateTime.Now - padTipsAt > TimeSpan.FromSeconds(3)) { padTipsAt = DateTime.Now; UpdatePadTips(); }
        }

        static HashSet<string> RunningProcs()
        {
            return new HashSet<string>(Process.GetProcesses().Select(p => { try { return p.ProcessName.ToLowerInvariant(); } catch { return ""; } }));
        }
        static readonly string[] PadTools = { "ds4windows", "dsx", "dualsensex", "rewasd", "rewaseengine", "x360ce", "inputmapper" };

        void UpdatePadTips()
        {
            padTips.Children.Clear();
            var procs = RunningProcs();
            Action<string, string> warn = (glyph, text) => padTips.Children.Add(Note(glyph, text, "Warn", "WarnSoft", new Thickness(0, 0, 0, 8)));
            bool sdlOn = !Inv.Same(M.Get("PAD_SDL"), "0");
            try { padHid = HidScan.Controllers(); } catch { padHid = new List<HidScan.Found>(); }

            if (!Sdl.Available(M.P))
                warn("", "SDL3 is missing (" + Sdl.DllPath(M.P) + "), so the game reads Xbox controllers only. Run Tools › Update now (or the inFamous Setup again) to get it back.");
            else if (!padSdlOk)
                warn("", "SDL3 could not start, so PlayStation, Steam and Switch Pro controllers can't be shown here.");
            List<string> gone = patchesGone;
            if (gone.Contains("PlayStation / Steam controllers"))
                warn("", "The game was updated without the controller patch, so it reads Xbox controllers only. Tools › Re-apply launcher patches puts it back.");
            if (!sdlOn && padHid.Count > 0)
                warn("", "\"Read PlayStation and Steam controllers\" is off below, so the game ignores your " + padHid[0].Name + ". Switch it on.");

            // Windows sees a PlayStation / Steam / Switch controller that SDL3 can't read
            int sdlPads = padPanels.Count(p => p.XiSlot < 0);
            var unread = padHid.Where(h => !padPanels.Any(p => p.XiSlot < 0 && SamePad(p, h))).ToList();
            if (sdlOn && padSdlOk && unread.Count > 0)
            {
                var h = unread[0];
                bool tool = PadTools.Any(procs.Contains);
                bool steamOff = h.Vid == 0x28DE && Inv.Same(M.Get("PAD_STEAM_CONTROLLER"), "0");
                if (!steamOff)
                    warn("", "Windows sees your " + h.Name + (h.Bluetooth ? " (Bluetooth)" : " (USB)") + ", but the game can't read it" +
                         (Sdl.Failed.Count > 0 ? " (" + Sdl.Failed.Values.First() + ")" : "") + ". " +
                         (tool ? "A controller tool is running (" + string.Join(", ", PadTools.Where(procs.Contains)) + ") and has it to itself: close it, or turn off its option to hide the controller. "
                               : "Another program has it to itself: close DS4Windows, DSX or reWASD if they run in the background, and check HidHide doesn't hide it. ") +
                         (h.Bluetooth ? "Over Bluetooth, also try removing the controller in Windows' Bluetooth settings and pairing it again (hold PS + Create until the light flashes)." : "Also try another USB port or cable (some cables only charge)."));
            }

            List<string> sleeping;
            try { sleeping = HidScan.PairedNotConnected(); } catch { sleeping = new List<string>(); }
            foreach (var name in sleeping)
                warn("", "Your " + name + " is paired over Bluetooth but not connected right now, so nothing can read it. Press its PS (or Home) button: " +
                     "the light comes on and it appears above within a few seconds. If it doesn't, remove it in Windows' Bluetooth settings and pair it again " +
                     "(for a DualSense, hold PS + Create until the light bar flashes quickly). Unplug its USB cable first: while it's plugged in, it uses USB.");

            bool steam = procs.Contains("steam");
            if (steam && padPanels.Any(p => p.XiSlot < 0))
                warn("", "Steam is running, and Steam Input can read PlayStation and Steam controllers too: outside a Steam game it may turn them into a mouse and keyboard, " +
                     "so the camera moves or buttons do two things. Either close Steam before playing, turn Steam Input off for your controller type (Steam › Settings › Controller), " +
                     "or add the game to Steam and play it through Steam Input (it then appears as an Xbox controller).");
            var tools = PadTools.Where(procs.Contains).ToList();
            if (tools.Count > 0 && unread.Count == 0)
                warn("", string.Join(", ", tools) + " is running. It adds a virtual Xbox controller on top of your real one, so the game sees two. " +
                     "That works (the controller you use is player 1), but if buttons do two things, close it or turn on its option to hide the real controller.");
        }

        static bool SamePad(PadPanel p, HidScan.Found h)
        {
            if (p.Gp == IntPtr.Zero) return false;
            ushort vid = Sdl.SDL_GetGamepadVendor(p.Gp), pid = Sdl.SDL_GetGamepadProduct(p.Gp);
            return vid == h.Vid && (pid == h.Pid || h.Vid == 0x28DE || (h.Vid == 0x054C && h.Pid == 0x0BA0));
        }

        PadPanel NewSdlPanel(KeyValuePair<uint, IntPtr> kv)
        {
            var p = new PadPanel { Key = "sdl:" + kv.Key, Gp = kv.Value, Type = Sdl.SDL_GetGamepadType(kv.Value) };
            string type = Sdl.TypeName(p.Gp, p.Type), name = Sdl.Name(p.Gp);
            int conn = Sdl.SDL_GetGamepadConnectionState(p.Gp);
            string link = conn == 1 ? "USB" : conn == 2 ? (Sdl.IsSony(p.Type) ? "Bluetooth" : "Wireless") : null;
            p.Label = name + " (" + type + (link != null ? ", " + link : "") + ", SDL3)";
            var chips = new List<Border> { Chip(type.ToUpperInvariant(), "AccentHi", "AccentSoft") };
            if (link != null) chips.Add(Chip(link.ToUpperInvariant(), "Dim", "CardHi"));
            var buttons = HStack(Btn("Vibrate", () => Sdl.SDL_RumbleGamepad(p.Gp, 0xC000, 0xC000, 450), "Btn", ""));
            if (Sdl.IsSony(p.Type))
                buttons.Children.Add(Btn("Light bar", () =>
                {
                    var cols = new[] { new byte[] { 0, 110, 255 }, new byte[] { 255, 10, 0 }, new byte[] { 160, 32, 240 }, new byte[] { 255, 255, 255 }, new byte[] { 0, 0, 0 } };
                    var c = cols[p.LedStep++ % cols.Length];
                    Sdl.SDL_SetGamepadLED(p.Gp, c[0], c[1], c[2]);
                }, "Btn", "", "Cycles blue, red, purple, white and off. The game sets its own colour when it starts."));
            BuildPadCard(p, name, chips, buttons);
            return p;
        }

        PadPanel NewXiPanel(int slot)
        {
            var p = new PadPanel { Key = "xi:" + slot, XiSlot = slot, Type = Sdl.TYPE_XBOXONE };
            string name, chip; bool wireless; ushort vid, pid;
            XInput.Describe(slot, out name, out chip, out wireless, out vid, out pid);
            p.Label = name + " (XInput slot " + (slot + 1) + (vid != 0 ? ", " + vid.ToString("X4") + ":" + pid.ToString("X4") : "") + (wireless ? ", wireless" : "") + ")";
            var chips = new List<Border> { Chip(chip, "AccentHi", "AccentSoft"), Chip(wireless ? "WIRELESS" : "WIRED", "Dim", "CardHi") };
            var buttons = HStack(Btn("Vibrate", () =>
            {
                var v = new XInput.Vibration { Left = 0xC000, Right = 0xC000 };
                XInput.XInputSetState((uint)slot, ref v);
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                t.Tick += (s, e) => { t.Stop(); var off = new XInput.Vibration(); XInput.XInputSetState((uint)slot, ref off); };
                t.Start();
            }, "Btn", ""));
            BuildPadCard(p, name, chips, buttons);
            return p;
        }

        void BuildPadCard(PadPanel p, string name, List<Border> chips, StackPanel buttons)
        {
            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = B("AccentSoft"), Child = Glyph("", 20, "Accent"),
                                    VerticalAlignment = VerticalAlignment.Top };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var title = HStack(T(name, 15, "Text", FontWeights.SemiBold));
            foreach (var c in chips) title.Children.Add(c);
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

        // What the game receives from one controller, in SDL's units (sticks -32768..32767 with down positive,
        // triggers 0..32767), whichever way it is read.
        sealed class PadRead
        {
            public readonly Dictionary<int, bool> On = new Dictionary<int, bool>();
            public int LT, RT, LX, LY, RX, RY;
            public double Deadzone;
        }

        PadRead ReadPad(PadPanel p)
        {
            var r = new PadRead();
            if (p.XiSlot >= 0)
            {
                XInput.Pad x;
                if (!XInput.Read(p.XiSlot, out x)) x = new XInput.Pad();
                Func<ushort, bool> b = m => (x.Buttons & m) != 0;
                r.On[PB_CROSS] = b(XInput.A); r.On[PB_CIRCLE] = b(XInput.B); r.On[PB_SQUARE] = b(XInput.X); r.On[PB_TRIANGLE] = b(XInput.Y);
                r.On[PB_SELECT] = b(XInput.BACK); r.On[PB_START] = b(XInput.START); r.On[PB_L1] = b(XInput.LB); r.On[PB_R1] = b(XInput.RB);
                r.On[PB_UP] = b(XInput.UP); r.On[PB_DOWN] = b(XInput.DOWN); r.On[PB_LEFT] = b(XInput.LEFT); r.On[PB_RIGHT] = b(XInput.RIGHT);
                r.On[PB_PS] = false; r.On[PB_TOUCH] = false; r.On[PB_L3] = b(XInput.LTHUMB); r.On[PB_R3] = b(XInput.RTHUMB);
                r.LT = x.LT * 32767 / 255; r.RT = x.RT * 32767 / 255;
                r.LX = x.LX; r.LY = -Math.Max((int)x.LY, -32767); r.RX = x.RX; r.RY = -Math.Max((int)x.RY, -32767);   // XInput: up is positive
                r.Deadzone = XInput.Deadzone / 32767.0;
                return r;
            }
            IntPtr g = p.Gp;
            Func<int, bool> s = k => Sdl.SDL_GetGamepadButton(g, k);
            bool touchSel = !Inv.Same(M.Get("PAD_TOUCHPAD"), "0");
            r.On[PB_CROSS] = s(Sdl.SOUTH); r.On[PB_CIRCLE] = s(Sdl.EAST); r.On[PB_SQUARE] = s(Sdl.WEST); r.On[PB_TRIANGLE] = s(Sdl.NORTH);
            r.On[PB_SELECT] = s(Sdl.BACK) || (touchSel && s(Sdl.TOUCHPAD)); r.On[PB_START] = s(Sdl.START);
            r.On[PB_L1] = s(Sdl.LSHOULDER); r.On[PB_R1] = s(Sdl.RSHOULDER);
            r.On[PB_UP] = s(Sdl.UP); r.On[PB_DOWN] = s(Sdl.DOWN); r.On[PB_LEFT] = s(Sdl.LEFT); r.On[PB_RIGHT] = s(Sdl.RIGHT);
            r.On[PB_PS] = s(Sdl.GUIDE); r.On[PB_TOUCH] = s(Sdl.TOUCHPAD);
            r.On[PB_L3] = s(Sdl.LSTICK) || s(Sdl.LPADDLE1); r.On[PB_R3] = s(Sdl.RSTICK) || s(Sdl.RPADDLE1);
            r.LT = Math.Max((int)Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LT), 0); r.RT = Math.Max((int)Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RT), 0);
            r.LX = Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LX); r.LY = Sdl.SDL_GetGamepadAxis(g, Sdl.AX_LY);
            r.RX = Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RX); r.RY = Sdl.SDL_GetGamepadAxis(g, Sdl.AX_RY);
            double v; if (Inv.TryNum(M.Get("PAD_DEADZONE"), out v)) r.Deadzone = v / 100.0;
            return r;
        }

        void UpdatePadPanel(PadPanel p)
        {
            var r = ReadPad(p);
            r.On[PB_L2] = r.LT * 255 / 32767 > 30;
            r.On[PB_R2] = r.RT * 255 / 32767 > 30;
            foreach (var kv in r.On)
            {
                if (kv.Key == PB_L2 || kv.Key == PB_R2 || kv.Key == PB_L3 || kv.Key == PB_R3) continue;
                var want = kv.Value ? B("Accent") : B("CardHi");
                if (p.Buttons[kv.Key].Fill != want) p.Buttons[kv.Key].Fill = want;
            }
            p.L2Fill.Width = 80.0 * r.LT / 32767.0; p.L2Fill.Fill = r.On[PB_L2] ? B("Accent") : B("AccentSoft");
            p.R2Fill.Width = 80.0 * r.RT / 32767.0; p.R2Fill.Fill = r.On[PB_R2] ? B("Accent") : B("AccentSoft");
            Stick(p.LDot, p.LDead, 165, 148, r.LX, r.LY, r.Deadzone, r.On[PB_L3]);
            Stick(p.RDot, p.RDead, 275, 148, r.RX, r.RY, r.Deadzone, r.On[PB_R3]);

            string bat = "";
            if (p.XiSlot >= 0) bat = XInput.BatteryText(p.XiSlot);
            else
            {
                int pct;
                int ps = Sdl.SDL_GetGamepadPowerInfo(p.Gp, out pct);   // 1 battery, 2 none, 3 charging, 4 charged
                bat = ps == 1 ? "Battery " + (pct >= 0 ? pct + " %" : "in use") : ps == 3 ? "Charging" + (pct >= 0 ? ", " + pct + " %" : "") : ps == 4 ? "Charged" : "";
            }
            if (p.Battery.Text != bat) { p.Battery.Text = bat; p.Battery.Visibility = bat.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        static void Stick(Ellipse dot, Ellipse dead, double cx, double cy, int ax, int ay, double dz, bool click)
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

        // Everything needed to answer "my controller doesn't work", as text to paste.
        void CopyPadReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("inFamous PC Enhanced - controller report (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv.C) + ")");
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString);
            sb.AppendLine("SDL3.dll: " + (Sdl.Available(M.P) ? "present" : "MISSING (" + Sdl.DllPath(M.P) + ")") + (padSdlOk ? ", started" : Running ? ", the game is running" : ", NOT started"));
            List<string> gone = patchesGone;
            sb.AppendLine("Controller patch in the game: " + (gone.Contains("PlayStation / Steam controllers") ? "MISSING (Tools > Re-apply launcher patches)" : "yes"));
            foreach (var k in new[] { "PAD_SDL", "PAD_STEAM_CONTROLLER", "PAD_FOLLOW_ACTIVE", "PAD_DEADZONE", "PAD_TOUCHPAD", "PAD_RUMBLE", "PAD_LIGHTBAR" })
                sb.AppendLine("  " + k + " = " + (M.Get(Catalog.Find(k)) ?? "(default)"));
            sb.AppendLine("Controllers the launcher reads (game order):");
            if (padPanels.Count == 0) sb.AppendLine("  none");
            foreach (var p in padPanels.OrderBy(p => p.XiSlot >= 0 ? p.XiSlot : 10)) sb.AppendLine("  " + p.Label + " - " + p.Status.Text);
            foreach (var f in Sdl.Failed.Values) sb.AppendLine("  SDL3 could not open: " + f);
            sb.AppendLine("PlayStation / Steam / Switch controllers Windows sees:");
            List<HidScan.Found> hid;
            try { hid = HidScan.Controllers(); } catch { hid = new List<HidScan.Found>(); }
            if (hid.Count == 0) sb.AppendLine("  none");
            foreach (var h in hid) sb.AppendLine("  " + h.Name + " " + h.Vid.ToString("X4") + ":" + h.Pid.ToString("X4") + (h.Bluetooth ? " Bluetooth" : " USB") + "  " + h.Id);
            try { foreach (var n in HidScan.PairedNotConnected()) sb.AppendLine("  " + n + ": paired over Bluetooth, NOT connected"); } catch { }
            var procs = RunningProcs();
            var others = PadTools.Concat(new[] { "steam", "hidhide", "hidhideclient" }).Where(procs.Contains).ToList();
            sb.AppendLine("Programs that also read controllers: " + (others.Count > 0 ? string.Join(", ", others) : "none running"));
            string log = System.IO.Path.Combine(M.Port, "play.out.log");
            try
            {
                if (File.Exists(log))
                {
                    var lines = File.ReadLines(log).Where(l => l.StartsWith("[cellPad]") && (l.Contains("SDL3") || l.Contains("player 1"))).Take(20).ToList();
                    sb.AppendLine("Last game start (" + File.GetLastWriteTime(log).ToString("yyyy-MM-dd HH:mm", Inv.C) + "):");
                    foreach (var l in lines) sb.AppendLine("  " + l);
                    if (lines.Count == 0) sb.AppendLine("  no controller lines");
                }
            }
            catch { }
            try { Clipboard.SetText(sb.ToString()); lastMessage = "Controller report copied."; UpdateBar(); }
            catch (Exception e) { MessageBox.Show("Could not copy: " + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
