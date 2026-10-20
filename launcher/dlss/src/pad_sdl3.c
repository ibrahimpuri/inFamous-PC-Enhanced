/* PlayStation and Steam controllers for cellPad through SDL3. See pad_sdl3.h for what it covers and the env switches.
 *
 * SDL3.dll is loaded at run time and only its HIDAPI / DirectInput drivers are used: XInput, RawInput, WGI and
 * GameInput stay off so no Xbox pad shows up twice (cellPad reads those through XInput), and gamepads SDL types as
 * Xbox are skipped for the same reason. Steam Input's own virtual pads are XInput pads, and Steam tells SDL which
 * physical controllers it is virtualising (SDL_GAMECONTROLLER_IGNORE_DEVICES), so a game started from Steam does not
 * read a controller twice either.
 *
 * All SDL calls happen on cellPad's host poll thread (pad_sdl3_update); the guest side only reads a snapshot and
 * leaves rumble requests, which that thread applies. */
/* Built only with the launcher's CMake hook, which adds the SDL3 headers and defines PS3RECOMP_SDL3. The port's
 * CMakeLists globs libs/input/*.c, so after an update that restores the upstream CMakeLists (hook gone) this file
 * is still compiled: it then compiles to nothing and the game builds without PlayStation / Steam controllers. */
#ifdef PS3RECOMP_SDL3
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <SDL3/SDL_init.h>
#include <SDL3/SDL_gamepad.h>
#include <SDL3/SDL_hints.h>
#include <SDL3/SDL_error.h>
#include <SDL3/SDL_stdinc.h>

#include "cellPad.h"
#include "pad_sdl3.h"

/* Defined here and, for builds without this file, by the title (selectany: whichever is linked wins). */
__declspec(selectany) volatile uint32_t g_pad_lightbar_rgb = 0;

#define SDLFN(X) X(SDL_Init) X(SDL_SetHint) X(SDL_GetError) X(SDL_free) X(SDL_GetGamepads) X(SDL_OpenGamepad) \
    X(SDL_CloseGamepad) X(SDL_GamepadConnected) X(SDL_GetGamepadID) X(SDL_GetGamepadType) X(SDL_GetGamepadName) \
    X(SDL_GetGamepadButton) X(SDL_GetGamepadAxis) X(SDL_UpdateGamepads) X(SDL_RumbleGamepad) X(SDL_SetGamepadLED) \
    X(SDL_SetGamepadPlayerIndex) X(SDL_GetGamepadConnectionState) X(SDL_GetGamepadTypeForID) X(SDL_GetGamepadNameForID)
#define SDL_DECL(n) static __typeof__(n)* p_##n;
SDLFN(SDL_DECL)

#define MAXPADS 4

typedef struct {
    SDL_Gamepad* gp;
    SDL_JoystickID id;
    uint16_t rumble_sent;        /* small << 8 | large last sent */
    unsigned long long rumble_at;
    uint32_t led_sent;
} OpenPad;

static int s_state = 0;                  /* 0 not started, 1 running, -1 off / failed */
static OpenPad s_open[MAXPADS];
static int s_nopen = 0;
static CRITICAL_SECTION s_lock;
static PadSdlPad s_snap[MAXPADS];
static int s_nsnap = 0;
static volatile LONG s_rumble_req[MAXPADS];   /* small << 8 | large */
static float s_deadzone = 0.10f;
static int s_touchpad = 1, s_rumble = 1, s_steam = 1;

int pad_rumble_enabled(void)
{
    static int v = -1;
    if (v < 0) { const char* e = getenv("PAD_RUMBLE"); v = (e && *e == '0') ? 0 : 1; }
    return v;
}

