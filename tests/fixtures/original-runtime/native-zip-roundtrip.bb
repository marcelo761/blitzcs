; Real unchanged zlibwapi.dll + original engine banks. Run in an isolated working directory.
source = CreateBank(256)
For i = 0 To 255
 PokeByte source, i, (i * 17) And 255
Next
packed = CreateBank(ZlibWapi_CompressBound(256))
length = CreateBank(4)
PokeInt length, 0, BankSize(packed)
Print ZlibWapi_Compress2(packed, length, source, 256, 6)
packedLength = PeekInt(length, 0)
restored = CreateBank(256)
PokeInt length, 0, 256
Print ZlibWapi_UnCompress(restored, length, packed, packedLength)
Print PeekInt(length, 0)
matched = True
For i = 0 To 255
 If PeekByte(source, i) <> PeekByte(restored, i) Then matched = False
Next
Print matched
Print ZlibWapi_Crc32(0, source, 256) = ZlibWapi_Crc32(0, restored, 256)
Print ZlibWapi_Adler32(1, source, 256) = ZlibWapi_Adler32(1, restored, 256)
archive = ZlibWapi_ZipOpen("native-zip-roundtrip.zip", 0)
Print archive <> 0
Print ZlibWapi_ZipOpenNewFileInZip(archive, "entry.bin", 0, 0, 0, 0, 0, "", 8, 6)
Print ZlibWapi_ZipWriteFileInZip(archive, source, 256)
Print ZlibWapi_ZipCloseFileInZip(archive)
Print ZlibWapi_ZipClose(archive, "roundtrip")
archive = ZlibWapi_UnzOpen("native-zip-roundtrip.zip")
Print archive <> 0
info = CreateBank(8)
Print ZlibWapi_UnzGetGlobalInfo(archive, info)
Print PeekInt(info, 0)
Print ZlibWapi_UnzLocateFile(archive, "entry.bin", 1)
fileInfo = CreateBank(80)
fileName = CreateBank(32)
Print ZlibWapi_UnzGetCurrentFileInfo(archive, fileInfo, fileName, 32, 0, 0, 0, 0)
Print ZlibWapi_UnzOpenCurrentFile(archive)
Print ZlibWapi_UnzReadCurrentFile(archive, restored, 256)
Print ZlibWapi_UnzCloseCurrentFile(archive)
Print ZlibWapi_UnzClose(archive)
matched = True
For i = 0 To 255
 If PeekByte(source, i) <> PeekByte(restored, i) Then matched = False
Next
Print matched
FreeBank fileName
FreeBank fileInfo
FreeBank info
FreeBank restored
FreeBank length
FreeBank packed
FreeBank source
