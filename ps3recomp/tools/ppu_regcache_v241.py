#!/usr/bin/env python3
"""v241B: ppu_regcache.py + --vr / --vr-ab / --nodrain (VMX register caching, FP fast helpers, trampoline-drain elision;
see the v241B blocks below). Without those flags the output is byte-identical to ppu_regcache.py.

v103 PPU register caching (perf agent C) -- source-to-source pass over the lifted PPU TUs.

usage: python ppu_regcache.py <in.cpp> <out.cpp> [--stats] [--vmb]     (env PPU_RC_MINREF, default 4)

v156 --vmb (CMake PORT_PPU_VMB_LOCAL, default ON): every lifted function (transformed or copied
unchanged) gets `uint8_t* const rc_vmb = vm_base;` at entry and is wrapped in
`#define PPU_VMB rc_vmb` ... `#define PPU_VMB vm_base`, so the inline guest-memory helpers of
ppu_recomp.h (v156: base pointer passed as PPU_VMB) use a local the guest stores cannot alias
instead of re-loading the global vm_base after every store. vm_base is set before any guest code
runs and never changes, so the copy is exact.

The lifter writes every guest register access as ctx->gpr[N] / ctx->fpr[N] / ctx->cr / ctx->xer.
Every guest store goes through a char-typed memcpy into vm_base, which may alias *ctx, so the
compiler must reload every register after each store and store every result back at once.
This pass keeps the GPRs/FPRs/CR/XER that a lifted function references >= MINREF times in locals
(rc_gN, rc_fN, rc_cr, rc_xer) and synchronises them with *ctx only where someone else can look:

  * sync points: every call that gets ctx (func_X(ctx), ps3_indirect_call, lv2_syscall,
    ps3_hle_call, DRAIN_TRAMPOLINE) or may read g_active_ctx (ps3_taskwait_*()), every `return;`
    and the closing brace. Before a sync: store back the registers that MAY be dirty there
    (line-level forward dataflow over the goto graph). After a call: reload the "eager" set E.
  * loads are lazy: a register is loaded from ctx at the start of the first line that reads it
    while its local is not known valid. Registers for which that is unsafe (dirty on one path,
    stale on another at a read or a sync) are moved into E (loaded at entry, reloaded after every
    call) until the analysis is consistent.
  * a function whose lines are not single complete statements (unbalanced braces, braceless
    if/else) or that calls anything outside the known helper set is copied unchanged.

VR registers, lr, ctr and the reservation stay in ctx. Output is deterministic; it is generated at
build time from the hand-patched recompiled/*.cpp (CMake PORT_PPU_REGCACHE).
Verification (cloud, 2026-10-08): differential harness, old vs new from identical random state,
every runtime call hashes the whole ctx and clobbers registers; 0 diffs on ~1,400 functions x 40
seeds with NaN-free inputs; planted missing-spill / missing-reload bugs are caught (428 / 369 of 678
functions). Known benign difference: with NaN inputs, the host may pick a different NaN payload
(the compiler is free to commute FP operands / fold float<->double round trips).
"""
import re, sys, os
MINREF = int(os.environ.get('PPU_RC_MINREF', '4'))
VRMODE = '--vr' in sys.argv          # v241B: also cache the VMX registers (see vr_rewrite / VR_HELPERS)
# v241B --vr-ab=<file>: in-run A/B twin. Functions named in <file> (one func_XXXXXXXX per line) are emitted twice,
# func_X__o (exactly the v103/v156 code) and func_X__n (--vr code), behind a dispatcher func_X that picks one per call
# from g_v241_vrsel / the PS3_AB phase (ppu_loader.cpp). All other functions are emitted exactly as without --vr.
VRAB = None
for _a in sys.argv:
    if _a.startswith('--vr-ab='):
        VRAB = set(open(_a[8:]).read().split())
if VRAB is not None:
    VRMODE = False
# v241B --nodrain: drop the DRAIN_TRAMPOLINE after a direct call to a lifted function that never assigns
# g_trampoline_fn. Exact: g_trampoline_fn is 0 before every call (the caller drained the previous one), a callee
# drains after each of its own calls (ps3_indirect_call drains inside, the runtime never sets it), so it is still
# 0 when a non-setter returns and the drain loop would not run. The setter set is computed over ALL sibling TUs
# (ppu_recomp_00N.cpp next to the input); a call to anything not defined there keeps its drain.
NODRAIN = '--nodrain' in sys.argv

FN_START = re.compile(r'^void (func_\w+)\(ppu_context\* ctx\) \{\s*$')
REG_V = re.compile(r'ctx->(gpr|fpr)\[(\d+)\]|ctx->(cr|xer)\b|ctx->vr\[(\d+)\]')
REG_N = re.compile(r'ctx->(gpr|fpr)\[(\d+)\]|ctx->(cr|xer)\b')
REG = REG_V if VRMODE else REG_N
# write = assignment (not ==), compound assignment, ++/--, or address taken
WRITE_AFTER = re.compile(r'\s*(?:[-+*/%&|^]|<<|>>)?=(?!=)|\s*(?:\+\+|--)')
SYNC = re.compile(
    r'(?:(?:\bfunc_[0-9A-Fa-f]{8}\w*|\bps3_indirect_call|\blv2_syscall)\(ctx\);'
    r'|\bps3_hle_call\([^;()]*\bctx\);'
    r'|\bDRAIN_TRAMPOLINE\(ctx\);'
    r'|\bps3_taskwait_\w+\(\);)'
    r'(?:\s*DRAIN_TRAMPOLINE\(ctx\);)?')