static HMODULE load_sdl(void)
{
    const char* forced = getenv("PAD_SDL3_DLL");
    if (forced && *forced) return LoadLibraryExA(forced, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    wchar_t dir[MAX_PATH], path[MAX_PATH + 64];
    if (!GetModuleFileNameW(NULL, dir, MAX_PATH)) return NULL;
    wchar_t* s = wcsrchr(dir, L'\\');
    if (s) *s = 0;
    /* full paths only: never an SDL3.dll some other program left on PATH */
    _snwprintf(path, MAX_PATH + 64, L"%ls\\SDL3.dll", dir);
    HMODULE h = LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (h) return h;
    _snwprintf(path, MAX_PATH + 64, L"%ls\\..\\..\\sdl3\\lib\\x64\\SDL3.dll", dir);
    return LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
}

static const char* type_name(SDL_GamepadType t)
{
    switch (t) {
    case SDL_GAMEPAD_TYPE_PS3: return "PlayStation 3";
    case SDL_GAMEPAD_TYPE_PS4: return "DualShock 4";
    case SDL_GAMEPAD_TYPE_PS5: return "DualSense";
    case SDL_GAMEPAD_TYPE_STEAM: return "Steam Controller";
    case SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO: return "Switch Pro";
    case SDL_GAMEPAD_TYPE_XBOX360: case SDL_GAMEPAD_TYPE_XBOXONE: return "Xbox";
    default: return "gamepad";
    }
}

static int start(void)
{
    const char* e = getenv("PAD_SDL");
    if (e && *e == '0') { printf("[cellPad] SDL3 gamepads off (PAD_SDL=0): XInput only\n"); fflush(stdout); return 0; }
    if ((e = getenv("PAD_DEADZONE")) && *e) { const float d = (float)atof(e) / 100.0f; if (d >= 0.0f && d <= 0.5f) s_deadzone = d; }
    if ((e = getenv("PAD_TOUCHPAD")) && *e == '0') s_touchpad = 0;
    if ((e = getenv("PAD_STEAM_CONTROLLER")) && *e == '0') s_steam = 0;
    s_rumble = pad_rumble_enabled();
    if ((e = getenv("PAD_LIGHTBAR")) && *e && g_pad_lightbar_rgb == 0) {
        if (!_stricmp(e, "off")) g_pad_lightbar_rgb = 0x01000000u;
        else if (strlen(e) == 6) { char* end = NULL; const unsigned long v = strtoul(e, &end, 16); if (end && !*end) g_pad_lightbar_rgb = 0x01000000u | (uint32_t)v; }
    }

    HMODULE h = load_sdl();
    if (!h) {
        printf("[cellPad] SDL3.dll not found (next to the exe or in sdl3\\lib\\x64): PlayStation / Steam controllers need it -- XInput only\n");
        fflush(stdout); return 0;
    }
#define SDL_LOAD(n) p_##n = (__typeof__(n)*)(void*)GetProcAddress(h, #n); if (!p_##n) { printf("[cellPad] SDL3.dll has no %s (too old?) -- XInput only\n", #n); fflush(stdout); return 0; }
    SDLFN(SDL_LOAD)

    /* only the drivers XInput does not cover */
    p_SDL_SetHint(SDL_HINT_XINPUT_ENABLED, "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_RAWINPUT, "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_WGI, "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_GAMEINPUT, "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_XBOX, "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS4, "1");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS5, "1");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_STEAM, s_steam ? "1" : "0");
    p_SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
    if (!p_SDL_Init(SDL_INIT_GAMEPAD)) {
        printf("[cellPad] SDL3 gamepad init failed: %s -- XInput only\n", p_SDL_GetError()); fflush(stdout); return 0;
    }
    printf("[cellPad] SDL3 gamepads on (DualSense / DualShock 4%s; deadzone %.0f %%, touchpad %s, rumble %s)\n",
           s_steam ? " / Steam Controller" : "", s_deadzone * 100.0f, s_touchpad ? "= SELECT" : "off", s_rumble ? "on" : "off");
    fflush(stdout);
    return 1;
}

static uint8_t axis8(float v)
{
    int i = 128 + (int)lrintf(v * 127.0f);
    return (uint8_t)(i < 0 ? 0 : i > 255 ? 255 : i);
}

/* radial deadzone, rescaled so the stick still reaches full deflection */
static void stick(Sint16 x, Sint16 y, uint8_t* ox, uint8_t* oy)
{
    float fx = x / 32767.0f, fy = y / 32767.0f;
    const float m = sqrtf(fx * fx + fy * fy);
    if (m <= s_deadzone) fx = fy = 0.0f;
    else {
        float k = (m - s_deadzone) / (1.0f - s_deadzone) / m;
        if (k * m > 1.0f) k = 1.0f / m;
        fx *= k; fy *= k;
    }
    *ox = axis8(fx);
    *oy = axis8(fy);   /* SDL: down is positive, as on the PS3 */
}

static void read_pad(SDL_Gamepad* g, PadSdlPad* o)
{
    uint16_t b = 0;
#define B(sdl, ps3) if (p_SDL_GetGamepadButton(g, sdl)) b |= ps3;
    B(SDL_GAMEPAD_BUTTON_SOUTH, CELL_PAD_CTRL_CROSS)  B(SDL_GAMEPAD_BUTTON_EAST, CELL_PAD_CTRL_CIRCLE)
    B(SDL_GAMEPAD_BUTTON_WEST, CELL_PAD_CTRL_SQUARE)  B(SDL_GAMEPAD_BUTTON_NORTH, CELL_PAD_CTRL_TRIANGLE)
    B(SDL_GAMEPAD_BUTTON_BACK, CELL_PAD_CTRL_SELECT)  B(SDL_GAMEPAD_BUTTON_START, CELL_PAD_CTRL_START)
    B(SDL_GAMEPAD_BUTTON_LEFT_STICK, CELL_PAD_CTRL_L3) B(SDL_GAMEPAD_BUTTON_RIGHT_STICK, CELL_PAD_CTRL_R3)
    B(SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, CELL_PAD_CTRL_L1) B(SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, CELL_PAD_CTRL_R1)
    B(SDL_GAMEPAD_BUTTON_DPAD_UP, CELL_PAD_CTRL_UP)   B(SDL_GAMEPAD_BUTTON_DPAD_DOWN, CELL_PAD_CTRL_DOWN)
    B(SDL_GAMEPAD_BUTTON_DPAD_LEFT, CELL_PAD_CTRL_LEFT) B(SDL_GAMEPAD_BUTTON_DPAD_RIGHT, CELL_PAD_CTRL_RIGHT)
    /* back paddles / grips (DualSense Edge, Steam Controller): the stick clicks, which are awkward there */
    B(SDL_GAMEPAD_BUTTON_LEFT_PADDLE1, CELL_PAD_CTRL_L3) B(SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1, CELL_PAD_CTRL_R3)
#undef B
    if (s_touchpad && p_SDL_GetGamepadButton(g, SDL_GAMEPAD_BUTTON_TOUCHPAD)) b |= CELL_PAD_CTRL_SELECT;
    const Sint16 lt = p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFT_TRIGGER), rt = p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER);
    o->l2 = (uint8_t)(lt <= 0 ? 0 : (lt * 255 + 16383) / 32767);
    o->r2 = (uint8_t)(rt <= 0 ? 0 : (rt * 255 + 16383) / 32767);
    if (o->l2 > 30) b |= CELL_PAD_CTRL_L2;   /* the XInput path's trigger threshold */
    if (o->r2 > 30) b |= CELL_PAD_CTRL_R2;
    stick(p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFTX), p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_LEFTY), &o->lx, &o->ly);
    stick(p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHTX), p_SDL_GetGamepadAxis(g, SDL_GAMEPAD_AXIS_RIGHTY), &o->rx, &o->ry);
    o->buttons = b;
    o->connected = 1;
}

