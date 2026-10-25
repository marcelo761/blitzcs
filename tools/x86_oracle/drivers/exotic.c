#include "common.h"
extern char o_btmem[], o_btimm[], o_pusha[], o_enterleave[], o_xlat[], o_cmpxchg8b[], o_cmpxchg8b_eq[], o_highbyte[], o_movzx_ah[], o_imul3[], o_imul16[], o_cmovall[], o_shifts_imm[], o_mmx[], o_fxch[], o_fucomi[], o_fprem[], o_fistp_modes[], o_cpuid_vendor[], o_rdtsc_mono[], o_lods_sto_d[], o_repne_scasd[], o_pushf_popf[];
static const Fn T[] = {
  {"o_btmem", 'C', 2, {0}, o_btmem},
  {"o_btimm", 'C', 2, {0}, o_btimm},
  {"o_pusha", 'C', 2, {0}, o_pusha},
  {"o_enterleave", 'C', 2, {0}, o_enterleave},
  {"o_xlat", 'C', 2, {0}, o_xlat},
  {"o_cmpxchg8b", 'C', 2, {0}, o_cmpxchg8b},
  {"o_cmpxchg8b_eq", 'C', 2, {0}, o_cmpxchg8b_eq},
  {"o_highbyte", 'C', 2, {0}, o_highbyte},
  {"o_movzx_ah", 'C', 2, {0}, o_movzx_ah},
  {"o_imul3", 'C', 2, {0}, o_imul3},
  {"o_imul16", 'C', 2, {0}, o_imul16},
  {"o_cmovall", 'C', 4, {0}, o_cmovall},
  {"o_shifts_imm", 'C', 2, {0}, o_shifts_imm},
  {"o_mmx", 'C', 2, {0}, o_mmx},
  {"o_fxch", 'C', 2, {0}, o_fxch},
  {"o_fucomi", 'C', 2, {0}, o_fucomi},
  {"o_fprem", 'C', 2, {0}, o_fprem},
  {"o_fistp_modes", 'C', 2, {0}, o_fistp_modes},
  {"o_cpuid_vendor", 'C', 1, {0}, o_cpuid_vendor},
  {"o_rdtsc_mono", 'C', 1, {0}, o_rdtsc_mono},
  {"o_lods_sto_d", 'C', 2, {0}, o_lods_sto_d},
  {"o_repne_scasd", 'C', 2, {0}, o_repne_scasd},
  {"o_pushf_popf", 'C', 2, {0}, o_pushf_popf},
};
ORACLE_MAIN(T, 80)
