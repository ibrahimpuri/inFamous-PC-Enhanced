# inFamous Launcher

`inFamous Launcher.exe` (in the game folder) is a settings app and launcher for the inFamous PC port.
It starts the game exactly like `inFamous.exe` does, from `installer\launch.txt`, and adds your choices on top.

## Where settings go

| What | File | Who else writes it |
|---|---|---|
| In-game menu settings (display mode, resolution, VSync, frame rate limit, button prompts, mouse speed, shadows, texture filtering, mipmaps) | `infamous-recomp\settings.ini` | the in-game menu (F10); the game lets this file win over everything else |
| Every other switch the game reads (draw distance, CPU core use, mouse feel, audio buffer, ...) | `launcher\launcher.ini`, `[env]` section, only the values you changed | nobody |
| Enabled mods, process priority, chosen build, play time | `launcher\launcher.ini` | nobody |
| The ReShade page | `launcher\launcher.ini`, `[reshade]` section; at Play also `ReShade.ini` and `ReShade-inFamous-Launcher.ini` beside the game exe | ReShade's overlay (its own settings in `ReShade.ini`) |

`installer\launch.txt` is never modified, so `infamous-installer update` can't overwrite your changes, and
deleting `launcher\launcher.ini` returns everything to the installer's defaults.

## Mods

A mod is a folder in `infamous-recomp\mods` with a `mod.ini`:

```ini
[mod]
name=Longer Draw Distance
version=1.0
author=you
description=What the mod changes.
experimental=0

[env]
LOD_CLASS_SCALE=3
```

Enabled mods are applied after your own settings (and lock the settings they change). `<unset>` removes a variable.

**Cheat mods.** The game reads these through `infamous-recomp\infamous_cheats.h`, which the launcher added to the port:

| Mod | What it does |
|---|---|
| Infinite XP | Available XP stays at 999,999; anything you spend is topped up again within a second |
| Max Karma: Hero / Max Karma: Infamous | Karma is held at maximum good or evil, so that side's upgrade ranks are met. Enable only one |
| Unlock All Powers | All 16 powers in the Powers menu are unlocked with every upgrade tier. Upgrades still cost XP and need the karma rank, so combine it with the other two |

- **Saving keeps the changes.** Once you save with a cheat on, the extra XP, karma or powers stay in that save even after you turn the mod off. Back up the save first (Saved Data page) if you want to keep a clean one.
- **New Game:** a new game started with Unlock All Powers on has every power from the first mission.
- **After "Update inFamous":** the update removes the hook, so run `dlss\apply-dlss.cmd` to put it back. That script re-applies DLSS and the cheats together.
The nine in-game menu settings can't be set by a mod. `mods\traffic-popin` is an example. `mods\skate` (Skate Mode)
needs its own build with `PORT_SKATE_MOD=ON`; the launcher detects such a build next to `build-install`.

## Controllers

The **Controller** page lists every connected controller with its type, connection and battery level, and a live view of what the game will receive (shown with PS3 buttons). It also has a vibration test and, on PlayStation pads, a light bar test.

