/*
 port/HwRandom.c — RDRAND/RDSEED via compiler intrinsics.

 cpu.c's MSVC branch calls RDRAND_getBytes/RDSEED_getBytes, which upstream
 implements in rdrand.c + MASM assembly (rdrand_ml.asm/rdseed_ml.asm). To keep
 the build system free of assembler steps, this replacement uses the compiler
 intrinsics directly. On non-MSVC-x86 targets cpu.c never references these
 symbols, so the file compiles to an empty translation unit there.
*/

#include "Crypto/cpu.h"
#include "Crypto/misc.h"

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64)) && !defined(_UEFI)

#include <immintrin.h>

int RDRAND_getBytes(unsigned char* buf, size_t bufLen);
int RDSEED_getBytes(unsigned char* buf, size_t bufLen);

#if defined(_M_IX86)
/* 32-bit x86 has no 64-bit RDRAND/RDSEED instruction: the _rdrand64_step /
   _rdseed64_step intrinsics are declared in <immintrin.h> but unimplemented
   there, which surfaces as LNK2019 on __rdrand64_step/__rdseed64_step.
   Draw two 32-bit halves instead — both are real instructions on x86. */
static int DrawRandom64(unsigned long long* out, int useRdseed)
{
    unsigned int lo = 0, hi = 0;
    int ok = useRdseed ? _rdseed32_step(&lo) : _rdrand32_step(&lo);
    if (!ok)
        return 0;
    ok = useRdseed ? _rdseed32_step(&hi) : _rdrand32_step(&hi);
    if (!ok)
        return 0;
    *out = ((unsigned long long)hi << 32) | lo;
    return 1;
}
#else
static int DrawRandom64(unsigned long long* out, int useRdseed)
{
    return useRdseed ? _rdseed64_step(out) : _rdrand64_step(out);
}
#endif

static int GenerateBlockWithIntrinsic(unsigned char* buf, size_t bufLen, int useRdseed)
{
    unsigned char* p = buf;
    size_t remaining = bufLen;
    int retries = 32;

    while (remaining > 0)
    {
        size_t chunk = remaining > 8 ? 8 : remaining;
        unsigned long long value = 0;

        /* the intrinsics return 1 on success; retry a bounded number of times */
        if (!DrawRandom64(&value, useRdseed))
        {
            if (--retries == 0)
                return 0;
            continue;
        }

        retries = 32;
        for (size_t i = 0; i < chunk; i++)
            p[i] = (unsigned char) (value >> (8 * i));

        p += chunk;
        remaining -= chunk;
    }

    return 1;
}

int RDRAND_getBytes(unsigned char* buf, size_t bufLen)
{
    if (!buf || !HasRDRAND())
        return 0;
    if (bufLen)
        return GenerateBlockWithIntrinsic(buf, bufLen, 0);
    return 1;
}

int RDSEED_getBytes(unsigned char* buf, size_t bufLen)
{
    if (!buf || !HasRDSEED())
        return 0;
    if (bufLen)
        return GenerateBlockWithIntrinsic(buf, bufLen, 1);
    return 1;
}

#endif /* _MSC_VER && x86/x64 */
