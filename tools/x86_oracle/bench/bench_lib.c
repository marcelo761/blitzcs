/* CPU-bound workloads for the M21 binary-compiler benchmark. Compiled ONCE (-m32 -O2); the same machine code runs
 * natively as the x86 oracle (ELF32 driver) and translated through the PE32 -> x64 pipeline. Every workload is
 * deterministic and returns a checksum that must match between both executions before any timing is reported. */
#define NOINLINE __attribute__((noinline))
#define BUFSZ 65536
#define MAT 40

unsigned char g_a[BUFSZ] __attribute__((aligned(64)));
unsigned char g_b[BUFSZ] __attribute__((aligned(64)));
unsigned g_crc[256];
int g_ma[MAT * MAT], g_mb[MAT * MAT], g_mc[MAT * MAT];
float g_fa[1024], g_fb[1024];
unsigned g_sink;

NOINLINE unsigned w_init(unsigned seed)
{
    unsigned s = seed;
    for (unsigned i = 0; i < BUFSZ; i++) { s = s * 1664525u + 1013904223u; g_a[i] = (unsigned char)((s >> 24) & 0x3f); /* compressible-ish */ }
    for (unsigned i = 0; i < 256; i++) { unsigned c = i; for (int k = 0; k < 8; k++) c = (c & 1) ? (c >> 1) ^ 0xEDB88320u : c >> 1; g_crc[i] = c; }
    for (int i = 0; i < MAT * MAT; i++) { s = s * 1664525u + 1013904223u; g_ma[i] = (int)(s >> 20) & 255; s = s * 1664525u + 1013904223u; g_mb[i] = (int)(s >> 20) & 255; }
    for (int i = 0; i < 1024; i++) { g_fa[i] = (float)(i % 17) * 0.25f + 1.0f; g_fb[i] = (float)(i % 13) * 0.5f - 2.0f; }
    return s;
}

NOINLINE int nop_fn(void) { return 0; }
NOINLINE int add3(int a, int b, int c) { return a + b + c; }

/* 1. integer loop: xorshift + multiply/rotate mixing */
NOINLINE unsigned w_int(unsigned n)
{
    unsigned x = 2463534242u, acc = 0;
    for (unsigned i = 0; i < n; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; acc += (x * 2654435761u) ^ (acc >> 3); acc = (acc << 7) | (acc >> 25); }
    return acc;
}

/* 2a. FNV-1a over 64 KB, n times */
NOINLINE unsigned w_fnv(unsigned n)
{
    unsigned h = 2166136261u;
    for (unsigned r = 0; r < n; r++) for (unsigned i = 0; i < BUFSZ; i++) h = (h ^ g_a[i]) * 16777619u;
    return h;
}
/* 2b. table CRC32 */
NOINLINE unsigned w_crc(unsigned n)
{
    unsigned c = 0xFFFFFFFFu;
    for (unsigned r = 0; r < n; r++) for (unsigned i = 0; i < BUFSZ; i++) c = g_crc[(c ^ g_a[i]) & 255] ^ (c >> 8);
    return ~c;
}

/* 3. memory bound: rep movsd copy + repne scasb scan (strength-reduced by hand, the compiler never emits rep here) */
NOINLINE unsigned w_mem(unsigned n)
{
    unsigned acc = 0;
    for (unsigned r = 0; r < n; r++)
    {
        void *d = g_b, *s = g_a; unsigned cnt = BUFSZ / 4;
        __asm__ volatile("cld; rep movsl" : "+D"(d), "+S"(s), "+c"(cnt) : : "memory");
        g_b[(r * 977u) & (BUFSZ - 1)] = (unsigned char)(r | 0x40);   /* mutate so each pass differs */
        unsigned char *p = g_b; unsigned left = BUFSZ; unsigned char key = 0x7f; /* never present except via mutation: scans whole buffer */
        __asm__ volatile("cld; repne scasb" : "+D"(p), "+c"(left) : "a"(key) : "memory", "cc");
        acc = acc * 31u + (BUFSZ - left) + g_b[r & 1023];
        g_a[(r * 131u) & (BUFSZ - 1)] ^= (unsigned char)acc & 0x3f;
    }
    return acc;
}

