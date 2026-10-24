/* Controlled test library: known source, deterministic results. */
int g_counter = 5;
unsigned char g_buf[64];
int add32(int a, int b) { return a + b; }
unsigned mulhi(unsigned a, unsigned b) { return (unsigned)(((unsigned long long)a * b) >> 32); }
int sdiv(int a, int b) { return b ? a / b : 0; }
unsigned fnv(const unsigned char *p, unsigned n) { unsigned h = 2166136261u; while (n--) h = (h ^ *p++) * 16777619u; return h; }
int fib(int n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
int bump(int d) { g_counter += d; return g_counter; }
int fillbuf(unsigned seed) { for (int i = 0; i < 64; i++) { seed = seed * 1103515245u + 12345u; g_buf[i] = (unsigned char)(seed >> 16); } return fnv(g_buf, 64) & 0x7fffffff; }
int sw(int x) { switch (x) { case 0: return 10; case 1: return 21; case 2: return 33; case 3: return 47; case 4: return 52; case 5: return 68; default: return -1; } }
int ovf(int a, int b) { int r; return __builtin_add_overflow(a, b, &r); }
