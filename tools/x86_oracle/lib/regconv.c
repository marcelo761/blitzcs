// FLAGS: -msse2 -mfpmath=387
/* Register-argument conventions: thiscall (ECX = this) and fastcall (ECX, EDX), 0..5 arguments, nested calls across all four conventions,
 * callee-saved register pressure, and int / float / double / int64 returns. Arguments are raw bit patterns. */
typedef unsigned u32; typedef unsigned long long u64; typedef long long s64;
#define T __attribute__((thiscall))
#define F __attribute__((fastcall))
#define C __attribute__((cdecl))
#define S __attribute__((stdcall))
typedef union { u32 u; float f; } F32; typedef union { u64 u; double d; } F64;
static inline float f32(u32 b) { F32 x; x.u = b; return x.f; }
static inline double f64(u32 lo, u32 hi) { F64 x; x.u = ((u64)hi << 32) | lo; return x.d; }

u32 g_this_sum = 0x12345u;     /* data state: the hash of the data section after each call must match */
u32 g_pad[3] = {0x11111111u, 0x22222222u, 0x33333333u};

T u32 t_a0(u32 self) { g_this_sum += self; return self * 3u + 1u; }
T u32 t_a1(u32 self, u32 a) { g_this_sum ^= a + self; return self ^ (a << 2); }
T u32 t_a2(u32 self, u32 a, u32 b) { return self * a + b - (self >> 3); }
T u32 t_a5(u32 self, u32 a, u32 b, u32 c, u32 d, u32 e) { g_this_sum += c; return ((self + a) * b) ^ (c - d) ^ (e << 5) ^ self; }

F u32 f_a0(void) { g_this_sum += 7; return 0xabcdu; }
F u32 f_a1(u32 a) { g_this_sum += a; return a * 5u + 2u; }
F u32 f_a2(u32 a, u32 b) { return a * 3u + b; }
F u32 f_a3(u32 a, u32 b, u32 c) { return a * 3u + b + c * 5u; }
F u32 f_a5(u32 a, u32 b, u32 c, u32 d, u32 e) { g_this_sum ^= e; return ((a ^ b) + c * d) - e; }

/* nested: thiscall -> fastcall -> stdcall -> cdecl, with the register arguments kept live across the inner calls */
C u32 n_cdecl(u32 a, u32 b) { return a * 7u ^ b; }
S u32 n_std(u32 a, u32 b, u32 c) { return n_cdecl(a, b) + c; }
F u32 n_fast(u32 a, u32 b, u32 c) { return n_std(a, b, c) * 3u + a; }
T u32 n_this(u32 self, u32 a, u32 b) { return n_fast(self, a, b) ^ n_fast(a, self, b) ^ self; }
/* cdecl and stdcall callers of register-convention callees */
C u32 n_cdecl_calls_this(u32 a, u32 b) { u32 r = ((u32 (T *)(u32, u32))t_a1)(a, b); return r + ((u32 (F *)(u32, u32))f_a2)(b, a); }
S u32 n_std_calls_fast(u32 a, u32 b) { return ((u32 (F *)(u32, u32, u32))f_a3)(a, b, a ^ b); }
T u32 n_pressure(u32 self, u32 a, u32 b)
{
  u32 x1 = self * 3u, x2 = a ^ 0x55u, x3 = b + 11u, x4 = (self >> 5) + a, x5 = (a << 3) ^ b;
  u32 r = n_fast(x1, x2, x3);
  return r + x1 + x2 * x3 + x4 - x5;
}
F u32 f_pressure(u32 a, u32 b, u32 c)
{
  u32 x1 = a * 3u, x2 = b ^ 0x55u, x3 = c + 11u, x4 = (a >> 5) + b, x5 = (b << 3) ^ c;
  u32 r = ((u32 (T *)(u32, u32, u32))n_this)(x1, x2, x3);
  return r + x1 + x2 * x3 + x4 - x5;
}

/* non-integer returns */
T float tr_f32(u32 self, u32 a) { return f32(self) * f32(a) + f32(self); }
F float fr_f32(u32 a, u32 b) { return f32(a) * f32(b) + f32(b); }
T double tr_f64(u32 self, u32 alo, u32 ahi) { return f64(alo, ahi) * (double)(int)self + 0.5; }
F double fr_f64(u32 a, u32 blo, u32 bhi) { return f64(blo, bhi) - (double)(int)a; }
T s64 tr_i64(u32 self, u32 lo, u32 hi) { return (s64)(((u64)hi << 32) | lo) * (s64)(int)self + 1; }
F s64 fr_i64(u32 a, u32 b, u32 hi) { return (s64)(((u64)hi << 32) | b) - (s64)(int)a; }
T float tr_f32_nested(u32 self, u32 a) { return ((float (F *)(u32, u32))fr_f32)(self, a) + f32(a); }
F long double fr_ld(u32 a, u32 blo, u32 bhi) { return (long double)f64(blo, bhi) * (long double)(int)a + 1.0L; }
