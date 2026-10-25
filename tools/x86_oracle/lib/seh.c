/* Controlled SEH library: software exceptions raised with KERNEL32!RaiseException and handled through a hand-built FS:[0] registration chain the way the MSVC
 * runtime does it (docs/binary-seh.md): search-phase handlers, a catching handler that calls RtlUnwind (EH_UNWINDING calls of the inner handlers), relinks the
 * dispatcher's record (_UnwindNestedFrames), unlinks it again and transfers control to the continuation with esp/ebp/jmp (_JumpToContinuation), a raise inside a
 * handler (nested exception), cdecl and stdcall handlers, ExceptionContinueExecution, and a direct RtlUnwind without a record (STATUS_UNWIND).
 * Imports (IAT at 0x10003400): slot 0 RaiseException, slot 1 RtlUnwind. Only address-independent facts are logged (g_log): record identities are logged as tags.
 * s_unhandled and s_noncontinuable are exported for the runtime's named-fault tests only (the drivers do not run them). */
typedef unsigned u32;
typedef struct Reg { struct Reg *prev; void *handler; } Reg;
typedef struct ExRec { u32 code, flags; struct ExRec *nested; u32 addr, n; u32 info[15]; } ExRec;
typedef struct Frame { Reg reg; u32 magic, tag, want; void *jb[5]; } Frame;
#define S __attribute__((stdcall))
#define RAISE (*(void (S * volatile *)(u32, u32, u32, const u32 *))0x10003400)
#define UNWIND (*(void (S * volatile *)(void *, void *, ExRec *, u32))0x10003404)
#define END ((Reg *)0xFFFFFFFFu)
#define MAGIC 0x5EC0F2A3u
#define CODE_A 0xE0434F44u
#define CODE_B 0xE0425F52u

u32 g_log[64]; u32 g_n;
static void L(u32 v) { if (g_n < 64) g_log[g_n++] = v; }
static u32 hash_log(void) { u32 h = 2166136261u; for (u32 i = 0; i < g_n; i++) h = (h ^ g_log[i]) * 16777619u; return h ^ g_n; }
static inline Reg *fs0(void) { Reg *r; __asm__ volatile("movl %%fs:0,%0" : "=r"(r) : : "memory"); return r; }
static inline void set_fs0(Reg *r) { __asm__ volatile("movl %0,%%fs:0" : : "r"(r) : "memory"); }
static u32 tag_of(Reg *r) { return r == END ? 0xEu : ((Frame *)r)->magic == MAGIC ? ((Frame *)r)->tag : 0xFu; }
static void push(Frame *f, void *h, u32 tag, u32 want) { f->reg.handler = h; f->magic = MAGIC; f->tag = tag; f->want = want; f->reg.prev = fs0(); set_fs0(&f->reg); }
static void pop(Frame *f) { set_fs0(f->reg.prev); }
static void note(Frame *f, ExRec *rec) { L((f->tag << 24) | ((rec->flags & 0xFFu) << 16) | (rec->code & 0xFFFFu)); }

/* a frame with only cleanup work (a destructor / __finally): logs every call, never handles */
static u32 h_log(ExRec *rec, Reg *frame, void *ctx, void *dc) { (void)ctx; (void)dc; note((Frame *)frame, rec); return 1; }
static u32 S h_log_std(ExRec *rec, Reg *frame, void *ctx, void *dc) { (void)ctx; (void)dc; note((Frame *)frame, rec); L(0x5D000000u); return 1; }

/* the MSVC catch sequence */
static u32 h_catch(ExRec *rec, Reg *frame, void *ctx, void *dc) {
  (void)ctx; (void)dc;
  Frame *f = (Frame *)frame;
  note(f, rec);
  if ((rec->flags & 6u) || rec->code != f->want) return 1;
  Reg *disp = fs0();                                            /* the dispatcher's own record, installed around this call */
  L(0xD0000000u | (tag_of(disp) << 8) | tag_of(disp->prev) | ((rec->nested == 0) << 4));
  UNWIND(frame, 0, rec, 0x77);
  L(0xE0000000u | (tag_of(fs0()) << 8) | (rec->flags & 0xFFu)); /* target frame on top, record marked unwinding */
  rec->flags &= ~2u;
  disp->prev = fs0(); set_fs0(disp);                            /* _UnwindNestedFrames */
  L(rec->n); for (u32 i = 0; i < rec->n && i < 15; i++) L(rec->info[i]);
  set_fs0(fs0()->prev);                                         /* _JumpToContinuation: unlink the dispatcher's record ... */
  __builtin_longjmp(f->jb, 1);                                  /* ... and continue with the frame's esp/ebp */
}

