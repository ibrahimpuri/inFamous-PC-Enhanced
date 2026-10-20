/* v242 configure-time CPU probe (CMakeLists.txt PORT_CPU_AUTO). Exit code: 2 = the default AVX2 build runs on this
 * CPU, 1 = only the SSE4.1 compatibility build runs, 0 = neither. Same feature test as v242b_cpu_level in main.cpp. */
#include <intrin.h>
#if !defined(__clang__)
#include <immintrin.h>
#endif
int main(void)
{
    int r[4];
    __cpuid(r, 0); const int maxl = r[0];
    __cpuid(r, 1); const unsigned c1 = (unsigned)r[2];
    const int sse41 = (c1 >> 19) & 1, ssse3 = (c1 >> 9) & 1;
    const int fma = (c1 >> 12) & 1, movbe = (c1 >> 22) & 1, popcnt = (c1 >> 23) & 1, osx = (c1 >> 27) & 1, avx = (c1 >> 28) & 1;
    int avx2 = 0, bmi1 = 0, bmi2 = 0, lzcnt = 0, osavx = 0;
    if (maxl >= 7) { __cpuidex(r, 7, 0); avx2 = (r[1] >> 5) & 1; bmi1 = (r[1] >> 3) & 1; bmi2 = (r[1] >> 8) & 1; }
    __cpuid(r, (int)0x80000000); if ((unsigned)r[0] >= 0x80000001u) { __cpuid(r, (int)0x80000001); lzcnt = (r[2] >> 5) & 1; }
    if (osx) {
#if defined(__clang__)
        unsigned lo, hi; __asm__ volatile("xgetbv" : "=a"(lo), "=d"(hi) : "c"(0)); (void)hi;
#else
        const unsigned lo = (unsigned)_xgetbv(0);
#endif
        osavx = (lo & 6u) == 6u;
    }
    if (ssse3 && sse41 && avx && osavx && avx2 && fma && bmi1 && bmi2 && lzcnt && movbe && popcnt) return 2;
    if (ssse3 && sse41) return 1;
    return 0;
}
