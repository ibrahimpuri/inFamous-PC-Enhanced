# ReShade for the inFamous port

The launcher's **ReShade** page adds post-processing on top of the finished picture, with effects written for
this game (`shaders\inFamous.fx`):

| Effect | What it does |
|---|---|
| Sharpening | Contrast-adaptive sharpening (after AMD FidelityFX CAS): crisper textures and edges, no halos |
| Clarity | Local contrast: brick, concrete and cloth detail stand out, overall contrast stays the same |
| Ambient occlusion | Soft contact shadows where surfaces meet, from the game's depth buffer |
| Deband | Smooths the 8-bit colour banding in the sky, fog and smoke |
| Colour | Haze removal, contrast, vibrance, saturation, exposure, shadows / highlights, warmth, tint |
| Vignette | Darkens the edges of the picture |

Pick a **look** (Natural, Remastered, Vivid, Cinematic), then fine-tune any slider. Every look switches ReShade on.

**In game:**
- **Home** opens ReShade's own overlay.
- **End** switches the effects off and on, for a before / after.

## What's in this folder

| Path | What it is |
|---|---|
| `ReShade64.dll` | ReShade itself (6.8.0, from reshade.me; BSD-3-Clause). Not in the GitHub repo: add it with the page's **Add ReShade…** button, which takes it out of `ReShade_Setup_<version>.exe` without running the setup |
| `shaders\inFamous.fx` | the effects above, plus debug views (depth, AO) and a split-screen compare |
| `shaders\`, `textures\` | ReShade's search folders: shader packs dropped here show up in the overlay |

## How it is installed

At every **Play** the launcher brings the game's exe folder (`infamous-recomp\build-install`) in line with the page:

- **ReShade on, DLSS 5 off:** `ReShade64.dll` is copied there as `dxgi.dll`. The game imports `dxgi.dll`, so Windows loads that copy, and ReShade loads the real one.
- **ReShade on, DLSS 5 on:** OptiScaler already is `dxgi.dll`. ReShade goes in as `ReShade64.dll` instead, and `OptiScaler.ini` gets `[Plugins] LoadReshade=true`, so OptiScaler loads it.
- **ReShade off:** the launcher's copies are taken out again. A `dxgi.dll` from another program is never touched.

It also writes two files beside the exe:
- **`ReShade.ini`:** points ReShade at this folder's shaders and sets what the game needs. The rest is left as you set it in the overlay. The settings it forces:
  - `DepthCopyBeforeClears=1`: the HUD clears the depth buffer, so ReShade has to copy it first.
  - Home / End as the default keys.
  - Depth preprocessor values (24-bit depth, not reversed) for shaders built on `ReShade.fxh`.
- **`ReShade-inFamous-Launcher.ini`:** the page's look as a ReShade preset. It is rewritten at every Play, so changes made to it in the overlay don't last.

To build your own preset in the overlay, set **Effects** to *My own ReShade preset*. The launcher then points ReShade at `ReShadePreset.ini` and leaves it alone.

## Notes

- **Depth works with DLSS:** ReShade finds the scene's depth buffer in DLAA and in the DLSS upscaling modes. With upscaling, depth is at the render size, and `inFamous.fx` reads it per depth texel.
- **The AO is built for this depth buffer:** the game's near and far planes aren't known, so it doesn't reconstruct positions. It extends each pixel's surface plane in raw depth, which is linear across flat surfaces, and darkens where the scene sticks out in front of it. Flat floors and walls stay clean, whatever the projection.
- **The HUD is included:** the effects run on the finished frame, so they touch the HUD too. The looks are mild enough that it stays clean.
- **Cost:** ambient occlusion is the most expensive effect. On a weak graphics card, lower it or turn ReShade off. The Potato PC preset turns ReShade off.
- **Debug techniques:** `inFamous.fx` also has *Depth view*, *Ambient occlusion view* and *Compare, start / end* (a split screen: untouched on the left, effects on the right). Enable them in the overlay to check what the effects do.