/* raises CODE_B from inside its handler while CODE_A is being dispatched */
static u32 h_raiser(ExRec *rec, Reg *frame, void *ctx, void *dc) {
  (void)ctx; (void)dc;
  note((Frame *)frame, rec);
  if (!(rec->flags & 6u) && rec->code == CODE_A) { u32 a[2] = {rec->info[0] + 1, 0xB0}; RAISE(CODE_B, 0, 2, a); L(0xBAD1); }
  return 1;
}

/* resumes a continuable exception */
static u32 h_resume(ExRec *rec, Reg *frame, void *ctx, void *dc) {
  (void)ctx; (void)dc;
  Frame *f = (Frame *)frame;
  note(f, rec);
  return (!(rec->flags & 6u) && rec->code == f->want) ? 0u : 1u;
}

static __attribute__((noinline)) void inner(u32 a, u32 b, u32 d) {
  Frame f; push(&f, d == 1 ? (void *)h_log_std : (void *)h_log, 2 + d, 0);
  if (d) inner(a, b, d - 1);
  else {
    u32 args[20];
    for (u32 i = 0; i < 20; i++) args[i] = a * (i + 1) ^ b;
    u32 n = (b & 8u) ? 20u : (b & 3u);
    RAISE(CODE_A, (b >> 4) & 0xFFu, n, (b & 4u) ? 0 : args);    /* count > 15 is clamped, a NULL array gives no parameters */
  }
  L(0xBAD2); pop(&f);
}

u32 s_catch(u32 a, u32 b) {
  g_n = 0;
  Frame top; push(&top, h_catch, 1, CODE_A);
  if (__builtin_setjmp(top.jb)) { L(0xC0000000u | tag_of(fs0())); pop(&top); return hash_log() ^ tag_of(fs0()); }
  inner(a, b, a & 3u);
  L(0xBAD3); pop(&top);
  return 0;
}

static __attribute__((noinline)) void nest_raise(u32 a) {
  Frame f; push(&f, h_raiser, 2, 0);
  u32 args[1] = {a};
  RAISE(CODE_A, 0, 1, args);
  L(0xBAD4); pop(&f);
}

u32 s_nested(u32 a, u32 b) {
  g_n = 0;
  Frame top; push(&top, h_catch, 1, CODE_B);
  if (__builtin_setjmp(top.jb)) { L(0xC0000000u | tag_of(fs0())); pop(&top); return hash_log() + b; }
  nest_raise(a);
  L(0xBAD5); pop(&top);
  return 0;
}

u32 s_resume(u32 a, u32 b) {
  g_n = 0;
  Frame f; push(&f, h_resume, 9, CODE_B);
  volatile u32 seen = 0;
  u32 x = a * 3u, y = b ^ 0x5Au, z = a + b;                      /* live across the raises in callee-saved registers */
  for (u32 k = 0; k < (a & 3u) + 1; k++) { u32 args[1] = {a + k}; RAISE(CODE_B, 0, 1, args); seen++; }
  L(x + y * 7u + z * 13u); L(seen); L(tag_of(fs0()));
  pop(&f);
  return hash_log();
}

static __attribute__((noinline)) u32 unwind_inner(Frame *target, u32 d, u32 v) {
  Frame f; push(&f, h_log, 4 + d, 0);
  u32 keep = v * 5u + d;
  if (d) return unwind_inner(target, d - 1, v ^ d) + keep;
  UNWIND(&target->reg, 0, 0, v);                                 /* longjmp-style global unwind: no record, STATUS_UNWIND */
  L(tag_of(fs0()));
  return keep;                                                   /* our records were unlinked by the unwind: nothing to pop */
}

u32 s_unwind(u32 a, u32 b) {
  g_n = 0;
  Frame top; push(&top, h_log, 1, 0);
  u32 r = unwind_inner(&top, a & 3u, b);
  L(tag_of(fs0())); pop(&top);
  return hash_log() ^ r;
}

/* not run by the drivers: an exception nobody handles, and ContinueExecution for a non-continuable one */
u32 s_unhandled(u32 code) { u32 args[1] = {code}; RAISE(code, 0, 1, args); return 1; }
u32 s_noncontinuable(u32 code) {
  Frame f; push(&f, h_resume, 9, code);
  RAISE(code, 1, 0, 0);
  pop(&f);
  return 2;
}
