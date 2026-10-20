# Puts the launcher's port patches back after "Update inFamous" replaced the game's source, then rebuilds the game:
# NVIDIA DLSS (DLAA) / AMD FSR, the gameplay cheats behind the launcher's mods (Infinite XP, Max Karma, Unlock All
# Powers), the PlayStation / Steam controller support (SDL3) and the game language (PS3_LANG). Start it with
# apply-dlss.cmd in this folder. Running it again is harmless: a hook that is already there is left alone.
#
# What it does:
#   1. checks the DLSS SDK files are in <game>\dlss-sdk (SDL3 in <game>\sdl3 is optional: without it the game is
#      built with XInput controllers only),
#   2. copies src\rsx_dlss.cpp + rsx_dlss.h into ps3recomp\libs\video, src\pad_sdl3.c + pad_sdl3.h into
#      ps3recomp\libs\input and src\infamous_cheats.h + infamous_lang.h into infamous-recomp,
#   3. adds the hooks to ps3recomp\libs\video\rsx_d3d12_backend.c, ps3recomp\libs\input\cellPad.c,
#      ps3recomp\libs\system\cellSysutil.c, ps3recomp\CMakeLists.txt and infamous-recomp\stubs.cpp. Each hook goes next to a line that must appear exactly
#      once; if an update changed one of those lines, nothing is written,
#   4. rebuilds the game (Visual Studio Build Tools + the existing build folder). If the build fails, the patched
#      files are put back as they were, so the next update applies cleanly.
# The files as they were before patching are kept in backup\<date-time>.
#
#   -NoBuild         patch only
#   -Ps3recomp DIR   patch another ps3recomp checkout (testing); -Port DIR the same for infamous-recomp
param([switch]$NoBuild, [string]$Ps3recomp = '', [string]$Port = '', [string]$BuildDir = '', [string]$BackupDir = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $Ps3recomp) { $Ps3recomp = Join-Path $root 'ps3recomp' }
if (-not $BuildDir)  { $BuildDir  = Join-Path $root 'infamous-recomp\build-install' }
if (-not $BackupDir) { $BackupDir = Join-Path $here 'backup' }
$sdk     = Join-Path $root 'dlss-sdk'
$video   = Join-Path $Ps3recomp 'libs\video'
$backend = Join-Path $video 'rsx_d3d12_backend.c'
$cmake   = Join-Path $Ps3recomp 'CMakeLists.txt'
$input_  = Join-Path $Ps3recomp 'libs\input'
$cellpad = Join-Path $input_ 'cellPad.c'
if (-not $Port) { $Port = Join-Path $root 'infamous-recomp' }
$stubs   = Join-Path $Port 'stubs.cpp'
$sysutil = Join-Path $Ps3recomp 'libs\system\cellSysutil.c'
$latin1  = [Text.Encoding]::GetEncoding(28591)   # byte-exact round trip whatever the files' encoding