RET = re.compile(r'\breturn;')
CALL = re.compile(r'\b([A-Za-z_]\w*)\s*\(')
ALLOWED = set('''vm_read8 vm_read16 vm_read32 vm_read64 vm_write8 vm_write16 vm_write32 vm_write64
memcpy memset if for while switch return sizeof void PPU_FENCE ppc_rldicl ppc_rldicr ppc_rldic ppc_rldimi
ppc_rlwinm ppc_rlwimi ppc_sraw ppc_srad ppc_mulhd ppc_mulhdu ppc_rotl64 ppc_mask64
ppu_vldf4 ppu_vstf4 ppu_vldu4 ppu_vstu4 ppu_stvx16 ppu_fp_single ppu_fmul ppu_fadd ppu_fsub ppu_fdiv
ppu_fmadd_core ppu_frsp ppu_f2i32 ppu_f2i64 ppu_stwcx32 ppu_stdcx64 ppu_timebase_now ppu_dcbz128
ppu_atomic_add16 ppu_vbswap32 sqrtf sqrt fabs fabsf signbit __builtin_clz __builtin_clzll
DRAIN_TRAMPOLINE ps3_indirect_call lv2_syscall ps3_hle_call
ps3_taskwait_begin ps3_taskwait_spin ps3_taskwait_end'''.split())
COMMENT = re.compile(r'/\*.*?\*/')


def regname(m):
    if m.group(1):
        return ('g' if m.group(1) == 'gpr' else 'f') + m.group(2)
    if m.group(3):
        return m.group(3)
    return 'v' + m.group(4)          # v241B --vr


def local(r):
    return {'cr': 'rc_cr', 'xer': 'rc_xer'}.get(r, 'rc_' + r)


def ctxref(r):
    if r in ('cr', 'xer'):
        return 'ctx->' + r
    if r[0] == 'v':
        return 'ctx->vr[%s]' % r[1:]
    return 'ctx->%s[%s]' % ('gpr' if r[0] == 'g' else 'fpr', r[1:])


def ctype(r):
    return {'g': 'uint64_t', 'f': 'double', 'v': '__m128i'}.get(r[0], 'uint32_t') if r not in ('cr', 'xer') else 'uint32_t'


def st_reg(r):    # store a cached register back into ctx
    if r[0] == 'v' and r not in ('cr', 'xer'):
        return '_mm_store_si128((__m128i*)&%s, %s); ' % (ctxref(r), local(r))
    return '%s = %s; ' % (ctxref(r), local(r))


def ld_reg(r):    # (re)load a cached register from ctx
    if r[0] == 'v' and r not in ('cr', 'xer'):
        return '%s = _mm_load_si128((const __m128i*)&%s); ' % (local(r), ctxref(r))
    return '%s = %s; ' % (local(r), ctxref(r))


def order(r):
    if r in ('cr', 'xer'):
        return (2, r)
    return ({'g': 0, 'f': 1, 'v': 3}[r[0]], int(r[1:]))


LOC = re.compile(r'\brc_(g\d+|f\d+|v\d+|cr|xer)\b')
EVT = re.compile('(' + SYNC.pattern + ')|' + RET.pattern)
GOTO = re.compile(r'\bgoto (loc_\w+);')
UNCOND_PREFIX = re.compile(r'^\s*(?:ctx->lr = 0x[0-9A-Fa-f]+;\s*)?$')
BRACELESS = re.compile(r'^\s*(?:(?:if|for|while|switch)\b.*\)\s*$|else\b|case\b|default\b)')
LABEL = re.compile(r'^\s*(loc_\w+):')



