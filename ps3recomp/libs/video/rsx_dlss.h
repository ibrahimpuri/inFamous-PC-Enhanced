/* NVIDIA DLSS (DLAA) for the D3D12 backend -- rsx_dlss.cpp.
 *
 * Built only when the DLSS SDK sits next to the checkout (dlss-sdk/, see CMakeLists.txt), which defines
 * PS3RECOMP_DLSS. Off unless DLSS=1 (the launcher's Graphics page sets it).
 *   DLSS_MODE=dlaa (default): the game renders at its normal internal resolution and DLSS replaces the aliased image
 *     with a temporally anti-aliased one of the same size.
 *   DLSS_MODE=quality | balanced | performance (| ultra): upscaling. The game renders at 67 / 58 / 50 (/ 33) % of the
 *     output size (RES_SCALE), never below the PS3's own 720p, and DLSS rebuilds the full-size image. The HUD is
 *     drawn by the game at the render size too; it is laid over DLSS's output enlarged smoothly. Frames DLSS does not
 *     run on (menus, loading screens, video) are enlarged bilinear.
 *
 * What the backend provides:
 *   - record time (FIFO thread): every draw that rasterizes the main 3D scene gets a sub-pixel camera jitter
 *     added to its viewport epilogue (rsx_dlss_draw_qualifies / rsx_dlss_jitter_ndc), so successive frames
 *     sample different points inside each pixel;
 *   - hand-off (render_frame): the jitter and camera of the batch going to the render thread;
 *   - end of the presented frame (render thread): the finished back buffer plus the scene depth surface.
 *     DLSS runs there, before the port's own overlay (FPS counter, settings menu, dialogs) is drawn. When upscaling,
 *     the backbuffer is the output size and the display buffer the render size: the backend hands the display buffer
 *     over (rsx_dlss_capture_finished) instead of copying it, and rsx_dlss_frame_end fills the backbuffer.
 *
 * Env: DLSS=1 on. DLSS_MODE=dlaa|quality|balanced|performance|ultra (above). DLSS_PRESET=<letter> the model
 *      (default: DLSS picks, K). DLSS_JITTER=0 no jitter (debug).
 *      DLSS_MV=0 zero motion vectors (debug). DLSS_MIPBIAS=<f> texture LOD bias while on (default: NVIDIA's
 *      log2(render/output) - 1). DLSS_JSIGN=-1 flips the jitter sign reported to DLSS. DLSS_DEBUG=mv shows the
 *      motion vectors instead of the image, =depth the depth, =warp the error left after moving last frame along
 *      the motion vectors (=nowarp: without them), =zcheck which vector each pixel got. DLSS_MVFIX=0: camera
 *      motion only (no zero-motion fallback for objects moving with the camera, e.g. Cole while running).
 *      DLSS_PROBE=<n>: write one gameplay frame's vertex-constant layout
 *      (dlss_probe.txt + dlss_probe_vs_*.hlsl in the working directory) after n gameplay frames.
 */
#ifndef RSX_DLSS_H
#define RSX_DLSS_H
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct ID3D12Device;
struct ID3D12GraphicsCommandList;
struct ID3D12Resource;

typedef struct rsx_dlss_draw_info {
    uint32_t frame;                 /* batch number being recorded (s_frame_no) */
    uint32_t rec_index;             /* the draw's record index in its batch */
    int vs_idx; uint32_t vs_hash;
    const char* vs_src;             /* decompiled HLSL of the draw's vertex program (may be NULL) */
    const uint32_t* cuse;           /* 16 words: vertex constants the program reads (NULL = unknown) */
    const float* consts;            /* rsx_state vertex constants, 512 x float4 */
    uint32_t rt_off, rt_w, rt_h, zeta_off, zw, zh;
    uint32_t vp_x, vp_y, vp_w, vp_h;
    uint32_t fp_addr, vcount, color_target, mrt;
    int depth_test, depth_mask, blend;
    float posscale[3], posoffset[3];
    uint32_t guest_w, guest_h;      /* guest display size (1280x720) */
} rsx_dlss_draw_info;

/* Before the window and the render targets exist: the share of the output size the game should render at
 * (1 = DLAA / off; the backend clamps the render size at the guest size). */
float rsx_dlss_render_ratio(void);
/* Device creation: starts NGX when DLSS=1. Afterwards rsx_dlss_active() says whether DLSS can run.
 * render_w/h = the internal render size; out_w/h = the backbuffer (larger when upscaling). */
void  rsx_dlss_device_init(struct ID3D12Device* dev, uint32_t render_w, uint32_t render_h, uint32_t out_w, uint32_t out_h);
int   rsx_dlss_active(void);
int   rsx_dlss_upscaling(void);      /* active, and the output is larger than the render size */
float rsx_dlss_mip_bias(void);       /* add to mipmapped samplers' LOD bias (0 when off) */
int   rsx_dlss_probe_on(void);

/* Record time. qualifies: this draw rasterizes the main scene (gets the jitter). jitter_ndc: the jitter of the
 * frame being recorded, as an NDC offset for the viewport epilogue. */
int   rsx_dlss_draw_qualifies(int depth_test, uint32_t zeta_off, uint32_t zw, uint32_t zh, uint32_t rt_off,
                              uint32_t rt_w, uint32_t rt_h, uint32_t vp_w, uint32_t vp_h,
                              uint32_t guest_w, uint32_t guest_h);
void  rsx_dlss_jitter_ndc(float* dx, float* dy);
void  rsx_dlss_note_draw(const rsx_dlss_draw_info* di, int qualifies);   /* camera tracking + probe */

/* render_frame: the batch just handed to the render thread; present = it ends with a Present. */
void  rsx_dlss_handoff(int present);
uint32_t rsx_dlss_exec_zeta(void);   /* main depth surface (guest offset) of the batch being executed */
/* Replay: index of the batch's last main-scene draw (0xFFFFFFFF = none). Right after it the scene depth is complete;
 * the game clears it later in the frame (its HUD draws with depth), so the backend hands it over there. */
uint32_t rsx_dlss_exec_last_scene_draw(void);
void  rsx_dlss_capture_depth(struct ID3D12GraphicsCommandList* cl, struct ID3D12Resource* zeta, uint32_t* zeta_state);
/* Replay: index of the full-screen draw that composites the finished scene onto the display buffer (0xFFFFFFFF =
 * none). Right after it the backend hands over the display buffer: DLSS works on that HUD-less image and the HUD
 * is laid back over the result unchanged. */
uint32_t rsx_dlss_exec_composite_draw(void);
void  rsx_dlss_capture_color(struct ID3D12GraphicsCommandList* cl, struct ID3D12Resource* display, uint32_t* state);
/* Upscaling, at present time: the finished display buffer (render size, in COPY_SOURCE) in place of the copy to the
 * backbuffer. */
void  rsx_dlss_capture_finished(struct ID3D12GraphicsCommandList* cl, struct ID3D12Resource* display);

/* End of a presented frame on the render thread: back buffer in RENDER_TARGET state (restored on return);
 * zeta = the scene depth surface (R24G8_TYPELESS) in zeta_state (restored on return), or NULL. */
void  rsx_dlss_frame_end(struct ID3D12GraphicsCommandList* cl, struct ID3D12Resource* backbuffer,
                         uint32_t w, uint32_t h, struct ID3D12Resource* zeta, uint32_t zeta_state);

#ifdef __cplusplus
}
#endif
#endif
