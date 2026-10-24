/* Controlled callback library: the guest calls host-supplied functions through import (IAT) slots. */
#include "iat.h"
typedef unsigned u32;
u32 g_acc; u32 g_calls;
u32 k_once(u32 a, u32 b) { g_calls++; return CB_STD(a, b) + 1; }
u32 k_loop(u32 n, u32 seed) { u32 acc = seed; n &= 7; for (u32 i = 0; i < n; i++) { acc = CB_STD(acc, seed + i) ^ (acc << 3); g_calls++; } g_acc = acc; return acc; }
u32 k_cdecl(u32 a, u32 b, u32 c) { g_calls++; return CB_C(a, b, c) * 2; }
u32 k_mixed(u32 a, u32 b) { u32 r = CB_C(a, b, 7); r = CB_STD(r, a); r += CB_C(r, b, a); g_acc += r; return r; }
u32 k_nested(u32 n, u32 s) { n &= 3; if (!n) return CB_STD(s, 1); return k_nested(n - 1, CB_STD(s, n)) + CB_C(s, n, 0); }
