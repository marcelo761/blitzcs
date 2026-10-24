// FLAGS: -mmmx
/* Less common IA-32 instructions: memory-form bit tests, string compares, pusha, enter/leave, xlat, cmpxchg8b, BCD, MMX, high-byte registers, x87 extras. */
typedef unsigned u32; typedef int s32; typedef unsigned char u8; typedef unsigned short u16; typedef unsigned long long u64;
u32 g_bits[8]; u8 g_tab[256]; u32 g_q[2];
u32 o_btmem(u32 n, u32 op) { u8 c; g_bits[0] = 0x0f0f0f0f; g_bits[1] = 0xf0f0f0f0; g_bits[2] = 0x12345678; n &= 0x5f;
  switch (op & 3) { case 0: __asm__("btl %2,%1; setc %0" : "=q"(c), "+m"(g_bits) : "r"(n) : "cc"); break; case 1: __asm__("btsl %2,%1; setc %0" : "=q"(c), "+m"(g_bits) : "r"(n) : "cc"); break; case 2: __asm__("btrl %2,%1; setc %0" : "=q"(c), "+m"(g_bits) : "r"(n) : "cc"); break; default: __asm__("btcl %2,%1; setc %0" : "=q"(c), "+m"(g_bits) : "r"(n) : "cc"); }
  return c ^ (g_bits[0] * 3) ^ (g_bits[1] * 5) ^ (g_bits[2] * 7); }
