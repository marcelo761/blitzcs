/* Controlled calling-convention library: cdecl and stdcall entry points, mixed nested calls, callee-saved register pressure. */
typedef unsigned u32;
#define STD __attribute__((stdcall))
#define NOINL __attribute__((noinline))
NOINL static u32 helper_c(u32 a, u32 b, u32 c) { return a * 7 + b * 13 + c; }
NOINL STD static u32 helper_s(u32 a, u32 b) { return (a ^ b) + (a >> 3); }
u32 c_cdecl0(void) { return 0xc0de; }
u32 c_cdecl3(u32 a, u32 b, u32 c) { return helper_c(a, b, c); }
u32 c_cdecl6(u32 a, u32 b, u32 c, u32 d, u32 e, u32 f) { return a - b + c * d - e + f; }
STD u32 c_std0(void) { return 0x5d0; }
STD u32 c_std1(u32 a) { return ~a; }
STD u32 c_std2(u32 a, u32 b) { return helper_s(a, b); }
STD u32 c_std4(u32 a, u32 b, u32 c, u32 d) { return helper_c(a, b, c) ^ d; }
STD u32 c_std6(u32 a, u32 b, u32 c, u32 d, u32 e, u32 f) { return a - b + c * d - e + f; }
u32 c_cdecl_calls_std(u32 a, u32 b) { return helper_s(a, b) + helper_s(b, a); }
STD u32 c_std_calls_cdecl(u32 a, u32 b) { return helper_c(a, b, a ^ b) + helper_c(b, a, 1); }
u32 c_pressure(u32 a, u32 b, u32 c, u32 d) {
  u32 e = a * 3, f = b * 5, g = c * 7, h = d * 11, i = a ^ d, j = b + c;
  u32 r = helper_c(e, f, g); r += helper_c(h, i, j); r ^= e + f + g + h + i + j;
  return r + helper_c(r, e, f);
}
STD u32 c_std_pressure(u32 a, u32 b, u32 c) { u32 x = a + 1, y = b + 2, z = c + 3; u32 r = helper_c(x, y, z); return r + x * y + z; }
