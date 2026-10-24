#include "common.h"
/* Register-convention driver: each wrapper makes the typed native call (thiscall/fastcall/...) and returns a hash of the result; the translated
 * side recomputes the hash from the typed runtime result. Vectors are the usual edge x edge + random. */
extern char t_a0[], t_a1[], t_a2[], t_a5[], f_a0[], f_a1[], f_a2[], f_a3[], f_a5[], n_this[], n_cdecl_calls_this[], n_std_calls_fast[], n_pressure[], f_pressure[],
  tr_f32[], fr_f32[], tr_f64[], fr_f64[], tr_i64[], fr_i64[], tr_f32_nested[], fr_ld[];
#define TC __attribute__((thiscall))
#define FC __attribute__((fastcall))
#define CD __attribute__((cdecl))
#define SD __attribute__((stdcall))
typedef unsigned long long u64; typedef long long s64; typedef unsigned char u8;
static u32 fnv(const void *p, int n) { const u8 *b = p; u32 h = 2166136261u; while (n--) h = (h ^ *b++) * 16777619u; return h; }
static u32 hf(float r) { return fnv(&r, 4); }
static u32 hd(double r) { return fnv(&r, 8); }
static u32 hld(long double r) { u8 raw[16]; __builtin_memset(raw, 0, 16); __builtin_memcpy(raw, &r, 10); return fnv(raw, 10); }
static u32 h64(u64 r) { return fnv(&r, 8); }
#define TI(n, ...) ((u32 (TC *)())n)(__VA_ARGS__)
static u32 w_t_a0(u32 a) { return ((u32 (TC *)(u32))t_a0)(a); }
static u32 w_t_a1(u32 a, u32 b) { return ((u32 (TC *)(u32, u32))t_a1)(a, b); }
static u32 w_t_a2(u32 a, u32 b, u32 c) { return ((u32 (TC *)(u32, u32, u32))t_a2)(a, b, c); }
static u32 w_t_a5(u32 a, u32 b, u32 c, u32 d, u32 e, u32 f) { return ((u32 (TC *)(u32, u32, u32, u32, u32, u32))t_a5)(a, b, c, d, e, f); }
static u32 w_f_a0(void) { return ((u32 (FC *)(void))f_a0)(); }
static u32 w_f_a1(u32 a) { return ((u32 (FC *)(u32))f_a1)(a); }
static u32 w_f_a2(u32 a, u32 b) { return ((u32 (FC *)(u32, u32))f_a2)(a, b); }
static u32 w_f_a3(u32 a, u32 b, u32 c) { return ((u32 (FC *)(u32, u32, u32))f_a3)(a, b, c); }
static u32 w_f_a5(u32 a, u32 b, u32 c, u32 d, u32 e) { return ((u32 (FC *)(u32, u32, u32, u32, u32))f_a5)(a, b, c, d, e); }
static u32 w_n_this(u32 a, u32 b, u32 c) { return ((u32 (TC *)(u32, u32, u32))n_this)(a, b, c); }
static u32 w_n_cdecl_calls_this(u32 a, u32 b) { return ((u32 (CD *)(u32, u32))n_cdecl_calls_this)(a, b); }
static u32 w_n_std_calls_fast(u32 a, u32 b) { return ((u32 (SD *)(u32, u32))n_std_calls_fast)(a, b); }
static u32 w_n_pressure(u32 a, u32 b, u32 c) { return ((u32 (TC *)(u32, u32, u32))n_pressure)(a, b, c); }
static u32 w_f_pressure(u32 a, u32 b, u32 c) { return ((u32 (FC *)(u32, u32, u32))f_pressure)(a, b, c); }
static u32 w_tr_f32(u32 a, u32 b) { return hf(((float (TC *)(u32, u32))tr_f32)(a, b)); }
static u32 w_fr_f32(u32 a, u32 b) { return hf(((float (FC *)(u32, u32))fr_f32)(a, b)); }
static u32 w_tr_f64(u32 a, u32 b, u32 c) { return hd(((double (TC *)(u32, u32, u32))tr_f64)(a, b, c)); }
static u32 w_fr_f64(u32 a, u32 b, u32 c) { return hd(((double (FC *)(u32, u32, u32))fr_f64)(a, b, c)); }
static u32 w_tr_i64(u32 a, u32 b, u32 c) { return h64((u64)((s64 (TC *)(u32, u32, u32))tr_i64)(a, b, c)); }
static u32 w_fr_i64(u32 a, u32 b, u32 c) { return h64((u64)((s64 (FC *)(u32, u32, u32))fr_i64)(a, b, c)); }
static u32 w_tr_f32_nested(u32 a, u32 b) { return hf(((float (TC *)(u32, u32))tr_f32_nested)(a, b)); }
static u32 w_fr_ld(u32 a, u32 b, u32 c) { return hld(((long double (FC *)(u32, u32, u32))fr_ld)(a, b, c)); }

