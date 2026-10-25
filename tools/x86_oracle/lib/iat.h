/* Import slots of the PE fixture: .idata at RVA 0x3000, IAT at +0x400 => VA 0x10003400, 4 bytes each, in declaration order. */
#define CB_STD (*(unsigned (__attribute__((stdcall)) * volatile *)(unsigned, unsigned))0x10003400)
#define CB_C   (*(unsigned (* volatile *)(unsigned, unsigned, unsigned))0x10003404)
