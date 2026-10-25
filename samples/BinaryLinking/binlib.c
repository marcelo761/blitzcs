/* Controlled x86 library for the binary-linking sample. Built with tools/x86_oracle/build.sh. */
int add32(int a, int b) { return a + b; }
int sum3(int a, int b, int c) { return a + b + c; }
int clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
int str_length(const char *s) { int n = 0; while (s[n]) n++; return n; }
int str_first(const char *s) { return (unsigned char)s[0]; }
/* A float argument is a 32-bit IEEE word on the stack; returning its raw bits needs no FPU. */
int float_bits(float f) { union { float f; unsigned u; } v; v.f = f; return (int)v.u; }
int g_counter = 100;
void bump(int d) { g_counter += d; }
int counter(void) { return g_counter; }
