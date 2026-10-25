/* Windows oracle: the library object sits at the exact addresses of the PE fixture (same as oracle.ld); the driver text lives at 0x10010000, driver data at 0x10080000
 * and its kernel32 import table (IAT at 0x10008000) is appended by make_pe.py. The result is converted to a PE32 EXE by make_pe.py. */
SECTIONS
{
  . = 0x10001000;
  .ltext : { *lib.o(.text .text.* .rodata .rodata.*) }
  . = 0x10002000;
  .ldata : { __data_start = .; *lib.o(.data .data.* .bss .bss.* COMMON) __data_end = .; }
  . = 0x10003400;
  .iat : { *(.iat) }
  . = 0x10010000;
  .text : { *(.text .text.*) *(.rodata .rodata.*) }
  . = 0x10080000;
  .data : { *(.data .data.*) *(.bss .bss.*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.eh_frame*) }
}
