using Blitz3DCS.Windowing;
using BlitzCS.Compiler;
using Xunit;

namespace BlitzCS.CLI.Tests;

public sealed class LegacyInputBindingTests
{
    private static readonly string[] Names = ["KeyDown", "KeyHit", "GetKey", "WaitKey", "FlushKeys",
        "MouseDown", "MouseHit", "GetMouse", "WaitMouse", "MouseWait", "MouseX", "MouseY", "MouseZ",
        "MouseXSpeed", "MouseYSpeed", "MouseZSpeed", "FlushMouse", "MoveMouse"];
    private static ExternalFunctionRegistry Registry() => new(Names.Select(name =>
        new ExternalFunction(name, typeof(LegacyInput).GetMethod(name)!)));

    [Fact]
    public void Complete_keyboard_mouse_family_emits_direct_calls_without_initializing_video()
    {
        const string source = """
            MoveMouse 37,53
            FlushMouse
            FlushKeys
            Print KeyDown(30)+KeyHit(30)+GetKey()+WaitKey()
            Print MouseDown(1)+MouseHit(1)+GetMouse()+WaitMouse()+MouseWait()
            Print MouseX()+MouseY()+MouseZ()+MouseXSpeed()+MouseYSpeed()+MouseZSpeed()
            """;
        var code = BasicCompiler.Compile(source, "input.bb", Registry());
        foreach (var name in Names) Assert.Contains("@LegacyInput.@" + name, code);
        Assert.DoesNotContain(".Invoke(", code);
    }

    [Theory]
    [InlineData("MoveMouse 1")]
    [InlineData("Print MouseDown()")]
    [InlineData("Print KeyDown(1,2)")]
    public void Historical_required_arguments_are_checked(string source) =>
        Assert.Throws<BasicDiagnosticException>(() => BasicCompiler.Compile(source, "bad-input.bb", Registry()));

    [Fact]
    public void Missing_host_is_an_explicit_error_and_close_is_idempotent()
    {
        Assert.Equal(0, LegacyInput.WindowOpen());
        Assert.Throws<InvalidOperationException>(() => LegacyInput.MoveMouse(1, 2));
        LegacyInput.CloseWindow();
        LegacyInput.CloseWindow();
        Assert.Equal(0, LegacyInput.WindowOpen());
    }
}
