namespace BlitzCS.Compiler;

/// <summary>Provider requirements and symbol provenance, independent of any engine or executable binding.</summary>
/// <remarks>A runtime registration is not a DLL export. Unknown native conventions must remain null.</remarks>
public sealed class ExternalApiContract
{
    public string Provider { get; }
    public string Resolution { get; }
    public string Symbol { get; }
    public string? Profile { get; }
    public string? CallingConvention { get; }
    public string? Source { get; }

    public ExternalApiContract(string provider, string resolution, string symbol, string? profile = null,
        string? callingConvention = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(resolution) || string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("External contract requires nonempty provider, resolution and symbol metadata.");
        if (callingConvention is not (null or "cdecl" or "stdcall" or "thiscall" or "fastcall" or "managed"))
            throw new ArgumentException("External contract callingConvention must be null (unknown), cdecl, stdcall, thiscall, fastcall or managed.");
        Provider = provider; Resolution = resolution; Symbol = symbol;
        Profile = profile; CallingConvention = callingConvention; Source = source;
    }
}
