# NVIDIA DLSS (DLAA) for the inFamous port

The game can use NVIDIA DLSS as **DLAA**: it renders at the resolution set on the launcher's Display page, and DLSS
replaces the jagged, shimmering edges with a clean, temporally stable image. Nothing is upscaled.

Turn it on in the launcher: **Graphics › Anti-aliasing › NVIDIA DLAA (DLSS)**, then pick the **DLSS model**:

| Model | What it is |
|---|---|
| Automatic | DLSS chooses (currently K, DLSS 4) |
| **L** | DLSS 4.5, best quality: sharpest, steadiest, least ghosting. Recommended on RTX 40/50 cards |
| M | DLSS 4.5, a little cheaper than L |
| K | DLSS 4 |
| J | DLSS 4, a little less ghosting but more flicker |

It needs an NVIDIA RTX graphics card. On other cards, or with DLSS off, the game renders exactly as before.

## DLSS 5 Neural Rendering (OptiScaler)

**Graphics › DLSS 5 Neural Rendering** runs NVIDIA's DLSS 5 model (`nvngx_dlssnr.dll`) over the DLAA image. The model synthesises lighting and material detail; the HUD is put back afterwards, untouched.
- **How it runs:** NVIDIA offers no public way to call the model, so it goes through OptiScaler-DLSSNR, a community fork of OptiScaler. That fork is not NVIDIA's own integration.
- **Requirements:** it needs DLAA switched on and an RTX 50 series card.

**How it is installed.**
- **Where the files live:** `launcher\dlss5` holds the extracted OptiScaler-DLSSNR v0.2.0 package and NVIDIA's model DLL (DLSSNR 310.9.16, NVIDIA-signed).
- **Switch on:** each Play copies them beside the game exe. `OptiScaler.dll` becomes `dxgi.dll`; the game imports dxgi.dll, so Windows loads that copy. OptiScaler then intercepts the port's own DLSS calls and runs the model right after DLAA. The launcher also sets three values in the installed `OptiScaler.ini`:
  - `[Upscalers] Dx12Upscaler=dlss`
  - `[DlssNr] Enabled=true`
  - `[Libraries] NvngxFeaturePath`, set to the port's DLSS runtime
- **Overlay changes:** anything else you change in OptiScaler's overlay and save is kept.
- **Switch off:** the next Play removes that `dxgi.dll`, and the game is back to plain DLAA. Only the launcher's own copy is ever removed; a `dxgi.dll` from another program is left alone.

**In game.**
- **Overlay:** press **Insert** for OptiScaler's overlay. The "DLSS Neural Rendering" section has the strength controls and a Difference debug view, and shows why the pass isn't running if it can't.
- **Log:** `OptiScaler.log` is written beside the game exe (`infamous-recomp\build-install`).

**A newer package or model.** Replace the files in `launcher\dlss5`; the next Play copies whatever changed. Updates of the game don't touch this folder.

**If the game misbehaves.** Turn the switch off.
- **Do it from the launcher:** don't delete files by hand. Starting the game with `inFamous.exe` instead of the launcher uses whatever the last launcher Play installed.
- **Status:** `"inFamous Launcher.exe" --dry-run FILE` reports the switch's state in its `dlss5=` line.

## After "Update inFamous"

An update replaces the game's source with the upstream version and rebuilds the game. That removes DLSS, the hook behind the launcher's cheat mods (Infinite XP, Max Karma, Unlock All Powers) and the PlayStation / Steam controller support. Then run **`apply-dlss.cmd`** in this folder. It puts all three back and rebuilds the game, which takes a few minutes. If an update only reverted some of the files, it adds just the hooks that are missing.
- **Safe to repeat:** running it twice is harmless. A file that already has the hooks is left alone.
- **Backups:** the files as they were before patching are kept in `backup\<date-time>`.
- **When it stops with "has to be ported by hand":** the update changed code next to one of the hooks. Nothing has been written, and the game runs fine without DLSS. The DLSS switch then simply does nothing.
- **When the build fails:** the source is put back as it was. The log is in `build.log`.

## Files

| Path | Purpose |
|---|---|
| `src\rsx_dlss.cpp`, `src\rsx_dlss.h` | the DLSS module; the script copies them to `ps3recomp\libs\video` |
| `src\infamous_cheats.h` | the cheat module behind the launcher's cheat mods, and the karma light bar; the script copies it to `infamous-recomp` |
| `src\pad_sdl3.c`, `src\pad_sdl3.h` | DualSense / DualShock 4 / Steam Controller support through SDL3 (`..\..\sdl3`); the script copies them to `ps3recomp\libs\input` |
| `apply-dlss.ps1` / `.cmd` | re-applies the hooks in `rsx_d3d12_backend.c`, `cellPad.c`, `ps3recomp\CMakeLists.txt` and `infamous-recomp\stubs.cpp`, then rebuilds |
| `original\` | the upstream files as they were before the patches were first added |
| `removed-upstream\` | stale `rsx_skate_actor.*` files an upstream update had left behind. They broke the build, so they were moved here |
| `..\..\dlss-sdk\` | NVIDIA DLSS SDK 310.9.16 (headers, import library, `nvngx_dlss.dll`); see its `LICENSE.txt` |

## How it works

These parts are for anyone changing the code.

**Jitter.** Every draw that renders the main 3D scene gets a sub-pixel camera jitter in its vertex epilogue. The jitter follows a Halton(2,3) sequence with 16 phases. These are draws to the full-size off-screen targets with the main depth buffer and depth test on. HUD draws, which go to the display buffer, are not jittered.

**Camera.** The game renders camera-relative, so the camera is reconstructed from the vertex constants:
- the projection × rotation is the most common `c263..c266`;
- the per-frame camera move is the consensus of how static objects' `c259` (position relative to the camera) changed. Usually 250+ objects agree to within 0.5 cm.

**Motion vectors.** The scene depth is copied right after the last scene draw, because the HUD clears it later. Each pixel is then reprojected into the previous frame.
- Where the previous frame's depth contradicts that reprojection, the pixel may belong to an object moving with the camera, such as Cole while you run. In that case, if "no motion" fits the previous depth, the pixel gets a zero vector.
- Where something nearer covered the pixel last frame, the pixel is newly visible and keeps the camera's vector.

**HUD.** DLSS runs on the scene right after the post-processing composite, before the HUD is drawn. The HUD is then copied back over DLSS's output pixel for pixel, so it is never blurred or ghosted.

**Texture LOD bias.** A −1 bias is applied while DLSS is on, as NVIDIA recommends.

**Limitation:** the game skins characters on the SPU, so there are no per-object motion vectors. Animated limbs rely on DLSS's own history rejection and can look slightly softer than the static world while they move.

### Environment switches

These are for testing; the launcher sets `DLSS` and `DLSS_PRESET`.

| Variable | Effect |
|---|---|
| `DLSS=1` | on |
| `DLSS_PRESET=<letter>` | DLAA model (J, K, L, M) |
| `DLSS_MIPBIAS=<f>` | texture LOD bias while on (default −1) |
| `DLSS_JITTER=0` / `DLSS_MV=0` / `DLSS_MVFIX=0` | no jitter / no motion vectors / camera vectors only |
| `DLSS_DEBUG=mv` | motion vectors over the image |
| `DLSS_DEBUG=depth` | the depth DLSS gets |
| `DLSS_DEBUG=zcheck` | which vector each pixel got: green camera, cyan zero motion, red neither, orange newly visible |
| `DLSS_DEBUG=warp` / `nowarp` | reprojection error |
| `DLSS_PROBE=<n>` | dump one gameplay frame's vertex-constant layout |
