// FLAGS: -msse2 -mfpmath=sse -mstackrealign -fno-math-errno
/* Controlled SSE/SSE2 library: scalar float/double, packed int ops (vector extensions), conversions. */
typedef unsigned u32; typedef int s32;
typedef int v4si __attribute__((vector_size(16)));
typedef float v4sf __attribute__((vector_size(16)));
typedef double v2df __attribute__((vector_size(16)));
v4si g_vi[4] __attribute__((aligned(16))); v4sf g_vf[2] __attribute__((aligned(16))); v2df g_vd[2] __attribute__((aligned(16))); double g_sd[4]; float g_ss[4];
static double clampd(double x) { return x > 1e9 ? 1e9 : x < -1e9 ? -1e9 : x; }
u32 e_dbl(s32 a, s32 b, s32 c) { double x = a, y = b, z = (double)(c | 1); g_sd[0] = (x * 1.5 + y) / z - x / 3.0; g_sd[1] = __builtin_sqrt(x * x + y * y); return (u32)(s32)clampd(g_sd[0] * 8.0) ^ (u32)(s32)g_sd[1]; }
u32 e_flt(s32 a, s32 b) { float x = (float)a, y = (float)b; g_ss[0] = x * y; g_ss[1] = x - y; g_ss[2] = x / (y == 0.0f ? 1.0f : y); g_ss[3] = (float)(double)x; union { float f; u32 u; } v; v.f = g_ss[2]; return v.u ^ (u32)(s32)clampd((double)g_ss[1]); }
u32 e_cmp(s32 a, s32 b) { double x = (double)a, y = (double)b; return (x < y) | ((x <= y) << 1) | ((x == y) << 2) | ((x > y) << 3) | ((x != y) << 4); }
u32 e_cvt(s32 a, s32 b) { double x = (double)a / (double)(b | 1); return (u32)(s32)clampd(x) ^ (u32)(s32)clampd(x * 7.5) ^ (u32)(s32)(float)clampd(x); }
u32 e_padd(u32 a, u32 b, u32 c, u32 d) { v4si x = {(s32)a, (s32)b, (s32)c, (s32)d}; v4si y = {(s32)d, (s32)c, (s32)b, (s32)a}; g_vi[0] = x + y; g_vi[1] = x - y; g_vi[2] = x ^ y; g_vi[3] = (x << 3) | (y >> 5); return (u32)(g_vi[0][0] ^ g_vi[1][1] ^ g_vi[2][2] ^ g_vi[3][3]); }
u32 e_pmul(u32 a, u32 b, u32 c, u32 d) { v4si x = {(s32)a, (s32)b, (s32)c, (s32)d}; v4si y = x * 3 + 1; g_vi[0] = y; return (u32)(y[0] + y[1] * 3 + y[2] * 5 + y[3] * 7); }
u32 e_pcmp(u32 a, u32 b, u32 c, u32 d) { v4si x = {(s32)a, (s32)b, (s32)c, (s32)d}; v4si y = {(s32)c, (s32)b, (s32)a, 0}; v4si m = x > y; v4si e = x == y; g_vi[1] = m; g_vi[2] = e; return (u32)((m[0] & 1) | (m[1] & 2) | (m[2] & 4) | (m[3] & 8) | (e[0] & 16) | (e[1] & 32) | (e[2] & 64) | (e[3] & 128)); }
u32 e_shuf(u32 a, u32 b, u32 c, u32 d) { v4si x = {(s32)a, (s32)b, (s32)c, (s32)d}; v4si m = {3, 0, 2, 1}; v4si s = __builtin_shuffle(x, m); g_vi[3] = s; return (u32)(s[0] + s[1] * 3 + s[2] * 5 + s[3] * 7); }
u32 e_pmov(u32 a, u32 b) { v4si x = {(s32)a, (s32)b, (s32)(a ^ b), (s32)(a + b)}; int mk = __builtin_ia32_movmskps((v4sf)x); return (u32)mk; }
u32 e_pfloat(s32 a, s32 b, s32 c, s32 d) { v4sf x = {(float)a, (float)b, (float)c, (float)d}; v4sf y = x * x + x * 0.5f; g_vf[0] = y; return (u32)(s32)clampd((double)(y[0] + y[1] - y[2] + y[3])); }
u32 e_pdouble(s32 a, s32 b) { v2df x = {(double)a, (double)b}; v2df y = x * x - x / 4.0; g_vd[0] = y; return (u32)(s32)clampd(y[0] + y[1]); }
u32 e_minmax(s32 a, s32 b) { double x = a, y = b; double mn = x < y ? x : y, mx = x > y ? x : y; g_sd[2] = mn; g_sd[3] = mx; return (u32)(s32)(mx - mn); }
u32 e_movd(u32 a, u32 b) { v4si x = {(s32)a, (s32)b, 0, 0}; v4si y = __builtin_shuffle(x, (v4si){1, 0, 2, 3}); return (u32)y[0] ^ ((u32)y[1] << 1); }
