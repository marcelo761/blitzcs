#include "common.h"
extern char m_fill[], m_hash[], m_hashb[], m_movsb[], m_movsd[], m_movsw[], m_movs_back[], m_stosb[], m_stosd[], m_stosw[], m_scasb[], m_cmpsb[], m_lodsb[], m_setstr[], m_strlen_loop[], m_strlen_scas[], m_strhash[], m_rev[], m_xorsum[], m_crc[], m_wordsum[], m_bytes_to_words[];
static const Fn T[] = {
  {"m_fill", 'C', 1, {0}, m_fill},
  {"m_hash", 'C', 2, {0}, m_hash},
  {"m_hashb", 'C', 2, {0}, m_hashb},
  {"m_movsb", 'C', 3, {0}, m_movsb},
  {"m_movsd", 'C', 3, {0}, m_movsd},
  {"m_movsw", 'C', 3, {0}, m_movsw},
  {"m_movs_back", 'C', 3, {0}, m_movs_back},
  {"m_stosb", 'C', 3, {0}, m_stosb},
  {"m_stosd", 'C', 3, {0}, m_stosd},
  {"m_stosw", 'C', 3, {0}, m_stosw},
  {"m_scasb", 'C', 2, {0}, m_scasb},
  {"m_cmpsb", 'C', 3, {0}, m_cmpsb},
  {"m_lodsb", 'C', 2, {0}, m_lodsb},
  {"m_setstr", 'C', 2, {0}, m_setstr},
  {"m_strlen_loop", 'C', 0, {0}, m_strlen_loop},
  {"m_strlen_scas", 'C', 0, {0}, m_strlen_scas},
  {"m_strhash", 'C', 1, {0}, m_strhash},
  {"m_rev", 'C', 1, {0}, m_rev},
  {"m_xorsum", 'C', 2, {0}, m_xorsum},
  {"m_crc", 'C', 2, {0}, m_crc},
  {"m_wordsum", 'C', 1, {0}, m_wordsum},
  {"m_bytes_to_words", 'C', 1, {0}, m_bytes_to_words},
};
ORACLE_MAIN(T, 100)
