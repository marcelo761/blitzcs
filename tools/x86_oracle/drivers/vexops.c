#include "common.h"
/* The library uses AVX2 and BMI1: the driver checks the host first (cpuid + xgetbv) and prints "AVX2 unavailable" with exit 0 instead of executing VEX code. */
extern char vx_pxor[], vx_pand[], vx_pcmpeq_mask[], vx_scan_first[], vx_paddd[], vx_psubd[], vx_pshufb[], vx_xor128[], vx_zero_upper[], vx_andn[], vx_tzcnt[];
static const Fn T[] = {
  {"vx_pxor", 'C', 2, {0}, vx_pxor}, {"vx_pand", 'C', 2, {0}, vx_pand},
  {"vx_pcmpeq_mask", 'C', 2, {0}, vx_pcmpeq_mask}, {"vx_scan_first", 'C', 2, {0}, vx_scan_first},
  {"vx_paddd", 'C', 2, {0}, vx_paddd}, {"vx_psubd", 'C', 2, {0}, vx_psubd}, {"vx_pshufb", 'C', 2, {0}, vx_pshufb},
  {"vx_xor128", 'C', 2, {0}, vx_xor128}, {"vx_zero_upper", 'C', 2, {0}, vx_zero_upper},
  {"vx_andn", 'C', 2, {0}, vx_andn}, {"vx_tzcnt", 'C', 1, {0}, vx_tzcnt},
};

static int host_has_avx2_bmi1(void)
{
  u32 a, b, c, d;
  __asm__ volatile("cpuid" : "=a"(a), "=b"(b), "=c"(c), "=d"(d) : "a"(1), "c"(0));
  if (!(c & (1u << 27)) || !(c & (1u << 28))) return 0;   /* OSXSAVE and AVX */
  u32 lo, hi;
  __asm__ volatile("xgetbv" : "=a"(lo), "=d"(hi) : "c"(0));
  if ((lo & 6u) != 6u) return 0;                          /* the OS saves XMM and YMM state */
  __asm__ volatile("cpuid" : "=a"(a), "=b"(b), "=c"(c), "=d"(d) : "a"(7), "c"(0));
  return (b & (1u << 5)) && (b & (1u << 3));              /* AVX2 and BMI1 */
}

__attribute__((force_align_arg_pointer, noreturn)) void _start(void)
{
  if (!host_has_avx2_bmi1()) { puts_("AVX2 unavailable\n"); flush(); finish(); }
  run_all(T, (int)(sizeof T / sizeof T[0]), 80);
  finish();
}
