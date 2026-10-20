/* NVIDIA DLSS (DLAA) for the D3D12 backend. See rsx_dlss.h for what the backend hands over and the env switches.
 *
 * Per presented gameplay frame:
 *   1. copy the finished back buffer into colour_in (DLSS needs a shader-readable input),
 *   2. compute pass: scene depth -> R32F copy for DLSS + per-pixel motion vectors (camera reprojection),
 *   3. DLSS evaluate (DLAA: input size == output size) -> out,
 *   4. copy out back into the back buffer.
 * The port's overlay is drawn after this, so the FPS counter and menus never go through DLSS. */
#include <windows.h>
#include <d3d12.h>
#include <d3dcompiler.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_defs.h"
#include "nvsdk_ngx_params.h"
#include "nvsdk_ngx_helpers_d3d.h"
#include "rsx_dlss.h"

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace {

/* ---------------------------------------------------------------- configuration */
struct Cfg {
    int mode = 0;            /* 0 off, 1 DLAA */
    int jitter = 1, mv = 1, mvfix = 1, debug_mv = 0, probe = 0;
    float mipbias = -1.0f, jsign = 1.0f;
    int preset = 0;          /* NVSDK_NGX_DLSS_Hint_Render_Preset value, 0 = default */
};
Cfg g;
int g_cfg_loaded = 0;

void cfg_load()
{
    if (g_cfg_loaded) return;
    g_cfg_loaded = 1;
    const char* e = getenv("DLSS");
    if (e && *e && *e != '0' && _stricmp(e, "off") != 0) g.mode = 1;
    if ((e = getenv("DLSS_JITTER")) && *e == '0') g.jitter = 0;
    if ((e = getenv("DLSS_MV")) && *e == '0') g.mv = 0;
    if ((e = getenv("DLSS_MVFIX")) && *e == '0') g.mvfix = 0;
    if ((e = getenv("DLSS_DEBUG")) && _stricmp(e, "mv") == 0) g.debug_mv = 1;
    if (e && _stricmp(e, "depth") == 0) g.debug_mv = 2;
    if (e && _stricmp(e, "warp") == 0) g.debug_mv = 3;
    if (e && _stricmp(e, "nowarp") == 0) g.debug_mv = 4;
    if (e && _stricmp(e, "zcheck") == 0) g.debug_mv = 5;
    if ((e = getenv("DLSS_MIPBIAS")) && *e) g.mipbias = (float)atof(e);
    if ((e = getenv("DLSS_JSIGN")) && *e == '-') g.jsign = -1.0f;
    if ((e = getenv("DLSS_PROBE")) && *e) g.probe = atoi(e) > 0 ? atoi(e) : 1;
    if ((e = getenv("DLSS_PRESET")) && *e) {
        const char c = (char)toupper((unsigned char)*e);
        if (c >= 'A' && c <= 'O') g.preset = c - 'A' + 1;   /* A = 1 ... K = 11 (nvsdk_ngx_defs.h) */
    }
}

/* ---------------------------------------------------------------- NGX state */
ID3D12Device* s_dev = nullptr;
int s_ngx_ok = 0, s_failed = 0;
NVSDK_NGX_Parameter* s_params = nullptr;
NVSDK_NGX_Handle* s_feature = nullptr;
uint32_t s_feat_w = 0, s_feat_h = 0;
uint32_t s_render_w = 0, s_render_h = 0;

void fail(const char* what, long long code)
{
    fprintf(stderr, "[DLSS] %s failed (0x%llX) -- DLSS off for this run%c", what, (unsigned long long)code, 10);
    s_failed = 1;
}

/* ---------------------------------------------------------------- jitter (Halton 2,3; 16 phases) */
float halton(int i, int b)
{
    float f = 1.0f, r = 0.0f;
    while (i > 0) { f /= (float)b; r += f * (float)(i % b); i /= b; }
    return r;
}
uint32_t s_rec_jidx = 0;                 /* presented frames handed off so far (record side) */
void jitter_px(uint32_t k, float* jx, float* jy)
{
    const int i = (int)(k % 16u) + 1;
    *jx = halton(i, 2) - 0.5f;
    *jy = halton(i, 3) - 0.5f;
}

/* ---------------------------------------------------------------- per-batch state (record -> exec) */
struct Batch {
    float jx = 0, jy = 0;                /* jitter in render pixels */
    uint32_t qualifying = 0;             /* jittered draws in the frame */
    uint32_t last_q = 0xFFFFFFFFu;       /* record index of the batch's last main-scene draw */
    uint32_t composite = 0xFFFFFFFFu;    /* record index of the full-screen draw that puts the finished scene on the
                                          * display buffer (after it: only the HUD) */
    uint32_t zeta = 0;                   /* main depth surface (guest offset) */
    int cam_valid = 0; float cam[16] = {0};   /* camera-relative world -> D3D clip, row-major */
    int delta_valid = 0; float delta[3] = {0}; /* camera movement since the previous presented frame */
};
Batch s_rec, s_exec;
float s_prev_cam[16]; int s_prev_cam_valid = 0;
std::map<uint32_t, uint32_t> s_zeta_votes;

/* ---------------------------------------------------------------- camera (record side)
 * inFamous renders camera-relative. Its scene vertex programs compute
 *     r   = c256 * v.x + c257 * v.y + c258 * v.z + c259      object -> camera-relative world (columns)
 *     pos = c263 * r.x + c264 * r.y + c265 * r.z + c266 * r.w   projection x camera rotation (columns)
 * so the camera matrix is the most common c263..c266 of the frame's scene draws, and the camera's movement between
 * two frames is how far every STATIC object's c259 (its position relative to the camera) shifted: t_prev - t_cur.
 * Objects are matched across frames by program, material, vertex count and orientation (c256..c258); the shift
 * most of them agree on wins, so moving cars and people do not disturb it. */
struct CamVote { uint32_t n; float m[16]; float zs, zo; };
std::map<uint64_t, CamVote> s_cam_votes;
struct ObjT { float t[3]; uint32_t n; };
std::map<uint64_t, ObjT> s_obj_cur, s_obj_prev;
int s_obj_prev_valid = 0;

uint64_t fnv(const void* p, size_t n, uint64_t h = 1469598103934665603ull)
{
    const unsigned char* b = (const unsigned char*)p;
    for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; }
    return h;
}
inline int uses(const uint32_t* cu, int i) { return (cu[i >> 5] >> (i & 31)) & 1u; }

void cam_note(const rsx_dlss_draw_info* di)
{
    if (!di->cuse || !di->consts) return;
    const float* c = di->consts;
    if (uses(di->cuse, 263) && uses(di->cuse, 264) && uses(di->cuse, 265) && uses(di->cuse, 266)) {
        const uint64_t h = fnv(c + 263 * 4, 64);
        CamVote& v = s_cam_votes[h];
        if (!v.n) {   /* columns c263..c266 -> row-major rows; z lane through the draw's viewport epilogue */
            for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) v.m[r * 4 + k] = c[(263 + k) * 4 + r];
            v.zs = di->posscale[2]; v.zo = di->posoffset[2];
        }
        v.n++;
    }
    if (uses(di->cuse, 256) && uses(di->cuse, 257) && uses(di->cuse, 258) && uses(di->cuse, 259) && c[259 * 4 + 3] == 1.0f) {
        uint64_t h = fnv(c + 256 * 4, 48);                 /* orientation/scale columns */
        h = fnv(&di->vs_hash, 4, h); h = fnv(&di->fp_addr, 4, h); h = fnv(&di->vcount, 4, h);
        ObjT& o = s_obj_cur[h];
        if (!o.n) { o.t[0] = c[259 * 4 + 0]; o.t[1] = c[259 * 4 + 1]; o.t[2] = c[259 * 4 + 2]; }
        o.n++;
    }
}

