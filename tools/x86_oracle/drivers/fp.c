#include "common.h"
extern char x_arith[], x_fsqrt[], x_fabschs[], x_cmp[], x_round[], x_f32[], x_f32bits[], x_fld_consts[], x_fsin[], x_trunc[], x_cw[];
static const Fn T[] = {
  {"x_arith", 'C', 3, {0}, x_arith},
  {"x_fsqrt", 'C', 1, {0}, x_fsqrt},
  {"x_fabschs", 'C', 2, {0}, x_fabschs},
  {"x_cmp", 'C', 2, {0}, x_cmp},
  {"x_round", 'C', 2, {0}, x_round},
  {"x_f32", 'C', 2, {0}, x_f32},
  {"x_f32bits", 'C', 1, {0}, x_f32bits},
  {"x_fld_consts", 'C', 1, {0}, x_fld_consts},
  {"x_fsin", 'C', 1, {0}, x_fsin},
  {"x_trunc", 'C', 2, {0}, x_trunc},
  {"x_cw", 'C', 1, {0}, x_cw},
};
ORACLE_MAIN(T, 80)
