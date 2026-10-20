/* v242B: force-included into EVERY translation unit of the PORT_CPU_COMPAT (no FMA) build only
 * (CMakeLists.txt /FI). fma()/fmaf() become the bit-exact software versions of v242b_softfma.h, so the compat exe
 * does not depend on how the C runtime implements them on a CPU without FMA (the UCRT dispatches to vfmadd when the
 * CPU has it, so its software path cannot be verified on the bench PC; ours is: bench job 980, 60M cases, 0 diffs
 * vs vfmadd under RN/RZ/RU/RD/FTZ/DAZ). Never included by the default AVX2 build. */
#ifndef V242B_COMPAT_FMA_H
#define V242B_COMPAT_FMA_H
#ifdef __cplusplus
#include <cmath>
#endif
#include <math.h>
#include "v242b_softfma.h"
#define fmaf(a, b, c) v242b_fmaf((a), (b), (c))
#define fma(a, b, c)  v242b_fma((a), (b), (c))
#endif
