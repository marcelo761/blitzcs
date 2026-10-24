// FLAGS: -msse2 -mfpmath=387 -freg-struct-return
/* Controlled return-ABI library: every return class of the IA-32 calling conventions, in cdecl and stdcall.
 *   ST0 (float, double, long double)   EDX:EAX (int64)   EAX/EDX structs of 1/2/4/8 bytes   hidden-pointer structs   XMM0 (sseregparm, opt-in)
 * Arguments are raw bit patterns so the drivers can reach NaN/inf/-0/denormal/INT64_MIN. */
typedef unsigned u32; typedef unsigned long long u64; typedef long long s64; typedef unsigned char u8; typedef unsigned short u16;
#define C __attribute__((cdecl))
#define S __attribute__((stdcall))
typedef union { u32 u; float f; } F32; typedef union { u64 u; double d; } F64;
static inline float f32(u32 b) { F32 x; x.u = b; return x.f; }
static inline double f64(u32 lo, u32 hi) { F64 x; x.u = ((u64)hi << 32) | lo; return x.d; }

C float r_f32_c(u32 a, u32 b) { return f32(a) * f32(b) + f32(a); }
S float r_f32_s(u32 a, u32 b) { return f32(a) * f32(b) + f32(a); }
C float r_f32id_c(u32 a) { return f32(a); }                       /* pure pass-through: NaN payloads, -0, denormals */
C double r_f64_c(u32 ahi, u32 bhi, u32 alo, u32 blo) { return f64(alo, ahi) * f64(blo, bhi) - f64(alo, ahi); }
S double r_f64_s(u32 ahi, u32 bhi, u32 alo, u32 blo) { return f64(alo, ahi) * f64(blo, bhi) - f64(alo, ahi); }
C double r_f64r_c(u32 ahi, u32 bhi, u32 alo, u32 blo) { return f64(alo, ahi) * f64(blo, bhi) - f64(alo, ahi); }
C double r_f64id_c(u32 hi, u32 lo) { return f64(lo, hi); }
C long double r_ld_c(u32 ahi, u32 bhi, u32 alo, u32 blo) { return (long double)f64(alo, ahi) * (long double)f64(blo, bhi) + 1.0L; }
S long double r_ld_s(u32 ahi, u32 bhi, u32 alo, u32 blo) { return (long double)f64(alo, ahi) * (long double)f64(blo, bhi) + 1.0L; }
C long double r_ldid_c(u32 hi, u32 lo) { return (long double)f64(lo, hi); }

C s64 r_i64_c(u32 hi, u32 lo) { s64 x = (s64)(((u64)hi << 32) | lo); return x * 3 + 1; }
S s64 r_i64_s(u32 hi, u32 lo) { s64 x = (s64)(((u64)hi << 32) | lo); return x * 3 + 1; }
C s64 r_i64id_c(u32 hi, u32 lo) { return (s64)(((u64)hi << 32) | lo); }   /* INT64_MIN etc. */
S u64 r_u64id_s(u32 hi, u32 lo) { return ((u64)hi << 32) | lo; }

typedef struct { u8 a; } S1; typedef struct { u8 a, b; } S2; typedef struct { u8 a, b, c, d; } S4; typedef struct { u32 a, b; } S8; typedef struct { u32 v[5]; } S20;
C S1 r_s1_c(u32 a, u32 b) { S1 r = {(u8)(a ^ b)}; return r; }
S S1 r_s1_s(u32 a, u32 b) { S1 r = {(u8)(a + b)}; return r; }
C S2 r_s2_c(u32 a, u32 b) { S2 r = {(u8)a, (u8)(b >> 3)}; return r; }
C S4 r_s4_c(u32 a, u32 b) { S4 r = {(u8)a, (u8)(a >> 8), (u8)b, (u8)(b >> 24)}; return r; }
S S4 r_s4_s(u32 a, u32 b) { S4 r = {(u8)b, (u8)(b >> 8), (u8)a, (u8)(a >> 24)}; return r; }
C S8 r_s8_c(u32 a, u32 b) { S8 r = {a * 7 + 1, b ^ 0x80000000u}; return r; }
S S8 r_s8_s(u32 a, u32 b) { S8 r = {b, a}; return r; }
C S20 r_big_c(u32 a, u32 b) { S20 r = {{a, b, a + b, a ^ b, a * b}}; return r; }   /* hidden pointer */
S S20 r_big_s(u32 a, u32 b) { S20 r = {{b, a, a - b, a | b, a & b}}; return r; }

C float __attribute__((sseregparm)) r_x0f_c(u32 a, u32 b) { return f32(a) + f32(b); }
S double __attribute__((sseregparm)) r_x0d_s(u32 ahi, u32 alo) { return f64(alo, ahi) * 2.0; }
