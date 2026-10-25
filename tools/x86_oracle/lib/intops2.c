/* Controlled integer library 2: shifts/rotates with variable counts, carry-through shifts, flags via pushf, bit ops, partial registers, rep-free misc. */
typedef unsigned u32; typedef int s32; typedef unsigned long long u64; typedef long long s64; typedef unsigned short u16; typedef unsigned char u8;
u32 b_shl(u32 a, u32 n) { return a << (n & 31); }
u32 b_shr(u32 a, u32 n) { return a >> (n & 31); }
u32 b_sar(s32 a, u32 n) { return (u32)(a >> (n & 31)); }
u32 b_shl64hi(u32 a, u32 b, u32 n) { return (u32)((((u64)b << 32) | a) << (n & 63) >> 32); }
u32 b_shl64lo(u32 a, u32 b, u32 n) { return (u32)((((u64)b << 32) | a) << (n & 63)); }
u32 b_shr64lo(u32 a, u32 b, u32 n) { return (u32)((((u64)b << 32) | a) >> (n & 63)); }
u32 b_sar64hi(u32 a, u32 b, u32 n) { return (u32)((u64)((s64)(((u64)b << 32) | a) >> (n & 63)) >> 32); }
u32 b_shld(u32 a, u32 b, u32 n) { __asm__("shldl %%cl,%1,%0" : "+r"(a) : "r"(b), "c"(n)); return a; }
u32 b_shrd(u32 a, u32 b, u32 n) { __asm__("shrdl %%cl,%1,%0" : "+r"(a) : "r"(b), "c"(n)); return a; }
u32 b_rol(u32 a, u32 n) { __asm__("roll %%cl,%0" : "+r"(a) : "c"(n)); return a; }
u32 b_ror(u32 a, u32 n) { __asm__("rorl %%cl,%0" : "+r"(a) : "c"(n)); return a; }
u32 b_rol8(u32 a, u32 n) { u8 v = (u8)a; __asm__("rolb %%cl,%0" : "+q"(v) : "c"(n)); return v; }
u32 b_rcl(u32 a, u32 n, u32 c) { __asm__("btl $0,%2; rcll %%cl,%0" : "+r"(a) : "c"(n), "r"(c) : "cc"); return a; }
u32 b_rcr(u32 a, u32 n, u32 c) { __asm__("btl $0,%2; rcrl %%cl,%0" : "+r"(a) : "c"(n), "r"(c) : "cc"); return a; }
u32 b_adc(u32 a, u32 b, u32 c) { __asm__("btl $0,%2; adcl %1,%0" : "+r"(a) : "r"(b), "r"(c) : "cc"); return a; }
u32 b_sbb(u32 a, u32 b, u32 c) { __asm__("btl $0,%2; sbbl %1,%0" : "+r"(a) : "r"(b), "r"(c) : "cc"); return a; }
u32 b_bsf(u32 a) { u32 r = 0xdeadbeef; u8 z; __asm__("bsfl %2,%0; setz %1" : "+r"(r), "=q"(z) : "r"(a) : "cc"); return z ? 0x100 : r; }
u32 b_bsr(u32 a) { u32 r = 0xdeadbeef; u8 z; __asm__("bsrl %2,%0; setz %1" : "+r"(r), "=q"(z) : "r"(a) : "cc"); return z ? 0x100 : r; }
u32 b_ctz(u32 a) { return a ? (u32)__builtin_ctz(a) : 32; }
u32 b_clz(u32 a) { return a ? (u32)__builtin_clz(a) : 32; }
u32 b_bswap(u32 a) { return __builtin_bswap32(a); }
u32 b_bt(u32 a, u32 n) { u8 c; __asm__("btl %2,%1; setc %0" : "=q"(c) : "r"(a), "r"(n) : "cc"); return c; }
u32 b_bts(u32 a, u32 n) { __asm__("btsl %1,%0" : "+r"(a) : "r"(n) : "cc"); return a; }
u32 b_btr(u32 a, u32 n) { __asm__("btrl %1,%0" : "+r"(a) : "r"(n) : "cc"); return a; }
u32 b_btc(u32 a, u32 n) { __asm__("btcl %1,%0" : "+r"(a) : "r"(n) : "cc"); return a; }
u32 b_bits(u32 a, u32 b, u32 c, u32 d) { return (((a ^ b) & ~c) | (d & (a | b))) ^ ((a & b) << 3) ^ -(a >> 31); }
u32 b_neg(u32 a) { return (u32)-(s32)a; }
u32 b_not(u32 a) { return ~a; }
u32 b_lea(u32 a, u32 b, u32 c) { return a * 5 + b * 9 + c * 3 + 77; }
u32 b_imul3(u32 a) { return a * 0x1234567u; }
u32 b_sx8(u32 a) { return (u32)(s32)(signed char)a; }
u32 b_sx16(u32 a) { return (u32)(s32)(short)a; }
u32 b_zx8(u32 a) { return (u8)a; }
u32 b_zx16(u32 a) { return (u16)a; }
u32 b_add16(u32 a, u32 b) { return (u16)((u16)a + (u16)b); }
u32 b_mul16(u32 a, u32 b) { return (u16)((u16)a * (u16)b) ^ ((u32)((u16)a * (u32)(u16)b) >> 16); }
u32 b_mul8(u32 a, u32 b) { u16 r; __asm__("mulb %2" : "=a"(r) : "0"((u8)a), "q"((u8)b) : "cc"); return r; }
u32 b_imul8(u32 a, u32 b) { u16 r; __asm__("imulb %2" : "=a"(r) : "0"((u8)a), "q"((u8)b) : "cc"); return r; }
u32 b_swapb(u32 a) { __asm__("xchgb %%al,%%ah" : "+a"(a)); return a; }
u32 b_cbw(u32 a) { u32 r; __asm__("cbtw; cwtl" : "=a"(r) : "0"(a)); return r; }
u32 b_cdq(u32 a) { u32 d; __asm__("cltd" : "=d"(d) : "a"(a)); return d; }
u32 b_xchg(u32 a, u32 b) { __asm__("xchgl %1,%0" : "+r"(a), "+r"(b)); return a * 3 + b; }
u32 b_xadd(u32 a, u32 b) { __asm__("xaddl %1,%0" : "+r"(a), "+r"(b)); return a * 3 + b; }
u32 b_cmpxchg(u32 a, u32 b, u32 c) { u32 acc = a; __asm__("cmpxchgl %2,%1" : "+a"(acc), "+m"(b) : "r"(c) : "cc"); return acc * 3 + b; }
u32 b_loopsum(u32 n) { u32 s = 0; n = n % 1000 + 1; __asm__("1: addl %%ecx,%0; loop 1b" : "+r"(s), "+c"(n) :: "cc"); return s; }
u32 b_jecxz(u32 a) { u32 r = 1; __asm__("jecxz 1f; movl $2,%0; 1:" : "+r"(r) : "c"(a)); return r; }
/* flag-visible results via pushfl: mask keeps only architecturally defined bits */
#define FL(op, mask) u32 f; __asm__(op "; pushfl; popl %1" : "+r"(a), "=r"(f) : "r"(b) : "cc"); return (f & (mask)) ^ (a * 0x9e3779b1u)
u32 f_add(u32 a, u32 b) { FL("addl %2,%0", 0x8d5); }
u32 f_sub(u32 a, u32 b) { FL("subl %2,%0", 0x8d5); }
u32 f_cmp(u32 a, u32 b) { u32 f; __asm__("cmpl %2,%1; pushfl; popl %0" : "=r"(f) : "r"(a), "r"(b) : "cc"); return f & 0x8d5; }
u32 f_and(u32 a, u32 b) { FL("andl %2,%0", 0x8c5); }
u32 f_or(u32 a, u32 b) { FL("orl %2,%0", 0x8c5); }
u32 f_xor(u32 a, u32 b) { FL("xorl %2,%0", 0x8c5); }
u32 f_inc_keepcf(u32 a, u32 b) { FL("stc; incl %0", 0x8d5 & ~0u); (void)b; }
u32 f_dec(u32 a, u32 b) { FL("clc; decl %0", 0x8d5); }
u32 f_neg(u32 a, u32 b) { FL("negl %0", 0x8d5); }
u32 f_adc(u32 a, u32 b) { FL("stc; adcl %2,%0", 0x8d5); }
u32 f_sbb(u32 a, u32 b) { FL("stc; sbbl %2,%0", 0x8d5); }
u32 f_shl(u32 a, u32 b) { u32 f; __asm__("movl %2,%%ecx; shll %%cl,%0; pushfl; popl %1" : "+r"(a), "=r"(f) : "r"(b & 31) : "cc", "ecx"); return ((b & 31) ? (f & 0xc5) : 0) ^ (a * 0x9e3779b1u); }
u32 f_shr(u32 a, u32 b) { u32 f; __asm__("movl %2,%%ecx; shrl %%cl,%0; pushfl; popl %1" : "+r"(a), "=r"(f) : "r"(b & 31) : "cc", "ecx"); return ((b & 31) ? (f & 0xc5) : 0) ^ (a * 0x9e3779b1u); }
u32 f_sar1(u32 a, u32 b) { FL("sarl $1,%0", 0x8c5); (void)b; }
u32 f_mul(u32 a, u32 b) { u32 f, hi; __asm__("mull %3; pushfl; popl %2" : "+a"(a), "=d"(hi), "=r"(f) : "r"(b) : "cc"); return (f & 0x801) ^ a ^ (hi * 7); }
u32 f_lahf(u32 a, u32 b) { u32 r; __asm__("addl %2,%1; lahf; movzbl %%ah,%0" : "=a"(r), "+r"(a) : "r"(b) : "cc"); return (r & 0xd5) ^ a; }
u32 f_sahf(u32 a, u32 b) { u8 c, z, sg; __asm__("sahf; setc %0; setz %1; sets %2" : "=q"(c), "=q"(z), "=q"(sg) : "a"(a) : "cc"); (void)b; return c | (z << 1) | (sg << 2); }
u32 f_sahf2(u32 a, u32 b) { u8 p, ab, be; __asm__("sahf; setp %0; seta %1; setbe %2" : "=q"(p), "=q"(ab), "=q"(be) : "a"(a) : "cc"); (void)b; return p | (ab << 1) | (be << 2); }
u32 f_cmc(u32 a, u32 b) { u32 f; __asm__("addl %2,%0; cmc; pushfl; popl %1" : "+r"(a), "=r"(f) : "r"(b) : "cc"); return f & 0x1; }
u32 f_setcc(u32 a, u32 b) { u8 x1, x2, x3; __asm__("cmpl %4,%3; setg %0; setl %1; seta %2" : "=q"(x1), "=q"(x2), "=q"(x3) : "r"(a), "r"(b) : "cc"); return x1 | (x2 << 1) | (x3 << 2); }
u32 f_setcc2(u32 a, u32 b) { u8 x4, x5, x6; __asm__("cmpl %4,%3; setbe %0; setno %1; setnp %2" : "=q"(x4), "=q"(x5), "=q"(x6) : "r"(a), "r"(b) : "cc"); return x4 | (x5 << 1) | (x6 << 2); }