function Fail([string]$m) { Write-Host ''; Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- the hooks
# A hook is there already when the file contains its marker: the replacement text, or the hook's longest line.
$H = New-Object System.Collections.Generic.List[object]
function Hunk([string]$file, [string]$op, [string]$anchor, [string]$text) {
    $marker = if ($op -eq 'replace') { $text } else { @($text -split "`r?`n" | ForEach-Object { $_.Trim() } | Sort-Object Length -Descending)[0] }
    $H.Add([pscustomobject]@{ File = $file; Op = $op; Anchor = $anchor; Text = $text; Marker = $marker })
}

Hunk backend after '#include "rsx_getenv_cache.h"' @'
#ifdef PS3RECOMP_DLSS
#include "rsx_dlss.h"           /* optional NVIDIA DLAA (DLSS=1), rsx_dlss.cpp */
#endif
'@

Hunk backend after 'if (lb && mip) sd.MipLODBias =' @'
#ifdef PS3RECOMP_DLSS
                if (mip) sd.MipLODBias += rsx_dlss_mip_bias();   /* DLAA: NVIDIA's negative texture LOD bias */
#endif
'@

Hunk backend before '/* Create swap chain */' @'
#ifdef PS3RECOMP_DLSS
    rsx_dlss_device_init(s_d3d.device, RBW, RBH, BBW, BBH);   /* DLSS=1: start NGX (no-op otherwise) */
    if (s_outw && !rsx_dlss_active()) {   /* upscaling asked for, but no DLSS here: render at the full size after all */
        s_bbw = s_outw; s_bbh = s_outh; s_res_scale = s_out_scale; s_outw = s_outh = 0;
        fprintf(stderr, "[RES] DLSS unavailable: rendering at the output size %ux%u%c", s_bbw, s_bbh, 10);
    }
#endif

'@

# AMD FSR frame generation: wrap the swap chain before its buffers are taken
Hunk backend before '/* Create RTV descriptor heap */' @'
#ifdef PS3RECOMP_DLSS
    /* FSR_FG=1: AMD FSR frame generation replaces the swap chain with its proxy (which presents real + generated frames) */
    { const int fgw = rsx_dlss_wrap_swapchain((void**)&s_d3d.swap_chain, s_d3d.cmd_queue);
      if (fgw < 0) { printf("[D3D12] ERROR: no swap chain left after the frame generation wrap\n"); return -1; }
      if (fgw > 0) s_d3d.frame_index = s_d3d.swap_chain->lpVtbl->GetCurrentBackBufferIndex(s_d3d.swap_chain); }
#endif
'@

# DLSS upscaling: the render size (RBW x RBH) and the output size (BBW x BBH) become separate
Hunk backend before '#define BBW (s_bbw ? s_bbw : s_d3d.width)' @'
/* DLSS upscaling renders below the output size: RBW x RBH = the internal render size (s_bbw: render targets, depth,
 * the display buffer), BBW x BBH = the swap chain / window / overlay size (s_outw while upscaling, else the same). */
static u32   s_outw = 0, s_outh = 0;
static float s_out_scale = 1.0f;
#define RBW (s_bbw ? s_bbw : s_d3d.width)
#define RBH (s_bbh ? s_bbh : s_d3d.height)
'@

Hunk backend replace '#define BBW (s_bbw ? s_bbw : s_d3d.width)' '#define BBW (s_outw ? s_outw : RBW)'
Hunk backend replace '#define BBH (s_bbh ? s_bbh : s_d3d.height)' '#define BBH (s_outh ? s_outh : RBH)'

Hunk backend after 'fprintf(stderr, "[RES] v154 internal resolution %ux%u (RES_SCALE %.2f, guest %ux%u)%c", s_bbw, s_bbh, sc, width, height, 10);' @'
#ifdef PS3RECOMP_DLSS
        {   /* DLSS upscaling (DLSS_MODE quality / balanced / performance): the game renders at the mode's share of that
             * size, never below the PS3's own 720p, and DLSS rebuilds the image at the full size */
            float rs = sc * rsx_dlss_render_ratio();
            if (rs < 1.0f) rs = 1.0f;
            if (rs < sc - 0.001f) {
                s_outw = s_bbw; s_outh = s_bbh; s_out_scale = sc;
                s_res_scale = rs;
                s_bbw = ((u32)((float)width * rs + 0.5f) + 1u) & ~1u;
                s_bbh = ((u32)((float)height * rs + 0.5f) + 1u) & ~1u;
                if (rs <= 1.0f) { s_bbw = width; s_bbh = height; }
                fprintf(stderr, "[RES] DLSS upscaling: rendering %ux%u, output %ux%u%c", s_bbw, s_bbh, s_outw, s_outh, 10);
            }
        }
#endif
'@

Hunk backend replace 's_d3d.off_rt[_k].w == BBW && s_d3d.off_rt[_k].h == BBH) {' 's_d3d.off_rt[_k].w == RBW && s_d3d.off_rt[_k].h == RBH) {'
Hunk backend replace 's_d3d.off_rt[last_disp_slot].w == BBW && s_d3d.off_rt[last_disp_slot].h == BBH &&' 's_d3d.off_rt[last_disp_slot].w == RBW && s_d3d.off_rt[last_disp_slot].h == RBH &&'

Hunk backend after 's_d3d.off_rt[last_disp_slot].dxgi == (u32)DXGI_FORMAT_R8G8B8A8_UNORM) {' @'
#ifdef PS3RECOMP_DLSS
              if (rsx_dlss_upscaling()) {   /* DLSS upscaling: the display buffer (render size) goes to DLSS, which fills the backbuffer */
                  off_rt_transition(last_disp_slot, D3D12_RESOURCE_STATE_COPY_SOURCE);
                  rsx_dlss_capture_finished(s_d3d.cmd_list, s_d3d.off_rt[last_disp_slot].res);
                  off_rt_transition(last_disp_slot, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
                  cur_rt = 0x7FFF;   /* force the backbuffer rebind below */
              } else
#endif
              {
'@

Hunk backend after 's_d3d.off_rt[last_disp_slot].off, last_disp_slot, 10); }' @'
              }   /* DLSS upscaling: end of the copy branch */
'@

Hunk backend before '{   /* v219g VCB_DEDUP' @'
#ifdef PS3RECOMP_DLSS
    /* DLAA: does this draw rasterize the main scene (full-size target + depth, depth test on)? Such draws get the
     * frame's sub-pixel jitter in the epilogue below, so it is part of the VCB_DEDUP identity. */
    const int dlss_q = dr ? rsx_dlss_draw_qualifies(st->depth_test_enable, dr->zeta_off, dr->zw, dr->zh, dr->rt_off,
                                                    dr->rt_w, dr->rt_h, dr->vp_w, dr->vp_h, s_d3d.width, s_d3d.height) : 0;
#else
    const int dlss_q = 0;
#endif
'@

Hunk backend after 'static const void* s_vcb_ve = NULL; static u32 s_vcb_hash = 0;' @'
        static int s_vcb_dq = -1;   /* DLAA jitter flag of the last full slot */
'@

Hunk backend replace '!memcmp(zin, s_vcb_z, sizeof zin) &&' '!memcmp(zin, s_vcb_z, sizeof zin) && s_vcb_dq == dlss_q &&'

Hunk backend after 'memcpy(s_vcb_z, zin, sizeof zin);' @'
        s_vcb_dq = dlss_q;
'@

Hunk backend after 'vpx[0] = k; vpx[1] = k; vpx[4] = -ux * k; vpx[5] = -uy * k; } }' @'

#ifdef PS3RECOMP_DLSS
    if (dr) {   /* DLAA: camera tracking / probe see the unjittered epilogue, then the scene draws get the jitter */
        const VPVSEntry* dve = (vs_idx >= 0 && vs_idx < s_d3d.vp_vs_n) ? &s_d3d.vp_vs[vs_idx] : NULL;
        rsx_dlss_draw_info di;
        di.frame = s_frame_no; di.rec_index = slot; di.vs_idx = vs_idx; di.vs_hash = dve ? dve->hash : 0;
        di.vs_src = dve ? dve->src : NULL; di.cuse = (dve && dve->cvalid && !dve->crel) ? dve->cuse : NULL;
        di.consts = (const float*)st->vertex_constants;
        di.rt_off = dr->rt_off; di.rt_w = dr->rt_w; di.rt_h = dr->rt_h; di.zeta_off = dr->zeta_off; di.zw = dr->zw; di.zh = dr->zh;
        di.vp_x = dr->vp_x; di.vp_y = dr->vp_y; di.vp_w = dr->vp_w; di.vp_h = dr->vp_h;
        di.fp_addr = dr->fp_addr; di.vcount = dr->vertex_count; di.color_target = st->color_target;
        di.mrt = (dr->rt_mrt[0] ? 1u : 0u) + (dr->rt_mrt[1] ? 1u : 0u) + (dr->rt_mrt[2] ? 1u : 0u);
        di.depth_test = st->depth_test_enable; di.depth_mask = st->depth_mask; di.blend = dr->blend;
        di.posscale[0] = vpx[0]; di.posscale[1] = vpx[1]; di.posscale[2] = vpx[2];
        di.posoffset[0] = vpx[4]; di.posoffset[1] = vpx[5]; di.posoffset[2] = vpx[6];
        di.guest_w = s_d3d.width; di.guest_h = s_d3d.height;
        rsx_dlss_note_draw(&di, dlss_q);
        if (dlss_q) rsx_dlss_jitter_ndc(&vpx[4], &vpx[5]);
    }
#endif
'@

Hunk backend after 'gq_mark(GQ_VPBEGIN);' @'
#ifdef PS3RECOMP_DLSS
            const u32 dlss_last_scene = rsx_dlss_exec_last_scene_draw();   /* DLAA: copy the scene depth after this draw */
            const u32 dlss_composite = rsx_dlss_exec_composite_draw();     /* ... and the HUD-less image after this one */
#endif
'@

Hunk backend before '/* Conversion stores depth only, not stencil. Read-only particles' @'
#ifdef PS3RECOMP_DLSS
                if (d == dlss_last_scene && cur_zs >= 0 && s_zs[cur_zs].res) {   /* scene complete: its depth for DLAA */
                    u32 zst = (u32)s_zs[cur_zs].st;
                    rsx_dlss_capture_depth(s_d3d.cmd_list, s_zs[cur_zs].res, &zst);
                    s_zs[cur_zs].st = (D3D12_RESOURCE_STATES)zst;
                }
                if (d == dlss_composite) {   /* post-processed scene on the display buffer, HUD not drawn yet */
                    if (cur_rt >= 0 && cur_rt < MAX_OFF_RTS && s_d3d.off_rt[cur_rt].res) {
                        u32 rst = (u32)s_d3d.off_rt[cur_rt].st;
                        rsx_dlss_capture_color(s_d3d.cmd_list, s_d3d.off_rt[cur_rt].res, &rst);
                        s_d3d.off_rt[cur_rt].st = (D3D12_RESOURCE_STATES)rst;
                    } else if (cur_rt == -1) {
                        u32 rst = (u32)D3D12_RESOURCE_STATE_RENDER_TARGET;
                        rsx_dlss_capture_color(s_d3d.cmd_list, s_d3d.render_targets[fi], &rst);
                    }
                }
#endif
'@

Hunk backend after 'composite_present(fi);' @'
#ifdef PS3RECOMP_DLSS
    if (s_rf_present && rsx_dlss_active()) {   /* DLAA over the finished game image, before the port's overlay */
        const int dz = zs_find_off(rsx_dlss_exec_zeta(), s_d3d.width, s_d3d.height);
        rsx_dlss_frame_end(s_d3d.cmd_list, s_d3d.render_targets[fi], BBW, BBH,
                           dz >= 0 ? s_zs[dz].res : NULL, dz >= 0 ? (u32)s_zs[dz].st : 0u);
    }
#endif
'@

Hunk backend after 's_rf_present = s_present_this_frame;' @'
#ifdef PS3RECOMP_DLSS
    rsx_dlss_handoff(s_present_this_frame);   /* this batch's jitter/camera -> the render thread; next frame's jitter */
#endif
'@

Hunk cmake after 'list(APPEND HLE_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/libs/video/rsx_shader_msl.cpp")' @'
# Optional temporal upscaling for the D3D12 backend (libs/video/rsx_dlss.cpp): NVIDIA DLSS (DLSS=1 at run time) when
# the DLSS SDK is next to the checkout (dlss-sdk/include + lib/Windows_x86_64/x64/nvsdk_ngx_d.lib), AMD FSR 3.1 / 4
# (FSR=1) when the AMD FSR SDK is (fsr-sdk/Kits/FidelityFX: headers; its signed DLLs are loaded at run time).
set(PS3RECOMP_DLSS_SDK "${CMAKE_CURRENT_SOURCE_DIR}/../dlss-sdk" CACHE PATH "NVIDIA DLSS SDK folder (optional)")
set(PS3RECOMP_FSR_SDK "${CMAKE_CURRENT_SOURCE_DIR}/../fsr-sdk" CACHE PATH "AMD FSR SDK folder (optional)")
set(_ps3recomp_ngx OFF)
set(_ps3recomp_ffx OFF)
if(WIN32 AND EXISTS "${PS3RECOMP_DLSS_SDK}/include/nvsdk_ngx.h"
         AND EXISTS "${PS3RECOMP_DLSS_SDK}/lib/Windows_x86_64/x64/nvsdk_ngx_d.lib")
    set(_ps3recomp_ngx ON)
endif()
if(WIN32 AND EXISTS "${PS3RECOMP_FSR_SDK}/Kits/FidelityFX/upscalers/include/ffx_upscale.h")
    set(_ps3recomp_ffx ON)
endif()
set(_ps3recomp_dlss OFF)
if(_ps3recomp_ngx OR _ps3recomp_ffx)
    set(_ps3recomp_dlss ON)
    list(APPEND HLE_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/libs/video/rsx_dlss.cpp")
endif()
'@

Hunk cmake before '# HLE modules include their own headers' @'
if(_ps3recomp_dlss)
    target_compile_definitions(ps3recomp_runtime PRIVATE PS3RECOMP_DLSS=1)
endif()
if(_ps3recomp_ngx)
    target_include_directories(ps3recomp_runtime PRIVATE "${PS3RECOMP_DLSS_SDK}/include")
    target_compile_definitions(ps3recomp_runtime PRIVATE PS3RECOMP_NGX=1)
    target_link_libraries(ps3recomp_runtime PUBLIC "${PS3RECOMP_DLSS_SDK}/lib/Windows_x86_64/x64/nvsdk_ngx_d.lib")
    message(STATUS "  DLSS:         ON (${PS3RECOMP_DLSS_SDK})")
else()
    message(STATUS "  DLSS:         off (no DLSS SDK in ${PS3RECOMP_DLSS_SDK})")
endif()
if(_ps3recomp_ffx)
    target_include_directories(ps3recomp_runtime PRIVATE "${PS3RECOMP_FSR_SDK}/Kits/FidelityFX")
    target_compile_definitions(ps3recomp_runtime PRIVATE PS3RECOMP_FFX=1)
    message(STATUS "  FSR:          ON (${PS3RECOMP_FSR_SDK})")
else()
    message(STATUS "  FSR:          off (no AMD FSR SDK in ${PS3RECOMP_FSR_SDK})")
endif()

'@

Hunk stubs after '#include "car_v233.h"' @'

/* Gameplay cheats for the launcher's mods: CHEAT_XP / CHEAT_KARMA / CHEAT_ALL_POWERS (inert unless set; see the header) */
#include "infamous_cheats.h"
'@

Hunk stubs after '#include "infamous_cheats.h"' @'
/* The game language for the launcher's Language setting: PS3_LANG (inert unless it names a language the US release
 * lacks; see the header) */
#include "infamous_lang.h"
'@

Hunk cellpad after '#include "../../runtime/ppu/ppu_memory.h"' @'
#ifdef PS3RECOMP_SDL3
#include "pad_sdl3.h"           /* PlayStation / Steam controllers through SDL3 (pad_sdl3.c) */
#endif
'@

Hunk cellpad after '#endif /* PAD_BACKEND_XINPUT */' @'

#if defined(PS3RECOMP_SDL3) && PAD_BACKEND_XINPUT
/* PlayStation / Steam controllers (pad_sdl3.c) take the host ports XInput left empty, in connection order.
 * s_sdl_slot[port] = their pad_sdl3 index, for rumble (-1 = not an SDL pad). */
static int s_sdl_slot[PAD_MAX_HOST_PORTS] = { -1, -1, -1, -1 };
static void pad_merge_sdl3(void)
{
    PadSdlPad sp[PAD_MAX_HOST_PORTS];
    const int ns = pad_sdl3_get(sp, PAD_MAX_HOST_PORTS);
    int k = 0;
    for (int i = 0; i < PAD_MAX_HOST_PORTS; i++) {
        s_sdl_slot[i] = -1;
        if (s_host_state[i].connected || k >= ns) continue;
        const PadSdlPad* p = &sp[k];
        PadHostState* h = &s_host_state[i];
        const u16 b = p->buttons;
        h->connected = 1; h->buttons = b;
        h->analog_lx = p->lx; h->analog_ly = p->ly; h->analog_rx = p->rx; h->analog_ry = p->ry;
        h->trigger_l2 = p->l2; h->trigger_r2 = p->r2;
        h->press_up = (b & CELL_PAD_CTRL_UP) ? 255 : 0;             h->press_down = (b & CELL_PAD_CTRL_DOWN) ? 255 : 0;
        h->press_left = (b & CELL_PAD_CTRL_LEFT) ? 255 : 0;         h->press_right = (b & CELL_PAD_CTRL_RIGHT) ? 255 : 0;
        h->press_triangle = (b & CELL_PAD_CTRL_TRIANGLE) ? 255 : 0; h->press_circle = (b & CELL_PAD_CTRL_CIRCLE) ? 255 : 0;
        h->press_cross = (b & CELL_PAD_CTRL_CROSS) ? 255 : 0;       h->press_square = (b & CELL_PAD_CTRL_SQUARE) ? 255 : 0;
        h->press_l1 = (b & CELL_PAD_CTRL_L1) ? 255 : 0;             h->press_r1 = (b & CELL_PAD_CTRL_R1) ? 255 : 0;
        /* controller activity -> controller prompts (sticks past ~37 %, like the XInput path) */
        const int moved = p->lx < 80 || p->lx > 176 || p->ly < 80 || p->ly > 176 || p->rx < 80 || p->rx > 176 || p->ry < 80 || p->ry > 176;
        if (b || moved) pad_note_device(0);
        else if (g_input_kbm < 0) pad_note_device(0);
        s_sdl_slot[i] = k++;
    }
}
#endif
'@

Hunk cellpad after 'pad_poll_xinput();' @'
#ifdef PS3RECOMP_SDL3
    pad_merge_sdl3();   /* PlayStation / Steam controllers into the ports XInput left empty */
#endif
'@

Hunk cellpad after 'while (s_pad_thread_run) {' @'
#ifdef PS3RECOMP_SDL3
        pad_sdl3_update();   /* SDL3 gamepads: every SDL call happens on this thread */
#endif
'@

Hunk cellpad after 'param = GUEST_PTR(param, CellPadActParam*);' @'
#if defined(PS3RECOMP_SDL3) && PAD_BACKEND_XINPUT
    if (!pad_rumble_enabled()) return CELL_OK;   /* PAD_RUMBLE=0 */
    if (port_no < PAD_MAX_HOST_PORTS && s_sdl_slot[port_no] >= 0) {   /* a PlayStation / Steam controller */
        pad_sdl3_rumble(s_sdl_slot[port_no], param->motor[CELL_PAD_ACTUATOR_PARAM_SMALL], param->motor[CELL_PAD_ACTUATOR_PARAM_LARGE]);
        return CELL_OK;
    }
#endif
'@

Hunk cmake after 'list(APPEND HLE_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/libs/video/rsx_shader_msl.cpp")' @'
# Optional SDL3 gamepads for cellPad (libs/input/pad_sdl3.c: DualSense, DualShock 4, Steam Controller). Built only
# when the SDL3 headers are present next to the checkout (sdl3/include); SDL3.dll is loaded at run time.
set(PS3RECOMP_SDL3_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../sdl3" CACHE PATH "SDL3 folder for PlayStation / Steam controllers (optional)")
set(_ps3recomp_sdl3 OFF)
if(WIN32 AND EXISTS "${PS3RECOMP_SDL3_DIR}/include/SDL3/SDL_gamepad.h")
    set(_ps3recomp_sdl3 ON)
    list(APPEND HLE_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/libs/input/pad_sdl3.c")
endif()
'@

Hunk cmake before '# HLE modules include their own headers' @'
if(_ps3recomp_sdl3)
    target_include_directories(ps3recomp_runtime PRIVATE "${PS3RECOMP_SDL3_DIR}/include")
    target_compile_definitions(ps3recomp_runtime PRIVATE PS3RECOMP_SDL3=1)
    message(STATUS "  SDL3 pads:    ON (${PS3RECOMP_SDL3_DIR})")
else()
    message(STATUS "  SDL3 pads:    off (no SDL3 in ${PS3RECOMP_SDL3_DIR})")
endif()

'@

# The game language (the launcher's Display page): the PS3 system language from PS3_LANG instead of always English
Hunk sysutil before '/* value_ea is a GUEST address, and it is spelt as one.' @'
/* inFamous launcher: the PS3 system language the game reads -- PS3_LANG, a name (english, british, french, german,
 * spanish, italian, dutch, polish, portuguese, russian, ...) or the CELL_SYSUTIL_LANG_ number; default English (US).
 * inFamous has no language menu: its spoken dialogue, text and subtitles follow this. The title's hook (inFamous:
 * infamous_lang.h) runs when the game reads it. */
__declspec(selectany) void (*g_sysutil_lang_hook)(int) = NULL;
static int sysutil_lang_eq(const char* a, const char* b)
{
    for (; *a && *b; a++, b++) { char x = *a, y = *b; if (x >= 'A' && x <= 'Z') x += 32; if (y >= 'A' && y <= 'Z') y += 32; if (x != y) return 0; }
    return *a == *b;
}
static s32 sysutil_lang_from_env(void)
{
    static s32 lang = -1;
    if (lang >= 0) return lang;
    static const struct { const char* n; s32 v; } k[] = {
        { "japanese", 0 }, { "english", 1 }, { "english_us", 1 }, { "french", 2 }, { "spanish", 3 }, { "german", 4 },
        { "italian", 5 }, { "dutch", 6 }, { "portuguese", 7 }, { "portuguese_pt", 7 }, { "russian", 8 }, { "korean", 9 },
        { "chinese_t", 10 }, { "chinese_s", 11 }, { "finnish", 12 }, { "swedish", 13 }, { "danish", 14 }, { "norwegian", 15 },
        { "polish", 16 }, { "portuguese_br", 17 }, { "english_gb", 18 }, { "british", 18 }, { "turkish", 19 } };
    lang = CELL_SYSUTIL_LANG_ENGLISH_US;
    const char* e = getenv("PS3_LANG");
    if (e && *e) {
        char* end = NULL;
        const long n = strtol(e, &end, 10);
        if (end && end != e && !*end && n >= 0 && n <= 19) lang = (s32)n;
        else for (size_t i = 0; i < sizeof k / sizeof k[0]; i++) if (sysutil_lang_eq(e, k[i].n)) { lang = k[i].v; break; }
    }
    printf("[cellSysutil] system language %d (PS3_LANG=%s)\n", (int)lang, (e && *e) ? e : "not set");
    return lang;
}

'@
Hunk sysutil replace 'v = CELL_SYSUTIL_LANG_ENGLISH_US;' 'v = sysutil_lang_from_env(); if (g_sysutil_lang_hook) g_sysutil_lang_hook((int)v);   /* inFamous launcher: PS3_LANG */'

# ---------------------------------------------------------------- patching
# Returns the patched text, or $null when every hook for the file is already there. Throws when an anchor line of a
# missing hook is missing or appears more than once.
function Patch([string]$path, [string]$which) {
    $raw = [IO.File]::ReadAllText($path, $latin1)
    $nl = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.AddRange([string[]]($raw -split "`r?`n"))
    $added = 0
    foreach ($h in $H) {
        if ($h.File -ne $which) { continue }
        if ($raw.Contains($h.Marker)) { continue }
        $added++
        $hits = @()
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Contains($h.Anchor)) { $hits += $i } }
        if ($hits.Count -ne 1) {
            throw ("{0}: the line with '{1}' was found {2} times instead of once -- the update changed this code, so this patch has to be ported by hand." -f (Split-Path $path -Leaf), $h.Anchor, $hits.Count)
        }
        $i = $hits[0]
        if ($h.Op -eq 'replace') { $lines[$i] = $lines[$i].Replace($h.Anchor, $h.Text); continue }
        $txt = [string[]]($h.Text -split "`r?`n")
        if ($h.Op -eq 'after') { $lines.InsertRange($i + 1, $txt) } else { $lines.InsertRange($i, $txt) }
    }
    if (-not $added) { return $null }
    return ($lines -join $nl)
}

Write-Host 'inFamous: re-applying the launcher''s port patches (DLSS / FSR, gameplay cheats, PlayStation / Steam controllers, game language)'
# NVIDIA DLSS needs the DLSS SDK in <game>\dlss-sdk, AMD FSR the AMD FSR SDK files in <game>\fsr-sdk; one is enough.
$hasNgx = @('include\nvsdk_ngx.h', 'lib\Windows_x86_64\x64\nvsdk_ngx_d.lib', 'lib\Windows_x86_64\rel\nvngx_dlss.dll') |
          ForEach-Object { Test-Path (Join-Path $sdk $_) } | Where-Object { -not $_ } | Measure-Object | ForEach-Object { $_.Count -eq 0 }
$fsr = Join-Path $root 'fsr-sdk'
$hasFfx = (Test-Path (Join-Path $fsr 'Kits\FidelityFX\upscalers\include\ffx_upscale.h')) -and
          (Test-Path (Join-Path $fsr 'Kits\FidelityFX\signedbin\amd_fidelityfx_loader_dx12.dll'))
if (-not $hasNgx -and -not $hasFfx) {
    Fail "neither the NVIDIA DLSS SDK ($sdk) nor the AMD FSR SDK ($fsr) is there. Download the DLSS SDK from https://github.com/NVIDIA/DLSS (v310.9.16 or newer) into $sdk, and/or the AMD FSR SDK files (see README.md) into $fsr."
}
if (-not $hasNgx) { Write-Host "  note: no DLSS SDK in $sdk -- the game is built without NVIDIA DLSS" -ForegroundColor Yellow }
if (-not $hasFfx) { Write-Host "  note: no AMD FSR SDK in $fsr -- the game is built without AMD FSR" -ForegroundColor Yellow }
foreach ($f in $backend, $cellpad, $cmake, $stubs, $sysutil) { if (-not (Test-Path $f)) { Fail "$f not found." } }
if (-not (Test-Path (Join-Path $root 'sdl3\include\SDL3\SDL_gamepad.h'))) {
    Write-Host '  note: no SDL3 in <game>\sdl3 -- the game is built with Xbox (XInput) controllers only' -ForegroundColor Yellow
}

$patched = @()
try {
    foreach ($t in @(@($backend, 'backend'), @($cellpad, 'cellpad'), @($cmake, 'cmake'), @($stubs, 'stubs'), @($sysutil, 'sysutil'))) {
        $new = Patch $t[0] $t[1]
        if ($new -ne $null) { $patched += ,@($t[0], $new) }
    }
}
catch { Fail $_.Exception.Message }

foreach ($c in @(@('rsx_dlss.cpp', $video), @('rsx_dlss.h', $video), @('pad_sdl3.c', $input_), @('pad_sdl3.h', $input_), @('infamous_cheats.h', $Port), @('infamous_lang.h', $Port))) {
    $s = Join-Path $here "src\$($c[0])"; $d = Join-Path $c[1] $c[0]
    if (-not (Test-Path $d) -or (Get-FileHash $s).Hash -ne (Get-FileHash $d).Hash) { Copy-Item -LiteralPath $s -Destination $d -Force; Write-Host "  copied $($c[0])" }
}

$written = @()
if ($patched.Count) {
    $bk = Join-Path $BackupDir (Get-Date -Format 'yyyyMMdd-HHmmss')
    New-Item -ItemType Directory -Force $bk | Out-Null
    foreach ($w in $patched) {
        Copy-Item -LiteralPath $w[0] -Destination $bk
        [IO.File]::WriteAllText($w[0], $w[1], $latin1); $written += ,$w
        Write-Host ("  hooks added to {0}" -f (Split-Path $w[0] -Leaf))
    }
    Write-Host "  previous files kept in $bk"
} else {
    Write-Host '  the source already has all the hooks'
}

if ($NoBuild) { Write-Host 'Done (-NoBuild): build the game to use it.'; exit 0 }

# ---------------------------------------------------------------- rebuild
$cache = Join-Path $BuildDir 'CMakeCache.txt'
if (-not (Test-Path $cache)) { Fail "no build folder at $BuildDir -- run Update inFamous once first." }
$m = Select-String -Path $cache -Pattern '^CMAKE_MAKE_PROGRAM:[A-Z]+=(.+)$'
if (-not $m) { Fail "CMAKE_MAKE_PROGRAM is not set in $cache." }
$ninja = $m.Matches[0].Groups[1].Value
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { $null }
$vcvars = if ($vs) { Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat' } else { '' }
if (-not $vcvars -or -not (Test-Path $vcvars)) { Fail 'Visual Studio Build Tools (C++) were not found.' }

$exe = Join-Path $BuildDir 'inFamousRecomp.exe'
$before = if (Test-Path $exe) { (Get-Item $exe).LastWriteTimeUtc } else { [datetime]::MinValue }
$bat = Join-Path $env:TEMP 'infamous-apply-dlss.cmd'
$log = Join-Path $here 'build.log'
Set-Content -Path $bat -Encoding ASCII -Value @(
    '@echo off',
    "call `"$vcvars`" >nul",
    'if errorlevel 1 exit /b 1',
    "cd /d `"$BuildDir`"",
    "`"$ninja`"")
Write-Host 'Building the game (a few minutes)...'
$p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "`"$bat`"" -NoNewWindow -Wait -PassThru `
                   -RedirectStandardOutput $log -RedirectStandardError "$log.err"
Remove-Item -LiteralPath $bat -ErrorAction SilentlyContinue
$obj = Get-ChildItem -Path $BuildDir -Recurse -Filter 'rsx_dlss.cpp.obj' -ErrorAction SilentlyContinue | Select-Object -First 1
$after = if (Test-Path $exe) { (Get-Item $exe).LastWriteTimeUtc } else { [datetime]::MinValue }
if ($p.ExitCode -ne 0 -or -not $obj) {
    Write-Host ''
    Get-Content $log -Tail 25 | Where-Object { $_ -match 'error|FAILED' } | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
    foreach ($w in $written) { Copy-Item -LiteralPath (Join-Path $bk (Split-Path $w[0] -Leaf)) -Destination $w[0] -Force }
    $state = if ($after -eq $before) { 'The game build you had is unchanged.' } else { 'Run Update inFamous to rebuild the game without the patches.' }
    Fail "the build failed (full log: $log). The game source was put back as it was. $state"
}
Write-Host ''
Write-Host 'DLSS / FSR, the cheat mods, PlayStation / Steam controller support and the game language are back in the game.' -ForegroundColor Green
exit 0
