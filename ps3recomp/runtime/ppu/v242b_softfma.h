/* v242B: software fused multiply-add for builds WITHOUT hardware FMA (PORT_CPU_COMPAT, pre-Haswell CPUs).
 * The AVX2 build computes fma/fmaf with one vfmadd (single rounding in the current MXCSR mode, DAZ/FTZ honoured);
 * these give the same bits without the instruction:
 *   v242b_fmaf: float, via double: the product of two floats is exact in double; the sum is formed toward zero
 *               with the inexact flag folded into the last bit (round-to-odd, 53 >= 24 + 2 bits) and then converted
 *               to float in the CALLER's MXCSR mode -> correctly rounded in every mode. In RZ (the SPU threads' mode)
 *               truncation composes, so the plain (float)(p + c) is already exact there (fast path).
 *   v242b_fma:  double, exact integer arithmetic (106-bit product, 128-bit aligned sum with sticky, round-to-odd to
 *               63 bits, then rounded to 53 bits in the MXCSR rounding mode; subnormal / overflow per mode).
 * Verified bit-for-bit against vfmadd under RN/RZ/RU/RD (+FTZ/DAZ) by tools/v242b_fmatest.c (bench job 980).
 * NaN inputs: a quiet NaN comes back (payload choice may differ from the hardware's operand order). */
#ifndef V242B_SOFTFMA_H
#define V242B_SOFTFMA_H
#include <stdint.h>
#include <string.h>
#include <emmintrin.h>
#include <xmmintrin.h>

#if defined(__clang__)
#  define V242B_NOINL __attribute__((noinline))
#else
#  define V242B_NOINL __declspec(noinline)
#endif

static inline float v242b_fmaf(float a, float b, float c)
{
    const unsigned csr = _mm_getcsr();
    /* conversions + product in the caller's mode: DAZ applies to the float inputs; the product is exact */
    const __m128d p = _mm_mul_sd(_mm_cvtss_sd(_mm_setzero_pd(), _mm_set_ss(a)), _mm_cvtss_sd(_mm_setzero_pd(), _mm_set_ss(b)));
    const __m128d cd = _mm_cvtss_sd(_mm_setzero_pd(), _mm_set_ss(c));
    if ((csr & 0x6000u) == 0x6000u)   /* RZ: RZ_float(RZ_double(x)) == RZ_float(x) */
        return _mm_cvtss_f32(_mm_cvtsd_ss(_mm_setzero_ps(), _mm_add_sd(p, cd)));
    _mm_setcsr((csr & ~0x603Fu) | 0x6000u);   /* toward zero, flags cleared */
    __m128d s = _mm_add_sd(p, cd);
    /* make the flag read depend on the add (no reordering of the add across the MXCSR accesses) */
    __asm__ volatile("" : "+x"(s));
    const unsigned inexact = _mm_getcsr() & 0x20u;
    _mm_setcsr(csr);
    if (inexact) s = _mm_castsi128_pd(_mm_or_si128(_mm_castpd_si128(s), _mm_cvtsi32_si128(1)));
    return _mm_cvtss_f32(_mm_cvtsd_ss(_mm_setzero_ps(), s));
}

