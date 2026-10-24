#include "common.h"
/* Return-ABI driver: each wrapper performs the typed call natively (with the same calling convention as the library function) and
 * prints a hash of the result bytes in the usual V line. The translated side recomputes the same hash from GuestCallResult. */
extern char r_f32_c[], r_f32_s[], r_f32id_c[], r_f64_c[], r_f64_s[], r_f64r_c[], r_f64id_c[], r_ld_c[], r_ld_s[], r_ldid_c[], r_i64_c[], r_i64_s[], r_i64id_c[], r_u64id_s[],
  r_s1_c[], r_s1_s[], r_s2_c[], r_s4_c[], r_s4_s[], r_s8_c[], r_s8_s[], r_big_c[], r_big_s[], r_x0f_c[], r_x0d_s[];
#define CD __attribute__((cdecl))
#define SD __attribute__((stdcall))
typedef unsigned long long u64; typedef long long s64; typedef unsigned char u8; typedef unsigned short u16;
static u32 fnv(const void *p, int n) { const u8 *b = p; u32 h = 2166136261u; while (n--) h = (h ^ *b++) * 16777619u; return h; }
static u32 hf(float r) { return fnv(&r, 4); }
static u32 hd(double r) { return fnv(&r, 8); }
static u32 hld(long double r) { u8 raw[16]; __builtin_memset(raw, 0, 16); __builtin_memcpy(raw, &r, 10); return fnv(raw, 10); }
static u32 h64(u64 r) { return fnv(&r, 8); }
static u32 w_r_f32_c(u32 a, u32 b) { return hf(((float (CD *)(u32, u32))r_f32_c)(a, b)); }
static u32 w_r_f32_s(u32 a, u32 b) { return hf(((float (SD *)(u32, u32))r_f32_s)(a, b)); }
static u32 w_r_f32id_c(u32 a) { return hf(((float (CD *)(u32))r_f32id_c)(a)); }
static u32 w_r_f64_c(u32 a, u32 b, u32 c, u32 d) { return hd(((double (CD *)(u32, u32, u32, u32))r_f64_c)(a, b, c, d)); }
static u32 w_r_f64_s(u32 a, u32 b, u32 c, u32 d) { return hd(((double (SD *)(u32, u32, u32, u32))r_f64_s)(a, b, c, d)); }
static u32 w_r_f64r_c(u32 a, u32 b, u32 c, u32 d) { return hd(((double (CD *)(u32, u32, u32, u32))r_f64r_c)(a, b, c, d)); }
static u32 w_r_f64id_c(u32 a, u32 b) { return hd(((double (CD *)(u32, u32))r_f64id_c)(a, b)); }
static u32 w_r_ld_c(u32 a, u32 b, u32 c, u32 d) { return hld(((long double (CD *)(u32, u32, u32, u32))r_ld_c)(a, b, c, d)); }
static u32 w_r_ld_s(u32 a, u32 b, u32 c, u32 d) { return hld(((long double (SD *)(u32, u32, u32, u32))r_ld_s)(a, b, c, d)); }
static u32 w_r_ldid_c(u32 a, u32 b) { return hld(((long double (CD *)(u32, u32))r_ldid_c)(a, b)); }
static u32 w_r_i64_c(u32 a, u32 b) { return h64((u64)((s64 (CD *)(u32, u32))r_i64_c)(a, b)); }
static u32 w_r_i64_s(u32 a, u32 b) { return h64((u64)((s64 (SD *)(u32, u32))r_i64_s)(a, b)); }
static u32 w_r_i64id_c(u32 a, u32 b) { return h64((u64)((s64 (CD *)(u32, u32))r_i64id_c)(a, b)); }
static u32 w_r_u64id_s(u32 a, u32 b) { return h64(((u64 (SD *)(u32, u32))r_u64id_s)(a, b)); }
typedef struct { u8 a; } S1; typedef struct { u8 a, b; } S2; typedef struct { u8 a, b, c, d; } S4; typedef struct { u32 a, b; } S8; typedef struct { u32 v[5]; } S20;
static u32 w_r_s1_c(u32 a, u32 b) { S1 r = ((S1 (CD *)(u32, u32))r_s1_c)(a, b); return fnv(&r, 1); }
static u32 w_r_s1_s(u32 a, u32 b) { S1 r = ((S1 (SD *)(u32, u32))r_s1_s)(a, b); return fnv(&r, 1); }
static u32 w_r_s2_c(u32 a, u32 b) { S2 r = ((S2 (CD *)(u32, u32))r_s2_c)(a, b); return fnv(&r, 2); }
static u32 w_r_s4_c(u32 a, u32 b) { S4 r = ((S4 (CD *)(u32, u32))r_s4_c)(a, b); return fnv(&r, 4); }
static u32 w_r_s4_s(u32 a, u32 b) { S4 r = ((S4 (SD *)(u32, u32))r_s4_s)(a, b); return fnv(&r, 4); }
static u32 w_r_s8_c(u32 a, u32 b) { S8 r = ((S8 (CD *)(u32, u32))r_s8_c)(a, b); return fnv(&r, 8); }
static u32 w_r_s8_s(u32 a, u32 b) { S8 r = ((S8 (SD *)(u32, u32))r_s8_s)(a, b); return fnv(&r, 8); }
static u32 w_r_big_c(u32 a, u32 b) { S20 r = ((S20 (CD *)(u32, u32))r_big_c)(a, b); return fnv(&r, 20); }
static u32 w_r_big_s(u32 a, u32 b) { S20 r = ((S20 (SD *)(u32, u32))r_big_s)(a, b); return fnv(&r, 20); }
static u32 w_r_x0f_c(u32 a, u32 b) { return hf(((float (CD __attribute__((sseregparm)) *)(u32, u32))r_x0f_c)(a, b)); }
static u32 w_r_x0d_s(u32 a, u32 b) { return hd(((double (SD __attribute__((sseregparm)) *)(u32, u32))r_x0d_s)(a, b)); }

