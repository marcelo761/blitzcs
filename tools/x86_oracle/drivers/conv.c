#include "common.h"
extern char c_cdecl0[], c_cdecl3[], c_cdecl6[], c_std0[], c_std1[], c_std2[], c_std4[], c_std6[], c_cdecl_calls_std[], c_std_calls_cdecl[], c_pressure[], c_std_pressure[];
static const Fn T[] = {
  {"c_cdecl0", 'C', 0, {0}, c_cdecl0},
  {"c_cdecl3", 'C', 3, {0}, c_cdecl3},
  {"c_cdecl6", 'C', 6, {0}, c_cdecl6},
  {"c_std0", 'S', 0, {0}, c_std0},
  {"c_std1", 'S', 1, {0}, c_std1},
  {"c_std2", 'S', 2, {0}, c_std2},
  {"c_std4", 'S', 4, {0}, c_std4},
  {"c_std6", 'S', 6, {0}, c_std6},
  {"c_cdecl_calls_std", 'C', 2, {0}, c_cdecl_calls_std},
  {"c_std_calls_cdecl", 'S', 2, {0}, c_std_calls_cdecl},
  {"c_pressure", 'C', 4, {0}, c_pressure},
  {"c_std_pressure", 'S', 3, {0}, c_std_pressure},
};
ORACLE_MAIN(T, 150)
