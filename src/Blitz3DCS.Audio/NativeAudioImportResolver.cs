using System.Reflection;
using System.Runtime.InteropServices;

namespace Blitz3DCS.Audio;

/// <summary>Maps the Unix sonames used in P/Invoke declarations to packaged Windows DLLs.</summary>
internal static class NativeAudioImportResolver
{
    private static readonly object Gate = new();
    private static bool _installed;

    internal static void EnsureInstalled()
    {
        if (_installed) return;
        lock (Gate)
        {
            if (_installed) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeAudioImportResolver).Assembly, Resolve);
            _installed = true;
        }
    }

    internal static string? WindowsFileName(string importName) => importName switch
    {
        "libSDL3.so.0" => "SDL3.dll",
        "libsndfile.so.1" => "sndfile.dll",
        _ => null,
    };

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var fileName = WindowsFileName(libraryName);
        if (fileName is null) return 0;

        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            return NativeLibrary.Load(path);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
        {
            throw new DllNotFoundException(
                $"Could not load the packaged {fileName} audio runtime at '{path}'. Install the matching Windows native runtime files beside the application.",
                error);
        }
    }
}
