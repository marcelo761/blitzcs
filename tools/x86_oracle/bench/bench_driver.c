/* ELF32 oracle driver: runs the bench_lib machine code natively. Self-times with rdtsc + clock_gettime and counts
 * its own user-space PMU events with perf_event_open around each run (no libc; raw int 0x80 syscalls).
 * usage: oracle <workload> <n> <reps>   ->  one line per rep:  "<result> <tsc> <ns> <cycles> <instr> <branchmiss> <cachemiss>" */
unsigned w_init(unsigned); unsigned w_int(unsigned), w_fnv(unsigned), w_crc(unsigned), w_mem(unsigned), w_fib(unsigned), w_calls(unsigned),
 w_switch(unsigned), w_ind(unsigned), w_lz(unsigned), w_matmul(unsigned), w_x87(unsigned), w_sse(unsigned);
unsigned w_cbloop(unsigned, unsigned (*)(unsigned)); int nop_fn(void);
static unsigned cbn(unsigned x) { return x + 1; }
static unsigned cbloop(unsigned n) { return w_cbloop(n, cbn); }
static unsigned nopn(unsigned n) { unsigned a = 0; for (unsigned i = 0; i < n; i++) a += (unsigned)nop_fn(); return a; }

static int sys(int n, int a, int b, int c, int d, int e) { int r; __asm__ volatile("int $0x80" : "=a"(r) : "0"(n), "b"(a), "c"(b), "d"(c), "S"(d), "D"(e) : "memory"); return r; }
static void wr(const char *s, int n) { sys(4, 1, (int)s, n, 0, 0); }
static int slen(const char *s) { int n = 0; while (s[n]) n++; return n; }
static int seq(const char *a, const char *b) { while (*a && *a == *b) { a++; b++; } return *a == *b; }
static unsigned long long rdtsc(void) { unsigned lo, hi; __asm__ volatile("lfence; rdtsc" : "=a"(lo), "=d"(hi)); return ((unsigned long long)hi << 32) | lo; }
static unsigned parse(const char *s) { unsigned v = 0; while (*s >= '0' && *s <= '9') v = v * 10 + (unsigned)(*s++ - '0'); return v; }
static void put(unsigned long long v) { char b[24]; int i = 24; b[--i] = ' '; if (!v) b[--i] = '0'; while (v) { b[--i] = (char)('0' + v % 10); v /= 10; } wr(b + i, 24 - i); }

struct pea { unsigned type, size; unsigned long long config, sample, stype, fmt; unsigned long long flags; unsigned wake; unsigned bptype; unsigned long long a1, a2; unsigned long long pad[8]; };
static int popen_(unsigned cfg) {
    struct pea a; char *p = (char *)&a; for (unsigned i = 0; i < sizeof a; i++) p[i] = 0;
    a.type = 0; a.size = 112; a.config = cfg; a.flags = 1ull | (1ull << 5) | (1ull << 6); /* disabled | exclude_kernel | exclude_hv */
    return sys(336, (int)&a, 0, -1, -1, 0);
}
int _start_c(int *sp)
{
    int argc = sp[0]; char **argv = (char **)(sp + 1);
    if (argc < 4) { wr("usage\n", 6); return 2; }
    const char *w = argv[1]; unsigned n = parse(argv[2]), reps = parse(argv[3]);
    unsigned (*fn)(unsigned) = 0;
    if (seq(w, "int")) fn = w_int; else if (seq(w, "fnv")) fn = w_fnv; else if (seq(w, "crc")) fn = w_crc; else if (seq(w, "mem")) fn = w_mem;
    else if (seq(w, "fib")) fn = w_fib; else if (seq(w, "calls")) fn = w_calls; else if (seq(w, "switch")) fn = w_switch; else if (seq(w, "ind")) fn = w_ind;
    else if (seq(w, "lz")) fn = w_lz; else if (seq(w, "matmul")) fn = w_matmul; else if (seq(w, "x87")) fn = w_x87; else if (seq(w, "sse")) fn = w_sse;
    else if (seq(w, "cbloop")) fn = cbloop; else if (seq(w, "nop")) fn = nopn;
    if (!fn) { wr("unknown\n", 8); return 2; }
    w_init(12345u);
    int fd[4]; unsigned cfgs[4] = { 0, 1, 5, 3 }; /* cycles, instructions, branch-misses, cache-misses */
    for (int i = 0; i < 4; i++) fd[i] = popen_(cfgs[i]);
    fn(n / 8 + 1); /* warmup */
    for (unsigned r = 0; r < reps; r++)
    {
        w_init(12345u);
        for (int i = 0; i < 4; i++) if (fd[i] >= 0) { sys(54, fd[i], 0x2403, 0, 0, 0); /* RESET */ sys(54, fd[i], 0x2400, 0, 0, 0); /* ENABLE */ }
        unsigned long long t0 = rdtsc(); struct { int s, ns; } a, b;
        sys(265, 1, (int)&a, 0, 0, 0);
        unsigned res = fn(n);
        sys(265, 1, (int)&b, 0, 0, 0);
        unsigned long long t1 = rdtsc();
        for (int i = 0; i < 4; i++) if (fd[i] >= 0) sys(54, fd[i], 0x2401, 0, 0, 0);
        put(res); put(t1 - t0); put((unsigned long long)(b.s - a.s) * 1000000000ull + (unsigned long long)(b.ns - a.ns));
        for (int i = 0; i < 4; i++) { unsigned long long v = 0; if (fd[i] < 0 || sys(3, fd[i], (int)&v, 8, 0, 0) != 8) v = (unsigned long long)-1; put(v); }
        wr("\n", 1);
    }
    (void)slen;
    return 0;
}
__asm__(".globl _start\n_start:\n mov %esp, %eax\n and $-16, %esp\n sub $12, %esp\n push %eax\n call start_main\n");
__attribute__((used)) void start_main(int *sp) { int rc = _start_c(sp); sys(1, rc, 0, 0, 0, 0); }
