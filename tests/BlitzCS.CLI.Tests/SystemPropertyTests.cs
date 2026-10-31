using Blitz3DCS.Runtime;
using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class SystemPropertyTests
{
    [Fact]
    public void MilliSecs_uses_system_uptime_and_the_Basic_call_is_statically_bound()
    {
        var before = Environment.TickCount;
        var value = LegacySystem.MilliSecs();
        var after = Environment.TickCount;
        // Windows timeGetTime and the system tick clock can have different timer
        // resolutions. Compare modulo 32 bits, including signed wraparound.
        var offset = unchecked((uint)(value - before) + 64u);
        var interval = unchecked((uint)(after - before));
        Assert.True(offset <= interval + 128u, "MilliSecs must use system uptime, not time since the first BASIC call.");
        var registry = new ExternalFunctionRegistry([new ExternalFunction("MilliSecs", typeof(LegacySystem).GetMethod("MilliSecs")!)]);
        var code = BasicCompiler.Compile("Local started = MilliSecs()\nPrint MilliSecs() - started", "clock.bb", registry);
        Assert.Contains("@LegacySystem.@MilliSecs", code);
        Assert.DoesNotContain(".Invoke(", code);
    }

    [Fact]
    public void Real_host_directories_are_case_insensitive_and_keep_legacy_trailing_separator()
    {
        var temp = LegacySystem.SystemProperty("tempdir");
        Assert.Equal(temp, LegacySystem.SystemProperty("TEMPDIR"));
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(temp.Replace('\\', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar));
        Assert.EndsWith("\\", temp);
        Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            LegacySystem.SystemProperty("AppDir").Replace('\\', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(2, LegacyFiles.FileType(temp));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData(" tempdir")]
    [InlineData("tempdir\0ignored")]
    public void Unknown_properties_are_empty_without_trimming_or_truncating(string property) =>
        Assert.Equal("", LegacySystem.SystemProperty(property));

    [Fact]
    public void Cpu_and_os_report_real_host_values()
    {
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), LegacySystem.SystemProperty("CPU"));
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription, LegacySystem.SystemProperty("OS"));
    }

    [Theory]
    [InlineData("AppHWND")]
    [InlineData("AppHInstance")]
    [InlineData("Direct3D7")]
    public void Missing_native_host_does_not_return_fictitious_handles(string property) =>
        Assert.Throws<PlatformNotSupportedException>(() => LegacySystem.SystemProperty(property));

    [Fact]
    public void Compiler_resolves_property_as_string_with_static_external_call()
    {
        var registry = new ExternalFunctionRegistry([new ExternalFunction("SystemProperty", typeof(LegacySystem).GetMethod("SystemProperty")!)]);
        var code = BasicCompiler.Compile("Print SystemProperty(\"tempdir\")", "property.bb", registry);
        Assert.Contains("@LegacySystem.@SystemProperty", code);
        Assert.DoesNotContain(".Invoke(", code);
    }
}
