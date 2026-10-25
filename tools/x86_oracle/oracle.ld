/* Oracle ELF: the library object sits at the exact addresses of the PE fixture; the driver lives elsewhere. */
SECTIONS
{
  . = 0x10001000;
  .ltext : { *lib.o(.text .text.* .rodata .rodata.*) }
  . = 0x10002000;
  .ldata : { __data_start = .; *lib.o(.data .data.* .bss .bss.* COMMON) __data_end = .; }
  . = 0x10003400;
  .iat : { *(.iat) }
  . = 0x20000000;
  .text : { *(.text .text.*) *(.rodata .rodata.*) }
  .data : { *(.data .data.*) *(.bss .bss.*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.eh_frame*) }
}
