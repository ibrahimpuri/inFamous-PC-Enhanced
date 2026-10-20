/* PlayStation and Steam controllers for cellPad through SDL3 -- pad_sdl3.c.
 *
 * Built only when the SDL3 headers sit next to the checkout (sdl3/, see CMakeLists.txt), which defines
 * PS3RECOMP_SDL3. SDL3.dll itself is loaded at run time (next to the exe, then ..\..\sdl3\lib\x64): without it the
 * game simply has XInput only.
 *
 * cellPad's XInput sweep keeps the Xbox / XInput pads (including Steam Input's virtual ones). SDL reads the rest --
 * DualSense, DualSense Edge, DualShock 4, Steam Controller, Switch Pro and other HID gamepads -- and they fill the
 * host ports XInput left empty, in the order they were connected.
 *
 * Env: PAD_SDL=0 off (XInput only). PAD_STEAM_CONTROLLER=0 leave Steam Controllers to Steam. PAD_DEADZONE=<percent>
 *      radial stick deadzone (default 10). PAD_TOUCHPAD=0 the touchpad click does nothing (default: SELECT).
 *      PAD_LIGHTBAR=off|RRGGBB light bar colour (default: SDL's player colour; "karma" is handled by the title).
 *      PAD_RUMBLE=0 no vibration (XInput too). PAD_SDL3_DLL=<path> a specific SDL3.dll.
 */
#ifndef PAD_SDL3_H
#define PAD_SDL3_H
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct PadSdlPad {
    int      connected;
    uint16_t buttons;              /* CELL_PAD_CTRL_* */
    uint8_t  lx, ly, rx, ry;       /* 0..255, centre 128, up = 0 */
    uint8_t  l2, r2;               /* 0..255 */
} PadSdlPad;

/* Host poll thread only: SDL init on first call, hot-plug, state, rumble and light bar. */
void pad_sdl3_update(void);
/* Any thread: the latest state of the open gamepads in connection order; returns how many. */
int  pad_sdl3_get(PadSdlPad* out, int max);
/* Any thread: PS3 actuator values for gamepad <index> (as returned by pad_sdl3_get); applied by the poll thread. */
void pad_sdl3_rumble(int index, uint8_t small_motor, uint8_t large_motor);
/* PAD_RUMBLE=0 */
int  pad_rumble_enabled(void);

/* Light bar request from the title (0 = none, 0x01RRGGBB = colour); pad_sdl3.c applies it to every gamepad. */
extern volatile uint32_t g_pad_lightbar_rgb;

#ifdef __cplusplus
}
#endif
#endif
