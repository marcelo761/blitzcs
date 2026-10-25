#include "common.h"
#include "fs_teb.h"
extern char f_sized[], f_rmw[], f_flagdep[], f_dynops[], f_pushpop[], f_sse[], f_lea[];
static const Fn T[] = {
  {"f_sized", 'C', 1, {0}, f_sized},
  {"f_rmw", 'C', 2, {0}, f_rmw},
  {"f_flagdep", 'C', 2, {0}, f_flagdep},
  {"f_dynops", 'C', 2, {0}, f_dynops},
  {"f_pushpop", 'C', 1, {0}, f_pushpop},
  {"f_sse", 'C', 1, {0}, f_sse},
  {"f_lea", 'C', 2, {0}, f_lea},
};
ORACLE_MAIN_FS(T, 60)
