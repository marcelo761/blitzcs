/* Controlled state/control-flow library: data-section counters, recursion, switch jump tables, function-pointer tables (with relocations), sorting. */
typedef unsigned u32; typedef int s32; typedef unsigned char u8; typedef unsigned short u16;
typedef struct { u16 a; u8 b; u8 pad; s32 c; u32 d; } Rec;
u32 g_cnt[8]; u32 g_log[16]; u32 g_pos = 3; Rec g_rec[4] = {{1, 2, 0, -3, 4}, {5, 6, 0, -7, 8}}; u32 g_arr[16] = {9, 3, 7, 1, 8, 2, 6, 4, 5, 0, 15, 11, 13, 12, 14, 10};
static u32 op_add(u32 a, u32 b) { return a + b; }
static u32 op_sub(u32 a, u32 b) { return a - b; }
static u32 op_mul(u32 a, u32 b) { return a * b; }
static u32 op_xor(u32 a, u32 b) { return a ^ b; }
static u32 op_shl(u32 a, u32 b) { return a << (b & 31); }
static u32 op_max(u32 a, u32 b) { return a > b ? a : b; }
static u32 op_div(u32 a, u32 b) { return b ? a / b : 0xffffffffu; }
static u32 op_rot(u32 a, u32 b) { return (a << 7 | a >> 25) + b; }
u32 (*g_ops[8])(u32, u32) = {op_add, op_sub, op_mul, op_xor, op_shl, op_max, op_div, op_rot};
static u32 (*const k_ops[4])(u32, u32) = {op_rot, op_xor, op_sub, op_add};
u32 s_bump(u32 idx, u32 delta) { g_cnt[idx & 7] += delta; g_log[g_pos++ & 15] = g_cnt[idx & 7] ^ delta; u32 s = 0; for (int i = 0; i < 8; i++) s += g_cnt[i]; return s; }
u32 s_calltab(u32 op, u32 a, u32 b) { return g_ops[op & 7](a, b); }
u32 s_constcall(u32 op, u32 a, u32 b) { return k_ops[op & 3](a, b); }
u32 s_swapops(u32 i, u32 j) { u32 (*t)(u32, u32) = g_ops[i & 7]; g_ops[i & 7] = g_ops[j & 7]; g_ops[j & 7] = t; return g_ops[0](1, 2) + g_ops[1](5, 3); }
u32 s_fib(u32 n) { n %= 21; return n < 2 ? n : s_fib(n - 1) + s_fib(n - 2); }
u32 s_ack(u32 m, u32 n);
u32 s_ack(u32 m, u32 n) { m %= 3; n %= 6; return m == 0 ? n + 1 : n == 0 ? s_ack(m - 1, 1) : s_ack(m - 1, s_ack(m, n - 1)); }
u32 s_gcd(u32 a, u32 b) { return b ? s_gcd(b, a % b) : a; }
u32 s_depth(u32 n) { n %= 3000; volatile u32 pad[4]; pad[0] = n; return n ? s_depth(n - 1) + pad[0] : 0; }
u32 s_collatz(u32 n) { u32 steps = 0; n = n % 5000 + 1; while (n != 1) { n = (n & 1) ? 3 * n + 1 : n >> 1; steps++; } return steps; }
u32 s_sw(u32 x) { switch (x) { case 0: return 100; case 1: return 211; case 2: return 322; case 3: return 433; case 4: return 544; case 5: return 655; case 6: return 766; case 7: return 877; case 8: return 988; case 9: return 1099; case 10: return 1210; case 11: return 1321; case 12: return 1432; case 13: return 1543; default: return 7; } }
u32 s_sw2(u32 x) { u32 r = 1; switch (x % 20) { case 3: r += 3; /* fallthrough */ case 4: r *= 5; break; case 7: case 8: case 9: r ^= 0x5a5a; break; case 12: r = 12; break; case 13: r += x; /* fallthrough */ case 14: r += 14; break; case 16: return 16; case 19: r <<= 4; break; default: r = x; } return r + 1; }
u32 s_sw3(u32 x) { switch (x & 7) { case 0: case 2: return s_sw((x >> 3) & 15); case 1: return 'a'; case 3: return 'b'; case 5: return 'e'; default: return x; } }
u32 s_struct(u32 i, u32 v) { Rec *r = &g_rec[i & 3]; r->a = (u16)(r->a + v); r->b = (u8)(r->b ^ (v >> 8)); r->c -= (s32)v; r->d = r->d * 3 + (r->a ^ r->b); return r->a + r->b + (u32)r->c + r->d; }
u32 s_isort(u32 seed) { for (int i = 0; i < 16; i++) { seed = seed * 1664525u + 1013904223u; g_arr[i] = seed >> 12; } for (int i = 1; i < 16; i++) { u32 k = g_arr[i]; int j = i - 1; while (j >= 0 && g_arr[j] > k) { g_arr[j + 1] = g_arr[j]; j--; } g_arr[j + 1] = k; } return g_arr[0] ^ g_arr[7] ^ g_arr[15]; }
u32 s_bsearch(u32 key) { int lo = 0, hi = 15; key >>= 12; while (lo <= hi) { int mid = (lo + hi) >> 1; if (g_arr[mid] == key) return (u32)mid; if (g_arr[mid] < key) lo = mid + 1; else hi = mid - 1; } return 0x100 + (u32)lo; }
u32 s_matmul(u32 seed) { s32 m[3][3], n[3][3], r[3][3]; for (int i = 0; i < 9; i++) { seed = seed * 69069u + 1; m[i / 3][i % 3] = (s32)(seed >> 24) - 128; n[i / 3][i % 3] = (s32)((seed >> 8) & 255) - 128; } u32 h = 0; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { r[i][j] = 0; for (int k = 0; k < 3; k++) r[i][j] += m[i][k] * n[k][j]; h = h * 33 + (u32)r[i][j]; } return h; }
u32 s_tailcalls(u32 n, u32 acc) { n %= 200; return n == 0 ? acc : s_tailcalls(n - 1, acc * 31 + n); }
