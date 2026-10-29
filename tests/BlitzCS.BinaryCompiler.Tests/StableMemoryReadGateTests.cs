using BlitzCS.BinaryCompiler.Runtime;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public sealed class StableMemoryReadGateTests
{
    [Fact]
    public async Task WriterWaitsForExistingReaderAndRejectsNewReaders()
    {
        var gate = new StableMemoryReadGate();
        Assert.True(gate.TryEnterRead(out var reader));
        var initialEpoch = reader.Epoch;
        using var entered = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            using (gate.EnterWrite()) entered.Set();
        });

        Assert.True(SpinWait.SpinUntil(() => gate.Epoch != initialEpoch, TimeSpan.FromSeconds(2)));
        Assert.False(entered.Wait(TimeSpan.FromMilliseconds(50)));
        Assert.False(gate.TryEnterRead(out _));

        reader.Dispose();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(initialEpoch + 2, gate.Epoch);
        Assert.True(gate.TryEnterRead(out var after));
        after.Dispose();
    }

    [Fact]
    public void NestedWritersRemainConcurrentAndReadersRejectUntilLastWriterLeaves()
    {
        var gate = new StableMemoryReadGate();
        var startEpoch = gate.Epoch;
        using (gate.EnterWrite())
        {
            Assert.Equal(startEpoch + 1, gate.Epoch);
            Assert.False(gate.TryEnterRead(out _));
            using (gate.EnterWrite())
            {
                Assert.Equal(startEpoch + 2, gate.Epoch);
                Assert.False(gate.TryEnterRead(out _));
            }

            Assert.Equal(startEpoch + 3, gate.Epoch);
            Assert.False(gate.TryEnterRead(out _));
        }

        Assert.Equal(startEpoch + 4, gate.Epoch);
        Assert.True(gate.TryEnterRead(out var reader));
        reader.Dispose();
    }

    [Fact]
    public async Task IndependentWritersDoNotWaitForOneAnother()
    {
        var gate = new StableMemoryReadGate();
        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();

        Task RunWriter() => Task.Run(() =>
        {
            using (gate.EnterWrite())
            {
                started.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            }
        });

        var first = RunWriter();
        var second = RunWriter();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "Both writers should enter without serializing each other.");
            Assert.False(gate.TryEnterRead(out _));
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(gate.TryEnterRead(out var reader));
        reader.Dispose();
    }

    [Fact]
    public void WriterScopeReleasesAndAdvancesEpochWhenBodyThrows()
    {
        var gate = new StableMemoryReadGate();
        var startEpoch = gate.Epoch;

        void ThrowInsideWriter()
        {
            using var writer = gate.EnterWrite();
            throw new InvalidOperationException("test");
        }

        Assert.Throws<InvalidOperationException>((Action)ThrowInsideWriter);

        Assert.Equal(startEpoch + 2, gate.Epoch);
        Assert.True(gate.TryEnterRead(out var reader));
        reader.Dispose();
    }

    [Fact]
    public void InterruptedWriterWaitRollsBackAndAllowsReaders()
    {
        var gate = new StableMemoryReadGate();
        Assert.True(gate.TryEnterRead(out var pinnedReader));
        using var interrupted = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            try
            {
                using var lease = gate.EnterWrite();
            }
            catch (ThreadInterruptedException)
            {
                interrupted.Set();
            }
        }) { IsBackground = true };
        writer.Start();

        try
        {
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != pinnedReader.Epoch, TimeSpan.FromSeconds(2)));
            writer.Interrupt();
            Assert.True(interrupted.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(pinnedReader.Epoch + 2, gate.Epoch);
            Assert.True(gate.TryEnterRead(out var concurrentReader));
            concurrentReader.Dispose();
        }
        finally
        {
            pinnedReader.Dispose();
            writer.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void ReaderLeaseReleasesWhenBodyThrows()
    {
        var gate = new StableMemoryReadGate();

        void ThrowInsideReader()
        {
            Assert.True(gate.TryEnterRead(out var reader));
            using (reader) throw new InvalidOperationException("test");
        }

        Assert.Throws<InvalidOperationException>((Action)ThrowInsideReader);
        using (gate.EnterWrite()) { }
        Assert.True(gate.TryEnterRead(out var after));
        after.Dispose();
    }

    [Fact]
    public async Task ConcurrentReadersAndWritersCompleteWithoutLeakingPins()
    {
        var gate = new StableMemoryReadGate();
        var work = Enumerable.Range(0, 6).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                if ((worker & 1) == 0)
                {
                    if (gate.TryEnterRead(out var reader))
                    {
                        _ = reader.Epoch;
                        reader.Dispose();
                    }
                }
                else
                {
                    using var writer = gate.EnterWrite();
                }
            }
        }));

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(gate.TryEnterRead(out var finalReader));
        finalReader.Dispose();
    }

    [Fact]
    public void DefaultScopesAreSafeToDispose()
    {
        default(StableMemoryReadGate.ReadLease).Dispose();
        default(StableMemoryReadGate.WriteLease).Dispose();

        var gate = new StableMemoryReadGate();
        using (gate.EnterWrite()) { }
        Assert.True(gate.TryEnterRead(out var reader));
        reader.Dispose();
    }
}
