/* Controlled FS library 1: SEH-style exception-registration chain on fs:[0], stack bounds, TEB self pointer.
 * Only address-independent facts are returned. Layout contract: docs/binary-teb-contract.md. */
typedef unsigned u32;
typedef struct Reg { struct Reg *prev; u32 handler; } Reg;
#define FS0(r) __asm__ volatile("movl %%fs:0,%0" : "=r"(r))
static __attribute__((noinline)) u32 nest(u32 d, u32 tag) {
  Reg r; Reg *head; FS0(head);
  r.prev = head; r.handler = tag + d;
  __asm__ volatile("movl %0,%%fs:0" : : "r"(&r) : "memory");   /* push registration: prev link = old fs:[0] */
  u32 res;
  if (d) res = nest(d - 1, tag);
  else {
    u32 n = 0, sum = 0; Reg *p; FS0(p);
    while (p != (Reg *)0xFFFFFFFFu && n < 64) { n++; sum += p->handler; p = p->prev; }
    res = (n << 16) | (sum & 0xFFFF);
  }
  Reg *cur; FS0(cur);
  if (cur != &r) res ^= 0x40000000u;                             /* chain must still end at our record */
  __asm__ volatile("movl %0,%%fs:0" : : "r"(r.prev) : "memory"); /* pop */
  return res;
}
u32 f_seh_nest(u32 depth, u32 tag) {
  Reg *h; FS0(h);
  u32 res = nest(depth & 7, tag);
  Reg *after; FS0(after);
  return res ^ (after == h ? 0u : 0x80000000u) ^ (h == (Reg *)0xFFFFFFFFu ? 0x00100000u : 0u);
}
u32 f_seh_unwind(u32 depth, u32 tag) { /* unwind several frames at once by restoring an older link */
  Reg a, b, c; Reg *h; FS0(h);
  a.prev = h; a.handler = tag;
  b.prev = &a; b.handler = tag + 1;
  c.prev = &b; c.handler = tag + 2;
  __asm__ volatile("movl %0,%%fs:0" : : "r"(&c) : "memory");
  u32 r = 0; Reg *p; FS0(p);
  while (p != h) { r = r * 31 + p->handler; p = p->prev; }
  __asm__ volatile("movl %0,%%fs:0" : : "r"(&a) : "memory");     /* longjmp-like: drop c and b */
  FS0(p); r += (p == &a) * 1000 + (depth & 3);
  __asm__ volatile("movl %0,%%fs:0" : : "r"(h) : "memory");
  FS0(p);
  return r + (p == h) * 100000;
}
u32 f_stack(u32 x) {
  u32 base, limit; volatile u32 local = x;
  __asm__ volatile("movl %%fs:4,%0" : "=r"(base));
  __asm__ volatile("movl %%fs:8,%0" : "=r"(limit));
  u32 a = (u32)&local;
  return (base > limit) | ((a < base) << 1) | ((a >= limit) << 2) | (((base - limit) >= 0x1000u) << 3) | ((local == x) << 4);
}
u32 f_self(u32 v) {
  u32 self, via; 
  __asm__ volatile("movl %%fs:0x18,%0" : "=r"(self));
  __asm__ volatile("movl %0,%%fs:0x34" : : "r"(v) : "memory");
  via = *(volatile u32 *)(self + 0x34);                          /* LastError seen through Self */
  *(volatile u32 *)(self + 0x34) = v + 1;
  u32 back; __asm__ volatile("movl %%fs:0x34,%0" : "=r"(back));
  u32 self2; __asm__ volatile("movl %%fs:(0x18),%0" : "=r"(self2));
  return (via ^ back ^ (((self & 0xFFF) == 0) ? 0x5A5A0000u : 0u)) + (self == self2);
}