u32 o_btimm(u32 a, u32 n) { u8 c; __asm__("btl $13,%1; setc %0" : "=q"(c) : "r"(a) : "cc"); (void)n; return c; }
u32 o_pusha(u32 a, u32 b) { u32 r; __asm__ volatile("pushal; movl %1,%%eax; movl %2,%%ebx; addl %%ebx,%%eax; movl %%eax,%0; popal" : "=m"(r) : "r"(a), "r"(b) : "memory", "cc"); return r; }
u32 o_enterleave(u32 a, u32 b) { u32 r; __asm__ volatile("pushl %%ebp; movl %%esp,%%ebp; subl $16,%%esp; movl %1,-4(%%ebp); movl %2,-8(%%ebp); movl -4(%%ebp),%%eax; imull -8(%%ebp),%%eax; leave; movl %%eax,%0" : "=m"(r) : "r"(a), "r"(b) : "eax", "memory", "cc"); return r; }
u32 o_xlat(u32 a, u32 b) { for (int i = 0; i < 256; i++) g_tab[i] = (u8)(i * 7 + b); u32 r = a; __asm__("xlatb" : "+a"(r) : "b"(g_tab) : "memory"); return r & 0xff; }
u32 o_cmpxchg8b(u32 a, u32 b) { g_q[0] = a; g_q[1] = b; u32 lo = a ^ 1, hi = b, nl = a + 5; u32 zf; __asm__("cmpxchg8b %1; setz %%cl; movzbl %%cl,%2" : "+a"(lo), "+m"(g_q), "=c"(zf) : "d"(hi), "b"(nl), "0"(lo) : "cc"); return lo ^ g_q[0] ^ (g_q[1] * 3) ^ zf; }
u32 o_cmpxchg8b_eq(u32 a, u32 b) { g_q[0] = a; g_q[1] = b; u32 lo = a, hi = b, nl = a + 5, ncx = b + 9; u8 z; __asm__("cmpxchg8b %2; setz %3" : "+a"(lo), "+d"(hi), "+m"(g_q), "=q"(z) : "b"(nl), "c"(ncx) : "cc"); return lo ^ g_q[0] ^ (g_q[1] * 3) ^ z ^ hi; }
u32 o_highbyte(u32 a, u32 b) { u32 r = a; __asm__("movb %b1,%%ah; addb %%ah,%%al; movzbl %%ah,%%edx; shll $8,%%edx; orl %%edx,%%eax" : "+a"(r) : "q"(b) : "edx", "cc"); return r; }
u32 o_movzx_ah(u32 a, u32 b) { u32 r; __asm__("movl %1,%%eax; movzbl %%ah,%0" : "=r"(r) : "r"(a ^ b) : "eax"); return r; }
u32 o_imul3(u32 a, u32 b) { u32 r; __asm__("imull $-7,%1,%0" : "=r"(r) : "r"(a)); u32 s; __asm__("imull $300,%1,%0" : "=r"(s) : "r"(b)); return r ^ s; }
u32 o_imul16(u32 a, u32 b) { u16 r = (u16)a; __asm__("imulw %1,%0" : "+r"(r) : "r"((u16)b) : "cc"); return r; }
u32 o_cmovall(u32 a, u32 b, u32 c, u32 d) { u32 r, t; __asm__("cmpl %5,%4; movl %3,%0; cmovel %2,%0; movl %3,%1; cmovbl %2,%1; addl %1,%0; movl %3,%1; cmovgl %2,%1; xorl %1,%0; movl %3,%1; cmovnsl %2,%1; addl %1,%0" : "=&r"(r), "=&r"(t) : "m"(c), "m"(d), "r"(a), "r"(b) : "cc"); return r; }
u32 o_shifts_imm(u32 a, u32 b) { u32 x = a, y = b; __asm__("shll $5,%0; shrl $3,%1; sarl $31,%1; roll $9,%0; rorl $2,%1; shldl $7,%1,%0; shrdl $11,%0,%1" : "+r"(x), "+r"(y) :: "cc"); return x ^ y; }
u32 o_mmx(u32 a, u32 b) { u32 r; __asm__ volatile("movd %1,%%mm0; movd %2,%%mm1; paddb %%mm1,%%mm0; pxor %%mm1,%%mm0; movd %%mm0,%0; emms" : "=r"(r) : "r"(a), "r"(b) : "mm0", "mm1", "memory"); return r; }
u32 o_fxch(s32 a, s32 b) { double x = a, y = b, r; __asm__("fxch %%st(1); fsubrp; " : "=t"(r) : "0"(x), "u"(y) : "st(1)"); return (u32)(s32)(r * 16.0); }
u32 o_fucomi(s32 a, s32 b) { double x = a, y = b; u32 r = 0; if (x < y) r |= 1; if (x == y) r |= 2; if (x > y) r |= 4; if (x != x) r |= 8; return r; }
u32 o_fprem(u32 a, u32 b) { double x = (double)(s32)a, y = (double)(s32)(b | 1), r; __asm__("1: fprem; fnstsw %%ax; testb $4,%%ah; jnz 1b" : "=t"(r) : "0"(x), "u"(y) : "ax", "cc"); return (u32)(s32)(r * 64.0); }
u32 o_fistp_modes(s32 a, s32 b) { double x = (double)a / ((double)(b | 1)); s32 i; __asm__("fistpl %0" : "=m"(i) : "t"(x) : "st"); return (u32)i; }
u32 o_cpuid_vendor(u32 a) { u32 b, c, d; __asm__ volatile("cpuid" : "+a"(a), "=b"(b), "=c"(c), "=d"(d)); return a > 0 ? 1 : 0; }
u32 o_rdtsc_mono(u32 a) { u32 lo1, hi1, lo2, hi2; __asm__ volatile("rdtsc" : "=a"(lo1), "=d"(hi1)); for (u32 i = 0; i < (a & 63); i++) __asm__ volatile(""); __asm__ volatile("rdtsc" : "=a"(lo2), "=d"(hi2)); return (((u64)hi2 << 32 | lo2) >= ((u64)hi1 << 32 | lo1)); }
u32 o_lods_sto_d(u32 a, u32 b) { u32 buf[4] = {a, b, a ^ b, a + b}; u32 acc = 0; u32 *p = buf; u32 v; for (int i = 0; i < 4; i++) { __asm__ volatile("cld; lodsl" : "=a"(v), "+S"(p) :: "memory"); acc = acc * 31 + v; } return acc; }
u32 o_repne_scasd(u32 a, u32 b) { u32 buf[8] = {1, 2, 3, 4, 5, 6, 7, 8}; buf[a & 7] = b; u32 *p = buf; u32 c = 8; __asm__ volatile("cld; repne scasl" : "+D"(p), "+c"(c) : "a"(b) : "cc", "memory"); return (u32)(p - buf) * 16 + c; }
u32 o_pushf_popf(u32 a, u32 b) { u32 f; __asm__ volatile("pushfl; popl %%eax; orl $0x400,%%eax; pushl %%eax; popfl; std; pushfl; popl %0; cld; pushfl; popl %%eax; andl $0x400,%%eax" : "=r"(f) :: "eax", "cc"); (void)a; (void)b; return (f & 0x400) ? 1 : 2; }
