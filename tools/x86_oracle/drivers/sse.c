#include "common.h"
extern char e_dbl[], e_flt[], e_cmp[], e_cvt[], e_padd[], e_pmul[], e_pcmp[], e_shuf[], e_pmov[], e_pfloat[], e_pdouble[], e_minmax[], e_movd[];
static const Fn T[] = {
  {"e_dbl", 'C', 3, {0}, e_dbl},
  {"e_flt", 'C', 2, {0}, e_flt},
  {"e_cmp", 'C', 2, {0}, e_cmp},
  {"e_cvt", 'C', 2, {0}, e_cvt},
  {"e_padd", 'C', 4, {0}, e_padd},
  {"e_pmul", 'C', 4, {0}, e_pmul},
  {"e_pcmp", 'C', 4, {0}, e_pcmp},
  {"e_shuf", 'C', 4, {0}, e_shuf},
  {"e_pmov", 'C', 2, {0}, e_pmov},
  {"e_pfloat", 'C', 4, {0}, e_pfloat},
  {"e_pdouble", 'C', 2, {0}, e_pdouble},
  {"e_minmax", 'C', 2, {0}, e_minmax},
  {"e_movd", 'C', 2, {0}, e_movd},
};
ORACLE_MAIN(T, 80)