/* The frame's camera and its movement since the previous presented frame. */
void cam_finish(Batch& b)
{
    b.cam_valid = 0; b.delta_valid = 0;
    const CamVote* best = nullptr; uint32_t total = 0;
    for (const auto& kv : s_cam_votes) { total += kv.second.n; if (!best || kv.second.n > best->n) best = &kv.second; }
    if (best && best->n >= 20 && best->n * 4 >= total) {
        memcpy(b.cam, best->m, sizeof b.cam);
        for (int k = 0; k < 4; k++) b.cam[8 + k] = best->zs * best->m[8 + k] + best->zo * best->m[12 + k];   /* GL z -> D3D z */
        b.cam_valid = 1;
    }
    if (s_obj_prev_valid) {
        std::vector<std::array<float, 3>> d;
        for (const auto& kv : s_obj_cur) {
            if (kv.second.n != 1) continue;                    /* instanced copies are ambiguous */
            auto it = s_obj_prev.find(kv.first);
            if (it == s_obj_prev.end() || it->second.n != 1) continue;
            d.push_back({ it->second.t[0] - kv.second.t[0], it->second.t[1] - kv.second.t[1], it->second.t[2] - kv.second.t[2] });
        }
        if (d.size() >= 8) {   /* the shift most matched objects agree on (within 0.5 cm) */
            size_t bi = 0, bn = 0;
            for (size_t i = 0; i < d.size(); i += (d.size() > 400 ? d.size() / 400 : 1)) {
                size_t n = 0;
                for (const auto& e : d)
                    if (fabsf(e[0] - d[i][0]) < 0.5f && fabsf(e[1] - d[i][1]) < 0.5f && fabsf(e[2] - d[i][2]) < 0.5f) n++;
                if (n > bn) { bn = n; bi = i; }
            }
            if (bn >= 6 && bn * 3 >= d.size()) {
                double s[3] = {0, 0, 0}; size_t n = 0;
                for (const auto& e : d)
                    if (fabsf(e[0] - d[bi][0]) < 0.5f && fabsf(e[1] - d[bi][1]) < 0.5f && fabsf(e[2] - d[bi][2]) < 0.5f) {
                        s[0] += e[0]; s[1] += e[1]; s[2] += e[2]; n++; }
                for (int k = 0; k < 3; k++) b.delta[k] = (float)(s[k] / (double)n);
                b.delta_valid = 1;
            }
            static int nlog = 0;
            if (nlog < 12 || (nlog % 600) == 0)
                fprintf(stderr, "[DLSS] camera: %s, moved %s (%.2f %.2f %.2f cm; %zu of %zu matched objects agree)%c",
                        b.cam_valid ? "found" : "NOT found", b.delta_valid ? "ok" : "UNKNOWN",
                        b.delta[0], b.delta[1], b.delta[2], bn, d.size(), 10);
            nlog++;
        }
    }
    s_obj_prev.swap(s_obj_cur); s_obj_cur.clear();
    s_obj_prev_valid = 1;
    s_cam_votes.clear();
}

/* ---------------------------------------------------------------- probe (one gameplay frame's constants) */
struct ProbeDraw { rsx_dlss_draw_info di; int q; std::vector<std::pair<int, std::vector<float>>> c; };
std::vector<ProbeDraw> s_pd;
std::map<int, std::string> s_pvs;
uint32_t s_pframe = 0xFFFFFFFFu; uint32_t s_pq = 0; int s_pstate = 0, s_pskip = 0;

void probe_write()
{
    FILE* f = fopen("dlss_probe.txt", "w");
    if (!f) return;
    fprintf(f, "# DLSS probe: frame %u, %zu draws (q = qualifies for jitter)\n", s_pframe, s_pd.size());
    for (size_t i = 0; i < s_pd.size(); i++) {
        const ProbeDraw& p = s_pd[i]; const rsx_dlss_draw_info& d = p.di;
        fprintf(f, "d %zu vs=%d h=%08X q=%d rt=%X %ux%u z=%X %ux%u vp=%u,%u,%u,%u dt=%d dm=%d ct=%X mrt=%u bl=%d fp=%X vc=%u ps=%g,%g,%g po=%g,%g,%g\n",
                i, d.vs_idx, d.vs_hash, p.q, d.rt_off, d.rt_w, d.rt_h, d.zeta_off, d.zw, d.zh, d.vp_x, d.vp_y, d.vp_w, d.vp_h,
                d.depth_test, d.depth_mask, d.color_target, d.mrt, d.blend, d.fp_addr, d.vcount,
                d.posscale[0], d.posscale[1], d.posscale[2], d.posoffset[0], d.posoffset[1], d.posoffset[2]);
        for (const auto& c : p.c)
            fprintf(f, " c%d=%.9g,%.9g,%.9g,%.9g\n", c.first, c.second[0], c.second[1], c.second[2], c.second[3]);
    }
    fclose(f);
    for (const auto& v : s_pvs) {
        char name[64]; snprintf(name, sizeof name, "dlss_probe_vs_%d.hlsl", v.first);
        FILE* h = fopen(name, "w");
        if (h) { fwrite(v.second.data(), 1, v.second.size(), h); fclose(h); }
    }
    fprintf(stderr, "[DLSS] probe: wrote dlss_probe.txt (%zu draws) and %zu vertex programs%c", s_pd.size(), s_pvs.size(), 10);
}

void probe_draw(const rsx_dlss_draw_info* di, int q)
{
    if (di->frame != s_pframe) {
        if (s_pstate == 1 && !s_pd.empty()) { probe_write(); s_pstate = 2; s_pd.clear(); }
        else if (s_pstate == 0 && s_pq >= 300) { if (--s_pskip <= 0) s_pstate = 1; }
        s_pframe = di->frame; s_pq = 0;
    }
    s_pq += (uint32_t)q;
    if (s_pstate != 1 || s_pd.size() >= 60000) return;
    ProbeDraw p; p.di = *di; p.q = q;
    p.di.vs_src = nullptr; p.di.cuse = nullptr; p.di.consts = nullptr;
    if (di->cuse && di->consts)
        for (int i = 0; i < 512; i++)
            if ((di->cuse[i >> 5] >> (i & 31)) & 1u)
                p.c.push_back({ i, std::vector<float>(di->consts + i * 4, di->consts + i * 4 + 4) });
    s_pd.push_back(std::move(p));
    if (di->vs_src && di->vs_idx >= 0 && !s_pvs.count(di->vs_idx)) s_pvs[di->vs_idx] = di->vs_src;
}

/* ---------------------------------------------------------------- GPU resources */
/* s_in = DLSS's colour input: the scene right after the composite (no HUD), or failing that the finished frame.
 * s_hud = the finished frame (with the HUD), used to put the HUD back over DLSS's output unchanged. */
