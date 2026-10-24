#include "common.h"
#include "fs_teb.h"
extern char f_tls_static[], f_tls_indexed[], f_lasterror[], f_ids[], f_peb[];
static const Fn T[] = {
  {"f_tls_static", 'C', 1, {0}, f_tls_static},
  {"f_tls_indexed", 'C', 2, {0}, f_tls_indexed},
  {"f_lasterror", 'C', 1, {0}, f_lasterror},
  {"f_ids", 'C', 1, {0}, f_ids},
  {"f_peb", 'C', 1, {0}, f_peb},
};
ORACLE_MAIN_FS(T, 60)
