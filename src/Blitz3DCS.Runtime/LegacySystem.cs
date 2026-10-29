using BlitzCS.LegacyRuntime;

namespace Blitz3DCS.Runtime;

/// <summary>Host system queries; no graphics or compiler dependency.</summary>
public static class LegacySystem
{
    private static Func<string, string?, int>? _shellExecuteOverride;
    private static Func<int, byte[], uint>? _moduleFileNameOverride;

    /// <summary>Opens a file, URL, or command using the host shell. Returns one when ShellExecute succeeds.</summary>
    public static int ExecFile(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0) return 0;

        var executable = command;
        string? parameters = null;
        while (executable.Length > 0 && executable[0] == ' ')
            executable = executable[1..];
        if (executable.StartsWith('"'))
        {
            var closingQuote = executable.IndexOf('"', 1);
            if (closingQuote >= 0)
            {
                parameters = executable[(closingQuote + 1)..];
                executable = executable[1..closingQuote];
            }
        }
        else
        {
            var firstSpace = executable.IndexOf(' ');
            if (firstSpace >= 0)
            {
                parameters = executable[(firstSpace + 1)..];
                executable = executable[..firstSpace];
            }
        }

        if (parameters is not null)
        {
            parameters = parameters.Trim(' ');
            if (parameters.Length == 0) parameters = null;
        }

        var result = _shellExecuteOverride is { } execute
            ? execute(executable, parameters)
            : ExecuteWithShell(executable, parameters);
        return result > 32 ? 1 : 0;
    }

    internal static Func<string, string?, int>? ShellExecuteOverride
    {
        get => _shellExecuteOverride;
        set => _shellExecuteOverride = value;
    }

    private static int ExecuteWithShell(string command, string? parameters)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ExecFile uses the Windows ShellExecute API.");
        var desktop = GetDesktopWindow();
        SetForegroundWindow(desktop);
        return unchecked((int)ShellExecute(desktop, null, command, parameters, null, 5).ToInt64());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDesktopWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "ShellExecuteW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint ShellExecute(nint window, string? operation, string file, string? parameters, string? directory, int showCommand);

    /// <summary>Copies the current process module path to a bank, matching GetModuleFileNameA's byte count.</summary>
    public static int api_GetModuleFileName(int module, int fileNameBank, int size)
    {
        if (size == 0) return 0;
        var bankSize = LegacyBanks.BankSize(fileNameBank);
        var capacity = (int)Math.Min(unchecked((uint)size), (uint)bankSize);
        if (capacity == 0) return 0;
        if (!OperatingSystem.IsWindows() && _moduleFileNameOverride is null)
            throw new PlatformNotSupportedException("api_GetModuleFileName requires the Windows GetModuleFileNameA API.");

        var bytes = new byte[capacity];
        var count = _moduleFileNameOverride is { } getModuleFileName
            ? getModuleFileName(module, bytes)
            : GetModuleFileNameA((nint)module, bytes, (uint)capacity);
        var copied = (int)Math.Min(count, (uint)capacity);
        var bytesToCopy = copied < capacity ? copied + 1 : copied;
        if (bytesToCopy > 0) LegacyBankStreams.WriteRange(fileNameBank, 0, bytes, bytesToCopy);
        return copied;
    }

    internal static Func<int, byte[], uint>? ModuleFileNameOverride
    {
        get => _moduleFileNameOverride;
        set => _moduleFileNameOverride = value;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameA", ExactSpelling = true)]
    private static extern uint GetModuleFileNameA(nint module, [System.Runtime.InteropServices.Out] byte[] fileName, uint size);

    /// <summary>Returns system uptime in milliseconds, wrapping as a signed 32-bit BASIC integer.</summary>
    public static int MilliSecs() => OperatingSystem.IsWindows()
        ? unchecked((int)TimeGetTime())
        : Environment.TickCount;

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeGetTime", ExactSpelling = true)]
    private static extern uint TimeGetTime();

    /// <summary>Queries real host properties. Native engine handles require a host integration.</summary>
    public static string SystemProperty(string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.ToLowerInvariant() switch
        {
            "tempdir" => DirectoryProperty(Path.GetTempPath()),
            "appdir" => DirectoryProperty(AppContext.BaseDirectory),
            "cpu" => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            "os" => System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            "windowsdir" when OperatingSystem.IsWindows() => DirectoryProperty(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            "systemdir" when OperatingSystem.IsWindows() => DirectoryProperty(Environment.SystemDirectory),
            "windowsdir" or "systemdir" => throw new PlatformNotSupportedException($"SystemProperty '{property}' requires Windows."),
            "apphwnd" or "apphinstance" or "direct3d7" or "direct3ddevice7" or "directdraw7" or "directinput7"
                => throw new PlatformNotSupportedException($"SystemProperty '{property}' requires a native Windows engine host; no handle is available in this runtime."),
            _ => ""
        };
    }

    private static string DirectoryProperty(string path) => Path.TrimEndingDirectorySeparator(path)
        .Replace('/', '\\').TrimEnd('\\') + "\\";

    /// <summary>Reads an environment variable using Windows-compatible case-insensitive names.</summary>
    public static string GetEnv(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var zero = name.IndexOf('\0');
        if (zero >= 0) name = name[..zero];
        if (name.Length == 0 || name.Contains('=')) return "";
        var exact = Environment.GetEnvironmentVariable(name);
        if (exact is not null) return exact;
        var matches = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => string.Equals(entry.Key as string, name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value as string ?? "" : "";
    }
}
