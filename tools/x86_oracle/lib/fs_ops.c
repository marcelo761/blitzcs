// FLAGS: -msse2 -mstackrealign
/* Controlled FS library 3: instruction forms on fs: (sized accesses, read-modify-write, xchg/cmpxchg, flag consumers with
 * run-time offsets, push/pop memory, SSE moves). Only address-independent values are returned. Slots: fs:0xE10 + 4*i. */
typedef unsigned u32; typedef unsigned short u16; typedef unsigned char u8; typedef unsigned long long u64;
#define SLOT(i) "0xE10+4*" #i
u32 f_sized(u32 v) {
  u32 a, b, c, d, e;
  __asm__ volatile("movl %0,%%fs:0xE10" : : "r"(v) : "memory");
  __asm__ volatile("movzbl %%fs:0xE11,%0" : "=r"(a));
  __asm__ volatile("movzwl %%fs:0xE12,%0" : "=r"(b));
  __asm__ volatile("movsbl %%fs:0xE13,%0" : "=r"(c));
  __asm__ volatile("movb %0,%%fs:0xE14" : : "q"((u8)(v >> 3)) : "memory");
  __asm__ volatile("movw %0,%%fs:0xE16" : : "r"((u16)(v >> 5)) : "memory");
  __asm__ volatile("movl %%fs:0xE14,%0" : "=r"(d));
  __asm__ volatile("movl $0x5a5a1234,%%fs:0xE18" : : : "memory");   /* store immediate */
  __asm__ volatile("movl %%fs:0xE18,%0" : "=r"(e));
  __asm__ volatile("movb $0x7f,%%fs:0xE19" : : : "memory");
  u32 f; __asm__ volatile("movl %%fs:0xE18,%0" : "=r"(f));
  return a * 3 + b * 5 + c * 7 + d * 11 + e + f * 13;
}
u32 f_rmw(u32 a, u32 b) {
  u32 r = 0, t; u8 cf, zf;
  __asm__ volatile("movl %0,%%fs:0xE10" : : "r"(a) : "memory");
  __asm__ volatile("addl %1,%%fs:0xE10" : "=@ccc"(cf) : "r"(b) : "memory");
  r += cf; __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(t)); r = r * 31 + t;
  __asm__ volatile("subl %1,%%fs:0xE10" : "=@ccc"(cf) : "r"(b ^ 0x55) : "memory");
  r += cf; __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(t)); r = r * 31 + t;
  __asm__ volatile("andl $0xf0f0f0f0,%%fs:0xE10" : "=@ccz"(zf) : : "memory");
  r += zf; __asm__ volatile("xorl %0,%%fs:0xE10" : : "r"(b) : "memory");
  __asm__ volatile("negl %%fs:0xE10" : : : "memory", "cc");
  __asm__ volatile("incl %%fs:0xE10" : : : "memory", "cc");
  __asm__ volatile("notl %%fs:0xE10" : : : "memory", "cc");
  __asm__ volatile("shll $3,%%fs:0xE10" : : : "memory", "cc");
  __asm__ volatile("orl $0x100,%%fs:0xE10" : : : "memory", "cc");
  __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(t)); r = r * 31 + t;
  u32 x = b;
  __asm__ volatile("xaddl %0,%%fs:0xE10" : "+r"(x) : : "memory", "cc");
  __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(t)); r = r * 31 + t + x;
  u32 old = 5; __asm__ volatile("xchgl %0,%%fs:0xE10" : "+r"(old) : : "memory");
  r += old; u32 exp = 5, nw = a ^ b;
  __asm__ volatile("lock cmpxchgl %2,%%fs:0xE10" : "+a"(exp), "=@ccz"(zf) : "r"(nw) : "memory");
  r += zf * 77 + exp; __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(t));
  return r * 31 + t;
}
u32 f_flagdep(u32 a, u32 b) {          /* consumers of CF with a run-time fs: offset (the bounds check must not clobber flags) */
  u32 i = (a ^ b) & 63, acc = a, sum = 0;
  __asm__ volatile("movl %0,%%fs:0xE10(,%1,4)" : : "r"(b), "r"(i) : "memory");
  __asm__ volatile("cmpl %2,%1\n\tadcl %%fs:0xE10(,%3,4),%0" : "+r"(acc) : "r"(a), "r"(b), "r"(i) : "cc");
  sum += acc;
  __asm__ volatile("cmpl %2,%1\n\tsbbl %%fs:0xE10(,%3,4),%0" : "+r"(acc) : "r"(a), "r"(b), "r"(i) : "cc");
  sum = sum * 31 + acc;
  u32 m = 1;
  __asm__ volatile("cmpl %2,%1\n\tcmovb %%fs:0xE10(,%3,4),%0" : "+r"(m) : "r"(a), "r"(b), "r"(i) : "cc");
  sum = sum * 31 + m;
  __asm__ volatile("cmpl %2,%1\n\trcll $1,%%fs:0xE10(,%0,4)" : : "r"(i), "r"(a), "r"(b) : "cc", "memory");
  u32 t; __asm__ volatile("movl %%fs:0xE10(,%1,4),%0" : "=r"(t) : "r"(i));
  return sum * 31 + t;
}
u32 f_dynops(u32 i, u32 v) {
  i &= 63; u32 r = 0, t;
  __asm__ volatile("movl %0,%%fs:0xE10(,%1,4)" : : "r"(v), "r"(i) : "memory");
  __asm__ volatile("addl $7,%%fs:0xE10(,%0,4)" : : "r"(i) : "memory", "cc");
  __asm__ volatile("incl %%fs:0xE10(,%0,4)" : : "r"(i) : "memory", "cc");
  __asm__ volatile("movl %%fs:0xE10(,%1,4),%0" : "=r"(t) : "r"(i)); r = t;
  u32 x = 3; __asm__ volatile("xchgl %0,%%fs:0xE10(,%1,4)" : "+r"(x) : "r"(i) : "memory"); r = r * 31 + x;
  __asm__ volatile("movzbl %%fs:0xE10+3(,%1,4),%0" : "=r"(t) : "r"(i)); r = r * 31 + t;
  u32 j = (i + 1) & 63;
  __asm__ volatile("movl %0,%%fs:0xE10(%1,%2,4)" : : "r"(v ^ 1), "r"(0u), "r"(j) : "memory");   /* base + index */
  __asm__ volatile("movl %%fs:0xE10(,%1,4),%0" : "=r"(t) : "r"(j)); r = r * 31 + t;
  return r;
}
u32 f_pushpop(u32 v) {
  u32 t, u;
  __asm__ volatile("movl %0,%%fs:0xE10" : : "r"(v) : "memory");
  __asm__ volatile("pushl %%fs:0xE10\n\tpopl %%fs:0xE14" : : : "memory");
  __asm__ volatile("movl %%fs:0xE14,%0" : "=r"(t));
  __asm__ volatile("pushl %%fs:0xE14\n\tpopl %0" : "=r"(u));
  return t ^ (u << 1);
}
u32 f_sse(u32 v) {
  u32 a, b, c, d;
  u32 q[4] __attribute__((aligned(16))) = {v, v + 1, v + 2, v + 3};
  __asm__ volatile("movups %0,%%xmm0\n\tmovups %%xmm0,%%fs:0xE20" : : "m"(q) : "xmm0", "memory");
  __asm__ volatile("movl %%fs:0xE20,%0" : "=r"(a));
  __asm__ volatile("movl %%fs:0xE2C,%0" : "=r"(b));
  __asm__ volatile("movq %%fs:0xE24,%%xmm1\n\tmovq %%xmm1,%%fs:0xE30" : : : "xmm1", "memory");
  __asm__ volatile("movl %%fs:0xE30,%0" : "=r"(c));
  __asm__ volatile("movl %%fs:0xE34,%0" : "=r"(d));
  __asm__ volatile("movd %%fs:0xE28,%%xmm2\n\tpaddd %%xmm2,%%xmm2\n\tmovd %%xmm2,%0" : "=r"(c) : : "xmm2");
  return a + b * 3 + c * 5 + d * 7;
}
u32 f_lea(u32 a, u32 b) {        /* lea never consults the segment */
  u32 r; __asm__ volatile(".byte 0x64\n\tleal 5(%1,%2,4),%0" : "=r"(r) : "r"(a), "r"(b));
  return r;
}