static const u32 EDGE2[] = {0, 1, 2, 3, 0x7f, 0x80, 0xff, 0xffff, 0x7fffffffu, 0x80000000u, 0xffffffffu, 0x3f800000, 0xbf800000, 0x7f800000, 0xff800000, 0x7fc00000, 0x7fa00000,
  0x80000001u, 0x007fffff, 0x7ff00000, 0x7ff80000, 0xfff00000u, 0x40000000, 0x55555555u, 0xaaaaaaaau, 0x43e00000, 0xc3e00000u};
#define NE2 ((int)(sizeof EDGE2 / sizeof EDGE2[0]))
static void (*const keep_run_all)(const Fn *, int, int) __attribute__((used)) = run_all;
static void runr(const Fn *t, int nf, int nrand) {
  puts_("H data "); hex((u32)(__data_end - __data_start)); nl();
  for (int i = 0; i < nf; i++) { puts_("F "); puts_(t[i].name); sp(); put(t[i].conv); sp(); hex((u32)t[i].n); nl(); }
  puts_("I "); hex(dhash()); nl();
  for (int i = 0; i < nf; i++) {
    u32 a[6] = {0, 0, 0, 0, 0, 0};
    if (t[i].n == 0) { vec(&t[i], a); continue; }
    if (t[i].n == 1) { for (int x = 0; x < NE2; x++) { a[0] = EDGE2[x]; vec(&t[i], a); } }
    else for (int x = 0; x < NE2; x++) for (int y = 0; y < NE2; y++) {
      for (int k = 2; k < 6; k++) a[k] = lcg();
      a[0] = EDGE2[x]; a[1] = EDGE2[y]; vec(&t[i], a);
    }
    for (int r = 0; r < nrand; r++) { for (int k = 0; k < 6; k++) a[k] = lcg(); vec(&t[i], a); }
  }
  puts_("D ");
  for (const unsigned char *p = (const unsigned char *)__data_start; p < (const unsigned char *)__data_end; p++) { put("0123456789abcdef"[*p >> 4]); put("0123456789abcdef"[*p & 15]); }
  nl(); puts_("E done\n"); flush();
}
#define E(n, na) {#n + 0, 'C', na, {0}, w_##n}
static const Fn Tb[] = {
  {"t_a0", 'C', 1, {0}, w_t_a0}, {"t_a1", 'C', 2, {0}, w_t_a1}, {"t_a2", 'C', 3, {0}, w_t_a2}, {"t_a5", 'C', 6, {0}, w_t_a5},
  {"f_a0", 'C', 0, {0}, w_f_a0}, {"f_a1", 'C', 1, {0}, w_f_a1}, {"f_a2", 'C', 2, {0}, w_f_a2}, {"f_a3", 'C', 3, {0}, w_f_a3}, {"f_a5", 'C', 5, {0}, w_f_a5},
  {"n_this", 'C', 3, {0}, w_n_this}, {"n_cdecl_calls_this", 'C', 2, {0}, w_n_cdecl_calls_this}, {"n_std_calls_fast", 'C', 2, {0}, w_n_std_calls_fast},
  {"n_pressure", 'C', 3, {0}, w_n_pressure}, {"f_pressure", 'C', 3, {0}, w_f_pressure},
  {"tr_f32", 'C', 2, {0}, w_tr_f32}, {"fr_f32", 'C', 2, {0}, w_fr_f32}, {"tr_f64", 'C', 3, {0}, w_tr_f64}, {"fr_f64", 'C', 3, {0}, w_fr_f64},
  {"tr_i64", 'C', 3, {0}, w_tr_i64}, {"fr_i64", 'C', 3, {0}, w_fr_i64}, {"tr_f32_nested", 'C', 2, {0}, w_tr_f32_nested}, {"fr_ld", 'C', 3, {0}, w_fr_ld},
};
__attribute__((force_align_arg_pointer, noreturn)) void _start(void) { oracle_init(); runr(Tb, (int)(sizeof Tb / sizeof Tb[0]), 60); finish(); }