static void close_pad(int i)
{
    const char* n = p_SDL_GetGamepadName(s_open[i].gp);
    printf("[cellPad] SDL3: %s disconnected\n", n ? n : "gamepad"); fflush(stdout);
    p_SDL_CloseGamepad(s_open[i].gp);
    memmove(&s_open[i], &s_open[i + 1], sizeof(OpenPad) * (size_t)(s_nopen - i - 1));
    s_nopen--;
    for (int k = i; k < MAXPADS - 1; k++) InterlockedExchange(&s_rumble_req[k], s_rumble_req[k + 1]);
    InterlockedExchange(&s_rumble_req[MAXPADS - 1], 0);
}

/* Says once per device why a gamepad SDL sees is not used, so play.out.log shows it. */
static void note_once(SDL_JoystickID id, const char* why)
{
    static SDL_JoystickID seen[32];
    static int nseen = 0;
    for (int i = 0; i < nseen; i++) if (seen[i] == id) return;
    if (nseen < 32) seen[nseen++] = id;
    const char* name = p_SDL_GetGamepadNameForID(id);
    printf("[cellPad] SDL3: %s %s\n", name ? name : "gamepad", why);
    fflush(stdout);
}

static void rescan(void)
{
    int n = 0;
    SDL_JoystickID* ids = p_SDL_GetGamepads(&n);
    if (!ids) return;
    for (int k = 0; k < n && s_nopen < MAXPADS; k++) {
        int open = 0;
        for (int i = 0; i < s_nopen; i++) if (s_open[i].id == ids[k]) open = 1;
        if (open) continue;
        const SDL_GamepadType t = p_SDL_GetGamepadTypeForID(ids[k]);
        if (t == SDL_GAMEPAD_TYPE_XBOX360 || t == SDL_GAMEPAD_TYPE_XBOXONE) { note_once(ids[k], "is left to XInput"); continue; }
        SDL_Gamepad* g = p_SDL_OpenGamepad(ids[k]);
        if (!g) {
            char why[200];
            snprintf(why, sizeof why, "could not be opened (%s): another program may have it to itself (DS4Windows, DSX, HidHide, reWASD)", p_SDL_GetError());
            note_once(ids[k], why);
            continue;
        }
        OpenPad* o = &s_open[s_nopen++];
        memset(o, 0, sizeof *o);
        o->gp = g; o->id = ids[k];
        p_SDL_SetGamepadPlayerIndex(g, s_nopen - 1);
        const SDL_JoystickConnectionState c = p_SDL_GetGamepadConnectionState(g);
        const char* name = p_SDL_GetGamepadName(g);
        printf("[cellPad] SDL3: %s (%s, %s) connected\n", name ? name : "gamepad", type_name(p_SDL_GetGamepadType(g)),
               c == SDL_JOYSTICK_CONNECTION_WIRELESS ? "wireless" : c == SDL_JOYSTICK_CONNECTION_WIRED ? "USB" : "connection unknown");
        fflush(stdout);
    }
    p_SDL_free(ids);
}