| Controller | How the game reads it |
|---|---|
| DualSense, DualSense Edge, DualShock 4 (USB or Bluetooth) | Directly, through SDL3 (`infamous-recomp`'s cellPad + `ps3recomp\libs\input\pad_sdl3.c`) |
| Steam Controller | Directly through SDL3, or through Steam Input (as an Xbox controller) if you play from Steam |
| Xbox and other XInput pads, Steam Input's virtual pads | XInput, as before |

**Button mapping:**
- **Face buttons:** used positionally, so Cross is the bottom button on every pad.
- **Create / Share and the touchpad click:** SELECT.
- **Options:** START.
- **Back paddles and grips** (DualSense Edge, Steam Controller): L3 / R3.

**Settings on the page:**
- **Stick deadzone:** for PlayStation and Steam pads.
- **Touchpad as SELECT.**
- **Vibration.**
- **Light bar colour:** the "Follows your karma" option turns it blue for good and red for evil.

**Things to know:**
- **Steam Controller with Steam open:** if Steam is open while you play, it also turns the Steam Controller into mouse and keyboard input. Either close Steam, or add the game to Steam and play through Steam Input with "Read Steam Controllers directly" switched off.
- **DS4Windows / DSX:** these add a second, virtual Xbox controller. Close them, or let them hide the real one.

SDL3 (3.4.18, zlib licence) sits in `sdl3\` next to the game. The game loads `SDL3.dll` from there when it starts; without it, only XInput controllers work. After "Update inFamous", run `dlss\apply-dlss.cmd`: it re-applies the controller support with the other patches.

## NVIDIA DLSS (DLAA and upscaling)

The Graphics page has an **NVIDIA DLSS** switch, a **DLSS mode** and a **DLSS model** choice. L and M are the DLSS 4.5
models. The modes:
- **DLAA** anti-aliases at the chosen resolution.
- **Quality / Balanced / Performance** render the 3D scene at 67 / 58 / 50 % of it, never below 720p, and DLSS
  rebuilds the image at full size. The game is mostly CPU-limited, so they lower GPU load and heat more than they
  raise FPS. At 720p output there is nothing to upscale, and they act as DLAA.

DLSS is added to the port's renderer by this launcher, not by upstream, so **after every "Update inFamous" run
`dlss\apply-dlss.cmd`** to put it back and rebuild the game. Details, limits and debug switches:
[dlss\README.md](dlss/README.md).

**DLSS 5 Neural Rendering** (same page) runs NVIDIA's DLSS 5 model over the DLSS image through OptiScaler-DLSSNR, a
community mod kept in `dlss5\`. When the switch is on, Play installs it beside the game exe as `dxgi.dll`; when it is
off, Play removes it again. See the DLSS 5 section of [dlss\README.md](dlss/README.md).

## ReShade

The **ReShade** page adds post-processing on top of the finished picture, with effects written for inFamous:
- sharpening and clarity, for texture detail;
- ambient occlusion from the game's depth;
- debanding;
- colour: haze removal, contrast, vibrance, white balance;
- a vignette.

Pick a look (Natural, Remastered, Vivid, Cinematic) and fine-tune the sliders. Or switch **Effects** to *My own
ReShade preset* and build one in ReShade's overlay from any shader packs in `reshade\shaders`.

- **In game:** Home opens ReShade's overlay; End switches the effects off and on.
- **Getting ReShade:** download `ReShade_Setup_<version>.exe` from reshade.me and press **Add ReShade…** on the page. Only `ReShade64.dll` is taken out of it; the setup never runs.
- **How it loads:** Play installs ReShade beside the game exe as `dxgi.dll`. While DLSS 5 is on, OptiScaler owns `dxgi.dll`, so ReShade goes in as `ReShade64.dll` and OptiScaler loads it.

Details: [reshade\README.md](reshade/README.md).

## Potato PC preset and Low-end CPU mode

The first card on the Graphics page, **Potato PC**, is for low-end PCs. The game is CPU-bound, so it mostly cuts CPU
work:
- **Graphics:** 720p, 30 FPS cap, shadows off, the game's own texture filtering.
- **Detail:** 0.75× draw distance, 0.75× vehicle draw distance, distant animation LOD on.
- **Extras off:** DLSS and ReShade.
- **Audio:** a larger audio buffer that stretches instead of crackling.
- **CPU:** it turns on **Low-end CPU mode** (Performance page).

**Low-end CPU mode.** The shipped thread setup is tuned for big CPUs. On a 4-core CPU it livelocks:
- **What goes wrong:** while the game's main thread waits for SPU work, the worker threads spin-wait instead of sleeping, and so does the main thread. The thread that would hand out the next batch of work never gets a core.
- **What the mode changes:** spin-waits become sleeps, and core reservations are off. It also clears one runtime scheduling bit (ABX `0x04`, through the port's A/B mechanism, set once).
- **Your overrides still win:** any of these variables you set yourself on the Advanced page takes priority.
- **The hint:** on a PC with 8 logical processors or fewer, the Performance page suggests the mode.

**Measured on four E-cores of a Core Ultra 7** (about a budget quad-core), uncapped, 2.5-minute camera pan:

| Setup | Average FPS | 1 % low |
|---|---|---|
| Shipped settings | never reached the world (8 min) | |
| High settings + Low-end CPU mode | 42.6 | 29.0 |
| **Potato PC** (with Low-end CPU mode) | **48.9** | **32.6** |

So the 30 FPS cap holds with headroom. On the full 24-core CPU, Low-end CPU mode cost about 2 % average FPS: 90.5 against 92.3 at High. In that one run its 1 % lows were better, 48.9 against 41.8.

## Command line

```
"inFamous Launcher.exe"                  open the launcher
"inFamous Launcher.exe" --play           start the game with the saved settings, no window
"inFamous Launcher.exe" --dry-run FILE   write the exe, arguments and environment --play would use to FILE
```

## Building

`build.cmd` compiles `src\*.cs` with the Roslyn C# compiler from the Visual Studio Build Tools the port already
needs, against .NET Framework 4.8 (part of Windows 10 and 11), and writes `..\inFamous Launcher.exe`.
`make_icon.ps1` redraws `assets\icon.ico`.

## Engine limits worth knowing

* **Frame rate limit** offers Off / 60 / 50 / 45 / 40 / 30. Those are the in-game menu's steps: when `settings.ini`
  exists, the game maps any other value back to Off (`rsx_ui_overlay.c`, `menu_init`). Higher caps such as 120 or 144
  need a change in the port's source.
* **Resolution** is the internal render scale (720p × 1 to × 3); the window itself is sized by the game.
* The game reads all settings at startup. Changes made while it is running apply the next time it starts.