ID3D12Resource *s_in = nullptr, *s_out = nullptr, *s_mv = nullptr, *s_dz = nullptr, *s_hud = nullptr;
D3D12_RESOURCE_STATES s_in_st, s_out_st, s_mv_st, s_dz_st, s_hud_st;
ID3D12Resource* s_pdz = nullptr;           /* last frame's s_dz: checks the camera's motion vectors */
ID3D12Resource* s_prev = nullptr;          /* DLSS_DEBUG=warp/nowarp only: last frame's s_in */
D3D12_RESOURCE_STATES s_prev_st, s_pdz_st;
float s_prev_jx = 0.0f, s_prev_jy = 0.0f;
ID3D12Resource* s_zcopy = nullptr;       /* the scene depth, copied right after the last scene draw */
D3D12_RESOURCE_STATES s_zcopy_st;
uint32_t s_zc_w = 0, s_zc_h = 0;
int s_zsnap = 0;                          /* scene depth taken since the last DLAA frame */
int s_csnap = 0;                          /* pre-HUD scene colour taken (into s_in) since the last DLAA frame */
uint32_t s_res_w = 0, s_res_h = 0;
ID3D12DescriptorHeap* s_heap = nullptr;   /* 0 depth SRV, 1-3 UAV mv/dz/out, 4-7 SRV scene/hud/prev/prev depth */
UINT s_heap_inc = 0;
ID3D12RootSignature* s_rs = nullptr;
ID3D12PipelineState *s_pso_mv = nullptr, *s_pso_dbg = nullptr, *s_pso_merge = nullptr;

void release(ID3D12Resource*& r) { if (r) { r->Release(); r = nullptr; } }

ID3D12Resource* make_tex(DXGI_FORMAT fmt, uint32_t w, uint32_t h, D3D12_RESOURCE_FLAGS fl, D3D12_RESOURCE_STATES st)
{
    D3D12_HEAP_PROPERTIES hp = {}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; rd.Width = w; rd.Height = h;
    rd.DepthOrArraySize = 1; rd.MipLevels = 1; rd.Format = fmt; rd.SampleDesc.Count = 1; rd.Flags = fl;
    ID3D12Resource* r = nullptr;
    if (FAILED(s_dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, st, nullptr, IID_PPV_ARGS(&r)))) return nullptr;
    return r;
}

/* Shared by the motion-vector and debug passes. Constants: ic = inverse of this frame's camera, pv = last frame's
 * camera x the camera move; dims = size, this frame's jitter (px); opt.x = 0 no vectors, 1 camera only, 2 camera +
 * fallback; opt.zw = last frame's jitter. pz = last frame's depth. */
#define DLSS_HLSL_COMMON \
    "cbuffer K : register(b0) { float4 ic0, ic1, ic2, ic3; float4 pv0, pv1, pv2, pv3; float4 dims; float4 opt; };\n" \
    "Texture2D<float> pz : register(t4);\n" \
    /* relative distance error of a predicted last-frame depth zq against last frame's depth at pixel position pos, \
       best of the 2x2 texels around it (1 - z is proportional to 1 / distance here): .x its size (-1 = off-screen  \
       last frame), .y signed: > 0 = last frame had something nearer there (this point was hidden behind it) */ \
    "float2 zerr(float2 pos, float zq) {\n" \
    "  if (pos.x < 0 || pos.y < 0 || pos.x >= dims.x || pos.y >= dims.y) return float2(-1, 0);\n" \
    "  int2 i = (int2)floor(pos - 0.5); float2 b = float2(1e9, 0);\n" \
    "  [unroll] for (int k = 0; k < 4; k++) {\n" \
    "    float zp = pz.Load(int3(clamp(i + int2(k & 1, k >> 1), int2(0, 0), int2(dims.xy) - 1), 0));\n" \
    "    float s = (zq - zp) / max(1.0 - zp, 1e-7);\n" \
    "    if (abs(s) < b.x) b = float2(abs(s), s);\n" \
    "  }\n" \
    "  return b;\n" \
    "}\n" \
    /* camera reprojection of the pixel id (depth z): .xy motion (px, last frame - this frame, unjittered), .z the  \
       depth it had last frame, .w 0 if it was behind the camera. The sample at a pixel centre was rasterized with  \
       the jitter added, so its unjittered position is centre - jitter. */ \
    "float4 reproj(uint2 id, float z) {\n" \
    "  float2 pc = (float2(id) + 0.5) - dims.zw;\n" \
    "  float4 p = float4(pc.x / dims.x * 2.0 - 1.0, 1.0 - pc.y / dims.y * 2.0, z, 1.0);\n" \
    "  float4 w = float4(dot(ic0, p), dot(ic1, p), dot(ic2, p), dot(ic3, p));\n" \
    "  float4 q = float4(dot(pv0, w), dot(pv1, w), dot(pv2, w), dot(pv3, w));\n" \
    "  if (q.w <= 1e-6) return float4(0, 0, z, 0);\n" \
    "  float2 n = q.xy / q.w;\n" \
    "  return float4(float2((n.x * 0.5 + 0.5) * dims.x, (0.5 - n.y * 0.5) * dims.y) - pc, q.z / q.w, 1);\n" \
    "}\n" \
    /* the vector to use: the camera's, unless this point would have been in front of what last frame shows there  \
       (so it moved) while 'moved with the camera' (zero motion, same depth) fits -- the player character the camera \
       follows. Where last frame had something nearer in front, the point is newly visible and the camera's vector \
       is right. .z: 0 camera (fits), 1 zero motion, 2 camera (nothing fits: animated/moving), 3 camera (newly visible) */ \
    "float3 pick(uint2 id, float z) {\n" \
    "  float4 r = reproj(id, z);\n" \
    "  if (r.w == 0) return float3(0, 0, 2);\n" \
    "  if (z >= 1.0 || opt.x < 1.5) return float3(r.xy, 0);\n" \
    "  float2 pc = (float2(id) + 0.5) - dims.zw;\n" \
    "  float2 ec = zerr(pc + r.xy + opt.zw, r.z);\n" \
    "  if (ec.x < 0.02) return float3(r.xy, 0);\n" \
    "  if (ec.y > 0.0) return float3(r.xy, 3);\n" \
    "  float2 e0 = zerr(pc + opt.zw, z);\n" \
    "  if (e0.x >= 0.0 && e0.x < 0.005 && e0.x * 4.0 < ec.x) return float3(0, 0, 1);\n" \
    "  return float3(r.xy, 2);\n" \
    "}\n"

const char* kCS =
    DLSS_HLSL_COMMON
    "Texture2D<float> zt : register(t0);\n"
    "RWTexture2D<float2> mv : register(u0);\n"
    "RWTexture2D<float> dz : register(u1);\n"
    "[numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID) {\n"
    "  if (id.x >= (uint)dims.x || id.y >= (uint)dims.y) return;\n"
    "  float z = zt.Load(int3(id.xy, 0));\n"
    "  dz[id.xy] = z;\n"
    "  mv[id.xy] = opt.x > 0.5 ? pick(id.xy, z).xy : float2(0, 0);\n"
    "}\n";

