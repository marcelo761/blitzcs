namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Verified managed fast paths for selected Blitz3D runtime string commands.</summary>
internal static class VerifiedRuntimeStrings
{
    internal const string RuntimeSha256 = VerifiedBankReader.RuntimeSha256;
    internal const uint ChrRva = 0x3B340;
    private static readonly string[] ChrValues = CreateChrValues();

    private static string[] CreateChrValues()
    {
        var values = new string[256];
        for (var value = 0; value < values.Length; value++)
            values[value] = BinaryLibrary.Ansi.GetString([(byte)value]);
        return values;
    }

    internal static string GetChr(uint value) => ChrValues[unchecked((byte)value)];

    internal static bool IsAsciiStringCommand(uint rva, string name, int count) => (rva, name, count) is
        (239600, "Left", 2) or (239792, "Right", 2) or (240624, "Mid", 3) or
        (240912, "Upper", 1) or (241248, "Lower", 1) or (241584, "Trim", 1);

    internal static bool IsAsciiIntCommand(uint rva, string name, int count) => (rva, name, count) is
        (240320, "Instr", 3) or (242992, "Asc", 1) or (243184, "Len", 1);

    private static bool CanUseAscii(GuestModule module) =>
        VerifiedBankReader.IsSupportedImage(module.Artifact.SourceSha256) && module.CurrentThreadTeb != 0;

    internal static bool TryAsciiString(GuestModule module, string name, ReadOnlySpan<BinaryArgument> arguments, out string value)
    {
        value = "";
        if (!CanUseAscii(module)) return false;
        try { module.EnterCall(); }
        catch (ObjectDisposedException) { return false; }
        try { return VerifiedAsciiStrings.TryCallString(name, arguments, out value); }
        finally { module.ExitCall(); }
    }

    internal static bool TryAsciiInt(GuestModule module, string name, ReadOnlySpan<BinaryArgument> arguments, out int value)
    {
        value = 0;
        if (!CanUseAscii(module)) return false;
        try { module.EnterCall(); }
        catch (ObjectDisposedException) { return false; }
        try { return VerifiedAsciiStrings.TryCallInt(name, arguments, out value); }
        finally { module.ExitCall(); }
    }

    internal static bool IsEligibleChr(string sourceSha256, uint rva, string name, int argumentCount, ReadOnlySpan<BinaryArgument> arguments) =>
        string.Equals(sourceSha256, RuntimeSha256, StringComparison.OrdinalIgnoreCase)
        && rva == ChrRva
        && name == "Chr"
        && argumentCount == 1
        && arguments.Length == 1
        && !arguments[0].IsText
        && !arguments[0].IsFloat
        && !arguments[0].IsPointer;

    internal static bool TryChr(GuestModule module, uint rva, string name, int argumentCount, ReadOnlySpan<BinaryArgument> arguments, out string value)
    {
        value = "";
        return IsEligibleChr(module.Artifact.SourceSha256, rva, name, argumentCount, arguments) &&
            TryCachedChr(module, arguments[0].Bits, out value);
    }

    // The caller must first verify the module fingerprint and numeric command identity.
    internal static bool TryCachedChr(GuestModule module, uint bits, out string value)
    {
        value = "";
        if (module.IsDisposed || module.CurrentThreadTeb == 0)
            return false;

        // This pure result references only permanent managed strings. The disposed
        // check is its lifetime observation; no guest allocation needs a read lock.
        value = GetChr(bits);
        return true;
    }
}