# ---------------------------------------------------------------------------------------------
# v241B --vr: VMX register caching + vector intrinsics for the lifter's VMX templates.
# With --vr the VRs a transformed function touches live in __m128i locals rc_vN that hold the SAME raw
# big-endian guest bytes as ctx->vr[N] (so byte-move ops stay byte-exact), synchronised with ctx by the
# same dataflow as the GPRs (spill may-dirty before a sync, lazy reload after). The lifter's per-lane
# scalar templates (ppu_vldf4 / byte loops) are replaced by SSE/AVX2 intrinsics computing the identical
# per-lane IEEE single operations (mul then add -- never fused, cmp*_ps = the C relational operators,
# sqrt/div correctly rounded, cvtdq2ps = the C int->float conversion under the same MXCSR); templates
# without a rewrite keep working on the local through its address. Only an out-of-line stvx gets the
# address of a TEMPORARY copy, so the cached locals never escape and stay in xmm registers.
# Without --vr the output is byte-identical to the v103/v156 pass.
VR_HELPERS = r'''/* v241B ppu_regcache.py --vr: VMX helpers (raw big-endian lanes in __m128i) */
#include <immintrin.h>
#if !defined(__SSE4_1__)
#error "PORT_PPU_VR (v241B) needs SSE4.1 in the lifted PPU TUs (-msse4.1 or -mavx2)"
#endif
/* per-lane variable shifts: AVX2 vpsllvd/vpsrlvd/vpsravd, else the same per-lane C (counts are already & 31) */
#if defined(__AVX2__)
#define v241_sllv(a, n) _mm_sllv_epi32((a), (n))
#define v241_srlv(a, n) _mm_srlv_epi32((a), (n))
#define v241_srav(a, n) _mm_srav_epi32((a), (n))
#else
static inline __m128i v241_sllv(__m128i a, __m128i n) { uint32_t x[4], c[4]; memcpy(x, &a, 16); memcpy(c, &n, 16);
    for (int i = 0; i < 4; i++) x[i] = x[i] << c[i]; memcpy(&a, x, 16); return a; }
static inline __m128i v241_srlv(__m128i a, __m128i n) { uint32_t x[4], c[4]; memcpy(x, &a, 16); memcpy(c, &n, 16);
    for (int i = 0; i < 4; i++) x[i] = x[i] >> c[i]; memcpy(&a, x, 16); return a; }
static inline __m128i v241_srav(__m128i a, __m128i n) { uint32_t x[4], c[4]; memcpy(x, &a, 16); memcpy(c, &n, 16);
    for (int i = 0; i < 4; i++) x[i] = (uint32_t)((int32_t)x[i] >> c[i]); memcpy(&a, x, 16); return a; }
#endif
static inline __m128i v241_bs(__m128i x) { return _mm_shuffle_epi8(x, _mm_setr_epi8(3,2,1,0,7,6,5,4,11,10,9,8,15,14,13,12)); }
static inline __m128  v241_f(__m128i x) { return _mm_castsi128_ps(v241_bs(x)); }
static inline __m128i v241_i(__m128 x)  { return v241_bs(_mm_castps_si128(x)); }
static inline __m128i v241_vperm(__m128i a, __m128i b, __m128i c) {
    const __m128i ix = _mm_and_si128(c, _mm_set1_epi8(0x0F));
    return _mm_blendv_epi8(_mm_shuffle_epi8(a, ix), _mm_shuffle_epi8(b, ix), _mm_slli_epi16(c, 3)); }
static inline __m128i v241_lvsl(uint8_t b) { return _mm_add_epi8(_mm_set1_epi8((char)b), _mm_setr_epi8(0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15)); }
static inline __m128i v241_lane32(uint32_t w, uint32_t k) {
    return _mm_and_si128(_mm_set1_epi32((int)w), _mm_cmpeq_epi32(_mm_set1_epi32((int)k), _mm_setr_epi32(0,1,2,3))); }
static inline __m128 v241_vmax(__m128 a, __m128 b) {   /* = the lifter's per-lane vmaxfp expression */
    __m128 r = _mm_blendv_ps(a, b, a);                                   /* equal: signbit(a) ? b : a */
    r = _mm_blendv_ps(r, b, _mm_cmpgt_ps(b, a)); r = _mm_blendv_ps(r, a, _mm_cmpgt_ps(a, b));
    r = _mm_blendv_ps(r, _mm_add_ps(b, b), _mm_cmpunord_ps(b, b));
    return _mm_blendv_ps(r, _mm_add_ps(a, a), _mm_cmpunord_ps(a, a)); }
static inline __m128 v241_vmin(__m128 a, __m128 b) {   /* = the lifter's per-lane vminfp expression */
    __m128 r = _mm_blendv_ps(b, a, a);                                   /* equal: signbit(a) ? a : b */
    r = _mm_blendv_ps(r, b, _mm_cmplt_ps(b, a)); r = _mm_blendv_ps(r, a, _mm_cmplt_ps(a, b));
    r = _mm_blendv_ps(r, _mm_add_ps(b, b), _mm_cmpunord_ps(b, b));
    return _mm_blendv_ps(r, _mm_add_ps(a, a), _mm_cmpunord_ps(a, a)); }
static inline uint32_t v241_cr6(__m128 m) { const int k = _mm_movemask_ps(m); return (k == 15 ? 8u : 0u) | (k == 0 ? 2u : 0u); }
static inline uint32_t v241_cr6i(__m128i m) { return v241_cr6(_mm_castsi128_ps(m)); }
/* --vr-ab dispatcher: g_v241_vrsel (ppu_loader.cpp, env PPU_VR / PPU_VR_PHASE): -1 = new code, 2 = old code,
 * 0/1 = new code only in PS3_AB phases of that parity */
extern "C" int g_v241_vrsel;
extern "C" volatile int g_ab_phase;
#define V241_VR_PICK() (g_v241_vrsel < 0 || (g_v241_vrsel < 2 && (g_ab_phase & 1) == g_v241_vrsel))
'''

# v241B --vr also: scalar FP ops with ONE NaN test on the result instead of one per operand (+ result). x86 propagates
# any NaN input into the result, so "result is not NaN" implies "no input NaN and no invalid op" -> the old helper
# would return exactly r; otherwise the old helper itself computes the PPC NaN result. Placed before the first
# function (they use the preamble's ppu_fp_bin / ppu_fmadd_core).
FP_HELPERS = r'''/* v241B --vr: scalar FP fast forms (one NaN test) */
static inline double ppu_fadd_f(double a, double b) { const double r = a + b; return __builtin_expect(r != r, 0) ? ppu_fp_bin(a, b, r) : r; }
static inline double ppu_fsub_f(double a, double b) { const double r = a - b; return __builtin_expect(r != r, 0) ? ppu_fp_bin(a, b, r) : r; }
static inline double ppu_fmul_f(double a, double b) { const double r = a * b; return __builtin_expect(r != r, 0) ? ppu_fp_bin(a, b, r) : r; }
static inline double ppu_fdiv_f(double a, double b) { const double r = a / b; return __builtin_expect(r != r, 0) ? ppu_fp_bin(a, b, r) : r; }
static inline double ppu_fmadd_core_f(double a, double c, double b, int neg_b, int neg_res) {
    const double r = fma(a, c, neg_b ? -b : b);
    if (__builtin_expect(r != r, 0)) return ppu_fmadd_core(a, c, b, neg_b, neg_res);
    return neg_res ? -r : r; }
'''
FP_FAST = re.compile(r'\b(ppu_fadd|ppu_fsub|ppu_fmul|ppu_fdiv|ppu_fmadd_core)\(')

V = r'ctx->vr\[(\d+)\]'
X = r'([^;{}]+?)'     # an ea expression
def _rx(t):
    t = re.escape(t).replace(r'V', V).replace(r'XX', X).replace(r'NN', r'(\d+)')
    return re.compile(t)
def _v(n): return 'ctx->vr[%s]' % n

def _bswap32(x): x &= 0xFFFFFFFF; return int.from_bytes(x.to_bytes(4, 'big'), 'little')
def _bswap16(x): x &= 0xFFFF; return int.from_bytes(x.to_bytes(2, 'big'), 'little')
def _s32(x): x &= 0xFFFFFFFF; return x - (1 << 32) if x >> 31 else x
def _s16(x): x &= 0xFFFF; return x - (1 << 16) if x >> 15 else x