/* 4. recursion */
NOINLINE int fib(int n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
NOINLINE unsigned w_fib(unsigned n) { return (unsigned)fib((int)n); }

/* 5. call-heavy: many tiny non-inlined leaves and a 3-deep chain */
NOINLINE unsigned leaf1(unsigned x) { return x * 3u + 1u; }
NOINLINE unsigned leaf2(unsigned x) { return (x ^ (x >> 5)) + 7u; }
NOINLINE unsigned mid(unsigned x) { return leaf1(x) + leaf2(x + 1u); }
NOINLINE unsigned w_calls(unsigned n)
{
    unsigned acc = 1;
    for (unsigned i = 0; i < n; i++) acc = mid(acc ^ i) + leaf1(i);
    return acc;
}

/* 6. switch / jump-table dispatch (bytecode-interpreter style) */
NOINLINE unsigned w_switch(unsigned n)
{
    unsigned acc = 1, pc = 0;
    for (unsigned i = 0; i < n; i++)
    {
        unsigned op = (g_a[pc & (BUFSZ - 1)] + (acc & 7)) & 15; pc++;
        switch (op)
        {
        case 0: acc += 3; break;       case 1: acc ^= 0x9e3779b9u; break;
        case 2: acc = (acc << 1) | (acc >> 31); break;  case 3: acc -= pc; break;
        case 4: acc *= 5; break;       case 5: acc += acc >> 3; break;
        case 6: acc ^= pc << 4; break; case 7: acc = ~acc; break;
        case 8: acc += 11; break;      case 9: acc ^= acc << 7; break;
        case 10: acc -= 17; break;     case 11: acc = (acc >> 1) | (acc << 31); break;
        case 12: acc += pc * 3; break; case 13: acc ^= 0x55; break;
        case 14: acc *= 3; break;      default: acc += 1; break;
        }
    }
    return acc;
}

/* 7. indirect calls through a function-pointer table */
static unsigned f0(unsigned x) { return x + 1u; }
static unsigned f1(unsigned x) { return x * 3u; }
static unsigned f2(unsigned x) { return x ^ 0x5bd1e995u; }
static unsigned f3(unsigned x) { return (x << 3) | (x >> 29); }
static unsigned f4(unsigned x) { return x - 77u; }
static unsigned f5(unsigned x) { return x ^ (x >> 11); }
static unsigned f6(unsigned x) { return x * 33u + 5u; }
static unsigned f7(unsigned x) { return ~x; }
NOINLINE unsigned w_ind(unsigned n)
{
    static unsigned (*const tab[8])(unsigned) = { f0, f1, f2, f3, f4, f5, f6, f7 };
    unsigned acc = 1;
    for (unsigned i = 0; i < n; i++) acc = tab[(acc + i) & 7](acc);
    return acc;
}

/* 8a. LZ-style greedy match finder + mixing over 64 KB */
NOINLINE unsigned w_lz(unsigned n)
{
    static unsigned short head[4096];
    unsigned out = 0, h = 0;
    for (unsigned r = 0; r < n; r++)
    {
        for (int i = 0; i < 4096; i++) head[i] = 0;
        unsigned i = 0;
        while (i + 4 < BUFSZ)
        {
            unsigned key = (g_a[i] | (g_a[i + 1] << 6) | (g_a[i + 2] << 12) | (g_a[i + 3] << 18)) * 2654435761u >> 20;
            unsigned cand = head[key]; head[key] = (unsigned short)i;
            unsigned len = 0;
            if (cand && cand < i) { while (len < 255 && i + len < BUFSZ && g_a[cand + len] == g_a[i + len]) len++; }
            if (len >= 4) { out += len * 3u + (i - cand); h = (h ^ len) * 16777619u; i += len; }
            else { out += 1; h = (h ^ g_a[i]) * 16777619u; i++; }
        }
    }
    return out ^ h;
}
/* 8b. integer matrix multiply */
NOINLINE unsigned w_matmul(unsigned n)
{
    unsigned sum = 0;
    for (unsigned r = 0; r < n; r++)
    {
        for (int i = 0; i < MAT; i++) for (int j = 0; j < MAT; j++)
        { int s = 0; for (int k = 0; k < MAT; k++) s += g_ma[i * MAT + k] * g_mb[k * MAT + j]; g_mc[i * MAT + j] = s; }
        g_ma[r % (MAT * MAT)] += g_mc[(r * 7) % (MAT * MAT)] & 15;
        sum += (unsigned)g_mc[(r * 13) % (MAT * MAT)];
    }
    return sum;
}

/* 9. x87 (default float codegen for -m32 without SSE) */
NOINLINE unsigned w_x87(unsigned n)
{
    float acc = 0.0f;
    for (unsigned r = 0; r < n; r++) for (int i = 0; i < 1024; i++) acc = acc * 0.999f + g_fa[i] * g_fb[i];
    union { float f; unsigned u; } v; v.f = acc; return v.u;
}
/* 10. SSE2 scalar+packed */
typedef float v4 __attribute__((vector_size(16)));
__attribute__((target("sse2"), noinline)) unsigned w_sse(unsigned n)
{
    v4 acc = { 0, 0, 0, 0 }; const v4 k = { 0.999f, 0.999f, 0.999f, 0.999f };
    for (unsigned r = 0; r < n; r++) for (int i = 0; i < 1024; i += 4) { v4 a = *(const v4 *)&g_fa[i], b = *(const v4 *)&g_fb[i]; acc = acc * k + a * b; }
    union { v4 v; unsigned u[4]; } t; t.v = acc; return t.u[0] ^ t.u[1] ^ t.u[2] ^ t.u[3];
}

/* ABI probes. cb is a guest-callable function pointer (an import stub in the PE, a native function in the ELF). */
NOINLINE unsigned w_cbloop(unsigned n, unsigned (*cb)(unsigned))
{
    unsigned acc = 0;
    for (unsigned i = 0; i < n; i++) acc += cb(i);
    return acc;
}