static inline int v242b__clz128(unsigned __int128 x)
{
    const uint64_t hi = (uint64_t)(x >> 64), lo = (uint64_t)x;
    return hi ? __builtin_clzll(hi) : 64 + __builtin_clzll(lo);
}
static V242B_NOINL double v242b_fma(double a, double b, double c)
{
    const unsigned csr = _mm_getcsr();
    const unsigned rc = (csr >> 13) & 3u;   /* 0 RN, 1 RD, 2 RU, 3 RZ */
    uint64_t ua, ub, uc;
    memcpy(&ua, &a, 8); memcpy(&ub, &b, 8); memcpy(&uc, &c, 8);
    if (csr & 0x40u) {   /* DAZ: denormal inputs are zeros */
        if (!(ua & 0x7FF0000000000000ull)) ua &= 0x8000000000000000ull;
        if (!(ub & 0x7FF0000000000000ull)) ub &= 0x8000000000000000ull;
        if (!(uc & 0x7FF0000000000000ull)) uc &= 0x8000000000000000ull;
        memcpy(&a, &ua, 8); memcpy(&b, &ub, 8); memcpy(&c, &uc, 8);
    }
    const uint64_t ea_f = (ua >> 52) & 0x7FF, eb_f = (ub >> 52) & 0x7FF, ec_f = (uc >> 52) & 0x7FF;
    /* NaN / Inf anywhere: the plain ops give the IEEE special result (one rounding is never involved) */
    if (ea_f == 0x7FF || eb_f == 0x7FF || ec_f == 0x7FF) { volatile double t = a * b; return t + c; }
    const int a0 = !(ua << 1), b0 = !(ub << 1), c0 = !(uc << 1);
    if (a0 || b0) { volatile double t = a * b; return t + c; }   /* exact (signed) zero product: one rounding */
    if (c0) { volatile double t = a * b; return t; }             /* fma(a,b,+-0) = round(a*b) for a nonzero product */
    const uint64_t ma = (ua & 0xFFFFFFFFFFFFFull) | (ea_f ? 1ull << 52 : 0);
    const uint64_t mb = (ub & 0xFFFFFFFFFFFFFull) | (eb_f ? 1ull << 52 : 0);
    const uint64_t mc = (uc & 0xFFFFFFFFFFFFFull) | (ec_f ? 1ull << 52 : 0);
    const int ea = (ea_f ? (int)ea_f : 1) - 1075, eb = (eb_f ? (int)eb_f : 1) - 1075, ec = (ec_f ? (int)ec_f : 1) - 1075;
    const unsigned sp = (unsigned)((ua ^ ub) >> 63), sc = (unsigned)(uc >> 63);
    unsigned __int128 P = (unsigned __int128)ma * mb, C = mc;
    int xp = ea + eb, xc = ec;                 /* value = P * 2^xp, C * 2^xc */
    { const int sh = v242b__clz128(P) - 2; P <<= sh; xp -= sh; }   /* top bit at 125 */
    { const int sh = v242b__clz128(C) - 2; C <<= sh; xc -= sh; }
    unsigned __int128 L, S; int xl, xs; unsigned sl, ss;
    if (xp > xc || (xp == xc && P >= C)) { L = P; S = C; xl = xp; xs = xc; sl = sp; ss = sc; }
    else { L = C; S = P; xl = xc; xs = xp; sl = sc; ss = sp; }
    const int d = xl - xs;
    if (d > 0) {
        if (d >= 127) S = S ? 1 : 0;
        else { const unsigned __int128 lost = S & (((unsigned __int128)1 << d) - 1); S = (S >> d) | (lost ? 1 : 0); }
    }
    unsigned __int128 R = (sl == ss) ? L + S : L - S;
    if (!R) { const double z = (rc == 1) ? -0.0 : 0.0; return z; }   /* exact cancellation */
    const unsigned sign = sl;
    /* round-to-odd to a 63-bit significand: value = M * 2^E */
    const int t = 127 - v242b__clz128(R);
    uint64_t M; int E;
    if (t > 62) {
        const int sh = t - 62;
        const unsigned __int128 lost = R & (((unsigned __int128)1 << sh) - 1);
        M = (uint64_t)(R >> sh) | (lost ? 1 : 0); E = xl + sh;
    } else { M = (uint64_t)R << (62 - t); E = xl - (62 - t); }
    const int x = E + 62;                      /* unbiased exponent of the top bit */
    int shift = (x >= -1022) ? 10 : 10 + (-1022 - x);
    uint64_t Q, rem, half;
    if (shift >= 64) { Q = 0; rem = 1; half = 2; /* nonzero, below half */ }
    else { Q = M >> shift; rem = M & ((1ull << shift) - 1); half = 1ull << (shift - 1); }
    int up = 0;
    switch (rc) {
    case 0: up = rem > half || (rem == half && (Q & 1)); break;
    case 1: up = rem && sign; break;
    case 2: up = rem && !sign; break;
    default: up = 0; break;
    }
    Q += (uint64_t)up;
    uint64_t bits;
    if (x >= -1022) {
        int xe = x;
        if (Q >> 53) { Q >>= 1; xe++; }
        if (xe > 1023) {   /* overflow */
            const int to_inf = (rc == 0) || (rc == 2 && !sign) || (rc == 1 && sign);
            bits = to_inf ? 0x7FF0000000000000ull : 0x7FEFFFFFFFFFFFFFull;
        } else bits = ((uint64_t)(xe + 1023) << 52) | (Q & 0xFFFFFFFFFFFFFull);
    } else {
        if ((csr & 0x8000u) && Q < (1ull << 52)) Q = 0;   /* FTZ: tiny result -> zero */
        bits = Q;   /* subnormal; Q == 2^52 encodes the smallest normal */
    }
    bits |= (uint64_t)sign << 63;
    double r; memcpy(&r, &bits, 8);
    return r;
}
#endif
