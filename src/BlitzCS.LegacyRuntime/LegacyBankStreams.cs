namespace BlitzCS.LegacyRuntime;

/// <summary>Bounded byte-array transfers to and from legacy banks.</summary>
/// <remarks>Copies bytes across the bank boundary so callers never retain a bank span.</remarks>
public static class LegacyBankStreams
{
    public static int TransferLength(int bank, int offset, int requested)
    {
        if (requested <= 0 || offset < 0) return 0;
        var size = LegacyBanks.BankSize(bank);
        return offset >= size ? 0 : Math.Min(requested, size - offset);
    }

    public static byte[] ReadRange(int bank, int offset, int requested)
    {
        var count = TransferLength(bank, offset, requested);
        return count == 0 ? [] : LegacyBanks.WithBytes(bank, offset, count, bytes => bytes.ToArray());
    }

    public static int WriteRange(int bank, int offset, byte[] source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        count = Math.Min(count, source.Length);
        count = TransferLength(bank, offset, count);
        if (count == 0) return 0;
        LegacyBanks.WithBytes(bank, offset, count, bytes => { source.AsSpan(0, count).CopyTo(bytes); return 0; });
        return count;
    }
}
