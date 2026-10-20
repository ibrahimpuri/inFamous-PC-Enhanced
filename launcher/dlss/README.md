# NVIDIA DLSS (DLAA and upscaling) for the inFamous port

The game can use NVIDIA DLSS in two ways:
- **DLAA:** the game renders at the resolution set on the launcher's Display page, and DLSS replaces the jagged,
  shimmering edges with a clean, temporally stable image.
- **Upscaling (Quality / Balanced / Performance):** the 3D scene renders at a lower resolution and DLSS rebuilds it at
  the full size.

Turn it on in the launcher: **Graphics › Anti-aliasing › NVIDIA DLSS**, then pick the **DLSS mode**:

| Mode | Renders at | At 1440p output (Display › Internal resolution 2×) |
|---|---|---|
| **DLAA** | 100 %: native, no upscaling | 2560×1440 |
| Quality | 67 % | 1708×960 |
| Balanced | 58 % | 1486×836 |
| Performance | 50 % | 1280×720 |

- **Never below 720p:** the render size is never smaller than the PS3's own 1280×720. At the 1× internal resolution
  (720p output) the upscaling modes therefore have nothing to do, and the game simply uses DLAA.
- **What it saves:** the game is mostly CPU-limited, so upscaling lowers GPU load and heat far more than it raises FPS.
  On the test PC at 1440p, measured in the city, the FPS stayed at about 85 in all four modes while GPU load
  went from 39 % (DLAA) to 26 % (Quality), 24 % (Balanced) and 19 % (Performance).
- **HUD:** the HUD is drawn at the render size and enlarged smoothly with the image. Menus, loading screens and videos
  skip DLSS and are scaled up the same way.
- **Image:** Quality is close to DLAA once the camera settles; in fast camera moves the upscaling modes are a little
  softer.

Then pick the **DLSS model**:

| Model | What it is |
|---|---|
| Automatic | DLSS chooses (currently K, DLSS 4) |
| **L** | DLSS 4.5, best quality: sharpest, steadiest, least ghosting. Recommended on RTX 40/50 cards |
| M | DLSS 4.5, a little cheaper than L |
| K | DLSS 4 |
| J | DLSS 4, a little less ghosting but more flicker |

It needs an NVIDIA RTX graphics card. On other cards, or with DLSS off, the game renders exactly as before.

## AMD FSR 3.1 / FSR 4 and FSR frame generation

**Graphics › AMD FSR** is the same thing for every graphics card, through AMD's FSR API.
- **Which FSR runs:** AMD's loader picks the best version for the GPU. FSR 4 (machine learning) runs on Radeon RX 7000 / 9000, FSR 3.1 everywhere else, NVIDIA and Intel included.
- **The rest is shared:** FSR gets exactly what DLSS gets, so the modes, the 720p floor, the HUD handling and the menus all work the same: the jittered scene, the depth snapshot and the camera motion vectors.
- **One or the other:** the launcher allows DLSS or FSR, not both.

| FSR mode | Renders at | At 1440p output |
|---|---|---|
| **Native AA** | 100 % | 2560×1440 |
| Quality | 67 % (1.5×) | 1708×960 |
| Balanced | 59 % (1.7×) | 1506×848 |
| Performance | 50 % (2×) | 1280×720 |

**FSR sharpening** sets FSR's own sharpening pass (off by default).

**Frame generation (AMD FSR)** shows a generated frame between every two the game renders. The game is CPU-bound, so this is the setting that raises the frame rate you see, especially in crowded streets.
- **Works with DLSS or FSR:** it needs one of them on, for the depth and motion vectors, on any graphics card. FSR 3.1 frame generation runs everywhere; FSR 4's ML frame generation runs on Radeon RX 9000.
- **How it hooks in:** the swap chain is wrapped in AMD's frame generation swap chain, which presents and paces the real and generated frames. It gets the upscaled image before the HUD went on, so the HUD stays sharp on generated frames.
- **Off where it would look wrong:** menus, loading screens and videos.
- **Measured on the test PC** (RTX 5070 Ti, FSR 3.1 frame generation): about 89 rendered FPS gave about 178 displayed. The port's own FPS counter shows rendered frames; an overlay that counts presents (NVIDIA's, for example) shows the displayed rate.
- **The trade-off:** a little input latency. It is best with a variable-refresh display and at least ~60 FPS before generation.
- **DLSS 5 stays off while it is on:** OptiScaler has its own swap chain handling, so the launcher doesn't install it then.
- **5800X3D-class CPUs:** on 8 cores / 16 threads the shipped thread setup measured the same as the Low-end CPU mode (85.6 vs 87.0 FPS on 8 cores, 88.6 vs 89.5 on 16 threads), so the scheduler isn't the bottleneck there. Crowds add per-character CPU work. Frame generation is the fix that shows; distant animation LOD and adaptive draw distance (Performance page) trim the work itself.

