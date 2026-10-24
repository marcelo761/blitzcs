/* Shared oracle driver: runs a table of library functions over deterministic vectors and prints a text transcript.
 *   H data <len hex> / F name conv nargs / I <data hash> / [T trace]* V idx name a0.. ret hash / D <hex dump> / E done
 * All numbers are lower-case hex. Vectors: edge x edge on the first two args, then fixed-seed LCG randoms. */
typedef unsigned u32;
extern char __data_start[], __data_end[];
u32 iat[4] __attribute__((section(".iat")));

typedef struct { const char *name; char conv; int n; u32 lim[6]; void *p; } Fn;

static char obuf[1 << 16]; static u32 olen;
#ifdef ORACLE_WIN32
/* Windows backend (PE32 oracle, see make_pe.py): kernel32 imports through the IAT at 0x10008000 (GetStdHandle, WriteFile, ExitProcess).
 * io_enter/io_leave let fs-based drivers swap the real TEB state back in around OS calls (see fs_teb_win.h). */
#define W_IAT(i) ((volatile u32 *)0x10008000)[i]
#define W_STD __attribute__((stdcall))
static void (*io_enter)(void), (*io_leave)(void);
static inline void flush(void) {
  u32 o = 0; if (io_enter) io_enter();
  u32 h = ((u32 (W_STD *)(u32))W_IAT(0))(0xFFFFFFF5u); /* STD_OUTPUT_HANDLE */
  while (o < olen) { u32 w = 0; if (!((u32 (W_STD *)(u32, const void *, u32, u32 *, u32))W_IAT(1))(h, obuf + o, olen - o, &w, 0) || !w) break; o += w; }
  if (io_leave) io_leave();
  olen = 0;
}
#else
static inline void flush(void) { u32 o = 0; while (o < olen) { int r; __asm__ volatile("int $0x80" : "=a"(r) : "0"(4), "b"(1), "c"(obuf + o), "d"(olen - o) : "memory"); if (r <= 0) break; o += (u32)r; } olen = 0; }
#endif
static inline void put(char c) { if (olen == sizeof obuf) flush(); obuf[olen++] = c; }
static inline void puts_(const char *s) { while (*s) put(*s++); }
static inline void hex(u32 v) { char t[8]; int n = 0; do { t[n++] = "0123456789abcdef"[v & 15]; v >>= 4; } while (v); while (n) put(t[--n]); }
static inline void sp(void) { put(' '); }
static inline void nl(void) { put('\n'); }
static inline u32 dhash(void) { u32 h = 2166136261u; for (const unsigned char *p = (const unsigned char *)__data_start; p < (const unsigned char *)__data_end; p++) h = (h ^ *p) * 16777619u; return h; }
static inline void trace(const char *what, u32 a, u32 b, u32 c, u32 r) { puts_("T "); puts_(what); sp(); hex(a); sp(); hex(b); sp(); hex(c); sp(); hex(r); nl(); }

