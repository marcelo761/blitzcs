; Real libogg (ogg.dll, untouched) driven from plain BASIC. Pointers and structs live in checked
; guest blocks (GuestAlloc/GuestPoke*/GuestPeek*, catalog "intrinsic" functions).

; --- bit packer: write 4 fields, read them back ---
b = GuestAlloc(64)
OggPackWriteInit b
OggPackWrite b, 5, 3
OggPackWrite b, 1000, 11
OggPackWrite b, 123456, 20
OggPackWrite b, 1, 1
n = OggPackBytes(b)
Print n
p = OggPackGetBuffer(b)
For i = 0 To n - 1
    Print GuestPeek8(p + i)
Next
r = GuestAlloc(64)
OggPackReadInit r, p, n
Print OggPackRead(r, 3)
Print OggPackRead(r, 11)
Print OggPackRead(r, 20)
Print OggPackRead(r, 1)
OggPackWriteClear b

; --- one page through a stream: packet of 8 bytes, bos+eos ---
st = GuestAlloc(1024)
og = GuestAlloc(16)
op = GuestAlloc(32)
pk = GuestAlloc(8)
For i = 0 To 7
    GuestPoke8 pk + i, i * 10 + 3
Next
If OggStreamInit(st, 305441741) <> 0 Then Print "init failed"
GuestPoke32 op, pk
GuestPoke32 op + 4, 8
GuestPoke32 op + 8, 1
GuestPoke32 op + 12, 1
GuestPoke32 op + 16, 100
GuestPoke32 op + 24, 0
If OggStreamPacketIn(st, op) <> 0 Then Print "packetin failed"
If OggStreamFlush(st, og) = 0 Then Print "flush failed"
h = GuestPeek32(og)
Print GuestPeek32(og + 4)
Print GuestPeek32(og + 12)
Print GuestPeek8(h + 5)
Print OggPageSerialNo(og)
Print OggPagePageNo(og)
Print GuestPeek32(h + 22)
bd = GuestPeek32(og + 8)
For i = 0 To 7
    Print GuestPeek8(bd + i)
Next
OggStreamClear st