/* DLSS_DEBUG=mv: motion vectors as colour (red = x, green = y, 8 px = full); DLSS_DEBUG=depth: the depth DLSS
 * gets (bright = near); DLSS_DEBUG=warp: |this frame - last frame fetched along the motion vectors| x4 (black where
 * the vectors are right), nowarp: the same without the vectors, for comparison; zcheck: which vector each pixel got
 * (green = the camera's, consistent with last frame's depth; cyan = zero motion, an object moving with the camera;
 * red = nothing fits, e.g. an animated character; orange = newly visible; blue = sky). Written over DLSS's output. */
const char* kCSDbg =
    DLSS_HLSL_COMMON
    "RWTexture2D<float2> mv : register(u0);\n"
    "RWTexture2D<float> dz : register(u1);\n"
    "RWTexture2D<unorm float4> o : register(u2);\n"
    "Texture2D<float4> sc : register(t1);\n"
    "Texture2D<float4> pr : register(t3);\n"
    "float4 ld(int2 i) { return pr.Load(int3(clamp(i, int2(0, 0), int2(dims.xy) - 1), 0)); }\n"
    "[numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID) {\n"
    "  if (id.x >= (uint)dims.x || id.y >= (uint)dims.y) return;\n"
    "  if (opt.y > 4.5) {\n"
    "    float z = dz[id.xy];\n"
    "    if (z >= 1.0) { o[id.xy] = float4(0, 0, 0.4, 1); return; }\n"
    "    const float3 cols[4] = { float3(0, 1, 0), float3(0, 0.8, 1), float3(1, 0, 0), float3(1, 0.6, 0) };\n"
    "    float3 c = opt.x > 0.5 ? cols[(int)pick(id.xy, z).z] : float3(1, 0, 1);\n"
    "    float lum = dot(sc.Load(int3(id.xy, 0)).rgb, float3(0.3, 0.5, 0.2));\n"
    "    o[id.xy] = float4(c * (0.35 + 0.65 * lum), 1);\n"
    "    return;\n"
    "  }\n"
    "  if (opt.y > 2.5) {\n"
    /* this pixel's sample, unjittered, moved by its vector, re-jittered with last frame's jitter (opt.zw) */
    "    float2 m = opt.y > 3.5 ? float2(0, 0) : mv[id.xy];\n"
    "    float2 q = float2(id.xy) + m - dims.zw + opt.zw;\n"
    "    int2 i = (int2)floor(q); float2 f = q - floor(q);\n"
    "    float4 p = lerp(lerp(ld(i), ld(i + int2(1, 0)), f.x), lerp(ld(i + int2(0, 1)), ld(i + int2(1, 1)), f.x), f.y);\n"
    "    o[id.xy] = float4(saturate(abs(sc.Load(int3(id.xy, 0)).rgb - p.rgb) * 4.0), 1);\n"
    "    return;\n"
    "  }\n"
    "  if (opt.y > 1.5) { float z = dz[id.xy]; float v = saturate((1.0 - z) * 200.0); o[id.xy] = float4(v, v, z >= 1.0 ? 1 : v, 1); return; }\n"
    "  float2 m = mv[id.xy];\n"
    "  o[id.xy] = float4(saturate(0.5 + m.x / 16.0), saturate(0.5 + m.y / 16.0), saturate(length(m) / 32.0), 1);\n"
    "}\n";

/* Puts the HUD back: wherever the finished frame differs from the pre-HUD scene, the HUD (or a dialog) drew there,
 * so that pixel is taken from the finished frame instead of DLSS's output. */
const char* kCSMerge =
    DLSS_HLSL_COMMON
    "Texture2D<float4> sc : register(t1);\n"
    "Texture2D<float4> hu : register(t2);\n"
    "RWTexture2D<unorm float4> o : register(u2);\n"
    "[numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID) {\n"
    "  if (id.x >= (uint)dims.x || id.y >= (uint)dims.y) return;\n"
    "  float4 a = sc.Load(int3(id.xy, 0)), b = hu.Load(int3(id.xy, 0));\n"
    "  if (any(abs(a.rgb - b.rgb) > 0.5 / 255.0)) o[id.xy] = float4(b.rgb, 1);\n"
    "}\n";

int build_pipeline()
{
    if (s_pso_mv) return 1;
    /* root: [0] 40 constants b0, [1] SRV t0, [2] UAVs u0..u2, [3] SRVs t1..t4 */
    D3D12_DESCRIPTOR_RANGE r0 = {}; r0.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r0.NumDescriptors = 1;
    D3D12_DESCRIPTOR_RANGE r1 = {}; r1.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; r1.NumDescriptors = 3;
    D3D12_DESCRIPTOR_RANGE r2 = {}; r2.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; r2.NumDescriptors = 4; r2.BaseShaderRegister = 1;
    D3D12_ROOT_PARAMETER p[4] = {};
    p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS; p[0].Constants.Num32BitValues = 40;
    p[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[1].DescriptorTable.NumDescriptorRanges = 1;
    p[1].DescriptorTable.pDescriptorRanges = &r0; p[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[2].DescriptorTable.NumDescriptorRanges = 1;
    p[2].DescriptorTable.pDescriptorRanges = &r1; p[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[3].DescriptorTable.NumDescriptorRanges = 1;
    p[3].DescriptorTable.pDescriptorRanges = &r2; p[3].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    D3D12_ROOT_SIGNATURE_DESC rsd = {}; rsd.NumParameters = 4; rsd.pParameters = p;
    ID3DBlob *sig = nullptr, *err = nullptr;
    if (FAILED(D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1, &sig, &err))) {
        if (err) { fprintf(stderr, "[DLSS] root signature: %s%c", (const char*)err->GetBufferPointer(), 10); err->Release(); }
        return 0;
    }
    HRESULT hr = s_dev->CreateRootSignature(0, sig->GetBufferPointer(), sig->GetBufferSize(), IID_PPV_ARGS(&s_rs));
    sig->Release();
    if (FAILED(hr)) return 0;
    const char* srcs[3] = { kCS, kCSDbg, kCSMerge };
    const char* names[3] = { "dlss_mv", "dlss_dbg", "dlss_merge" };
    ID3D12PipelineState** outs[3] = { &s_pso_mv, &s_pso_dbg, &s_pso_merge };
    for (int k = 0; k < 3; k++) {
        ID3DBlob *cs = nullptr; err = nullptr;
        if (FAILED(D3DCompile(srcs[k], strlen(srcs[k]), names[k], nullptr, nullptr, "main", "cs_5_0",
                              D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &cs, &err))) {
            if (err) { fprintf(stderr, "[DLSS] shader: %s%c", (const char*)err->GetBufferPointer(), 10); err->Release(); }
            return 0;
        }
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
        pd.pRootSignature = s_rs; pd.CS.pShaderBytecode = cs->GetBufferPointer(); pd.CS.BytecodeLength = cs->GetBufferSize();
        hr = s_dev->CreateComputePipelineState(&pd, IID_PPV_ARGS(outs[k]));
        cs->Release();
        if (FAILED(hr)) return 0;
    }
    D3D12_DESCRIPTOR_HEAP_DESC hd = {}; hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = 8;
    hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(s_dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&s_heap)))) return 0;
    s_heap_inc = s_dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    return 1;
}

