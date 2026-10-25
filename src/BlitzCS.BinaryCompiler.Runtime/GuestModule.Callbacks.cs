namespace BlitzCS.BinaryCompiler.Runtime;

public sealed unsafe partial class GuestModule
{
    private Dictionary<uint, uint>? addressMap;

    /// <summary>
    /// Calls guest code at <paramref name="guestAddress"/> (a virtual address inside this image that the translator discovered as a code entry, for example a function pointer
    /// or window procedure handed to an import) with <paramref name="args"/> on the stack and returns EAX. Callable from inside an import handler (a nested call) or from host code.
    /// The callee's stack cleanup is irrelevant: the runtime discards the whole frame, so cdecl and stdcall targets are both fine. An address the translator did not map fails with
    /// <see cref="GuestTrapException"/> naming it, never with a jump into unknown memory.
    /// </summary>
    public uint CallAddress(uint guestAddress, params ReadOnlySpan<uint> args)
    {
        var rva = guestAddress - artifact.GuestImageBase;
        Dictionary<uint, uint> map;
        lock (resourceLock)
        {
            if (addressMap is null)
            {
                addressMap = new Dictionary<uint, uint>(artifact.AddressMap.Count);
                foreach (var m in artifact.AddressMap) addressMap.TryAdd(m.GuestRva, m.HostOffset);
                foreach (var e in artifact.Exports) addressMap.TryAdd(e.GuestRva, e.HostOffset);
            }
            map = addressMap;
        }
        if (!map.TryGetValue(rva, out var hostOffset))
            throw new GuestTrapException($"The guest asked a callback at 0x{guestAddress:X8}, which is not a translated code entry of this image.");
        return Invoke(hostMap + (nint)hostOffset, args);
    }
}
