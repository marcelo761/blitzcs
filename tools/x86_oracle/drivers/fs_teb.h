/* Include-only, after common.h: installs a REAL fs segment (set_thread_area, int 0x80 NR 243) whose base is a 4 KiB TEB laid
 * out per docs/binary-teb-contract.md, so controlled libraries can execute genuine %fs: accesses natively on x86.
 *   teb_install()   build the TEB/PEB, install the descriptor, load %fs      teb_block()/peb_block()  the blocks (to print values)
 *   teb_set_tls(p)  optional implicit-TLS array published at fs:[0x2C]
 * Drivers print ONLY address-independent facts (equalities, differences, round-tripped values). */
#define TEB_EXCEPTION_LIST 0x00
#define TEB_STACK_BASE 0x04
#define TEB_STACK_LIMIT 0x08
#define TEB_SELF 0x18
#define TEB_PROCESS_ID 0x20
#define TEB_THREAD_ID 0x24
#define TEB_TLS_POINTER 0x2C
#define TEB_PEB 0x30
#define TEB_LAST_ERROR 0x34
#define TEB_TLS_SLOTS 0xE10
#define PEB_BEING_DEBUGGED 0x02
#define PEB_IMAGE_BASE 0x08
#define TEB_IMAGE_BASE 0x10000000u /* image base of the oracle library (oracle.ld) */

static unsigned char teb_mem[4096 * 3] __attribute__((aligned(4096)));
static inline unsigned char *teb_block(void) { return teb_mem; }
static inline unsigned char *peb_block(void) { return teb_mem + 4096; }
static inline u32 teb_get(u32 off) { return *(u32 *)(teb_mem + off); }
static inline void teb_put(u32 off, u32 v) { *(u32 *)(teb_mem + off) = v; }
static inline void teb_set_tls(void *array) { teb_put(TEB_TLS_POINTER, (u32)array); }

typedef struct { u32 entry_number, base_addr, limit, flags; } teb_user_desc; /* flags: seg_32bit=1, useable=1<<6 */

static inline void teb_install(void) {
  u32 sp_; __asm__ volatile("movl %%esp,%0" : "=r"(sp_));
  u32 base = (sp_ + 0x10000u) & ~0xFFFu;
  teb_put(TEB_EXCEPTION_LIST, 0xFFFFFFFFu);
  teb_put(TEB_STACK_BASE, base); teb_put(TEB_STACK_LIMIT, base - 0x400000u);
  teb_put(TEB_SELF, (u32)teb_mem);
  teb_put(TEB_PROCESS_ID, 0x1234u); teb_put(TEB_THREAD_ID, 0x5678u);
  teb_put(TEB_TLS_POINTER, 0);
  teb_put(TEB_PEB, (u32)peb_block());
  teb_put(TEB_LAST_ERROR, 0);
  *(u32 *)(peb_block() + PEB_IMAGE_BASE) = TEB_IMAGE_BASE;
  peb_block()[PEB_BEING_DEBUGGED] = 0;
  teb_user_desc d = {0xFFFFFFFFu, (u32)teb_mem, 0xFFFu, 1u | (1u << 6)};
  int r; __asm__ volatile("int $0x80" : "=a"(r) : "0"(243), "b"(&d) : "memory");
  if (r != 0) { puts_("E fs-setup-failed\n"); flush(); finish(); }
  u32 sel = d.entry_number * 8 + 3;
  __asm__ volatile("movw %0,%%fs" : : "r"((unsigned short)sel));
}

/* ORACLE_MAIN variant that installs the fs segment first. */
#define ORACLE_MAIN_FS(tbl, nrand) __attribute__((force_align_arg_pointer, noreturn)) void _start(void) { teb_install(); run_all(tbl, (int)(sizeof tbl / sizeof tbl[0]), nrand); finish(); }
