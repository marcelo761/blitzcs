#include "common.h"
/* Struct-return and out-pointer driver. Each wrapper makes the native call with locally owned buffers and returns FNV over (return value | out bytes);
 * the translated side hashes the same byte sequence from the typed runtime results. */
extern char ts_big[], fs_big[], ss_big[], ts_s8[], fs_s8[], ts_s3[], fs_s3[], o_divmod_s[], o_i64_c[], o_f32_t[], o_f64_f[], o_arr_c[], o_inout_c[], o_struct_s[], o_two_t[];
#define TC __attribute__((thiscall))
#define FC __attribute__((fastcall))
#define CD __attribute__((cdecl))
#define SD __attribute__((stdcall))
typedef unsigned long long u64; typedef long long s64; typedef unsigned char u8;
typedef struct { u32 v[5]; } S20; typedef struct { u32 a, b; } S8; typedef struct { u8 a, b, c; } S3;
static u32 fnv(const void *p, int n, u32 h) { const u8 *b = p; while (n--) h = (h ^ *b++) * 16777619u; return h; }
#define H0 2166136261u
static u32 w_ts_big(u32 a, u32 b, u32 c) { S20 r = ((S20 (TC *)(u32, u32, u32))ts_big)(a, b, c); return fnv(&r, 20, H0); }
static u32 w_fs_big(u32 a, u32 b) { S20 r = ((S20 (FC *)(u32, u32))fs_big)(a, b); return fnv(&r, 20, H0); }
static u32 w_ss_big(u32 a, u32 b, u32 c) { S20 r = ((S20 (SD *)(u32, u32, u32))ss_big)(a, b, c); return fnv(&r, 20, H0); }
static u32 w_ts_s8(u32 a, u32 b) { S8 r = ((S8 (TC *)(u32, u32))ts_s8)(a, b); return fnv(&r, 8, H0); }
static u32 w_fs_s8(u32 a, u32 b) { S8 r = ((S8 (FC *)(u32, u32))fs_s8)(a, b); return fnv(&r, 8, H0); }
static u32 w_ts_s3(u32 a, u32 b) { S3 r = ((S3 (TC *)(u32, u32))ts_s3)(a, b); return fnv(&r, 3, H0); }
static u32 w_fs_s3(u32 a, u32 b) { S3 r = ((S3 (FC *)(u32, u32))fs_s3)(a, b); return fnv(&r, 3, H0); }
static u32 w_o_divmod_s(u32 a, u32 b) { u32 q = 0, r = 0; u32 ret = ((u32 (SD *)(u32, u32, u32 *, u32 *))o_divmod_s)(a, b, &q, &r); u32 h = fnv(&ret, 4, H0); h = fnv(&q, 4, h); return fnv(&r, 4, h); }
static u32 w_o_i64_c(u32 a, u32 b) { s64 out = 0; u32 ret = ((u32 (CD *)(u32, u32, s64 *))o_i64_c)(a, b, &out); return fnv(&out, 8, fnv(&ret, 4, H0)); }
static u32 w_o_f32_t(u32 a, u32 b) { float out = 0; u32 ret = ((u32 (TC *)(u32, u32, float *))o_f32_t)(a, b, &out); return fnv(&out, 4, fnv(&ret, 4, H0)); }
static u32 w_o_f64_f(u32 a, u32 b, u32 c) { double out = 0; u32 ret = ((u32 (FC *)(u32, u32, u32, double *))o_f64_f)(a, b, c, &out); return fnv(&out, 8, fnv(&ret, 4, H0)); }
static u32 w_o_arr_c(u32 n, u32 seed) { u32 buf[16]; for (int i = 0; i < 16; i++) buf[i] = 0; u32 ret = ((u32 (CD *)(u32 *, u32, u32))o_arr_c)(buf, n, seed); return fnv(buf, 64, fnv(&ret, 4, H0)); }
static u32 w_o_inout_c(u32 p, u32 add) { u32 v = p; u32 ret = ((u32 (CD *)(u32 *, u32))o_inout_c)(&v, add); return fnv(&v, 4, fnv(&ret, 4, H0)); }
static u32 w_o_struct_s(u32 a) { S20 out; for (int i = 0; i < 5; i++) out.v[i] = 0; u32 ret = ((u32 (SD *)(S20 *, u32))o_struct_s)(&out, a); return fnv(&out, 20, fnv(&ret, 4, H0)); }
static u32 w_o_two_t(u32 a) { u32 x = 0, y = 0; u32 ret = ((u32 (TC *)(u32, u32 *, u32 *))o_two_t)(a, &x, &y); return fnv(&y, 4, fnv(&x, 4, fnv(&ret, 4, H0))); }
static const Fn Tb[] = {
  {"ts_big", 'C', 3, {0}, w_ts_big}, {"fs_big", 'C', 2, {0}, w_fs_big}, {"ss_big", 'C', 3, {0}, w_ss_big}, {"ts_s8", 'C', 2, {0}, w_ts_s8}, {"fs_s8", 'C', 2, {0}, w_fs_s8},
  {"ts_s3", 'C', 2, {0}, w_ts_s3}, {"fs_s3", 'C', 2, {0}, w_fs_s3}, {"o_divmod_s", 'C', 2, {0}, w_o_divmod_s}, {"o_i64_c", 'C', 2, {0}, w_o_i64_c},
  {"o_f32_t", 'C', 2, {0}, w_o_f32_t}, {"o_f64_f", 'C', 3, {0}, w_o_f64_f}, {"o_arr_c", 'C', 2, {0, 0}, w_o_arr_c}, {"o_inout_c", 'C', 2, {0}, w_o_inout_c},
  {"o_struct_s", 'C', 1, {0}, w_o_struct_s}, {"o_two_t", 'C', 1, {0}, w_o_two_t},
};
ORACLE_MAIN(Tb, 80)
