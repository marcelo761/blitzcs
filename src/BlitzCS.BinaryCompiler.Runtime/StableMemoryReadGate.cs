using System.Threading;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Coordinates short host memory reads with process-wide guest writers.</summary>
internal sealed class StableMemoryReadGate
{
    private int readers;
    private int writers;
    private long epoch;

    /// <summary>A version that changes as each writer enters and leaves.</summary>
    internal long Epoch => Volatile.Read(ref epoch);

    /// <summary>
    /// Pins a bounded host read if no guest writer is active. A writer entering
    /// after the final check waits for this lease to be released.
    /// </summary>
    internal bool TryEnterRead(out ReadLease lease)
    {
        lease = default;
        Interlocked.Increment(ref readers);
        if (Volatile.Read(ref writers) != 0)
        {
            Interlocked.Decrement(ref readers);
            return false;
        }

        var capturedEpoch = Volatile.Read(ref epoch);
        if (Volatile.Read(ref writers) != 0)
        {
            Interlocked.Decrement(ref readers);
            return false;
        }

        lease = new ReadLease(this, capturedEpoch);
        return true;
    }

    /// <summary>Starts a writer phase and waits only for pinned readers.</summary>
    internal WriteLease EnterWrite()
    {
        Interlocked.Increment(ref writers);
        Interlocked.Increment(ref epoch);

        var spinner = new SpinWait();
        try
        {
            while (Volatile.Read(ref readers) != 0)
                spinner.SpinOnce();
        }
        catch
        {
            // An interrupted waiter never acquired its writer lease. Roll back
            // both the writer count and epoch transition before propagating.
            ExitWrite();
            throw;
        }

        return new WriteLease(this);
    }

    private void ExitRead() => Interlocked.Decrement(ref readers);

    private void ExitWrite()
    {
        Interlocked.Increment(ref epoch);
        Interlocked.Decrement(ref writers);
    }

    internal readonly struct ReadLease : IDisposable
    {
        private readonly StableMemoryReadGate? owner;

        internal ReadLease(StableMemoryReadGate owner, long epoch)
        {
            this.owner = owner;
            Epoch = epoch;
        }

        internal long Epoch { get; }

        public void Dispose() => owner?.ExitRead();
    }

    internal readonly struct WriteLease : IDisposable
    {
        private readonly StableMemoryReadGate? owner;

        internal WriteLease(StableMemoryReadGate owner) => this.owner = owner;

        public void Dispose() => owner?.ExitWrite();
    }
}
