using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace BlitzCS.OriginalRuntime;

/// <summary>Thin synchronous adapters to the unchanged x86 zlibwapi DLL. All data pointers are original-runtime banks.</summary>
public static class NativeZip
{
    private static readonly Lazy<nint> Library = new(LoadLibrary);
    private static readonly Encoding Ansi = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static void RequireWindowsX86()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("Original zlibwapi bindings require Windows .NET x86 (or Proton).");
    }

    private static nint LoadLibrary()
    {
        RequireWindowsX86();
        var root = Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Set BLITZCS_ORIGINAL_NATIVE_ROOT to the absolute directory containing the unchanged original zlibwapi.dll.");
        // Held for process lifetime: returned ZIP handles refer to code and allocations in this DLL.
        return NativeLibrary.Load(Path.Combine(root, "zlibwapi.dll"));
    }

    private static T Function<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));

    private static void Nonnegative(int value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name, "Native byte lengths must be nonnegative Int32 values.");
    }

    // One allocation per distinct bank preserves aliasing, including aliases across input/output slots.
    // Only public original-engine commands read/write banks; no native bank structure is guessed.
    private sealed class Buffers : IDisposable
    {
        private sealed record Buffer(byte[] Bytes, GCHandle Pin);
        private readonly Dictionary<int, Buffer> banks = new();
        private readonly List<GCHandle> strings = new();
        public Buffers() { RequireWindowsX86(); }

        private Buffer? Get(object value)
        {
            if (value is not int bank) throw new ArgumentException("Native zlib data pointers require an original-runtime bank handle (Int32), or Integer 0 for NULL. BASIC objects are not banks.");
            if (bank == 0) return null;
            if (banks.TryGetValue(bank, out var found)) return found;
            var size = Commands.BankSize(bank);
            if (size < 0) throw new ArgumentException("Original-runtime bank has an invalid size.");
            var bytes = new byte[Math.Max(size, 1)];
            for (var i = 0; i < size; i++) bytes[i] = unchecked((byte)Commands.PeekByte(bank, i));
            var buffer = new Buffer(bytes, GCHandle.Alloc(bytes, GCHandleType.Pinned));
            banks.Add(bank, buffer);
            return buffer;
        }

        public nint Pointer(object value, int length)
        {
            Nonnegative(length, nameof(length));
            var buffer = Get(value);
            if (buffer is null)
            {
                if (length != 0) throw new ArgumentException("NULL bank pointer cannot provide a nonzero byte length.");
                return 0;
            }
            // BankSize, rather than the one-byte allocation for an empty bank, is authoritative.
            if (length > Commands.BankSize((int)value)) throw new ArgumentException("Native byte length exceeds original-runtime bank capacity.");
            return buffer.Pin.AddrOfPinnedObject();
        }

        public nint OptionalStructure(object value, int size) => value is int bank && bank == 0 ? 0 : Pointer(value, size);

        public int OutputCapacity(object lengthBank, object outputBank)
        {
            Pointer(lengthBank, 4);
            var requested = BinaryPrimitives.ReadUInt32LittleEndian(Get(lengthBank)!.Bytes);
            if (requested > int.MaxValue) throw new ArgumentException("Native output length exceeds the supported Int32 bank capacity.");
            Pointer(outputBank, (int)requested);
            return (int)requested;
        }

        public nint String(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Contains('\0')) throw new ArgumentException("Native ANSI strings cannot contain embedded NUL bytes.", nameof(text));
            var bytes = Ansi.GetBytes(text + "\0");
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            strings.Add(pin);
            return pin.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            try
            {
                foreach (var (bank, buffer) in banks)
                {
                    var size = Commands.BankSize(bank);
                    if (size > buffer.Bytes.Length) throw new InvalidOperationException("Original-runtime bank resized during a synchronous native call.");
                    for (var i = 0; i < size; i++) Commands.PokeByte(bank, i, buffer.Bytes[i]);
                }
            }
            finally
            {
                foreach (var buffer in banks.Values) buffer.Pin.Free();
                foreach (var pin in strings) pin.Free();
            }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CompressBoundCall(int sourceLength);

    public static int CompressBound(int sourceLength)
    {
        using var buffers = new Buffers();
        Nonnegative(sourceLength, nameof(sourceLength));
        return Function<CompressBoundCall>("compressBound")(sourceLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Compress2Call(nint destBank, nint destSize, nint sourceBank, int sourceSize, int compressionLevel);

    public static int Compress2(object destBank, object destSize, object sourceBank, int sourceSize, int compressionLevel)
    {
        using var buffers = new Buffers();
        var capacity = buffers.OutputCapacity(destSize, destBank);
        var destBankPointer = buffers.Pointer(destBank, capacity);
        var destSizePointer = buffers.Pointer(destSize, 4);
        var sourceBankPointer = buffers.Pointer(sourceBank, sourceSize);
        return Function<Compress2Call>("compress2")(destBankPointer, destSizePointer, sourceBankPointer, sourceSize, compressionLevel);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnCompressCall(nint destBank, nint destSize, nint sourceBank, int sourceSize);

    public static int UnCompress(object destBank, object destSize, object sourceBank, int sourceSize)
    {
        using var buffers = new Buffers();
        var capacity = buffers.OutputCapacity(destSize, destBank);
        var destBankPointer = buffers.Pointer(destBank, capacity);
        var destSizePointer = buffers.Pointer(destSize, 4);
        var sourceBankPointer = buffers.Pointer(sourceBank, sourceSize);
        return Function<UnCompressCall>("uncompress")(destBankPointer, destSizePointer, sourceBankPointer, sourceSize);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adler32Call(int adler, nint sourceBuffer, int sourceLength);

    public static int Adler32(int adler, object sourceBuffer, int sourceLength)
    {
        using var buffers = new Buffers();
        var sourceBufferPointer = buffers.Pointer(sourceBuffer, sourceLength);
        return Function<Adler32Call>("adler32")(adler, sourceBufferPointer, sourceLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Crc32Call(int crcValue, nint sourceBuffer, int sourceLength);

    public static int Crc32(int crcValue, object sourceBuffer, int sourceLength)
    {
        using var buffers = new Buffers();
        var sourceBufferPointer = buffers.Pointer(sourceBuffer, sourceLength);
        return Function<Crc32Call>("crc32")(crcValue, sourceBufferPointer, sourceLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzOpenCall(nint filePath);

    public static int UnzOpen(string filePath)
    {
        using var buffers = new Buffers();
        var filePathPointer = buffers.String(filePath);
        return Function<UnzOpenCall>("unzOpen")(filePathPointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzCloseCall(int zipHandle);

    public static int UnzClose(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<UnzCloseCall>("unzClose")(zipHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzGetGlobalInfoCall(int zipHandle, nint infoBank);

    public static int UnzGetGlobalInfo(int zipHandle, object infoBank)
    {
        using var buffers = new Buffers();
        var infoBankPointer = buffers.OptionalStructure(infoBank, 8);
        return Function<UnzGetGlobalInfoCall>("unzGetGlobalInfo")(zipHandle, infoBankPointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzGetGlobalCommentCall(int zipHandle, nint commentBank, int commentLength);

    public static int UnzGetGlobalComment(int zipHandle, object commentBank, int commentLength)
    {
        using var buffers = new Buffers();
        var commentBankPointer = buffers.Pointer(commentBank, commentLength);
        return Function<UnzGetGlobalCommentCall>("unzGetGlobalComment")(zipHandle, commentBankPointer, commentLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzLocateFileCall(int zipHandle, nint fileName, int caseSensitive);

    public static int UnzLocateFile(int zipHandle, string fileName, int caseSensitive)
    {
        using var buffers = new Buffers();
        var fileNamePointer = buffers.String(fileName);
        return Function<UnzLocateFileCall>("unzLocateFile")(zipHandle, fileNamePointer, caseSensitive);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzGoToFirstFileCall(int zipHandle);

    public static int UnzGoToFirstFile(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<UnzGoToFirstFileCall>("unzGoToFirstFile")(zipHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzGoToNextFileCall(int zipHandle);

    public static int UnzGoToNextFile(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<UnzGoToNextFileCall>("unzGoToNextFile")(zipHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzGetCurrentFileInfoCall(int zipHandle, nint fileInfo, nint fileName, int fileNameBufferSize, nint extraField, int extraFieldBufferSize, nint comment, int commentBufferSize);

    public static int UnzGetCurrentFileInfo(int zipHandle, object fileInfo, object fileName, int fileNameBufferSize, object extraField, int extraFieldBufferSize, object comment, int commentBufferSize)
    {
        using var buffers = new Buffers();
        var fileInfoPointer = buffers.OptionalStructure(fileInfo, 80);
        var fileNamePointer = buffers.Pointer(fileName, fileNameBufferSize);
        var extraFieldPointer = buffers.Pointer(extraField, extraFieldBufferSize);
        var commentPointer = buffers.Pointer(comment, commentBufferSize);
        return Function<UnzGetCurrentFileInfoCall>("unzGetCurrentFileInfo")(zipHandle, fileInfoPointer, fileNamePointer, fileNameBufferSize, extraFieldPointer, extraFieldBufferSize, commentPointer, commentBufferSize);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzOpenCurrentFileCall(int zipHandle);

    public static int UnzOpenCurrentFile(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<UnzOpenCurrentFileCall>("unzOpenCurrentFile")(zipHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzOpenCurrentFilePasswordCall(int zipHandle, nint password);

    public static int UnzOpenCurrentFilePassword(int zipHandle, string password)
    {
        using var buffers = new Buffers();
        var passwordPointer = buffers.String(password);
        return Function<UnzOpenCurrentFilePasswordCall>("unzOpenCurrentFilePassword")(zipHandle, passwordPointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzReadCurrentFileCall(int zipHandle, nint buffer, int bufferLength);

    public static int UnzReadCurrentFile(int zipHandle, object buffer, int bufferLength)
    {
        using var buffers = new Buffers();
        var bufferPointer = buffers.Pointer(buffer, bufferLength);
        return Function<UnzReadCurrentFileCall>("unzReadCurrentFile")(zipHandle, bufferPointer, bufferLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnzCloseCurrentFileCall(int zipHandle);

    public static int UnzCloseCurrentFile(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<UnzCloseCurrentFileCall>("unzCloseCurrentFile")(zipHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipOpenCall(nint fileName, int appendMode);

    public static int ZipOpen(string fileName, int appendMode)
    {
        using var buffers = new Buffers();
        var fileNamePointer = buffers.String(fileName);
        return Function<ZipOpenCall>("zipOpen")(fileNamePointer, appendMode);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipCloseCall(int zipHandle, nint globalComment);

    public static int ZipClose(int zipHandle, string globalComment)
    {
        using var buffers = new Buffers();
        var globalCommentPointer = buffers.String(globalComment);
        return Function<ZipCloseCall>("zipClose")(zipHandle, globalCommentPointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipOpenNewFileInZipCall(int zipHandle, nint fileName, nint fileInfo, nint extraFieldLocal, int extraFieldLocalSize, nint extraFieldGlobal, int extraFieldGlobalSize, nint comment, int method, int level);

    public static int ZipOpenNewFileInZip(int zipHandle, string fileName, object fileInfo, object extraFieldLocal, int extraFieldLocalSize, object extraFieldGlobal, int extraFieldGlobalSize, string comment, int method, int level)
    {
        using var buffers = new Buffers();
        var fileNamePointer = buffers.String(fileName);
        var fileInfoPointer = buffers.OptionalStructure(fileInfo, 36);
        var extraFieldLocalPointer = buffers.Pointer(extraFieldLocal, extraFieldLocalSize);
        var extraFieldGlobalPointer = buffers.Pointer(extraFieldGlobal, extraFieldGlobalSize);
        var commentPointer = buffers.String(comment);
        return Function<ZipOpenNewFileInZipCall>("zipOpenNewFileInZip")(zipHandle, fileNamePointer, fileInfoPointer, extraFieldLocalPointer, extraFieldLocalSize, extraFieldGlobalPointer, extraFieldGlobalSize, commentPointer, method, level);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipOpenNewFileInZip3Call(int zipHandle, nint fileName, nint fileInfo, nint extraFieldLocal, int extraFieldLocalSize, nint extraFieldGlobal, int extraFieldGlobalSize, nint comment, int method, int level, int raw, int windowBits, int memLevel, int strategy, nint password, int crcForCrypting);

    public static int ZipOpenNewFileInZip3(int zipHandle, string fileName, object fileInfo, object extraFieldLocal, int extraFieldLocalSize, object extraFieldGlobal, int extraFieldGlobalSize, string comment, int method, int level, int raw, int windowBits, int memLevel, int strategy, string password, int crcForCrypting)
    {
        using var buffers = new Buffers();
        var fileNamePointer = buffers.String(fileName);
        var fileInfoPointer = buffers.OptionalStructure(fileInfo, 36);
        var extraFieldLocalPointer = buffers.Pointer(extraFieldLocal, extraFieldLocalSize);
        var extraFieldGlobalPointer = buffers.Pointer(extraFieldGlobal, extraFieldGlobalSize);
        var commentPointer = buffers.String(comment);
        var passwordPointer = buffers.String(password);
        return Function<ZipOpenNewFileInZip3Call>("zipOpenNewFileInZip3")(zipHandle, fileNamePointer, fileInfoPointer, extraFieldLocalPointer, extraFieldLocalSize, extraFieldGlobalPointer, extraFieldGlobalSize, commentPointer, method, level, raw, windowBits, memLevel, strategy, passwordPointer, crcForCrypting);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipWriteFileInZipCall(int zipHandle, nint fileData, int fileLength);

    public static int ZipWriteFileInZip(int zipHandle, object fileData, int fileLength)
    {
        using var buffers = new Buffers();
        var fileDataPointer = buffers.Pointer(fileData, fileLength);
        return Function<ZipWriteFileInZipCall>("zipWriteInFileInZip")(zipHandle, fileDataPointer, fileLength);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ZipCloseFileInZipCall(int zipHandle);

    public static int ZipCloseFileInZip(int zipHandle)
    {
        using var buffers = new Buffers();
        return Function<ZipCloseFileInZipCall>("zipCloseFileInZip")(zipHandle);
    }
}
