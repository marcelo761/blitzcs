#include "common.h"
extern char s_bump[], s_calltab[], s_constcall[], s_swapops[], s_fib[], s_ack[], s_gcd[], s_depth[], s_collatz[], s_sw[], s_sw2[], s_sw3[], s_struct[], s_isort[], s_bsearch[], s_matmul[], s_tailcalls[];
static const Fn T[] = {
  {"s_bump", 'C', 2, {0}, s_bump},
  {"s_calltab", 'C', 3, {0}, s_calltab},
  {"s_constcall", 'C', 3, {0}, s_constcall},
  {"s_swapops", 'C', 2, {0}, s_swapops},
  {"s_fib", 'C', 1, {0}, s_fib},
  {"s_ack", 'C', 2, {0}, s_ack},
  {"s_gcd", 'C', 2, {0}, s_gcd},
  {"s_depth", 'C', 1, {0}, s_depth},
  {"s_collatz", 'C', 1, {0}, s_collatz},
  {"s_sw", 'C', 1, {0}, s_sw},
  {"s_sw2", 'C', 1, {0}, s_sw2},
  {"s_sw3", 'C', 1, {0}, s_sw3},
  {"s_struct", 'C', 2, {0}, s_struct},
  {"s_isort", 'C', 1, {0}, s_isort},
  {"s_bsearch", 'C', 1, {0}, s_bsearch},
  {"s_matmul", 'C', 1, {0}, s_matmul},
  {"s_tailcalls", 'C', 2, {0}, s_tailcalls},
};
ORACLE_MAIN(T, 120)
