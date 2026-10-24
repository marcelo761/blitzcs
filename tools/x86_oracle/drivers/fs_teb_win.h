/* Windows variant of fs_teb.h (-DORACLE_WIN32): the OS already provides a real TEB at %fs:, so nothing is installed. Instead the driver
 * normalises the few TEB fields a real process does not initialise like the contract (docs/binary-teb-contract.md) and restores the
 * real values around every OS call and at exit:
 *   fs:[0x00] ExceptionList  real: ntdll/loader handler records;  normalised to 0xFFFFFFFF (empty chain) while library code runs
 *   fs:[0x2C] TLS pointer    real: implicit-TLS array or NULL;    normalised to 0
 *   fs:[0xE10..0xF0C] TlsSlots real: usually 0 but owned by the OS; saved, zeroed, restored at exit
 * Everything else (stack bounds, Self, ids, PEB, LastError) is the genuine value; the libraries print only invariants of those. */
#define TEB_IMAGE_BASE 0x10000000u
static u32 w_real_chain, w_real_tls, w_cur_chain, w_cur_tls, w_slots[64];
static inline u32 w_fs(u32 off) { u32 v; __asm__ volatile("movl %%fs:(%1),%0" : "=r"(v) : "r"(off) : "memory"); return v; }
static inline void w_fsput(u32 off, u32 v) { __asm__ volatile("movl %0,%%fs:(%1)" : : "r"(v), "r"(off) : "memory"); }
static void w_enter(void) { w_cur_chain = w_fs(0); w_cur_tls = w_fs(0x2C); w_fsput(0, w_real_chain); w_fsput(0x2C, w_real_tls); }
static void w_leave(void) { w_fsput(0, w_cur_chain); w_fsput(0x2C, w_cur_tls); }
static inline unsigned char *teb_block(void) { return (unsigned char *)w_fs(0x18); }
static inline unsigned char *peb_block(void) { return (unsigned char *)w_fs(0x30); }
static inline u32 teb_get(u32 off) { return w_fs(off); }
static inline void teb_put(u32 off, u32 v) { w_fsput(off, v); }
static void w_finish_restore(void) { w_enter(); for (int i = 0; i < 64; i++) w_fsput(0xE10 + 4u * (u32)i, w_slots[i]); }
static inline void teb_install(void) {
  w_real_chain = w_fs(0); w_real_tls = w_fs(0x2C);
  for (int i = 0; i < 64; i++) { w_slots[i] = w_fs(0xE10 + 4u * (u32)i); w_fsput(0xE10 + 4u * (u32)i, 0); }
  w_fsput(0, 0xFFFFFFFFu); w_fsput(0x2C, 0);
  io_enter = w_enter; io_leave = w_leave;
}
#define ORACLE_MAIN_FS(tbl, nrand) __attribute__((force_align_arg_pointer, noreturn)) void _start(void) { teb_install(); run_all(tbl, (int)(sizeof tbl / sizeof tbl[0]), nrand); w_finish_restore(); finish(); }
