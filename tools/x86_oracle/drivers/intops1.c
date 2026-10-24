#include "common.h"
extern char i_add64hi[], i_add64lo[], i_sub64hi[], i_neg64hi[], i_mul64lo[], i_mul64hi[], i_umulhi[], i_smulhi[], i_smul64lo[], i_udiv64q_lo[], i_udiv64q_hi[], i_udiv64r_lo[], i_udiv64r_hi[], i_udiv[], i_urem[], i_sdiv[], i_srem[], i_div64by32_q[], i_div64by32_r[], i_idiv64by32_q[], i_cmp[], i_sbbmask[], i_ovf_sadd[], i_ovf_ssub[], i_ovf_smul[], i_ovf_uadd[], i_ovf_usub[], i_ovf_umul[], i_cmov[], i_minmax[], i_abs[];
static const Fn T[] = {
  {"i_add64hi", 'C', 4, {0}, i_add64hi}, {"i_add64lo", 'C', 4, {0}, i_add64lo}, {"i_sub64hi", 'C', 4, {0}, i_sub64hi}, {"i_neg64hi", 'C', 2, {0}, i_neg64hi},
  {"i_mul64lo", 'C', 4, {0}, i_mul64lo}, {"i_mul64hi", 'C', 4, {0}, i_mul64hi}, {"i_umulhi", 'C', 2, {0}, i_umulhi}, {"i_smulhi", 'C', 2, {0}, i_smulhi}, {"i_smul64lo", 'C', 2, {0}, i_smul64lo},
  {"i_udiv64q_lo", 'C', 4, {0}, i_udiv64q_lo}, {"i_udiv64q_hi", 'C', 4, {0}, i_udiv64q_hi}, {"i_udiv64r_lo", 'C', 4, {0}, i_udiv64r_lo}, {"i_udiv64r_hi", 'C', 4, {0}, i_udiv64r_hi},
  {"i_udiv", 'C', 2, {0}, i_udiv}, {"i_urem", 'C', 2, {0}, i_urem}, {"i_sdiv", 'C', 2, {0}, i_sdiv}, {"i_srem", 'C', 2, {0}, i_srem},
  {"i_div64by32_q", 'C', 3, {0}, i_div64by32_q}, {"i_div64by32_r", 'C', 3, {0}, i_div64by32_r}, {"i_idiv64by32_q", 'C', 3, {0}, i_idiv64by32_q},
  {"i_cmp", 'C', 2, {0}, i_cmp}, {"i_sbbmask", 'C', 2, {0}, i_sbbmask}, {"i_ovf_sadd", 'C', 2, {0}, i_ovf_sadd}, {"i_ovf_ssub", 'C', 2, {0}, i_ovf_ssub}, {"i_ovf_smul", 'C', 2, {0}, i_ovf_smul},
  {"i_ovf_uadd", 'C', 2, {0}, i_ovf_uadd}, {"i_ovf_usub", 'C', 2, {0}, i_ovf_usub}, {"i_ovf_umul", 'C', 2, {0}, i_ovf_umul}, {"i_cmov", 'C', 4, {0}, i_cmov}, {"i_minmax", 'C', 3, {0}, i_minmax}, {"i_abs", 'C', 1, {0}, i_abs},
};
ORACLE_MAIN(T, 60)
