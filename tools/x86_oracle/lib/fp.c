/* Controlled x87 library (default -m32 code generation uses the x87 stack). Integer-returning, results also stored in data. */
typedef unsigned u32; typedef int s32;
double g_d[4]; float g_f[4]; u32 g_cw;
static double clampd(double x) { return x > 1e9 ? 1e9 : x < -1e9 ? -1e9 : x; }
u32 x_arith(s32 a, s32 b, s32 c) { double x = a, y = b, z = (double)(c | 1); g_d[0] = (x * 1.5 + y) / z - x / 3.0; g_d[1] = x * y - z; return (u32)(s32)clampd(g_d[0] * 8.0); }
u32 x_fsqrt(u32 a) { double x = (double)a; double r; __asm__("fsqrt" : "=t"(r) : "0"(x)); g_d[2] = r; return (u32)(s32)(r * 1024.0); }
u32 x_fabschs(s32 a, s32 b) { double x = (double)a, y = (double)b; __asm__("fabs; fchs; faddp" : "+t"(x) : "u"(y) : "st(1)"); (void)y; g_d[3] = x; return (u32)(s32)clampd(x); }
u32 x_cmp(s32 a, s32 b) { double x = (double)a, y = (double)b; return (x < y) | ((x <= y) << 1) | ((x == y) << 2) | ((x > y) << 3) | ((x != y) << 4); }
u32 x_round(s32 a, s32 b) { double x = (double)a / ((double)(b | 1)); double r; __asm__("frndint" : "=t"(r) : "0"(x)); g_d[0] = r; return (u32)(s32)clampd(r); }
u32 x_f32(s32 a, s32 b) { float x = (float)a, y = (float)b; g_f[0] = x * y; g_f[1] = x + y; g_f[2] = x / (y == 0.0f ? 1.0f : y); return (u32)(s32)clampd((double)g_f[2] * 100.0) ^ (u32)(s32)clampd((double)g_f[1]); }
u32 x_f32bits(s32 a) { float x = (float)a * 0.1f; union { float f; u32 u; } v; v.f = x; return v.u; }
u32 x_fld_consts(u32 a) { double p, l, z; __asm__("fldpi" : "=t"(p)); __asm__("fldl2e" : "=t"(l)); __asm__("fldz" : "=t"(z)); g_d[1] = p * (double)(a & 255) + l + z; return (u32)(s32)(g_d[1] * 1000.0); }
u32 x_fsin(u32 a) { double x = (double)(s32)(a & 0xffff) / 1000.0, r; __asm__("fsin" : "=t"(r) : "0"(x)); g_d[2] = r; return (u32)(s32)(r * 1000000.0); }
u32 x_trunc(s32 a, s32 b) { double x = (double)a / ((double)(b | 1)); return (u32)(s32)clampd(x) ^ (u32)(long long)(clampd(x) * 4.0); }
u32 x_cw(u32 a) { unsigned short cw; __asm__ volatile("fnstcw %0" : "=m"(cw)); g_cw = cw; (void)a; return cw; }
/* returns double in st0: eax carries garbage, so the runtime (EnterGuest returns EAX only) cannot observe it: documented gap */
double x_ret_double(s32 a, s32 b) { return (double)a / (double)(b | 1); }