#define S __attribute__((stdcall))
static u32 callfn(const Fn *f, const u32 *a) {
  void *p = f->p;
  if (f->conv == 'S') switch (f->n) {
    case 0: return ((u32 (S *)(void))p)();
    case 1: return ((u32 (S *)(u32))p)(a[0]);
    case 2: return ((u32 (S *)(u32, u32))p)(a[0], a[1]);
    case 3: return ((u32 (S *)(u32, u32, u32))p)(a[0], a[1], a[2]);
    case 4: return ((u32 (S *)(u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3]);
    case 5: return ((u32 (S *)(u32, u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3], a[4]);
    default: return ((u32 (S *)(u32, u32, u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3], a[4], a[5]);
  }
  switch (f->n) {
    case 0: return ((u32 (*)(void))p)();
    case 1: return ((u32 (*)(u32))p)(a[0]);
    case 2: return ((u32 (*)(u32, u32))p)(a[0], a[1]);
    case 3: return ((u32 (*)(u32, u32, u32))p)(a[0], a[1], a[2]);
    case 4: return ((u32 (*)(u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3]);
    case 5: return ((u32 (*)(u32, u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3], a[4]);
    default: return ((u32 (*)(u32, u32, u32, u32, u32, u32))p)(a[0], a[1], a[2], a[3], a[4], a[5]);
  }
}

static const u32 EDGE[] = {0, 1, 2, 3, 7, 8, 31, 32, 33, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0x8000, 0xffff, 0x10000, 0x7fffffffu, 0x80000000u, 0xffffffffu, 0xfffffffeu, 0x55555555u, 0xaaaaaaaau};
#define NEDGE ((int)(sizeof EDGE / sizeof EDGE[0]))
static u32 lcg_state = 0x1badb002u;
static inline u32 lcg(void) { lcg_state = lcg_state * 1664525u + 1013904223u; u32 x = lcg_state; x ^= x >> 15; return x * 2654435761u ^ (lcg_state >> 7); }

static int vec_idx;
static void vec(const Fn *f, u32 *a) {
  for (int i = 0; i < f->n; i++) if (f->lim[i]) a[i] %= f->lim[i];
  u32 r = callfn(f, a);
  puts_("V "); hex((u32)vec_idx++); sp(); puts_(f->name);
  for (int i = 0; i < f->n; i++) { sp(); hex(a[i]); }
  sp(); hex(r); sp(); hex(dhash()); nl();
}

/* Per-process setup shared by every driver loop (run_all and the custom loops of regconv/ret_abi). */
#ifndef ORACLE_FPCW
#define ORACLE_FPCW 0x037F /* x87 control word the oracle runs under (64-bit precision = Linux default = the contract); -DORACLE_FPCW=0x027F selects Windows' 53-bit default */
#endif
static inline void oracle_init(void) {
  /* Windows starts 32-bit threads with 0x27F (53-bit precision), Linux with 0x37F; pin one so both oracles execute identically. */
  static const unsigned short cw = ORACLE_FPCW; __asm__ volatile("fldcw %0" : : "m"(cw));
}

static void run_all(const Fn *t, int nf, int nrand) {
  oracle_init();
  puts_("H data "); hex((u32)(__data_end - __data_start)); nl();
  for (int i = 0; i < nf; i++) { puts_("F "); puts_(t[i].name); sp(); put(t[i].conv); sp(); hex((u32)t[i].n); nl(); }
  puts_("I "); hex(dhash()); nl();
  for (int i = 0; i < nf; i++) {
    u32 a[6] = {0, 0, 0, 0, 0, 0};
    if (t[i].n == 0) { vec(&t[i], a); continue; }
    if (t[i].n == 1) { for (int x = 0; x < NEDGE; x++) { a[0] = EDGE[x]; vec(&t[i], a); } }
    else for (int x = 0; x < NEDGE; x++) for (int y = 0; y < NEDGE; y++) {
      for (int k = 2; k < 6; k++) a[k] = lcg();
      a[0] = EDGE[x]; a[1] = EDGE[y]; vec(&t[i], a);
    }
    for (int r = 0; r < nrand; r++) { for (int k = 0; k < 6; k++) a[k] = lcg(); vec(&t[i], a); }
  }
  puts_("D ");
  for (const unsigned char *p = (const unsigned char *)__data_start; p < (const unsigned char *)__data_end; p++) { put("0123456789abcdef"[*p >> 4]); put("0123456789abcdef"[*p & 15]); }
  nl(); puts_("E done\n"); flush();
}

#ifdef ORACLE_WIN32
static inline __attribute__((noreturn)) void finish(void) { if (io_enter) io_enter(); ((void (W_STD *)(u32))W_IAT(2))(0); for (;;) {} }
#else
static inline __attribute__((noreturn)) void finish(void) { __asm__ volatile("int $0x80" : : "a"(1), "b"(0)); for (;;) {} }
#endif
#define ORACLE_MAIN(tbl, nrand) __attribute__((force_align_arg_pointer, noreturn)) void _start(void) { run_all(tbl, (int)(sizeof tbl / sizeof tbl[0]), nrand); finish(); }
