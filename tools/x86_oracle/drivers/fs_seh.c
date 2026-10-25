#include "common.h"
#include "fs_teb.h"
extern char f_seh_nest[], f_seh_unwind[], f_stack[], f_self[];
static const Fn T[] = {
  {"f_seh_nest", 'C', 2, {0}, f_seh_nest},
  {"f_seh_unwind", 'C', 2, {0}, f_seh_unwind},
  {"f_stack", 'C', 1, {0}, f_stack},
  {"f_self", 'C', 1, {0}, f_self},
};
ORACLE_MAIN_FS(T, 60)
