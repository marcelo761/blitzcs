#include "common.h"
#include "fs_teb.h"
/* SEH oracle. The library calls RaiseException / RtlUnwind through its IAT slots (0x10003400, 0x10003404).
 *   PE32 oracle (-DORACLE_WIN32, Windows or Wine): the slots hold the REAL kernel32 functions (make_pe.py imports them at IAT slots 3 and 4).
 *   ELF oracle (Linux): the slots hold the reference model below, written from the documented x86 algorithm (RtlDispatchException / RtlUnwind:
 *   a dispatcher record {Next, Handler, running record} around every handler call, ExceptionNestedException and EXCEPTION_NESTED_CALL, in-place
 *   EH_UNWINDING, unlinking during unwind). Any case outside the model ends the oracle with an E line, never a silent result. */
extern char s_catch[], s_nested[], s_resume[], s_unwind[];
static const Fn T[] = {
  {"s_catch", 'C', 2, {0}, s_catch},
  {"s_nested", 'C', 2, {0}, s_nested},
  {"s_resume", 'C', 2, {0}, s_resume},
  {"s_unwind", 'C', 2, {0}, s_unwind},
};

#ifdef ORACLE_WIN32
__attribute__((force_align_arg_pointer, noreturn)) void _start(void) {
  teb_install();
  iat[0] = W_IAT(3); iat[1] = W_IAT(4);
  run_all(T, (int)(sizeof T / sizeof T[0]), 40);
  w_finish_restore(); finish();
}
#else
typedef struct Reg { struct Reg *prev; void *handler; } Reg;
typedef struct ExRec { u32 code, flags; struct ExRec *nested; u32 addr, n; u32 info[15]; } ExRec;
typedef struct NestFrame { Reg reg; Reg *running; } NestFrame;
#define END ((Reg *)0xFFFFFFFFu)
static inline Reg *fs0(void) { Reg *r; __asm__ volatile("movl %%fs:0,%0" : "=r"(r) : : "memory"); return r; }
static inline void set_fs0(Reg *r) { __asm__ volatile("movl %0,%%fs:0" : : "r"(r) : "memory"); }
static __attribute__((noreturn)) void die(const char *why) { puts_("E model: "); puts_(why); nl(); flush(); __asm__ volatile("int $0x80" : : "a"(1), "b"(3)); for (;;) {} }

static u32 nested_handler(ExRec *rec, Reg *frame, void *ctx, Reg **dc) {
  (void)ctx;
  if (rec->flags & 6u) return 1;
  *dc = ((NestFrame *)frame)->running;
  return 2;
}
static u32 unwind_handler(ExRec *rec, Reg *frame, void *ctx, Reg **dc) {
  (void)frame; (void)ctx; (void)dc;
  if (rec->flags & 6u) die("collided unwind");
  return 1;
}
/* handler(rec, frame, ctx, dc) for a cdecl or stdcall handler: ESP is restored from ESI (callee-saved) after the call */
static u32 invoke(void *h, u32 *a) {
  u32 r;
  __asm__ volatile("movl %%esp,%%esi\n\tpushl 12(%2)\n\tpushl 8(%2)\n\tpushl 4(%2)\n\tpushl (%2)\n\tcall *%1\n\tmovl %%esi,%%esp"
                   : "=a"(r) : "r"(h), "r"(a) : "esi", "ecx", "edx", "memory");
  return r;
}
static u32 call_handler(ExRec *rec, Reg *frame, void *ctx, Reg **dc, void *nested) {
  NestFrame nf; nf.reg.handler = nested; nf.running = frame; nf.reg.prev = fs0(); set_fs0(&nf.reg);
  u32 a[4] = {(u32)rec, (u32)frame, (u32)ctx, (u32)dc};
  u32 d = invoke(frame->handler, a);
  set_fs0(nf.reg.prev);
  return d;
}
static u32 model_ctx[0x2CC / 4];

static void S model_raise(u32 code, u32 flags, u32 n, const u32 *args) {
  ExRec rec; rec.code = code; rec.flags = flags & 1u; rec.nested = 0; rec.addr = 0; rec.n = 0;
  if (n && args) { if (n > 15) n = 15; rec.n = n; for (u32 i = 0; i < n; i++) rec.info[i] = args[i]; }
  Reg *nested = 0;
  for (Reg *r = fs0(); r != END; r = r->prev) {
    Reg *dc = 0;
    u32 d = call_handler(&rec, r, model_ctx, &dc, (void *)nested_handler);
    if (r == nested) { rec.flags &= ~0x10u; nested = 0; }
    if (d == 0) { if (rec.flags & 1u) die("continue on a non-continuable exception"); return; }
    if (d == 2) { rec.flags |= 0x10u; if (dc > nested) nested = dc; }
    else if (d != 1) die("invalid disposition");
  }
  die("unhandled exception");
}

static void S model_unwind(Reg *target, void *ip, ExRec *rec, u32 retval) {
  (void)ip; (void)retval;
  ExRec local;
  if (!rec) { local.code = 0xC0000027u; local.flags = 0; local.nested = 0; local.addr = 0; local.n = 0; rec = &local; }
  if (!target) die("exit unwind");
  rec->flags |= 2u;
  Reg *r = fs0();
  while (r != target) {
    if (r == END) die("unwind target not on the chain");
    if (target != END && (u32)target < (u32)r) die("invalid unwind target");
    Reg *dc = 0;
    if (call_handler(rec, r, model_ctx, &dc, (void *)unwind_handler) != 1) die("unwind disposition");
    Reg *next = r->prev; set_fs0(next); r = next;
  }
}

__attribute__((force_align_arg_pointer, noreturn)) void _start(void) {
  teb_install();
  iat[0] = (u32)model_raise; iat[1] = (u32)model_unwind;
  run_all(T, (int)(sizeof T / sizeof T[0]), 40);
  finish();
}
#endif
