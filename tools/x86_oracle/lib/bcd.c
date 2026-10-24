/* BCD/ASCII-adjust instructions are invalid in 64-bit mode: the translator must report them as UNSUPPORTED (no oracle driver). */
typedef unsigned u32; typedef unsigned char u8;
u32 d_bcd(u32 a, u32 b) { u32 r = a & 0xff; __asm__("addb %b2,%%al; daa" : "+a"(r) : "q"((u8)b) : "cc"); u32 s = a & 0xff; __asm__("subb %b2,%%al; das" : "+a"(s) : "q"((u8)b) : "cc"); return (r & 0xff) | ((s & 0xff) << 8); }
u32 d_aam(u32 a, u32 b) { u32 r = a & 0xff; (void)b; __asm__("aam" : "+a"(r) : : "cc"); return r & 0xffff; }
