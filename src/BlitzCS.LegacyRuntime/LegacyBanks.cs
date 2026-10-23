using BlitzCS.NativeInterop;

namespace BlitzCS.LegacyRuntime;

/// <summary>Optional legacy bank commands backed by a process-owned managed store.</summary>
/// <remarks>An embedding host must call CloseBanks at the end of its program lifetime.
/// Independent embedded programs can instead bind wrappers over separate BankStore instances.</remarks>
public static class LegacyBanks
{
    private static readonly BankStore Store = new();

    public static int CreateBank(int size = 0) => Store.CreateBank(size);
    public static void FreeBank(int bank) => Store.FreeBank(bank);
    public static int BankSize(int bank) => Store.BankSize(bank);
    public static void ResizeBank(int bank, int size) => Store.ResizeBank(bank, size);
    public static void CopyBank(int sourceBank, int sourceOffset, int destinationBank, int destinationOffset, int count) =>
        Store.CopyBank(sourceBank, sourceOffset, destinationBank, destinationOffset, count);
    public static int PeekByte(int bank, int offset) => Store.PeekByte(bank, offset);
    public static int PeekShort(int bank, int offset) => Store.PeekShort(bank, offset);
    public static int PeekInt(int bank, int offset) => Store.PeekInt(bank, offset);
    public static float PeekFloat(int bank, int offset) => Store.PeekFloat(bank, offset);
    public static void PokeByte(int bank, int offset, int value) => Store.PokeByte(bank, offset, value);
    public static void PokeShort(int bank, int offset, int value) => Store.PokeShort(bank, offset, value);
    public static void PokeInt(int bank, int offset, int value) => Store.PokeInt(bank, offset, value);
    public static void PokeFloat(int bank, int offset, float value) => Store.PokeFloat(bank, offset, value);

    /// <summary>Trusted managed/native binding entry, not a BASIC pointer conversion.</summary>
    public static TResult WithPointer<TResult>(int bank, int offset, int count, Func<nint, int, TResult> operation, int alignment = 1) =>
        Store.WithPointer(bank, offset, count, operation, alignment);

    public static TResult WithBytes<TResult>(int bank, int offset, int count, NativeBufferOperation<TResult> operation) =>
        Store.WithBytes(bank, offset, count, operation);

    public static int OpenBankCount => Store.LiveBankCount;
    public static void CloseBanks() => Store.Clear();
}