void pad_sdl3_update(void)
{
    static unsigned long long next_scan = 0;
    if (s_state == 0) {
        InitializeCriticalSection(&s_lock);
        s_state = start() ? 1 : -1;
    }
    if (s_state < 0) return;
    p_SDL_UpdateGamepads();
    const unsigned long long now = GetTickCount64();
    for (int i = s_nopen - 1; i >= 0; i--) if (!p_SDL_GamepadConnected(s_open[i].gp)) close_pad(i);
    if (now >= next_scan) { next_scan = now + 500; rescan(); }

    PadSdlPad snap[MAXPADS];
    memset(snap, 0, sizeof snap);
    const uint32_t led = g_pad_lightbar_rgb;
    for (int i = 0; i < s_nopen; i++) {
        OpenPad* o = &s_open[i];
        read_pad(o->gp, &snap[i]);
        /* PS3 motors hold their setting until the next SetActDirect; SDL rumble runs for a duration -> refresh it */
        const uint16_t r = s_rumble ? (uint16_t)s_rumble_req[i] : 0;
        if (r != o->rumble_sent || (r && now >= o->rumble_at)) {
            p_SDL_RumbleGamepad(o->gp, (Uint16)((r & 0xFF) * 257), (Uint16)((r >> 8) * 257), r ? 600 : 0);
            o->rumble_sent = r; o->rumble_at = now + 400;
        }
        if (led && led != o->led_sent) {
            p_SDL_SetGamepadLED(o->gp, (Uint8)(led >> 16), (Uint8)(led >> 8), (Uint8)led);
            o->led_sent = led;
        }
    }
    EnterCriticalSection(&s_lock);
    memcpy(s_snap, snap, sizeof snap);
    s_nsnap = s_nopen;
    LeaveCriticalSection(&s_lock);
}

int pad_sdl3_get(PadSdlPad* out, int max)
{
    if (s_state <= 0) return 0;
    EnterCriticalSection(&s_lock);
    const int n = s_nsnap < max ? s_nsnap : max;
    memcpy(out, s_snap, sizeof(PadSdlPad) * (size_t)n);
    LeaveCriticalSection(&s_lock);
    return n;
}

void pad_sdl3_rumble(int index, uint8_t small_motor, uint8_t large_motor)
{
    if (index < 0 || index >= MAXPADS) return;
    /* libpad: the small motor is on/off (0 or 1), the large one 0..255 */
    InterlockedExchange(&s_rumble_req[index], (LONG)((small_motor ? 0xFF00u : 0u) | large_motor));
}

#endif   /* PS3RECOMP_SDL3 */