/* Float/double bit patterns that matter: +-0, +-1, denormals, max/min normal, +-inf, quiet/signalling NaNs, INT64/INT32 extremes. */
static const u32 EDGE2[] = {0, 1, 2, 0x7fffff, 0x800000, 0x3f800000, 0xbf800000, 0x40000000, 0x7f7fffff, 0x7f800000, 0xff800000, 0x7fc00000, 0x7fa00000, 0xffc00000, 0xffc12345u,
  0x80000000u, 0x80000001u, 0x807fffff, 0xffffffffu, 0x7fffffffu, 0x7ff00000, 0x7ff80000, 0x7ff40000, 0xfff00000u, 0x000fffff, 0x00100000, 0x7fefffff, 0x3ff00000, 0xbff00000u, 0x43e00000, 0xc3e00000u};
#define NE2 ((int)(sizeof EDGE2 / sizeof EDGE2[0]))
static void (*const keep_run_all)(const Fn *, int, int) __attribute__((used)) = run_all;
static void run2(const Fn *t, int nf, int nrand) {
  puts_("H data "); hex((u32)(__data_end - __data_start)); nl();
  for (int i = 0; i < nf; i++) { puts_("F "); puts_(t[i].name); sp(); put(t[i].conv); sp(); hex((u32)t[i].n); nl(); }
  puts_("I "); hex(dhash()); nl();
  for (int i = 0; i < nf; i++) {
    u32 a[6] = {0, 0, 0, 0, 0, 0};
    if (t[i].n == 1) { for (int x = 0; x < NE2; x++) { a[0] = EDGE2[x]; vec(&t[i], a); } }
    else for (int x = 0; x < NE2; x++) for (int y = 0; y < NE2; y++) {
      for (int k = 2; k < 6; k++) a[k] = lcg();
      a[0] = EDGE2[x]; a[1] = EDGE2[y]; vec(&t[i], a);
    }
    for (int r = 0; r < nrand; r++) { for (int k = 0; k < 6; k++) a[k] = lcg(); vec(&t[i], a); }
  }
  puts_("D "); nl(); puts_("E done\n"); flush();
}
#define E(n, na, ...) {#n, 'C', na, {__VA_ARGS__}, w_##n}
static const Fn T[] = {
  E(r_f32_c, 2, 0), E(r_f32_s, 2, 0), E(r_f32id_c, 1, 0),
  E(r_f64_c, 4, 0, 0, 2, 2), E(r_f64_s, 4, 0, 0, 2, 2), E(r_f64r_c, 4, 0), E(r_f64id_c, 2, 0, 2),
  E(r_ld_c, 4, 0, 0, 2, 2), E(r_ld_s, 4, 0, 0, 2, 2), E(r_ldid_c, 2, 0, 2),
  E(r_i64_c, 2, 0), E(r_i64_s, 2, 0), E(r_i64id_c, 2, 0), E(r_u64id_s, 2, 0),
  E(r_s1_c, 2, 0), E(r_s1_s, 2, 0), E(r_s2_c, 2, 0), E(r_s4_c, 2, 0), E(r_s4_s, 2, 0), E(r_s8_c, 2, 0), E(r_s8_s, 2, 0), E(r_big_c, 2, 0), E(r_big_s, 2, 0),
  E(r_x0f_c, 2, 0), E(r_x0d_s, 2, 0, 2),
};
__attribute__((force_align_arg_pointer, noreturn)) void _start(void) { run2(T, (int)(sizeof T / sizeof T[0]), 60); finish(); }
