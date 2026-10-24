/* Controlled integer library 1: 64-bit arithmetic (adc/sbb/mul/div, helper-free), 32-bit div/idiv, cmp/setcc, overflow builtins. */
typedef unsigned u32; typedef int s32; typedef unsigned long long u64; typedef long long s64;
#define X64(a, b) (((u64)(b) << 32) | (a))
u32 i_add64hi(u32 a, u32 b, u32 c, u32 d) { return (u32)((X64(a, b) + X64(c, d)) >> 32); }
u32 i_add64lo(u32 a, u32 b, u32 c, u32 d) { return (u32)(X64(a, b) + X64(c, d)); }
u32 i_sub64hi(u32 a, u32 b, u32 c, u32 d) { return (u32)((X64(a, b) - X64(c, d)) >> 32); }
u32 i_neg64hi(u32 a, u32 b) { return (u32)((u64)(-(s64)X64(a, b)) >> 32); }
u32 i_mul64lo(u32 a, u32 b, u32 c, u32 d) { return (u32)(X64(a, b) * X64(c, d)); }
u32 i_mul64hi(u32 a, u32 b, u32 c, u32 d) { return (u32)((X64(a, b) * X64(c, d)) >> 32); }
u32 i_umulhi(u32 a, u32 b) { return (u32)(((u64)a * b) >> 32); }
u32 i_smulhi(s32 a, s32 b) { return (u32)((u64)((s64)a * b) >> 32); }
u32 i_smul64lo(s32 a, s32 b) { return (u32)((s64)a * b); }
/* manual shift-subtract 64-bit division (no libgcc) */
static u64 udivmod(u64 x, u64 y, u64 *rem) {
  u64 q = 0, r = 0;
  if (y == 0) { *rem = x; return 0; }
  for (int i = 63; i >= 0; i--) { r = (r << 1) | ((x >> i) & 1); if (r >= y) { r -= y; q |= 1ULL << i; } }
  *rem = r; return q;
}
u32 i_udiv64q_lo(u32 a, u32 b, u32 c, u32 d) { u64 r; return (u32)udivmod(X64(a, b), X64(c, d), &r); }
u32 i_udiv64q_hi(u32 a, u32 b, u32 c, u32 d) { u64 r; return (u32)(udivmod(X64(a, b), X64(c, d), &r) >> 32); }
u32 i_udiv64r_lo(u32 a, u32 b, u32 c, u32 d) { u64 r; udivmod(X64(a, b), X64(c, d), &r); return (u32)r; }
u32 i_udiv64r_hi(u32 a, u32 b, u32 c, u32 d) { u64 r; udivmod(X64(a, b), X64(c, d), &r); return (u32)(r >> 32); }
u32 i_udiv(u32 a, u32 b) { return b ? a / b : 0; }
u32 i_urem(u32 a, u32 b) { return b ? a % b : 0; }
u32 i_sdiv(s32 a, s32 b) { return (b == 0 || (a == (s32)0x80000000 && b == -1)) ? 0 : (u32)(a / b); }
u32 i_srem(s32 a, s32 b) { return (b == 0 || (a == (s32)0x80000000 && b == -1)) ? 0 : (u32)(a % b); }
u32 i_div64by32_q(u32 lo, u32 hi, u32 d) { u32 q, r; if (d == 0 || hi >= d) return 0; __asm__("divl %4" : "=a"(q), "=d"(r) : "0"(lo), "1"(hi), "r"(d)); return q; }
u32 i_div64by32_r(u32 lo, u32 hi, u32 d) { u32 q, r; if (d == 0 || hi >= d) return 0; __asm__("divl %4" : "=a"(q), "=d"(r) : "0"(lo), "1"(hi), "r"(d)); return r; }
u32 i_idiv64by32_q(u32 lo, u32 hi, s32 d) {
  s64 n = (s64)X64(lo, hi); u64 un = n < 0 ? (u64)-n : (u64)n; u32 ud = d < 0 ? (u32)-d : (u32)d;
  if (d == 0 || (un >> 31) >= ud) return 0; /* would raise #DE */
  u32 ql, r; __asm__("idivl %4" : "=a"(ql), "=d"(r) : "0"(lo), "1"(hi), "r"(d)); return ql;
}
u32 i_cmp(u32 a, u32 b) { return ((s32)a < (s32)b) | (((s32)a <= (s32)b) << 1) | ((a < b) << 2) | ((a <= b) << 3) | ((a == b) << 4) | (((s32)a > 0) << 5) | ((!a) << 6); }
u32 i_sbbmask(u32 a, u32 b) { return a < b ? 0xffffffffu : 0; }
u32 i_ovf_sadd(s32 a, s32 b) { s32 r; u32 o = (u32)__builtin_add_overflow(a, b, &r); return (u32)r ^ (o << 31) ^ o; }
u32 i_ovf_ssub(s32 a, s32 b) { s32 r; u32 o = (u32)__builtin_sub_overflow(a, b, &r); return (u32)r ^ (o << 30) ^ o; }
u32 i_ovf_smul(s32 a, s32 b) { s32 r; u32 o = (u32)__builtin_mul_overflow(a, b, &r); return (u32)r ^ (o << 29) ^ o; }
u32 i_ovf_uadd(u32 a, u32 b) { u32 r; u32 o = (u32)__builtin_add_overflow(a, b, &r); return r ^ (o << 28) ^ o; }
u32 i_ovf_usub(u32 a, u32 b) { u32 r; u32 o = (u32)__builtin_sub_overflow(a, b, &r); return r ^ (o << 27) ^ o; }
u32 i_ovf_umul(u32 a, u32 b) { u32 r; u32 o = (u32)__builtin_mul_overflow(a, b, &r); return r ^ (o << 26) ^ o; }
u32 i_cmov(u32 a, u32 b, u32 c, u32 d) { return (s32)a < (s32)b ? c : d; }
u32 i_minmax(s32 a, s32 b, s32 c) { s32 m = a < b ? a : b; s32 M = a > c ? a : c; return (u32)(m * 3 + M); }
u32 i_abs(s32 a) { return a < 0 ? (u32)-a : (u32)a; }
