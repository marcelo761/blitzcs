#include "common.h"
extern char add32[], mulhi[], sdiv[], fnv[], fib[], bump[], fillbuf[], sw[], ovf[];
static const Fn T[] = {
  {"add32", 'C', 2, {0}, add32}, {"mulhi", 'C', 2, {0}, mulhi}, {"sdiv", 'C', 2, {0, 1000}, sdiv}, {"fib", 'C', 1, {20}, fib}, {"bump", 'C', 1, {0}, bump},
  {"fillbuf", 'C', 1, {0}, fillbuf}, {"sw", 'C', 1, {0}, sw}, {"ovf", 'C', 2, {0}, ovf},
};
ORACLE_MAIN(T, 40)
