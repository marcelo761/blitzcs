namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A host-side read cache for one identified runtime's opaque bank API.</summary>
internal sealed unsafe class VerifiedBankReader
{
    internal const string RuntimeSha256 = "ed61cd11cfae77f9f9c47c2ec2e5aa778ff79ddc5ee2f4f7353db4ce6006f39d";
    private const uint BankSizeRva = 0x2F9E0;
    private sealed record Snapshot(GuestModule Module, uint Bank, uint Data, uint Size, long Epoch);
    private Snapshot? snapshot;

    internal static bool IsSupportedImage(string sourceSha256) =>
        string.Equals(sourceSha256, RuntimeSha256, StringComparison.OrdinalIgnoreCase);

    internal bool TryRead(GuestModule module, uint bank, uint offset, out uint value)
    {
        value = 0;
        // The normal entry establishes thread-local guest state and DLL thread notifications.
        if (module.IsDisposed || module.CurrentThreadTeb == 0) return false;
        var cached = Volatile.Read(ref snapshot);
        if (cached is null || cached.Module != module || cached.Bank != bank || offset >= cached.Size || offset > int.MaxValue)
            return false;
        var gate = module.Environment.StableReads;
        if (gate is null) return false;
        if (!gate.TryEnterRead(out var lease)) return false;
        using (lease)
        {
            // Teardown holds the process writer lease through the actual unmap.
            // This read pin therefore protects the payload's lifetime as well as its contents.
            if (lease.Epoch != cached.Epoch) return false;
            value = *(byte*)(nuint)(cached.Data + offset);
            return true;
        }
    }

    internal void Remember(GuestModule module, uint bank)
    {
        var gate = module.Environment.StableReads;
        if (gate is null) return;
        try { module.EnterCall(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            long before;
            if (!gate.TryEnterRead(out var beforeLease)) return;
            using (beforeLease)
            {
                if (!module.Memory.IsMapped(bank, 16)) return;
                before = beforeLease.Epoch;
            }

            // Keep the registry check native. Invalid handles can return zero rather than throw.
            // Never enter guest code while holding a read pin: the guest entry is a writer.
            var size = module.InvokeRva(BankSizeRva, "BankSize", GuestReturn.Eax, [bank]).Eax;
            if (size == 0 || size > int.MaxValue || !gate.TryEnterRead(out var afterLease)) return;
            using (afterLease)
            {
                // Only that validation call may have intervened. A concurrent writer or a
                // nested host mutation makes the observation unsuitable for reuse.
                if (afterLease.Epoch != unchecked(before + 2)) return;
                var memory = module.Memory;
                if (!memory.IsMapped(bank, 16) || memory.ReadUInt32(bank + 8) != size) return;
                var data = memory.ReadUInt32(bank + 4);
                if ((ulong)data + size > 0x1_0000_0000UL || !memory.IsMapped(data, size)) return;
                if (memory.FindRegion(bank, out _) is not { Kind: GuestPageKind.Buffer } ||
                    memory.FindRegion(data, out _) is not { Kind: GuestPageKind.Buffer }) return;
                Volatile.Write(ref snapshot, new Snapshot(module, bank, data, size, afterLease.Epoch));
            }
        }
        catch (GuestException) { } // An optional cache must not change a completed command's result.
        finally { module.ExitCall(); }
    }
}
