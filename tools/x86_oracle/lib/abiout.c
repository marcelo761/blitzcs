// FLAGS: -msse2 -mfpmath=387 -freg-struct-return
/* Struct returns through the hidden pointer under thiscall/fastcall/stdcall, and out-pointer parameters (int, float, double, int64, arrays, structs,
 * in/out), in every convention. Arguments are raw bit patterns; pointers are created by the drivers. */
typedef unsigned u32; typedef unsigned long long u64; typedef long long s64; typedef unsigned char u8;
#define T __attribute__((thiscall))
#define F __attribute__((fastcall))
#define C __attribute__((cdecl))
#define S __attribute__((stdcall))
typedef union { u32 u; float f; } F32; typedef union { u64 u; double d; } F64;
static inline float f32(u32 b) { F32 x; x.u = b; return x.f; }
static inline double f64(u32 lo, u32 hi) { F64 x; x.u = ((u64)hi << 32) | lo; return x.d; }
typedef struct { u32 v[5]; } S20; typedef struct { u32 a, b; } S8; typedef struct { u8 a, b, c; } S3;
u32 g_calls = 1; u32 g_pad[3] = {0xdead0001u, 2, 3};

T S20 ts_big(u32 self, u32 a, u32 b) { S20 r = {{self, a, b, self ^ a, self + b}}; g_calls++; return r; }
F S20 fs_big(u32 a, u32 b) { S20 r = {{b, a, a + b, a ^ b, a * 3}}; g_calls++; return r; }
S S20 ss_big(u32 a, u32 b, u32 c) { S20 r = {{c, b, a, a + c, b - c}}; g_calls++; return r; }
T S8 ts_s8(u32 self, u32 a) { S8 r = {self ^ a, self + a}; return r; }
F S8 fs_s8(u32 a, u32 b) { S8 r = {a * 9, b ^ 0x5a5a5a5au}; return r; }
T S3 ts_s3(u32 self, u32 a) { S3 r = {(u8)self, (u8)a, (u8)(self + a)}; return r; }   /* 3 bytes: hidden pointer */
F S3 fs_s3(u32 a, u32 b) { S3 r = {(u8)a, (u8)b, (u8)(a ^ b)}; return r; }

S u32 o_divmod_s(u32 a, u32 b, u32 *q, u32 *r) { b |= 1; *q = a / b; *r = a % b; return a + b; }
C u32 o_i64_c(u32 lo, u32 hi, s64 *out) { s64 x = (s64)(((u64)hi << 32) | lo); *out = x * 3 + 1; return lo ^ hi; }
T u32 o_f32_t(u32 self, u32 a, float *out) { *out = f32(self) * f32(a) + 1.0f; return self + a; }
F u32 o_f64_f(u32 a, u32 lo, u32 hi, double *out) { *out = f64(lo, hi) * (double)(int)a; return a ^ lo; }
C u32 o_arr_c(u32 *buf, u32 n, u32 seed) { if (n > 16) n = 16; for (u32 i = 0; i < n; i++) buf[i] = seed * (i + 1) ^ i; return n; }
C u32 o_inout_c(u32 *p, u32 add) { *p = *p * 5 + add; return *p; }
S u32 o_struct_s(S20 *out, u32 a) { for (u32 i = 0; i < 5; i++) out[0].v[i] = a + i * 0x01010101u; return a; }
T u32 o_two_t(u32 self, u32 *x, u32 *y) { *x = self * 3; *y = ~self; return 1; }
