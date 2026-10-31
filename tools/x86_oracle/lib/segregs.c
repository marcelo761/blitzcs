/* Segment-register reads. The translator lowers cs/ss/ds/es/gs/fs to flat WoW64 selectors; the native oracle reports what the
 * running process really holds. Covers 16-bit and 32-bit register reads, push/pop, a memory destination (mov sreg, m16),
 * a reload of the same value (must not fault), and an OR of all six (catches operand-size bugs). */
typedef unsigned int u32; typedef unsigned short u16;

union sg_word { u32 w; u16 h[2]; };
/* The fixture builder pads any data section shorter than 16 bytes with zeros, so the initialised data must be at least 16 bytes. */
u32 sg_area[4] = {0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u};
union sg_word sg_mem = { 0xbeef0000u };   /* data: only the low half is written by mov %ds, m16; the high half must survive */

#define SEG16(name, seg) u32 name(void) { u16 r; __asm__ volatile("movw %%" #seg ", %0" : "=r"(r)); return r; }
#define SEG32(name, seg) u32 name(void) { u32 r; __asm__ volatile("mov %%" #seg ", %0" : "=r"(r)); return r; }
SEG16(sg_cs16, cs)
SEG16(sg_ss16, ss)
SEG16(sg_ds16, ds)
SEG16(sg_es16, es)
SEG16(sg_gs16, gs)
SEG16(sg_fs16, fs)
SEG32(sg_cs32, cs)
SEG32(sg_ss32, ss)
SEG32(sg_ds32, ds)
SEG32(sg_es32, es)
SEG32(sg_gs32, gs)
SEG32(sg_fs32, fs)

u32 sg_push_pop_ds(void) { u32 r; __asm__ volatile("push %%ds\n\tpop %0" : "=r"(r)); return r; }

u32 sg_store_ds_mem(void)
{
  sg_mem.w = 0xbeef0000u;
  __asm__ volatile("movw %%ds, %0" : "=m"(sg_mem.h[0]));
  return sg_mem.w;
}

/* push es / pop es, then read ds, reload ds with the value it already holds, read ds and es back. */
u32 sg_reload_same(void)
{
  u16 ds_before, ds_after, es_after;
  __asm__ volatile(
    "push %%es\n\t"
    "pop %%es\n\t"
    "mov %%ds, %0\n\t"
    "mov %0, %%ax\n\t"
    "mov %%ax, %%ds\n\t"
    "mov %%ds, %1\n\t"
    "mov %%es, %2"
    : "=&r"(ds_before), "=r"(ds_after), "=r"(es_after) : : "ax", "memory");
  return (u32)ds_after | ((u32)es_after << 16) | (ds_before == ds_after ? 0u : 0x80000000u);
}

u32 sg_or_all(void)
{
  u32 a, b, c, d, e, f;
  __asm__ volatile("mov %%cs, %0" : "=r"(a));
  __asm__ volatile("mov %%ss, %0" : "=r"(b));
  __asm__ volatile("mov %%ds, %0" : "=r"(c));
  __asm__ volatile("mov %%es, %0" : "=r"(d));
  __asm__ volatile("mov %%gs, %0" : "=r"(e));
  __asm__ volatile("mov %%fs, %0" : "=r"(f));
  return a | b | c | d | e | f;
}

/* OR of cs, ss, ds and es only: a composed check that is comparable on every host (fs/gs are host-specific, see sg_or_all). */
u32 sg_or_four(void)
{
  u32 a, b, c, d;
  __asm__ volatile("mov %%cs, %0" : "=r"(a));
  __asm__ volatile("mov %%ss, %0" : "=r"(b));
  __asm__ volatile("mov %%ds, %0" : "=r"(c));
  __asm__ volatile("mov %%es, %0" : "=r"(d));
  return a | b | c | d;
}