F = lambda n: 'v241_f(%s)' % _v(n)
VR_RULES = [
 # stvx: the cached local never escapes -- an out-of-line store gets a temporary copy
 (_rx('{ uint64_t ea = XX; ppu_stvx16((uint32_t)ea, &V); }'),
  lambda m: '{ uint64_t ea = %s; __m128i _t = %s; ppu_stvx16((uint32_t)ea, &_t); }' % (m.group(1), _v(m.group(2)))),
 # lvx
 (_rx('{ uint64_t ea = XX; memcpy(&V, vm_base + (uint32_t)ea, 16); }'),
  lambda m: '{ uint64_t ea = %s; %s = _mm_loadu_si128((const __m128i*)(vm_base + (uint32_t)ea)); }' % (m.group(1), _v(m.group(2)))),
 # lvewx (word-aligned ea: the word lands in lane (ea & 15) / 4, the rest is zero)
 (_rx('{ uint64_t ea = XX; ea &= ~3ULL; memset(&V, 0, 16); memcpy((uint8_t*)&V + (ea & 15), vm_base + (uint32_t)ea, 4); }'),
  lambda m: (None if m.group(2) != m.group(3) else
             '{ uint64_t ea = %s; ea &= ~3ULL; uint32_t _w; memcpy(&_w, vm_base + (uint32_t)ea, 4); %s = v241_lane32(_w, (uint32_t)(ea & 15) >> 2); }'
             % (m.group(1), _v(m.group(2))))),
 # vspltw
 (_rx('{ uint32_t* d = (uint32_t*)&V; uint32_t val = ((uint32_t*)&V)[NN]; d[0] = d[1] = d[2] = d[3] = val; }'),
  lambda m: None if int(m.group(3)) > 3 else '%s = _mm_shuffle_epi32(%s, 0x%02X);' % (_v(m.group(1)), _v(m.group(2)), 0x55 * int(m.group(3)))),
 # vmaddfp / vnmsubfp / vaddfp / vsubfp
 (_rx('{ float a[4],b[4],c[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); ppu_vldf4(&V,c); for(int i=0;i<4;i++) d[i]=a[i]*c[i]+b[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_add_ps(_mm_mul_ps(%s, %s), %s));' % (_v(m.group(4)), F(m.group(1)), F(m.group(3)), F(m.group(2)))),
 (_rx('{ float a[4],b[4],c[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); ppu_vldf4(&V,c); for(int i=0;i<4;i++) d[i]=b[i]-a[i]*c[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_sub_ps(%s, _mm_mul_ps(%s, %s)));' % (_v(m.group(4)), F(m.group(2)), F(m.group(1)), F(m.group(3)))),
 (_rx('{ float a[4],b[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i]=a[i]+b[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_add_ps(%s, %s));' % (_v(m.group(3)), F(m.group(1)), F(m.group(2)))),
 (_rx('{ float a[4],b[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i]=a[i]-b[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_sub_ps(%s, %s));' % (_v(m.group(3)), F(m.group(1)), F(m.group(2)))),
 # vrsqrtefp / vrefp (the lifter computes them exactly: 1/sqrt, 1/x)
 (_rx('{ float b[4],d[4]; ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i]=1.0f/sqrtf(b[i]); ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_div_ps(_mm_set1_ps(1.0f), _mm_sqrt_ps(%s)));' % (_v(m.group(2)), F(m.group(1)))),
 (_rx('{ float b[4],d[4]; ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i]=1.0f/b[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_div_ps(_mm_set1_ps(1.0f), %s));' % (_v(m.group(2)), F(m.group(1)))),
 # vcfsx (scale 0)
 (_rx('{ uint32_t bi[4]; ppu_vldu4(&V,bi); float d[4]; for(int i=0;i<4;i++) d[i]=(float)(int32_t)bi[i]; ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(_mm_cvtepi32_ps(v241_bs(%s)));' % (_v(m.group(2)), _v(m.group(1)))),
 # logic
 (_rx('{ uint64_t* d = (uint64_t*)&V; uint64_t* a = (uint64_t*)&V; uint64_t* b = (uint64_t*)&V; d[0] = a[0] ^ b[0]; d[1] = a[1] ^ b[1]; }'),
  lambda m: '%s = _mm_xor_si128(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))),
 (_rx('{ uint64_t* d = (uint64_t*)&V; uint64_t* a = (uint64_t*)&V; uint64_t* b = (uint64_t*)&V; d[0] = a[0] | b[0]; d[1] = a[1] | b[1]; }'),
  lambda m: '%s = _mm_or_si128(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))),
 (_rx('{ uint64_t* d=(uint64_t*)&V; uint64_t* a=(uint64_t*)&V; uint64_t* b=(uint64_t*)&V; for(int i=0;i<2;i++) d[i]=a[i]&b[i]; }'),
  lambda m: '%s = _mm_and_si128(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))),
 (_rx('{ uint64_t* d=(uint64_t*)&V; uint64_t* a=(uint64_t*)&V; uint64_t* b=(uint64_t*)&V; d[0]=a[0]&~b[0]; d[1]=a[1]&~b[1]; }'),
  lambda m: '%s = _mm_andnot_si128(%s, %s);' % (_v(m.group(1)), _v(m.group(3)), _v(m.group(2)))),
 (_rx('{ uint64_t* d=(uint64_t*)&V; uint64_t* a=(uint64_t*)&V; uint64_t* b=(uint64_t*)&V; uint64_t* c=(uint64_t*)&V; d[0]=(a[0]&~c[0])|(b[0]&c[0]); d[1]=(a[1]&~c[1])|(b[1]&c[1]); }'),
  lambda m: '%s = _mm_or_si128(_mm_andnot_si128(%s, %s), _mm_and_si128(%s, %s));' % (
      _v(m.group(1)), _v(m.group(4)), _v(m.group(2)), _v(m.group(3)), _v(m.group(4)))),
 # vsldoi
 (_rx('{ uint8_t tmp[32]; memcpy(tmp, &V, 16); memcpy(tmp+16, &V, 16); memcpy(&V, tmp + NN, 16); }'),
  lambda m: None if int(m.group(4)) > 16 else '%s = _mm_alignr_epi8(%s, %s, %d);' % (_v(m.group(3)), _v(m.group(2)), _v(m.group(1)), int(m.group(4)))),
 # vperm
 (_rx('{ uint8_t* a = (uint8_t*)&V; uint8_t* b = (uint8_t*)&V; uint8_t* c = (uint8_t*)&V; uint8_t* d = (uint8_t*)&V; uint8_t tmp[16]; for (int i = 0; i < 16; i++) { uint8_t sel = c[i] & 0x1F; tmp[i] = (sel < 16) ? a[sel] : b[sel - 16]; } memcpy(d, tmp, 16); }'),
  lambda m: '%s = v241_vperm(%s, %s, %s);' % (_v(m.group(4)), _v(m.group(1)), _v(m.group(2)), _v(m.group(3)))),
 # vmrghw / vmrglw
 (_rx('{ uint32_t* d=(uint32_t*)&V; uint32_t* a=(uint32_t*)&V; uint32_t* b=(uint32_t*)&V; uint32_t t[4]; t[0]=a[NN]; t[1]=b[NN]; t[2]=a[NN]; t[3]=b[NN]; memcpy(d, t, 16); }'),
  lambda m: ('%s = _mm_unpacklo_epi32(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))
             if m.group(4, 5, 6, 7) == ('0', '0', '1', '1') else
             '%s = _mm_unpackhi_epi32(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))
             if m.group(4, 5, 6, 7) == ('2', '2', '3', '3') else None)),
 # vmrghh / vmrglh
 (_rx('{ uint16_t* d=(uint16_t*)&V; uint16_t* a=(uint16_t*)&V; uint16_t* b=(uint16_t*)&V; uint16_t t[8]; t[0]=a[NN]; t[1]=b[NN]; t[2]=a[NN]; t[3]=b[NN]; t[4]=a[NN]; t[5]=b[NN]; t[6]=a[NN]; t[7]=b[NN]; memcpy(d, t, 16); }'),
  lambda m: ('%s = _mm_unpacklo_epi16(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))
             if m.group(*range(4, 12)) == ('0', '0', '1', '1', '2', '2', '3', '3') else
             '%s = _mm_unpackhi_epi16(%s, %s);' % (_v(m.group(1)), _v(m.group(2)), _v(m.group(3)))
             if m.group(*range(4, 12)) == ('4', '4', '5', '5', '6', '6', '7', '7') else None)),
 # vspltisw / vspltish
 (re.compile(r'\{ uint32_t v\[4\]; for\(int i=0;i<4;i\+\+\) v\[i\]=\(uint32_t\)\(int32_t\)(-?\d+); ppu_vstu4\(&' + V + r',v\); \}'),
  lambda m: '%s = _mm_set1_epi32(%d);' % (_v(m.group(2)), _s32(_bswap32(int(m.group(1)))))),
 (re.compile(r'\{ uint8_t\* p=\(uint8_t\*\)&' + V + r'; uint16_t hv=\(uint16_t\)\(\(int16_t\)(-?\d+)\); for\(int i=0;i<8;i\+\+\)\{ p\[i\*2\]=\(uint8_t\)\(hv>>8\); p\[i\*2\+1\]=\(uint8_t\)hv; \} \}'),
  lambda m: '%s = _mm_set1_epi16(%d);' % (_v(m.group(1)), _s16(_bswap16(int(m.group(2)))))),
 # lvsl / lvsr
 (re.compile(r'\{ uint8_t b = (\(uint8_t\)\([^;{}]+\)); for \(int i = 0; i < 16; i\+\+\) \(\(uint8_t\*\)&' + V + r'\)\[i\] = b \+ i; \}'),
  lambda m: '{ uint8_t b = %s; %s = v241_lvsl(b); }' % (m.group(1), _v(m.group(2)))),
 # float compares (vcmpgtfp / vcmpgefp / vcmpeqfp, with and without the CR6 dot form)
 (re.compile(r'\{ float a\[4\],b\[4\]; ppu_vldf4\(&' + V + r',a\); ppu_vldf4\(&' + V + r',b\); uint32_t d\[4\]; int t=0; for\(int i=0;i<4;i\+\+\)\{ int r=\(a\[i\](>|>=|==)b\[i\]\); d\[i\]=r\?~0u:0u; t\+=r; \} memcpy\(&' + V
             + r', d, 16\);( uint32_t c6=\(t==4\?8u:0u\)\|\(t==0\?2u:0u\); ctx->cr=\(ctx->cr & ~\(0xFu<<(\d+)\)\)\|\(c6<<(\d+)\);)? \}'),
  lambda m: (None if (m.group(5) and m.group(6) != m.group(7)) else
             ('{ const __m128 _m = _mm_%s_ps(%s, %s); %s = _mm_castps_si128(_m);%s }' % (
                 {'>': 'cmpgt', '>=': 'cmpge', '==': 'cmpeq'}[m.group(3)], F(m.group(1)), F(m.group(2)), _v(m.group(4)),
                 (' ctx->cr=(ctx->cr & ~(0xFu<<%s))|(v241_cr6(_m)<<%s);' % (m.group(6), m.group(6))) if m.group(5) else '')))),
 # vcmpequw (lane equality is byte-order independent)
 (re.compile(r'\{ uint32_t a\[4\],b\[4\]; ppu_vldu4\(&' + V + r',a\); ppu_vldu4\(&' + V + r',b\); uint32_t d\[4\]; int t=0; for\(int i=0;i<4;i\+\+\)\{ int r=\(a\[i\]==b\[i\]\); d\[i\]=r\?~0u:0u; t\+=r; \} memcpy\(&' + V
             + r', d, 16\);( uint32_t c6=\(t==4\?8u:0u\)\|\(t==0\?2u:0u\); ctx->cr=\(ctx->cr & ~\(0xFu<<(\d+)\)\)\|\(c6<<(\d+)\);)? \}'),
  lambda m: (None if (m.group(4) and m.group(5) != m.group(6)) else
             ('{ const __m128i _m = _mm_cmpeq_epi32(%s, %s); %s = _m;%s }' % (
                 _v(m.group(1)), _v(m.group(2)), _v(m.group(3)),
                 (' ctx->cr=(ctx->cr & ~(0xFu<<%s))|(v241_cr6i(_m)<<%s);' % (m.group(5), m.group(5))) if m.group(4) else '')))),
 # vmaxfp / vminfp
 (_rx('{ float a[4],b[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i] = (a[i]!=a[i]) ? a[i]+a[i] : (b[i]!=b[i]) ? b[i]+b[i] : ((a[i]>b[i]) ? a[i] : (b[i]>a[i]) ? b[i] : (signbit(a[i]) ? b[i] : a[i])); ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(v241_vmax(%s, %s));' % (_v(m.group(3)), F(m.group(1)), F(m.group(2)))),
 (_rx('{ float a[4],b[4],d[4]; ppu_vldf4(&V,a); ppu_vldf4(&V,b); for(int i=0;i<4;i++) d[i] = (a[i]!=a[i]) ? a[i]+a[i] : (b[i]!=b[i]) ? b[i]+b[i] : ((a[i]<b[i]) ? a[i] : (b[i]<a[i]) ? b[i] : (signbit(a[i]) ? a[i] : b[i])); ppu_vstf4(&V,d); }'),
  lambda m: '%s = v241_i(v241_vmin(%s, %s));' % (_v(m.group(3)), F(m.group(1)), F(m.group(2)))),
 # vslw / vsrw / vsraw
 (_rx('{ uint32_t a[4],b[4],d[4]; ppu_vldu4(&V,a); ppu_vldu4(&V,b); for(int i=0;i<4;i++) d[i]=a[i]<<(b[i]&31u); ppu_vstu4(&V,d); }'),
  lambda m: '%s = v241_bs(v241_sllv(v241_bs(%s), _mm_and_si128(v241_bs(%s), _mm_set1_epi32(31))));' % (_v(m.group(3)), _v(m.group(1)), _v(m.group(2)))),
 (_rx('{ uint32_t a[4],b[4],d[4]; ppu_vldu4(&V,a); ppu_vldu4(&V,b); for(int i=0;i<4;i++) d[i]=a[i]>>(b[i]&31u); ppu_vstu4(&V,d); }'),
  lambda m: '%s = v241_bs(v241_srlv(v241_bs(%s), _mm_and_si128(v241_bs(%s), _mm_set1_epi32(31))));' % (_v(m.group(3)), _v(m.group(1)), _v(m.group(2)))),
 (_rx('{ uint32_t a[4],b[4],d[4]; ppu_vldu4(&V,a); ppu_vldu4(&V,b); for(int i=0;i<4;i++) d[i]=(uint32_t)((int32_t)a[i]>>(b[i]&31u)); ppu_vstu4(&V,d); }'),
  lambda m: '%s = v241_bs(v241_srav(v241_bs(%s), _mm_and_si128(v241_bs(%s), _mm_set1_epi32(31))));' % (_v(m.group(3)), _v(m.group(1)), _v(m.group(2)))),
]


VR_OFF = set(int(x) for x in os.environ.get('PPU_VR_RULES_OFF', '').split(',') if x)   # debug: disable rules


def vr_rewrite(l, stats):
    """Replace the VMX template on this line (if any) by its intrinsic form, still spelled with ctx->vr[N]
    (REG.sub turns those into rc_vN afterwards). Lines without a known template are returned unchanged."""
    for k, (rx, fn) in enumerate(VR_RULES):
        if k in VR_OFF:
            continue
        m = rx.search(l)
        if m:
            r = fn(m)
            if r is None:
                continue
            stats['vr_rw'] = stats.get('vr_rw', 0) + 1
            return l[:m.start()] + r + l[m.end():]
    stats['vr_keep'] = stats.get('vr_keep', 0) + 1
    return l


def transform_fn(name, body, stats, vr=None, vexcl=frozenset(), minref=None):
    minref = MINREF if minref is None else minref
    REG = (REG_V if vr else REG_N) if vr is not None else globals()['REG']
    vr = VRMODE if vr is None else vr
    """body = list of lines between the opening and the closing brace (exclusive)."""
    refs = {}
    for l in body:
        code = COMMENT.sub('', l)
        for c in CALL.finditer(code):
            n = c.group(1)
            if n.startswith('func_') or n in ALLOWED:
                continue
            stats['skipped'] += 1
            stats.setdefault('skip_names', set()).add(name + ':' + n)
            return None
        # the dataflow below is line-based: every line must be a complete statement sequence
        if code.count('{') != code.count('}') or BRACELESS.match(code):
            stats['skipped'] += 1
            stats.setdefault('skip_names', set()).add(name + ':multiline')
            return None
        for m in REG.finditer(l):
            r = regname(m)
            refs[r] = refs.get(r, 0) + 1
    # only registers referenced often enough are worth a local: every cached register costs a load
    # at entry and a store at exit/sync, and with ~30 live locals x86 runs out of callee-saved
    # registers (the slow-path calls in the memory helpers) and parks them on the stack anyway.
    used = set(r for r, c in refs.items() if (c >= minref and r not in vexcl) or (r[0] == 'v' and r not in ('cr', 'xer') and r not in vexcl))
    if not used:
        return None
    U = sorted(used, key=order)
    bit = {r: 1 << k for k, r in enumerate(U)}
    tl = []          # transformed lines (registers -> locals)
    for l in body:
        if vr and 'ctx->vr[' in l and not (vexcl and any('ctx->vr[%s]' % x[1:] in l for x in vexcl)):
            l = vr_rewrite(l, stats)
        if vr and 'ppu_f' in l:
            l = FP_FAST.sub(lambda m: m.group(1) + '_f(', l)
        nl = REG.sub(lambda m: local(regname(m)) if regname(m) in used else m.group(0), l)
        chk = EVT.sub('', nl)
        chk = REG.sub('', chk)
        chk = re.sub(r'ctx->(?:vr\[\d+\]|(?:lr|ctr|fpscr|vscr|reserve_addr|reserve_value|reserve_valid)\b)', '', chk)
        chk = COMMENT.sub('', chk)
        if re.search(r'\bctx\b', chk):
            stats['skipped_ctx'] += 1
            stats.setdefault('skip_names', set()).add(name + ':ctx:' + l.strip()[:60])
            return None
        tl.append(nl)
    # per line: ordered events (pos, kind, mask): 'R' read, 'w' write, 's' sync, 'c' conditional
    # sync, 'r' return. A register is cached lazily: loaded from ctx at the start of the first line
    # that reads it while its local is not known valid (V), so a call only costs the reloads of the
    # E set (registers that must stay valid everywhere) instead of every cached register.
    evs = []; labels = {}; succ = []; rd_line = []; late = 0
    for i, nl in enumerate(tl):
        e = []
        for m in LOC.finditer(nl):
            mk = bit[m.group(1)]
            pre = nl[max(0, m.start() - 1):m.start()]
            wa = WRITE_AFTER.match(nl, m.end())
            if pre == '&' or wa:
                e.append((m.start(), 'w', mk))
            if pre == '&' or not wa or not re.match(r'\s*=(?!=)', nl[m.end():]):
                e.append((m.start(), 'R', mk))   # plain read, compound assignment, ++/--, &x
        for m in EVT.finditer(nl):
            k = 'r'
            if m.group(1):
                # a sync resets the state only when it runs unconditionally: nothing but an
                # `ctx->lr = imm;` in front of it on its line (else: `if (c) { ...; sync }` etc.)
                k = 's' if UNCOND_PREFIX.match(COMMENT.sub('', nl[:m.start()])) else 'c'
            e.append((m.start(), k, 0))
        e.sort(key=lambda t: (t[0], t[1] != 'R'))
        evs.append(e)
        r_all = 0; seen_sync = False
        for pos, k, mk in e:
            if k in ('s', 'c'): seen_sync = True
            elif k == 'R':
                if seen_sync: late |= mk     # read after a call on the same line: keep it eager
                else: r_all |= mk
        rd_line.append(r_all)
        lm = LABEL.match(nl)
        if lm: labels[lm.group(1)] = i
        succ.append(GOTO.findall(nl))
    n = len(tl)
    ALL = (1 << len(U)) - 1
    E = late
    it = 0
    while True:
        it += 1
        vin = [ALL] * (n + 1); din = [0] * (n + 1); reach = [False] * (n + 1)
        vin[0] = E; reach[0] = True
        conflict = 0
        def run(i, v, d, emit=None):
            need = rd_line[i] & ~v
            nonlocal conflict
            conflict |= need & d
            v |= need
            if emit is not None: emit.append(need & ~d)
            for pos, k, mk in evs[i]:
                if k == 'w': v |= mk; d |= mk
                elif k in ('s', 'c', 'r'):
                    conflict |= d & ~v
                    if emit is not None: emit.append(d)
                    if k == 's': v = E; d = 0
                    elif k == 'c': v &= E   # merge of taken (V=E, D=0) and not-taken paths
            return v, d
        changed = True
        while changed:
            changed = False
            for i in range(n):
                if not reach[i]: continue
                v, d = run(i, vin[i], din[i])
                for t in [i + 1] + [labels[x] for x in succ[i] if x in labels]:
                    nv = vin[t] & v if reach[t] else v
                    nd = din[t] | d
                    if not reach[t] or nv != vin[t] or nd != din[t]:
                        reach[t] = True; vin[t] = nv; din[t] = nd
                        if t <= i: changed = True
        # end of function: spill what may be dirty (must be valid)
        if reach[n]: conflict |= din[n] & ~vin[n]
        if conflict & ~E == 0: break
        E |= conflict
    # v241B: a VR that would have to be reloaded after EVERY call (eager set) stays in ctx instead (its templates are
    # left as they were): 16-byte reloads at each of many call sites cost more than the cached local saves.
    evr = frozenset(r for r in U if r[0] == 'v' and r not in ('cr', 'xer') and E & bit[r])
    if vr and evr:
        nsy = sum(1 for e in evs for _, k, _ in e if k in ('s', 'c'))
        nvl = sum(1 for x in body if 'ctx->vr[' in x)
        if nsy * len(evr) > VR_EAGER_K * nvl:   # many call sites x many eager VRs: the reloads would dominate
            return transform_fn(name, body, stats, vr, vexcl | evr, minref)
    def spill(d):
        return ''.join(st_reg(r) for r in U if d & bit[r])
    def loads(m):
        return ''.join(ld_reg(r) for r in U if m & bit[r])
    out = []; nsync = 0; nst = 0; nld = 0
    for i, nl in enumerate(tl):
        if not reach[i]:
            out.append(nl); continue
        sp = []
        run(i, vin[i], din[i], sp)
        ld = loads(sp[0]); nld += ld.count(';')
        k = iter(sp[1:])
        def _s(m):
            d = next(k); s_ = spill(d)
            nonlocal nsync, nst
            nst += s_.count(';')
            if m.group(1):
                nsync += 1
                return '{ ' + s_ + m.group(0) + ' RC_RL }'
            return '{ ' + s_ + 'return; }'
        nl2 = EVT.sub(_s, nl)
        if ld:
            lm = LABEL.match(nl2)
            if lm:   # keep the label first: `loc_X: loads; rest`
                nl2 = nl2[:lm.end()] + ' ' + ld + nl2[lm.end():]
            else:
                nl2 = '        ' + ld + nl2.lstrip(' ')
        out.append(nl2)
    end = spill(din[n]) if reach[n] else ''
    nst += end.count(';')
    stats['fns'] += 1; stats['sync'] += nsync; stats['regs'] += len(U); stats['spills'] += nst
    stats['lazyloads'] = stats.get('lazyloads', 0) + nld
    stats['eager'] = stats.get('eager', 0) + bin(E).count('1')
    stats['iters'] = max(stats.get('iters', 0), it)
    rl = ' '.join(ld_reg(r).strip() for r in U if E & bit[r])
    def init(r):
        if E & bit[r]:
            return ld_reg(r)[len(local(r)) + 3:].rstrip('; ')
        return '_mm_setzero_si128()' if r[0] == 'v' and r not in ('cr', 'xer') else '0'
    decl = ''.join('    %s %s = %s;\n' % (ctype(r), local(r), init(r)) for r in U)
    head = '#define RC_RL %s\n' % rl
    return head, decl, out, end


def emit_ab(o, name, l, body, close, stats, vmb):
    """v241B --vr-ab: old/new twin + dispatcher. Returns False (emit normally) when --vr changes nothing."""
    st2 = dict(fns=0, skipped=0, skipped_ctx=0, sync=0, regs=0, spills=0)
    body_n = [ND_SUB(x) for x in body] if ND_SUB else body     # --nodrain only in the new copy
    rn = transform_fn(name, body_n, st2, True, minref=MINREF_NEW)
    ro = transform_fn(name, body, stats, False)
    if vmb:
        raise SystemExit('--vr-ab with --vmb is not supported')
    def render(tag, r, b):
        hdr = l.replace('void %s(' % name, 'void %s__%s(' % (name, tag), 1)
        if r is None:
            return [hdr] + list(b) + [close]
        head, decl, out, end = r
        return [head, hdr, decl] + list(out) + ['    ' + end + '\n', close, '#undef RC_RL\n']
    to, tn = render('o', ro, body), render('n', rn, body_n)
    if [x.replace('__n(', '__o(', 1) for x in tn] == to:
        return False    # the new code generation changes nothing here
    o.extend(to); o.extend(tn)
    o.append('void %s(ppu_context* ctx) { if (V241_VR_PICK()) %s__n(ctx); else %s__o(ctx); }   /* v241B --vr-ab */\n'
             % (name, name, name))
    stats['vr_ab'] = stats.get('vr_ab', 0) + 1
    return True


ND_SUB = None
MINREF_NEW = int(os.environ.get('PPU_RC_MINREF_NEW', str(MINREF)))   # v241B experiment: MINREF of the --vr-ab new copies only
VR_EAGER_K = float(os.environ.get('PPU_VR_EAGER_K', '2'))
DRAIN_CALL = re.compile(r'\b(func_[0-9A-Fa-f]{8}\w*)\(ctx\); DRAIN_TRAMPOLINE\(ctx\);')
def scan_setters(src):
    import glob
    d = os.path.dirname(os.path.abspath(src))
    defined, setters = set(), set()
    files = sorted(glob.glob(os.path.join(d, 'ppu_recomp_[0-9][0-9][0-9].cpp')))
    if not files:
        raise SystemExit('--nodrain: no sibling ppu_recomp_NNN.cpp next to ' + src)
    for f in files:
        cur = None
        for l in open(f, 'r', encoding='latin-1'):
            m = FN_START.match(l.rstrip('\r\n'))
            if m:
                cur = m.group(1); defined.add(cur); continue
            if l.startswith('}'):
                cur = None; continue
            if cur and 'g_trampoline_fn' in l:
                setters.add(cur)
    return defined, setters


def main():
    src, dst = sys.argv[1], sys.argv[2]
    data = open(src, 'r', encoding='latin-1', newline='').read()
    lines = data.splitlines(True)
    global ND_SUB
    nd = [0]
    if NODRAIN:
        defined, setters = scan_setters(src)
        def _nd(m):
            if m.group(1) in defined and m.group(1) not in setters:
                nd[0] += 1
                return m.group(1) + '(ctx);'
            return m.group(0)
        ND_SUB = lambda l: DRAIN_CALL.sub(_nd, l) if 'DRAIN_TRAMPOLINE' in l else l
        if VRAB is None:
            lines = [ND_SUB(l) for l in lines]
            ND_SUB = None
    stats = dict(fns=0, skipped=0, skipped_ctx=0, sync=0, regs=0, spills=0, total=0)
    vmb = '--vmb' in sys.argv
    if vmb:
        stats['vmb'] = 0
    VMB_DECL = '    uint8_t* const rc_vmb = vm_base;   /* v156 --vmb */\n'
    VMB_ON = '#undef PPU_VMB\n#define PPU_VMB rc_vmb\n'
    VMB_OFF = '#undef PPU_VMB\n#define PPU_VMB vm_base\n'
    o = []
    helpers_done = not (VRMODE or VRAB is not None)
    fp_done = not (VRMODE or VRAB is not None)
    o.append('/* v103 ppu_regcache.py: register-cached copy of %s -- generated at build time, do not edit. */\n'
             % src.replace('\\', '/').split('/')[-1])
    i, n = 0, len(lines)
    while i < n:
        l = lines[i]
        m = FN_START.match(l.rstrip('\r\n'))
        if not m:
            o.append(l); i += 1
            if not helpers_done and l.startswith('#include "ppu_recomp.h"'):
                o.append(VR_HELPERS); helpers_done = True
            continue
        j = i + 1
        while j < n and not lines[j].startswith('}'):
            j += 1
        if j >= n:
            raise SystemExit('unterminated function at line %d' % (i + 1))
        stats['total'] += 1
        if not fp_done:
            o.append(FP_HELPERS); fp_done = True
        body = lines[i + 1:j]
        if VRAB is not None and m.group(1) in VRAB:
            if emit_ab(o, m.group(1), l, body, lines[j], stats, vmb):
                i = j + 1
                continue
        r = transform_fn(m.group(1), body, stats)
        if vmb:
            o.append(VMB_ON); stats['vmb'] += 1
        if r is None:
            o.append(l)
            if vmb: o.append(VMB_DECL)
            o.extend(lines[i + 1:j + 1])
        else:
            head, decl, out, end = r
            o.append(head); o.append(l)
            if vmb: o.append(VMB_DECL)
            o.append(decl); o.extend(out)
            o.append('    ' + end + '\n'); o.append(lines[j])
            o.append('#undef RC_RL\n')
        if vmb:
            o.append(VMB_OFF)
        i = j + 1
    if not helpers_done:
        raise SystemExit('--vr: no #include "ppu_recomp.h" line to put the VMX helpers after')
    open(dst, 'w', encoding='latin-1', newline='').write(''.join(o))
    if NODRAIN:
        stats['nodrain'] = nd[0]
    if '--stats' in sys.argv:
        sk = stats.pop('skip_names', set())
        print(src, stats, file=sys.stderr)
        for s in sorted(sk)[:40]:
            print('  skipped', s, file=sys.stderr)


if __name__ == '__main__':
    main()
