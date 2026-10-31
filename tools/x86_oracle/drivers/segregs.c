#include "common.h"
extern char sg_cs16[], sg_ss16[], sg_ds16[], sg_es16[], sg_gs16[], sg_fs16[];
extern char sg_cs32[], sg_ss32[], sg_ds32[], sg_es32[], sg_gs32[], sg_fs32[];
extern char sg_push_pop_ds[], sg_store_ds_mem[], sg_reload_same[], sg_or_all[], sg_or_four[];
static const Fn T[] = {
  {"sg_cs16", 'C', 0, {0}, sg_cs16}, {"sg_ss16", 'C', 0, {0}, sg_ss16}, {"sg_ds16", 'C', 0, {0}, sg_ds16},
  {"sg_es16", 'C', 0, {0}, sg_es16}, {"sg_gs16", 'C', 0, {0}, sg_gs16}, {"sg_fs16", 'C', 0, {0}, sg_fs16},
  {"sg_cs32", 'C', 0, {0}, sg_cs32}, {"sg_ss32", 'C', 0, {0}, sg_ss32}, {"sg_ds32", 'C', 0, {0}, sg_ds32},
  {"sg_es32", 'C', 0, {0}, sg_es32}, {"sg_gs32", 'C', 0, {0}, sg_gs32}, {"sg_fs32", 'C', 0, {0}, sg_fs32},
  {"sg_push_pop_ds", 'C', 0, {0}, sg_push_pop_ds}, {"sg_store_ds_mem", 'C', 0, {0}, sg_store_ds_mem},
  {"sg_reload_same", 'C', 0, {0}, sg_reload_same}, {"sg_or_all", 'C', 0, {0}, sg_or_all}, {"sg_or_four", 'C', 0, {0}, sg_or_four},
};
__attribute__((force_align_arg_pointer, noreturn)) void _start(void) { run_all(T, (int)(sizeof T / sizeof T[0]), 0); finish(); }
