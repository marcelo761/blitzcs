using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace BlitzCS.OriginalRuntime;

/// <summary>Direct calls to the original Windows x86 FMOD 3.75 and BlitzMovie libraries.</summary>
public static class NativeMedia
{
    // Keep modules and their delegates alive together for the process lifetime. No global resolver is installed.
    private static readonly ConcurrentDictionary<string, nint> Modules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string, string, Type), Delegate> Functions = new();

    private static T Function<T>(string library, string export) where T : Delegate
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
            throw new PlatformNotSupportedException("Original native media requires Windows .NET x86 (or Proton).");
        var root = Environment.GetEnvironmentVariable("BLITZCS_ORIGINAL_NATIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Set BLITZCS_ORIGINAL_NATIVE_ROOT to an absolute directory containing the original media DLLs.");
        var path = Path.GetFullPath(Path.Combine(root, library));
        return (T)Functions.GetOrAdd((path, export, typeof(T)), key =>
        {
            var module = Modules.GetOrAdd(path, LoadModule);
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, export));
        });
    }

    private static nint LoadModule(string path)
    {
        if (!File.Exists(path))
            throw new DllNotFoundException($"Original native media library is missing: '{path}'. Supply the unchanged original DLL in BLITZCS_ORIGINAL_NATIVE_ROOT.");
        try { return NativeLibrary.Load(path); }
        catch (DllNotFoundException ex)
        {
            throw new DllNotFoundException($"Cannot load original native media library '{path}'. Verify its original Windows x86 dependencies are available. {ex.Message}", ex);
        }
    }

    private static readonly Encoding FilenameEncoding = CreateFilenameEncoding();
    private static Encoding CreateFilenameEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // Match the original-runtime bridge's explicit Windows-1252 convention rather than
    // silently replacing characters through the host's LPStr marshaller or truncating at NUL.
    private static int WithFilename(string filename, Func<nint, int> call)
    {
        ArgumentNullException.ThrowIfNull(filename);
        if (filename.Contains('\0')) throw new ArgumentException("Original media filenames cannot contain NUL.", nameof(filename));
        var bytes = FilenameEncoding.GetBytes(filename + "\0");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return call(pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static T Fmod<T>(string export) where T : Delegate => Function<T>("fmod.dll", export);
    private static T Movie<T>(string export) where T : Delegate => Function<T>("BlitzMovie.dll", export);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate sbyte Bool1(int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate sbyte Bool2(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate sbyte Pause(int channel, sbyte paused);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate sbyte Init(int rate, int channels, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Void0();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Int0();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate float Float0();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Int2(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int StreamOpen(nint name, uint mode, int offset, int length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int MovieOpen(nint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int MovieImage(nint name, int image, int looping);

    public static int FSOUND_Init(int frequency, int channels, int flags) => Fmod<Init>("_FSOUND_Init@12")(frequency, channels, unchecked((uint)flags));
    public static void FSOUND_Close() => Fmod<Void0>("_FSOUND_Close@0")();
    public static int FSOUND_SetOutput(int output) => Fmod<Bool1>("_FSOUND_SetOutput@4")(output);
    public static float FSOUND_GetVersion() => Fmod<Float0>("_FSOUND_GetVersion@0")();
    public static int FSOUND_GetError() => Fmod<Int0>("_FSOUND_GetError@0")();
    public static void FSOUND_SetVolume(int channel, int volume) => Fmod<Bool2>("_FSOUND_SetVolume@8")(channel, volume);
    public static void FSOUND_SetPaused(int channel, int paused) => Fmod<Pause>("_FSOUND_SetPaused@8")(channel, unchecked((sbyte)paused));
    public static void FSOUND_StopSound(int channel) => Fmod<Bool1>("_FSOUND_StopSound@4")(channel);
    public static int FSOUND_IsPlaying(int channel) => Fmod<Bool1>("_FSOUND_IsPlaying@4")(channel);
    public static int FSOUND_SetPan(int channel, int pan) => Fmod<Bool2>("_FSOUND_SetPan@8")(channel, pan);
    public static int FSOUND_Stream_Play(int channel, int stream) => Fmod<Int2>("_FSOUND_Stream_Play@8")(channel, stream);
    public static int FSOUND_Stream_Stop(int stream) => Fmod<Bool1>("_FSOUND_Stream_Stop@4")(stream);
    public static int FSOUND_Stream_Close(int stream) => Fmod<Bool1>("_FSOUND_Stream_Close@4")(stream);
    public static int FSOUND_Stream_Open(string filename, int mode, int offset)
    {
        // The legacy three-argument declaration predates the fourth native length slot.
        // File streams use zero length (whole file); managed strings are never native memory buffers.
        if ((mode & 0x8000) != 0)
            throw new NotSupportedException("FSOUND_LOADMEMORY requires a native memory provider; this adapter accepts file names only.");
        return WithFilename(filename, pointer => Fmod<StreamOpen>("_FSOUND_Stream_Open@16")(pointer, unchecked((uint)mode), offset, 0));
    }

    public static int BlitzMovie_Open(string filename) => WithFilename(filename, pointer => Movie<MovieOpen>("_BLITZMOVIE_Open@4")(pointer));
    public static void BlitzMovie_Close() => Movie<Void0>("_BLITZMOVIE_Close@0")();
    public static int BlitzMovie_GetWidth() => Movie<Int0>("_BLITZMOVIE_GetWidth@0")();
    public static int BlitzMovie_GetHeight() => Movie<Int0>("_BLITZMOVIE_GetHeight@0")();
    public static int BlitzMovie_Play() => Movie<Int0>("_BLITZMOVIE_Play@0")();
    public static int BlitzMovie_Stop() => Movie<Int0>("_BLITZMOVIE_Stop@0")();
    // image is an original-runtime IMAGE handle, forwarded without replacing its representation.
    public static int BlitzMovie_OpenDecodeToImage(string filename, int image, int looping) =>
        WithFilename(filename, pointer => Movie<MovieImage>("_BLITZMOVIE_OpenDecodeToImage@12")(pointer, image, looping));
}
