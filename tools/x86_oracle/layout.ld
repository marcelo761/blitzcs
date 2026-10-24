SECTIONS
{
  . = 0x10001000;
  .text : { *(.text .text.*) *(.rodata .rodata.*) }
  . = 0x10002000;
  .data : { *(.data .data.*) *(.bss .bss.*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.eh_frame*) }
}