int ensure_resources(uint32_t w, uint32_t h)
{
    if (s_in && s_res_w == w && s_res_h == h) return 1;
    release(s_in); release(s_out); release(s_mv); release(s_dz); release(s_hud); release(s_prev); release(s_pdz);
    s_csnap = 0;
    if (g.debug_mv == 3 || g.debug_mv == 4) {
        s_prev = make_tex(DXGI_FORMAT_R8G8B8A8_UNORM, w, h, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
        s_prev_st = D3D12_RESOURCE_STATE_COPY_DEST;
    }
    s_pdz = make_tex(DXGI_FORMAT_R32_FLOAT, w, h, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
    s_pdz_st = D3D12_RESOURCE_STATE_COPY_DEST;
    s_in  = make_tex(DXGI_FORMAT_R8G8B8A8_UNORM, w, h, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
    s_hud = make_tex(DXGI_FORMAT_R8G8B8A8_UNORM, w, h, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
    s_out = make_tex(DXGI_FORMAT_R8G8B8A8_UNORM, w, h, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    s_mv  = make_tex(DXGI_FORMAT_R16G16_FLOAT, w, h, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    s_dz  = make_tex(DXGI_FORMAT_R32_FLOAT, w, h, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    if (!s_in || !s_out || !s_mv || !s_dz || !s_hud || !s_pdz) return 0;
    s_in_st = s_hud_st = D3D12_RESOURCE_STATE_COPY_DEST; s_out_st = s_mv_st = s_dz_st = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    s_res_w = w; s_res_h = h;
    {   /* slots 4, 5: the pre-HUD scene and the finished frame, read by the merge */
        D3D12_CPU_DESCRIPTOR_HANDLE c = s_heap->GetCPUDescriptorHandleForHeapStart();
        D3D12_SHADER_RESOURCE_VIEW_DESC sv = {};
        sv.Format = DXGI_FORMAT_R8G8B8A8_UNORM; sv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        sv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; sv.Texture2D.MipLevels = 1;
        c.ptr += 4 * (SIZE_T)s_heap_inc; s_dev->CreateShaderResourceView(s_in, &sv, c);
        c.ptr += s_heap_inc;             s_dev->CreateShaderResourceView(s_hud, &sv, c);
        c.ptr += s_heap_inc;             s_dev->CreateShaderResourceView(s_prev, &sv, c);   /* null views when off */
        sv.Format = DXGI_FORMAT_R32_FLOAT;
        c.ptr += s_heap_inc;             s_dev->CreateShaderResourceView(s_pdz, &sv, c);
    }
    /* fixed UAV descriptors: slots 1..3 = mv, dz, out (slot 0 = the scene depth SRV, rewritten per frame) */
    D3D12_CPU_DESCRIPTOR_HANDLE c = s_heap->GetCPUDescriptorHandleForHeapStart();
    D3D12_UNORDERED_ACCESS_VIEW_DESC u = {}; u.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
    u.Format = DXGI_FORMAT_R16G16_FLOAT; c.ptr += s_heap_inc; s_dev->CreateUnorderedAccessView(s_mv, nullptr, &u, c);
    u.Format = DXGI_FORMAT_R32_FLOAT;    c.ptr += s_heap_inc; s_dev->CreateUnorderedAccessView(s_dz, nullptr, &u, c);
    u.Format = DXGI_FORMAT_R8G8B8A8_UNORM; c.ptr += s_heap_inc; s_dev->CreateUnorderedAccessView(s_out, nullptr, &u, c);
    fprintf(stderr, "[DLSS] buffers %ux%u ready%c", w, h, 10);
    return 1;
}

void barrier(ID3D12GraphicsCommandList* cl, ID3D12Resource* r, D3D12_RESOURCE_STATES& cur, D3D12_RESOURCE_STATES to)
{
    if (cur == to) return;
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = r;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = cur; b.Transition.StateAfter = to;
    cl->ResourceBarrier(1, &b);
    cur = to;
}
void uav_barrier(ID3D12GraphicsCommandList* cl, ID3D12Resource* r)
{
    D3D12_RESOURCE_BARRIER b = {}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; b.UAV.pResource = r;
    cl->ResourceBarrier(1, &b);
}

/* 4x4 inverse (row-major, general) */
int invert4(const float* m, float* o)
{
    float inv[16];
    inv[0] = m[5]*m[10]*m[15] - m[5]*m[11]*m[14] - m[9]*m[6]*m[15] + m[9]*m[7]*m[14] + m[13]*m[6]*m[11] - m[13]*m[7]*m[10];
    inv[4] = -m[4]*m[10]*m[15] + m[4]*m[11]*m[14] + m[8]*m[6]*m[15] - m[8]*m[7]*m[14] - m[12]*m[6]*m[11] + m[12]*m[7]*m[10];
    inv[8] = m[4]*m[9]*m[15] - m[4]*m[11]*m[13] - m[8]*m[5]*m[15] + m[8]*m[7]*m[13] + m[12]*m[5]*m[11] - m[12]*m[7]*m[9];
    inv[12] = -m[4]*m[9]*m[14] + m[4]*m[10]*m[13] + m[8]*m[5]*m[14] - m[8]*m[6]*m[13] - m[12]*m[5]*m[10] + m[12]*m[6]*m[9];
    inv[1] = -m[1]*m[10]*m[15] + m[1]*m[11]*m[14] + m[9]*m[2]*m[15] - m[9]*m[3]*m[14] - m[13]*m[2]*m[11] + m[13]*m[3]*m[10];
    inv[5] = m[0]*m[10]*m[15] - m[0]*m[11]*m[14] - m[8]*m[2]*m[15] + m[8]*m[3]*m[14] + m[12]*m[2]*m[11] - m[12]*m[3]*m[10];
    inv[9] = -m[0]*m[9]*m[15] + m[0]*m[11]*m[13] + m[8]*m[1]*m[15] - m[8]*m[3]*m[13] - m[12]*m[1]*m[11] + m[12]*m[3]*m[9];
    inv[13] = m[0]*m[9]*m[14] - m[0]*m[10]*m[13] - m[8]*m[1]*m[14] + m[8]*m[2]*m[13] + m[12]*m[1]*m[10] - m[12]*m[2]*m[9];
    inv[2] = m[1]*m[6]*m[15] - m[1]*m[7]*m[14] - m[5]*m[2]*m[15] + m[5]*m[3]*m[14] + m[13]*m[2]*m[7] - m[13]*m[3]*m[6];
    inv[6] = -m[0]*m[6]*m[15] + m[0]*m[7]*m[14] + m[4]*m[2]*m[15] - m[4]*m[3]*m[14] - m[12]*m[2]*m[7] + m[12]*m[3]*m[6];
    inv[10] = m[0]*m[5]*m[15] - m[0]*m[7]*m[13] - m[4]*m[1]*m[15] + m[4]*m[3]*m[13] + m[12]*m[1]*m[7] - m[12]*m[3]*m[5];
    inv[14] = -m[0]*m[5]*m[14] + m[0]*m[6]*m[13] + m[4]*m[1]*m[14] - m[4]*m[2]*m[13] - m[12]*m[1]*m[6] + m[12]*m[2]*m[5];
    inv[3] = -m[1]*m[6]*m[11] + m[1]*m[7]*m[10] + m[5]*m[2]*m[11] - m[5]*m[3]*m[10] - m[9]*m[2]*m[7] + m[9]*m[3]*m[6];
    inv[7] = m[0]*m[6]*m[11] - m[0]*m[7]*m[10] - m[4]*m[2]*m[11] + m[4]*m[3]*m[10] + m[8]*m[2]*m[7] - m[8]*m[3]*m[6];
    inv[11] = -m[0]*m[5]*m[11] + m[0]*m[7]*m[9] + m[4]*m[1]*m[11] - m[4]*m[3]*m[9] - m[8]*m[1]*m[7] + m[8]*m[3]*m[5];
    inv[15] = m[0]*m[5]*m[10] - m[0]*m[6]*m[9] - m[4]*m[1]*m[10] + m[4]*m[2]*m[9] + m[8]*m[1]*m[6] - m[8]*m[2]*m[5];
    double det = (double)m[0]*inv[0] + (double)m[1]*inv[4] + (double)m[2]*inv[8] + (double)m[3]*inv[12];
    if (!(fabs(det) > 1e-30)) return 0;
    const float id = (float)(1.0 / det);
    for (int i = 0; i < 16; i++) o[i] = inv[i] * id;
    return 1;
}

int create_feature(ID3D12GraphicsCommandList* cl, uint32_t w, uint32_t h)
{
    if (s_feature && s_feat_w == w && s_feat_h == h) return 1;
    if (s_feature) { NVSDK_NGX_D3D12_ReleaseFeature(s_feature); s_feature = nullptr; }
    NVSDK_NGX_DLSS_Create_Params cp = {};
    cp.Feature.InWidth = w; cp.Feature.InHeight = h;
    cp.Feature.InTargetWidth = w; cp.Feature.InTargetHeight = h;
    cp.Feature.InPerfQualityValue = NVSDK_NGX_PerfQuality_Value_DLAA;
    cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure;
    if (g.preset) NVSDK_NGX_Parameter_SetUI(s_params, NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, (unsigned)g.preset);
    NVSDK_NGX_Result r = NGX_D3D12_CREATE_DLSS_EXT(cl, 1, 1, &s_feature, s_params, &cp);
    if (NVSDK_NGX_FAILED(r)) { fail("DLSS feature creation", r); s_feature = nullptr; return 0; }
    s_feat_w = w; s_feat_h = h;
    fprintf(stderr, "[DLSS] DLAA %ux%u created (preset %s)%c", w, h, g.preset ? "set" : "default", 10);
    return 1;
}

} // namespace

/* ======================================================================= C API */
extern "C" {

void rsx_dlss_device_init(ID3D12Device* dev, uint32_t render_w, uint32_t render_h)
{
    cfg_load();
    s_render_w = render_w; s_render_h = render_h;
    if (g.probe) { s_pskip = g.probe; fprintf(stderr, "[DLSS] probe armed: frame after %d gameplay frames%c", g.probe, 10); }
    if (!g.mode || !dev || s_ngx_ok || s_failed) return;
    s_dev = dev;
    /* NGX logs + scratch next to the game (cwd = infamous-recomp); the DLSS model DLL is looked up next to the
     * exe and in ..\..\dlss-sdk\lib\Windows_x86_64\rel (DLSS_DLL_DIR overrides). */
    static wchar_t data_dir[MAX_PATH], dll_dir[MAX_PATH], exe_dir[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, data_dir);
    GetModuleFileNameW(nullptr, exe_dir, MAX_PATH);
    if (wchar_t* s = wcsrchr(exe_dir, L'\\')) *s = 0;
    const char* dd = getenv("DLSS_DLL_DIR");
    if (dd && *dd) MultiByteToWideChar(CP_ACP, 0, dd, -1, dll_dir, MAX_PATH);
    else _snwprintf(dll_dir, MAX_PATH, L"%ls\\..\\..\\dlss-sdk\\lib\\Windows_x86_64\\rel", exe_dir);
    static const wchar_t* paths[2]; paths[0] = dll_dir; paths[1] = exe_dir;
    NVSDK_NGX_FeatureCommonInfo fi = {};
    fi.PathListInfo.Path = paths; fi.PathListInfo.Length = 2;
    fi.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_OFF;
    NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init_with_ProjectID("7c3a1e52-2f4b-4d6e-9a8c-1b5d0f2e6a93",
        NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0", data_dir, dev, &fi, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(r)) { fail("NGX init", r); return; }
    r = NVSDK_NGX_D3D12_GetCapabilityParameters(&s_params);
    if (NVSDK_NGX_FAILED(r) || !s_params) { fail("NGX capability query", r); return; }
    int avail = 0, need_drv = 0; unsigned drv_maj = 0, drv_min = 0;
    NVSDK_NGX_Parameter_GetI(s_params, NVSDK_NGX_Parameter_SuperSampling_Available, &avail);
    NVSDK_NGX_Parameter_GetI(s_params, NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &need_drv);
    NVSDK_NGX_Parameter_GetUI(s_params, NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor, &drv_maj);
    NVSDK_NGX_Parameter_GetUI(s_params, NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor, &drv_min);
    if (!avail) {
        fprintf(stderr, "[DLSS] DLSS is not available on this system%s (min driver %u.%u) -- off%c",
                need_drv ? ": the graphics driver is too old" : "", drv_maj, drv_min, 10);
        s_failed = 1; return;
    }
    s_ngx_ok = 1;
    fprintf(stderr, "[DLSS] NGX ready: DLAA at %ux%u, jitter %s, motion vectors %s, mip bias %+.2f%c",
            render_w, render_h, g.jitter ? "on" : "off", g.mv ? "on" : "off", g.mipbias, 10);
}

int rsx_dlss_active(void) { return s_ngx_ok && !s_failed; }
int rsx_dlss_probe_on(void) { cfg_load(); return g.probe && s_pstate < 2; }
float rsx_dlss_mip_bias(void) { cfg_load(); return (g.mode && !s_failed) ? g.mipbias : 0.0f; }

int rsx_dlss_draw_qualifies(int depth_test, uint32_t zeta_off, uint32_t zw, uint32_t zh, uint32_t rt_off,
                            uint32_t rt_w, uint32_t rt_h, uint32_t vp_w, uint32_t vp_h, uint32_t guest_w, uint32_t guest_h)
{
    if (!depth_test || !zeta_off) return 0;
    if (zw != guest_w || zh != guest_h) return 0;                         /* the full-size scene depth only */
    if (!rt_off) return 0;                  /* inFamous draws its scene off-screen; display-buffer draws are the HUD */
    if (rt_w != guest_w || rt_h != guest_h) return 0;                      /* no reduced-size passes */
    if (vp_w && vp_h && (vp_w != guest_w || vp_h != guest_h)) return 0;   /* no sub-viewport passes */
    return 1;
}

void rsx_dlss_jitter_ndc(float* dx, float* dy)
{
    if (!rsx_dlss_active() || !g.jitter || !s_render_w || !s_render_h) return;
    float jx, jy; jitter_px(s_rec_jidx, &jx, &jy);
    *dx += 2.0f * jx / (float)s_render_w;      /* geometry moves +jx px right ...  */
    *dy -= 2.0f * jy / (float)s_render_h;      /* ... and +jy px down (NDC y is up) */
}

void rsx_dlss_note_draw(const rsx_dlss_draw_info* di, int qualifies)
{
    if (!di) return;
    if (g.probe && s_pstate < 2) probe_draw(di, qualifies);
    if (!qualifies) {
        /* The composite: after the frame's scene, the first opaque full-screen triangle/quad on the display buffer
         * without depth test. In inFamous it applies the post-processing; everything after it is the HUD. */
        if (s_rec.qualifying && s_rec.composite == 0xFFFFFFFFu && di->rt_off == 0 && !di->blend && !di->depth_test &&
            di->vcount >= 3 && di->vcount <= 6 && di->vp_w == di->guest_w && di->vp_h == di->guest_h)
            s_rec.composite = di->rec_index;
        return;
    }
    s_rec.qualifying++;
    s_rec.last_q = di->rec_index;
    s_rec.composite = 0xFFFFFFFFu;   /* the scene went on after a candidate */
    s_zeta_votes[di->zeta_off]++;
    if (rsx_dlss_active() && g.mv) cam_note(di);
}

void rsx_dlss_handoff(int present)
{
    if (present && rsx_dlss_active() && g.mv) cam_finish(s_rec);
    s_exec = s_rec;
    uint32_t best = 0, bz = 0;
    for (const auto& v : s_zeta_votes) if (v.second > best) { best = v.second; bz = v.first; }
    s_exec.zeta = bz;
    if (rsx_dlss_active() && g.jitter) jitter_px(s_rec_jidx, &s_exec.jx, &s_exec.jy);
    else s_exec.jx = s_exec.jy = 0.0f;
    s_rec.last_q = 0xFFFFFFFFu;          /* record indices restart with the next batch */
    s_rec.composite = 0xFFFFFFFFu;
    if (present) {
        s_rec_jidx++;
        s_rec = Batch();
        s_zeta_votes.clear();
    }
}

uint32_t rsx_dlss_exec_zeta(void) { return s_exec.zeta; }
uint32_t rsx_dlss_exec_last_scene_draw(void) { return rsx_dlss_active() ? s_exec.last_q : 0xFFFFFFFFu; }
uint32_t rsx_dlss_exec_composite_draw(void) { return rsx_dlss_active() ? s_exec.composite : 0xFFFFFFFFu; }

void rsx_dlss_capture_color(ID3D12GraphicsCommandList* cl, ID3D12Resource* src, uint32_t* state)
{
    if (!rsx_dlss_active() || !cl || !src || !state) return;
    const D3D12_RESOURCE_DESC d = src->GetDesc();
    if (d.Format != DXGI_FORMAT_R8G8B8A8_UNORM) {
        static int n = 0; if (n++ < 2) fprintf(stderr, "[DLSS] display buffer format %d is not RGBA8 -- HUD goes through DLSS%c", (int)d.Format, 10);
        return;
    }
    if (!build_pipeline() || !ensure_resources((uint32_t)d.Width, d.Height)) return;
    D3D12_RESOURCE_STATES st = (D3D12_RESOURCE_STATES)*state;
    const D3D12_RESOURCE_STATES orig = st;
    barrier(cl, src, st, D3D12_RESOURCE_STATE_COPY_SOURCE);
    barrier(cl, s_in, s_in_st, D3D12_RESOURCE_STATE_COPY_DEST);
    cl->CopyResource(s_in, src);
    barrier(cl, src, st, orig);
    *state = (uint32_t)st;
    s_csnap = 1;
}

void rsx_dlss_capture_depth(ID3D12GraphicsCommandList* cl, ID3D12Resource* z, uint32_t* zstate)
{
    if (!rsx_dlss_active() || !cl || !z || !zstate) return;
    const D3D12_RESOURCE_DESC d = z->GetDesc();
    if (!s_zcopy || s_zc_w != (uint32_t)d.Width || s_zc_h != d.Height) {
        release(s_zcopy);
        s_zcopy = make_tex(DXGI_FORMAT_R24G8_TYPELESS, (uint32_t)d.Width, d.Height, D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL,
                           D3D12_RESOURCE_STATE_DEPTH_WRITE);
        if (!s_zcopy) { fail("depth snapshot buffer", 0); return; }
        s_zcopy_st = D3D12_RESOURCE_STATE_DEPTH_WRITE; s_zc_w = (uint32_t)d.Width; s_zc_h = d.Height;
    }
    D3D12_RESOURCE_STATES st = (D3D12_RESOURCE_STATES)*zstate;
    const D3D12_RESOURCE_STATES orig = st;
    barrier(cl, z, st, D3D12_RESOURCE_STATE_COPY_SOURCE);
    barrier(cl, s_zcopy, s_zcopy_st, D3D12_RESOURCE_STATE_COPY_DEST);
    cl->CopyResource(s_zcopy, z);
    barrier(cl, z, st, orig);
    *zstate = (uint32_t)st;
    s_zsnap = 1;
}

void rsx_dlss_frame_end(ID3D12GraphicsCommandList* cl, ID3D12Resource* bb, uint32_t w, uint32_t h,
                        ID3D12Resource* zeta, uint32_t zeta_state)
{
    if (!rsx_dlss_active() || !cl || !bb || !w || !h) return;
    (void)zeta; (void)zeta_state;              /* the frame-end depth is the HUD's; the snapshot holds the scene */
    static int s_hist_ok = 0;                  /* DLSS history belongs to an unbroken run of gameplay frames */
    if (s_exec.qualifying < 50) { s_hist_ok = 0; s_zsnap = 0; return; }   /* menus, loading screens, video */
    if (!s_zsnap || !s_zcopy) {
        static int n = 0; if (n++ < 3) fprintf(stderr, "[DLSS] no scene depth snapshot this frame -- skipped%c", 10);
        s_hist_ok = 0; return;
    }
    s_zsnap = 0;
    ID3D12Resource* const zs = s_zcopy;
    if (!build_pipeline() || !ensure_resources(w, h)) { fail("DLSS resources", 0); return; }
    if (!create_feature(cl, w, h)) return;

    /* 1. colour: the pre-HUD scene (taken right after the composite) + the finished frame for the HUD merge;
     *    without that snapshot, the finished frame itself (the HUD then goes through DLSS too) */
    D3D12_RESOURCE_STATES bb_st = D3D12_RESOURCE_STATE_RENDER_TARGET;
    const int merge = s_csnap;
    s_csnap = 0;
    barrier(cl, bb, bb_st, D3D12_RESOURCE_STATE_COPY_SOURCE);
    if (merge) {
        barrier(cl, s_hud, s_hud_st, D3D12_RESOURCE_STATE_COPY_DEST);
        cl->CopyResource(s_hud, bb);
        barrier(cl, s_hud, s_hud_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    } else {
        barrier(cl, s_in, s_in_st, D3D12_RESOURCE_STATE_COPY_DEST);
        cl->CopyResource(s_in, bb);
        static int n = 0; if (n++ < 3) fprintf(stderr, "[DLSS] no pre-HUD snapshot this frame -- HUD included%c", 10);
    }
    barrier(cl, s_in, s_in_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

    /* 2. depth copy + motion vectors */
    barrier(cl, zs, s_zcopy_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    barrier(cl, s_mv, s_mv_st, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    barrier(cl, s_dz, s_dz_st, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    {   /* the previous frame's command list may still read this descriptor: rewrite it only for a new surface */
        static ID3D12Resource* s_srv_for = nullptr;
        if (s_srv_for != zs) {
            D3D12_SHADER_RESOURCE_VIEW_DESC sv = {};
            sv.Format = DXGI_FORMAT_R24_UNORM_X8_TYPELESS; sv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            sv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING; sv.Texture2D.MipLevels = 1;
            s_dev->CreateShaderResourceView(zs, &sv, s_heap->GetCPUDescriptorHandleForHeapStart());
            s_srv_for = zs;
        }
    }
    float k[40] = {0};
    int mv_on = 0, cut = 0;
    if (g.mv) {
        const float* d = s_exec.delta;
        if (!s_exec.cam_valid || !s_exec.delta_valid || d[0] * d[0] + d[1] * d[1] + d[2] * d[2] > 1000.0f * 1000.0f) cut = 1;
        float inv[16];
        if (!cut && s_prev_cam_valid && s_hist_ok && invert4(s_exec.cam, inv)) {
            /* previous camera x translate(delta): camera-relative position now -> camera-relative position then */
            float pv[16]; memcpy(pv, s_prev_cam, 64);
            for (int r = 0; r < 4; r++) pv[r * 4 + 3] += s_prev_cam[r * 4 + 0] * d[0] + s_prev_cam[r * 4 + 1] * d[1] + s_prev_cam[r * 4 + 2] * d[2];
            memcpy(k, inv, 64); memcpy(k + 16, pv, 64); mv_on = 1;
        }
    }
    k[32] = (float)w; k[33] = (float)h; k[34] = s_exec.jx; k[35] = s_exec.jy;
    k[36] = mv_on ? (g.mvfix ? 2.0f : 1.0f) : 0.0f; k[37] = (float)g.debug_mv; k[38] = s_prev_jx; k[39] = s_prev_jy;
    barrier(cl, s_pdz, s_pdz_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);   /* last frame's depth */
    ID3D12DescriptorHeap* heaps[] = { s_heap };
    cl->SetDescriptorHeaps(1, heaps);
    cl->SetComputeRootSignature(s_rs);
    cl->SetPipelineState(s_pso_mv);
    cl->SetComputeRoot32BitConstants(0, 40, k, 0);
    D3D12_GPU_DESCRIPTOR_HANDLE g0 = s_heap->GetGPUDescriptorHandleForHeapStart(), g1 = g0, g4 = g0;
    g1.ptr += s_heap_inc; g4.ptr += 4 * (UINT64)s_heap_inc;
    cl->SetComputeRootDescriptorTable(1, g0);
    cl->SetComputeRootDescriptorTable(2, g1);
    cl->SetComputeRootDescriptorTable(3, g4);
    cl->Dispatch((w + 7) / 8, (h + 7) / 8, 1);
    uav_barrier(cl, s_mv); uav_barrier(cl, s_dz);
    barrier(cl, s_mv, s_mv_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    barrier(cl, s_dz, s_dz_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

    /* 3. DLAA */
    barrier(cl, s_out, s_out_st, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    NVSDK_NGX_D3D12_DLSS_Eval_Params ep = {};
    ep.Feature.pInColor = s_in; ep.Feature.pInOutput = s_out;
    ep.pInDepth = s_dz; ep.pInMotionVectors = s_mv;
    ep.InJitterOffsetX = g.jsign * s_exec.jx; ep.InJitterOffsetY = g.jsign * s_exec.jy;
    ep.InRenderSubrectDimensions.Width = w; ep.InRenderSubrectDimensions.Height = h;
    ep.InReset = (s_hist_ok && !cut) ? 0 : 1;   /* new run of gameplay frames, or a camera cut */
    ep.InMVScaleX = 1.0f; ep.InMVScaleY = 1.0f;
    {   static LARGE_INTEGER f, t0; LARGE_INTEGER t; QueryPerformanceCounter(&t);
        if (!f.QuadPart) QueryPerformanceFrequency(&f);
        ep.InFrameTimeDeltaInMsec = t0.QuadPart ? (float)((double)(t.QuadPart - t0.QuadPart) * 1000.0 / (double)f.QuadPart) : 16.6f;
        t0 = t; }
    NVSDK_NGX_Result r = NGX_D3D12_EVALUATE_DLSS_EXT(cl, s_feature, s_params, &ep);
    if (NVSDK_NGX_FAILED(r)) {
        static int n = 0; if (n++ < 5) fprintf(stderr, "[DLSS] evaluate failed (0x%X)%c", (unsigned)r, 10);
        barrier(cl, bb, bb_st, D3D12_RESOURCE_STATE_RENDER_TARGET);
        s_hist_ok = 0;
        return;
    }
    if (merge || g.debug_mv) {   /* NGX left its own heaps and state on the list */
        cl->SetDescriptorHeaps(1, heaps);
        cl->SetComputeRootSignature(s_rs);
        cl->SetComputeRoot32BitConstants(0, 40, k, 0);
        cl->SetComputeRootDescriptorTable(1, g0);
        cl->SetComputeRootDescriptorTable(2, g1);
        cl->SetComputeRootDescriptorTable(3, g4);
    }
    if (merge) {   /* 3b. the HUD back on top, untouched by DLSS */
        cl->SetPipelineState(s_pso_merge);
        uav_barrier(cl, s_out);
        cl->Dispatch((w + 7) / 8, (h + 7) / 8, 1);
        uav_barrier(cl, s_out);
    }
    if (g.debug_mv) {   /* motion vectors / depth / reprojection error over the image */
        cl->SetPipelineState(s_pso_dbg);
        barrier(cl, s_mv, s_mv_st, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        barrier(cl, s_dz, s_dz_st, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (s_prev) barrier(cl, s_prev, s_prev_st, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        uav_barrier(cl, s_out);
        cl->Dispatch((w + 7) / 8, (h + 7) / 8, 1);
        uav_barrier(cl, s_out);
        if (s_prev) {   /* this frame's input becomes the next one's "last frame" */
            barrier(cl, s_prev, s_prev_st, D3D12_RESOURCE_STATE_COPY_DEST);
            barrier(cl, s_in, s_in_st, D3D12_RESOURCE_STATE_COPY_SOURCE);
            cl->CopyResource(s_prev, s_in);
        }
    }
    /* this frame's depth and jitter become the next one's "last frame" */
    barrier(cl, s_pdz, s_pdz_st, D3D12_RESOURCE_STATE_COPY_DEST);
    barrier(cl, s_dz, s_dz_st, D3D12_RESOURCE_STATE_COPY_SOURCE);
    cl->CopyResource(s_pdz, s_dz);
    s_prev_jx = s_exec.jx; s_prev_jy = s_exec.jy;

    /* 4. out -> back buffer */
    barrier(cl, s_out, s_out_st, D3D12_RESOURCE_STATE_COPY_SOURCE);
    barrier(cl, bb, bb_st, D3D12_RESOURCE_STATE_COPY_DEST);
    cl->CopyResource(bb, s_out);
    barrier(cl, bb, bb_st, D3D12_RESOURCE_STATE_RENDER_TARGET);

    if (s_exec.cam_valid) { memcpy(s_prev_cam, s_exec.cam, 64); s_prev_cam_valid = 1; } else s_prev_cam_valid = 0;
    s_hist_ok = 1;
    { static int n = 0; if (n++ == 0) fprintf(stderr, "[DLSS] first DLAA frame (%u jittered draws, depth 0x%X)%c", s_exec.qualifying, s_exec.zeta, 10); }
}

} // extern "C"
