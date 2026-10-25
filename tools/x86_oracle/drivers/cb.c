#include "common.h"
#define S __attribute__((stdcall))
extern char k_once[], k_loop[], k_cdecl[], k_mixed[], k_nested[];
/* host functions: the C# test registers the identical formulas */
static u32 S cb_std(u32 a, u32 b) { u32 r = a * 31u + (b ^ 0x55u) + 7u; trace("cb_std", a, b, 0, r); return r; }
static u32 cb_c(u32 a, u32 b, u32 c) { u32 r = (a ^ b) + c * 3u; trace("cb_c", a, b, c, r); return r; }
static const Fn T[] = {
  {"k_once", 'C', 2, {0}, k_once},
  {"k_loop", 'C', 2, {0}, k_loop},
  {"k_cdecl", 'C', 3, {0}, k_cdecl},
  {"k_mixed", 'C', 2, {0}, k_mixed},
  {"k_nested", 'C', 2, {0}, k_nested},
};
__attribute__((force_align_arg_pointer, noreturn)) void _start(void) { iat[0] = (u32)cb_std; iat[1] = (u32)cb_c; run_all(T, (int)(sizeof T / sizeof T[0]), 100); finish(); }