**The files.** FSR needs the AMD FSR SDK files in `<game>\fsr-sdk`, keeping the SDK's folder layout. Only these are needed, from the AMD FSR SDK 2.3.0 repository (github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, tag `v2.3.0`):
- `Kits\FidelityFX\api\include\` (`ffx_api.h`, `ffx_api_types.h`, `ffx_api_loader.h`, `dx12\ffx_api_dx12.h`);
- `Kits\FidelityFX\upscalers\include\ffx_upscale.h`;
- `Kits\FidelityFX\framegeneration\include\` (`ffx_framegeneration.h`, `ffx_framegeneration_api_types.h`, `dx12\ffx_api_framegeneration_dx12.h`);
- `Kits\FidelityFX\signedbin\` (`amd_fidelityfx_loader_dx12.dll`, `amd_fidelityfx_upscaler_dx12.dll`, `amd_fidelityfx_framegeneration_dx12.dll`: AMD-signed, loaded at run time);
- `docs\license.md`.

With only the FSR SDK and no DLSS SDK, the game is built with FSR alone, and the other way round.

**Not tested here: FSR 4.** The test PC has an NVIDIA card. FSR 3.1 upscaling and FSR 3.1 frame generation were checked in game; FSR 4 runs through the same calls, but only on Radeon cards.

## DLSS 5 Neural Rendering (OptiScaler)

**Graphics › DLSS 5 Neural Rendering** runs NVIDIA's DLSS 5 model (`nvngx_dlssnr.dll`) over the DLSS image. The model synthesises lighting and material detail; the HUD is put back afterwards, untouched.
- **How it runs:** NVIDIA offers no public way to call the model, so it goes through OptiScaler-DLSSNR, a community fork of OptiScaler. That fork is not NVIDIA's own integration.
- **Requirements:** it needs DLSS switched on (any mode) and an RTX 50 series card.

**How it is installed.**
- **Where the files live:** `launcher\dlss5` holds the extracted OptiScaler-DLSSNR v0.2.0 package and NVIDIA's model DLL (DLSSNR 310.9.16, NVIDIA-signed).
- **Switch on:** each Play copies them beside the game exe. `OptiScaler.dll` becomes `dxgi.dll`; the game imports dxgi.dll, so Windows loads that copy. OptiScaler then intercepts the port's own DLSS calls and runs the model right after DLSS. The launcher also sets three values in the installed `OptiScaler.ini`:
  - `[Upscalers] Dx12Upscaler=dlss`
  - `[DlssNr] Enabled=true`
  - `[Libraries] NvngxFeaturePath`, set to the port's DLSS runtime
- **Overlay changes:** anything else you change in OptiScaler's overlay and save is kept.
- **Switch off:** the next Play removes that `dxgi.dll`, and the game is back to plain DLSS. Only the launcher's own copy is ever removed; a `dxgi.dll` from another program is left alone.

**In game.**
- **Overlay:** press **Insert** for OptiScaler's overlay. The "DLSS Neural Rendering" section has the strength controls and a Difference debug view, and shows why the pass isn't running if it can't.
- **Log:** `OptiScaler.log` is written beside the game exe (`infamous-recomp\build-install`).

**A newer package or model.** Replace the files in `launcher\dlss5`; the next Play copies whatever changed. Updates of the game don't touch this folder.

**If the game misbehaves.** Turn the switch off.
- **Do it from the launcher:** don't delete files by hand. Starting the game with `inFamous.exe` instead of the launcher uses whatever the last launcher Play installed.
- **Status:** `"inFamous Launcher.exe" --dry-run FILE` reports the switch's state in its `dlss5=` line.

## Game language

inFamous has no language menu: it follows the PS3's system language. The launcher's **Display › Language** sets it through `PS3_LANG`, a name from the table below or a `CELL_SYSUTIL_LANG_` number.

**Why it needs two hooks:**
- **`cellSysutil.c`:** the port always reported English (US). The system language now comes from `PS3_LANG`.
- **`src\infamous_lang.h`:** that alone only gave French and Spanish.
  - **How the game picks:** it works out which release it is from the title ID the port reports, matched against the seven it knows (`BCUS98119`, `BCES00220`, `BCES00591`, `BCES00609`, `BCAS20074`, `BCET00010`, `BCET00220`). Each release offers only the languages it shipped with.
  - **Why it fell back:** the port reports `BLES00000`, which matches none of them, so the game took the US release: English, French and Spanish.
  - **The fix:** when the game reads the system language, the hook points the US release's language list at the chosen language. The game's own lists already hold every European language.
  - **Saves stay put:** reporting a real release ID would have moved the save folder, because the game names it after that ID. The ID stays the same, so saves work in every language.

| Language | `PS3_LANG` | Voices | Text and subtitles |
|---|---|---|---|
| English | *(unset)*, `english` | English | English |
| English (UK) | `british` | English | British English |
| French | `french` | French | French |
| German | `german` | German | German |
| Italian | `italian` | Italian | Italian |
| Spanish | `spanish` | Spanish | Spanish |
| Dutch | `dutch` | Dutch | Dutch |
| Polish | `polish` | Polish | Polish |
| Portuguese | `portuguese` | Portuguese | Portuguese |
| Russian | `russian` | Russian | Russian |
| Korean | `korean` | English (none on the disc) | Korean |
| Chinese | `chinese_s` | English (none on the disc) | Chinese |

All twelve were run on the test PC: each one loaded its own dialogue archive (`dialog\archive\<language>.psarc`) and showed the HUD's karma rank in that language. The disc has Korean and Chinese text and subtitles, but no voices in them.

## After "Update inFamous" or a repair

An update or a repair restores the port's own copies of the five files the patches hook into, and rebuilds the game. That removes:
- DLSS;
- the hook behind the launcher's cheat mods (Infinite XP, Max Karma, Unlock All Powers);
- the PlayStation / Steam controller support;
- the game language (the launcher's Language setting goes back to English).

The launcher's own files stay, and the game still builds and runs without them.

**Getting them back:**
- **Update from the launcher:** Tools › **Update now** runs `update-and-patch.cmd`. It runs the update, then this script, then rebuilds the launcher, so nothing goes missing.
- **Updated another way** (the *Update inFamous* shortcut, or a repair): the launcher notices the missing hooks.
  - The bottom bar says so.
  - Play offers to put them back.
  - Tools › **Re-apply launcher patches** runs **`apply-dlss.cmd`**, which you can also run yourself.

The script puts them all back and rebuilds the game, which takes a few minutes. If only some files were reverted, it adds just the hooks that are missing.
- **The build can't break:** `pad_sdl3.c` compiles to nothing without its CMake hook. The port's CMakeLists compiles every `libs\input\*.c`, so before this guard an update or repair failed with `'SDL3/SDL_init.h' file not found`.
- **Safe to repeat:** running it twice is harmless. A file that already has the hooks is left alone.
- **Backups:** the files as they were before patching are kept in `backup\<date-time>`.
- **When it stops with "has to be ported by hand":** the update changed code next to one of the hooks. Nothing has been written, and the game runs fine without DLSS. The DLSS switch then simply does nothing.
- **When the build fails:** the source is put back as it was. The log is in `build.log`.
- **Without an SDK:** neither the DLSS SDK nor the FSR SDK is required. The script then notes that the game is built without upscaling and puts the other patches back as usual.
- **Adding an SDK after installing:** put it in `dlss-sdk` / `fsr-sdk` and run this script (Tools › **Re-apply launcher patches**). It re-runs CMake before building, so the new SDK is picked up.

## Files

| Path | Purpose |
|---|---|
| `src\rsx_dlss.cpp`, `src\rsx_dlss.h` | the DLSS module; the script copies them to `ps3recomp\libs\video` |
| `src\infamous_cheats.h` | the cheat module behind the launcher's cheat mods, and the karma light bar; the script copies it to `infamous-recomp` |
| `src\infamous_lang.h` | the game language behind the launcher's Language setting (see [Game language](#game-language)); the script copies it to `infamous-recomp` |
| `src\pad_sdl3.c`, `src\pad_sdl3.h` | DualSense / DualShock 4 / Steam Controller support through SDL3 (`..\..\sdl3`); the script copies them to `ps3recomp\libs\input` |
| `apply-dlss.ps1` / `.cmd` | re-applies the hooks in `rsx_d3d12_backend.c`, `cellPad.c`, `cellSysutil.c`, `ps3recomp\CMakeLists.txt` and `infamous-recomp\stubs.cpp`, then rebuilds |
| `update-and-patch.cmd` | the launcher's *Update now*: `infamous-installer update`, then `apply-dlss.ps1`, then `..\build.cmd` (the launcher) |
| `original\` | the upstream files without the patches (refreshed to upstream `cdb581579f`), for testing the script |
| `removed-upstream\` | stale `rsx_skate_actor.*` files an upstream update had left behind. They broke the build, so they were moved here |
| `..\..\dlss-sdk\` | NVIDIA DLSS SDK 310.9.16 (headers, import library, `nvngx_dlss.dll`); see its `LICENSE.txt` |
| `..\..\fsr-sdk\` | AMD FSR SDK 2.3.0 files (headers + the signed loader / upscaler / frame generation DLLs); see `docs\license.md` |

## How it works

These parts are for anyone changing the code.

**Jitter.** Every draw that renders the main 3D scene gets a sub-pixel camera jitter in its vertex epilogue. These are draws to the full-size off-screen targets with the main depth buffer and depth test on. HUD draws, which go to the display buffer, are not jittered.
- **Sequence:** Halton(2,3), with 8 × (output ÷ render)² phases, at least 16 and at most 64. DLAA uses 16, Quality 18, Balanced 24, Performance 32.

**Camera.** The game renders camera-relative, so the camera is reconstructed from the vertex constants:
- the projection × rotation is the most common `c263..c266`;
- the per-frame camera move is the consensus of how static objects' `c259` (position relative to the camera) changed. Usually 250+ objects agree to within 0.5 cm.

**Motion vectors.** The scene depth is copied right after the last scene draw, because the HUD clears it later. Each pixel is then reprojected into the previous frame.
- Where the previous frame's depth contradicts that reprojection, the pixel may belong to an object moving with the camera, such as Cole while you run. In that case, if "no motion" fits the previous depth, the pixel gets a zero vector.
- Where something nearer covered the pixel last frame, the pixel is newly visible and keeps the camera's vector.

**HUD.** The scene is copied right after the post-processing composite, before the HUD is drawn, and that copy goes to DLSS. Wherever the finished frame differs from the copy, the HUD (or a dialog) drew there, and that pixel is taken from the finished frame instead of DLSS's output. In DLAA this is a pixel-for-pixel replace, so the HUD is never blurred or ghosted. When upscaling, each output pixel blends the 2×2 render pixels under it, so the HUD is enlarged smoothly and its edges fade into the DLSS image.

**Upscaling.** In an upscaling mode, the backend keeps two sizes:
- **Render size** (`RBW` × `RBH`): the Display page's internal resolution × the mode's ratio, never below 720p. Render targets, the depth buffer, the display buffer and every guest-size surface (through `RES_SCALE`) use it.
- **Output size** (`BBW` × `BBH`): the swap chain, the window and the overlay keep the full size.
- **Each frame:** at present, the display buffer (the finished frame at the render size) is handed to DLSS instead of being copied to the backbuffer. DLSS writes the output size straight into the backbuffer, and the HUD is merged on top.
- **Frames DLSS skips:** menus, loading screens and videos (fewer than 50 scene draws) get a bilinear upscale instead.
- **Without DLSS:** if NGX can't start (no RTX card, missing DLL), the render size goes back to the output size before anything is created, and the game runs as without DLSS.

**Texture LOD bias.** log2(render ÷ output) − 1, as NVIDIA recommends: −1 for DLAA, about −1.6 for Quality, −1.8 for Balanced and −2 for Performance.

**Limitation:** the game skins characters on the SPU, so there are no per-object motion vectors. Animated limbs rely on DLSS's own history rejection and can look slightly softer than the static world while they move.

### Environment switches

These are for testing; the launcher sets `DLSS`, `DLSS_MODE` and `DLSS_PRESET`.

| Variable | Effect |
|---|---|
| `DLSS=1` | on |
| `DLSS_MODE=<mode>` | `quality`, `balanced`, `performance` or `ultra` (Ultra Performance, 33 %; not in the launcher). Unset: DLAA |
| `DLSS_PRESET=<letter>` | DLSS model (J, K, L, M), for every mode |
| `DLSS_MIPBIAS=<f>` | texture LOD bias while on (default log2(render ÷ output) − 1) |
| `DLSS_JITTER=0` / `DLSS_MV=0` / `DLSS_MVFIX=0` | no jitter / no motion vectors / camera vectors only |
| `DLSS_DEBUG=mv` | motion vectors over the image |
| `DLSS_DEBUG=depth` | the depth DLSS gets |
| `DLSS_DEBUG=zcheck` | which vector each pixel got: green camera, cyan zero motion, red neither, orange newly visible |
| `DLSS_DEBUG=warp` / `nowarp` | reprojection error |
| `DLSS_PROBE=<n>` | dump one gameplay frame's vertex-constant layout |
| `FSR=1` | AMD FSR instead of DLSS (wins when both are set) |
| `FSR_MODE=<mode>` | `native` (default), `quality`, `balanced`, `performance`, `ultra` |
| `FSR_SHARPNESS=<0..1>` | FSR's sharpening (default 0, off) |
| `FSR_VERSION=<text>` | use the FSR provider whose name contains it, e.g. `3.1` (the log lists them) |
| `FSR_FG=1` | AMD FSR frame generation (with DLSS or FSR on) |
| `FSR_DLL_DIR=<dir>` | where `amd_fidelityfx_loader_dx12.dll` is (default: next to the exe, then `fsr-sdk\Kits\FidelityFX\signedbin`) |
