/* Controlled FS library 2: TLS slots (static and indexed offsets), LastError, ids, PEB, implicit-TLS pointer. */
typedef unsigned u32; typedef unsigned char u8;
u32 g_marker;
u32 f_tls_static(u32 v) {
  u32 a, b, c;
  __asm__ volatile("movl %0,%%fs:0xE10" : : "r"(v) : "memory");
  __asm__ volatile("movl %0,%%fs:0xE14" : : "r"(v * 3 + 1) : "memory");
  __asm__ volatile("movl %0,%%fs:0xF0C" : : "r"(~v) : "memory");        /* slot 63 */
  __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(a));
  __asm__ volatile("movl %%fs:0xE14,%0" : "=r"(b));
  __asm__ volatile("movl %%fs:0xF0C,%0" : "=r"(c));
  return (a ^ ((b << 8) | (b >> 24))) + c;
}
u32 f_tls_indexed(u32 i, u32 v) {
  i &= 63; u32 j = (i + 1) & 63, a, b, s0;
  __asm__ volatile("movl %0,%%fs:0xE10(,%1,4)" : : "r"(v), "r"(i) : "memory");
  __asm__ volatile("movl %0,%%fs:0xE10(,%1,4)" : : "r"(v ^ 0xA5A5A5A5u), "r"(j) : "memory");
  __asm__ volatile("movl %%fs:0xE10(,%1,4),%0" : "=r"(a) : "r"(i));
  __asm__ volatile("movl %%fs:0xE10(,%1,4),%0" : "=r"(b) : "r"(j));
  u32 off = 0xE10 + 4 * i; u32 viaReg;                                   /* register-only offset */
  __asm__ volatile("movl %%fs:(%1),%0" : "=r"(viaReg) : "r"(off));
  __asm__ volatile("movl %%fs:0xE10,%0" : "=r"(s0));                     /* slot 0 seen with a static offset */
  return a + b * 3 + (viaReg == a) * 0x10000 + (i == 0 ? (s0 == a) * 0x20000 : 0);
}
u32 f_lasterror(u32 v) {
  u32 old, mid, now;
  __asm__ volatile("movl %%fs:0x34,%0" : "=r"(old));
  __asm__ volatile("movl %0,%%fs:0x34" : : "r"(v) : "memory");
  __asm__ volatile("addl $1,%%fs:0x34" : : : "memory", "cc");           /* read-modify-write on fs: */
  __asm__ volatile("movl %%fs:0x34,%0" : "=r"(mid));
  __asm__ volatile("xchgl %0,%%fs:0x34" : "=r"(now) : "0"(old) : "memory"); /* swaps back the previous value */
  return (mid ^ now) + (mid == v + 1) * 7;
}
u32 f_ids(u32 x) {
  u32 pid, tid, tls;
  __asm__ volatile("movl %%fs:0x20,%0" : "=r"(pid));
  __asm__ volatile("movl %%fs:0x24,%0" : "=r"(tid));
  __asm__ volatile("movl %%fs:0x2C,%0" : "=r"(tls));
  u32 r = (pid != 0) | ((tid != 0) << 1) | ((tid != pid) << 2) | ((tls == 0) << 3);
  __asm__ volatile("movl %0,%%fs:0x2C" : : "r"(x | 0x1000) : "memory"); /* TLS pointer is read/write */
  u32 back; __asm__ volatile("movl %%fs:0x2C,%0" : "=r"(back));
  __asm__ volatile("movl $0,%%fs:0x2C" : : : "memory");
  return r | ((back == (x | 0x1000)) << 4);
}
u32 f_peb(u32 x) {
  u32 peb, img; u8 dbg;
  __asm__ volatile("movl %%fs:0x30,%0" : "=r"(peb));
  dbg = *(volatile u8 *)(peb + 2);
  img = *(volatile u32 *)(peb + 8);
  u32 fr = (u32)f_peb - img, dr = (u32)&g_marker - img;                  /* RVAs relative to the PEB image base */
  g_marker += x;
  return dbg | ((fr >= 0x1000 && fr < 0x2000) << 1) | ((dr >= 0x2000 && dr < 0x3000) << 2);
}
