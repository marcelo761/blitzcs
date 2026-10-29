using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class StableMemoryReadIntegrationTests
{
    private const string RuntimeHash = VerifiedBankReader.RuntimeSha256;

    private static GuestModule Load(FakeGuest fake, GuestProcessEnvironment environment, Func<uint, uint>? hook = null)
    {
        return GuestModule.Load(
            fake.Artifact,
            fake.Image,
            RuntimeLoaderTests.Resolver(hook),
            new GuestModuleOptions { RunEntryPoint = false },
            environment,
            "stable-read-test");
    }

    private static GuestModule Load(FakeGuest fake, Func<uint, uint>? hook = null)
    {
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        return Load(fake, environment, hook);
    }

    [Fact]
    public async Task NativeGuestInvocationWaitsForPinnedHostReader()
    {
        using var module = Load(FakeGuest.Create(withImport: false, withEntry: false));
        var gate = Assert.IsType<StableMemoryReadGate>(module.Environment.StableReads);
        var add = module.Bind("add", GuestCallingConvention.Cdecl, 2);
        Assert.Equal(0u, add.Call(0, 0)); // Create this thread's TEB before pinning.
        Assert.True(gate.TryEnterRead(out var reader));
        var before = reader.Epoch;
        var call = Task.Run(() => add.Call(20, 22));

        try
        {
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != before, TimeSpan.FromSeconds(2)));
            Assert.False(call.IsCompleted);
            var admitted = gate.TryEnterRead(out var unexpectedReader);
            if (admitted) unexpectedReader.Dispose();
            Assert.False(admitted);
        }
        finally
        {
            reader.Dispose();
        }

        Assert.Equal(42u, await call.WaitAsync(TimeSpan.FromSeconds(2)));
        var delta = gate.Epoch - before;
        Assert.True(delta >= 2);
        Assert.Equal(0, delta & 1);
    }

    [Fact]
    public void NestedImportCallbackCannotAcquireHostReadLease()
    {
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        bool? admitted = null;
        var fake = FakeGuest.Create(withImport: true, withEntry: false);
        using var module = Load(fake, environment, x =>
        {
            if (x == 77)
            {
                Assert.False(GuestModule.Current is null);
                var gate = environment.StableReads!;
                admitted = gate.TryEnterRead(out var reader);
                if (admitted.Value) reader.Dispose();
            }
            return x;
        });

        Assert.Equal(77u, module.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(77));
        Assert.False(admitted);
    }

    [Fact]
    public async Task PausedWaitAdmitsReadsAndNestedCallbacksReacquireWriter()
    {
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        var gate = Assert.IsType<StableMemoryReadGate>(environment.StableReads);
        var fake = FakeGuest.Create(withImport: true, withEntry: false);
        using var paused = new ManualResetEventSlim();
        using var returnFromWait = new ManualResetEventSlim();
        using var readerPinned = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        Task? readerTask = null;
        var readAdmittedWhilePaused = false;
        var callbackRejectedRead = false;
        var readAdmittedAfterCallback = false;
        var readRejectedAfterResume = false;

        using var module = Load(fake, environment, value =>
        {
            var current = GuestModule.Current!;
            if (value == 88)
            {
                callbackRejectedRead = !gate.TryEnterRead(out var unexpected);
                if (!callbackRejectedRead) unexpected.Dispose();
                return value;
            }

            using (current.SuspendMemoryWriterForHostWait())
            {
                readAdmittedWhilePaused = gate.TryEnterRead(out var duringPause);
                if (readAdmittedWhilePaused) duringPause.Dispose();

                current.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(88);
                readAdmittedAfterCallback = gate.TryEnterRead(out var afterCallback);
                if (readAdmittedAfterCallback) afterCallback.Dispose();

                readerTask = Task.Run(() =>
                {
                    if (!gate.TryEnterRead(out var reader)) return;
                    using (reader)
                    {
                        readerPinned.Set();
                        releaseReader.Wait();
                    }
                });
                Assert.True(readerPinned.Wait(TimeSpan.FromSeconds(2)));
                paused.Set();
                Assert.True(returnFromWait.Wait(TimeSpan.FromSeconds(2)));
            }

            readRejectedAfterResume = !gate.TryEnterRead(out var resumed);
            if (!readRejectedAfterResume) resumed.Dispose();
            return value;
        });

        var guestCall = Task.Run(() => module.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(77));
        try
        {
            Assert.True(paused.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(readAdmittedWhilePaused);
            Assert.True(callbackRejectedRead);
            Assert.True(readAdmittedAfterCallback);

            var beforeResume = gate.Epoch;
            returnFromWait.Set();
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != beforeResume, TimeSpan.FromSeconds(2)));
            Assert.False(guestCall.IsCompleted); // Resume cannot pass the host reader pin.
        }
        finally
        {
            returnFromWait.Set();
            releaseReader.Set();
        }

        await readerTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(77u, await guestCall.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(readRejectedAfterResume);
    }

    [Fact]
    public async Task InterruptedWaitResumeAbandonsGuestCallWithoutLeakingWriter()
    {
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        var gate = Assert.IsType<StableMemoryReadGate>(environment.StableReads);
        var fake = FakeGuest.Create(withImport: true, withEntry: false);
        using var paused = new ManualResetEventSlim();
        using var returnFromWait = new ManualResetEventSlim();
        using var readerPinned = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        Thread? guestThread = null;
        Task? readerTask = null;

        using var module = Load(fake, environment, value =>
        {
            guestThread = Thread.CurrentThread;
            using (GuestModule.Current!.SuspendMemoryWriterForHostWait())
            {
                readerTask = Task.Run(() =>
                {
                    if (!gate.TryEnterRead(out var reader)) return;
                    using (reader)
                    {
                        readerPinned.Set();
                        releaseReader.Wait();
                    }
                });
                Assert.True(readerPinned.Wait(TimeSpan.FromSeconds(2)));
                paused.Set();
                Assert.True(returnFromWait.Wait(TimeSpan.FromSeconds(2)));
            }
            return value;
        });

        var before = gate.Epoch;
        var guestCall = Task.Run(() => module.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(77));
        try
        {
            Assert.True(paused.Wait(TimeSpan.FromSeconds(2)));
            returnFromWait.Set();
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != before, TimeSpan.FromSeconds(2)));
            Thread.Sleep(50); // Let the resume writer reach its reader wait before interrupting it.
            guestThread!.Interrupt();
            var completed = await Task.WhenAny(guestCall, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(guestCall, completed);
            await Assert.ThrowsAnyAsync<Exception>(async () => await guestCall);
        }
        finally
        {
            returnFromWait.Set();
            releaseReader.Set();
        }

        await readerTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gate.TryEnterRead(out var afterFailure));
        afterFailure.Dispose();
    }

    [Fact]
    public async Task HostWriteProtectionAndRemovalWaitForReadersAndAdvanceEpoch()
    {
        using var module = Load(FakeGuest.Create(withImport: false, withEntry: false));
        var gate = module.Environment.StableReads!;
        var buffer = module.Memory.Allocate(4096);

        await AssertWriterWaitsForReader(gate, () => module.Memory.WriteByte(buffer.Address, 0x5A));
        Assert.Equal((byte)0x5A, module.Memory.ReadByte(buffer.Address));

        await AssertWriterWaitsForReader(gate, () =>
            Assert.True(module.Memory.TryProtect(buffer.Address, 4096, MemoryProtection.Read, 7, out var refusal), refusal));
        Assert.True(module.Memory.TryProtect(buffer.Address, 4096, MemoryProtection.ReadWrite, 8, out var restoreRefusal), restoreRefusal);

        await AssertWriterWaitsForReader(gate, buffer.Dispose);
        Assert.Throws<GuestMemoryException>(() => module.Memory.ReadByte(buffer.Address));
    }

    [Fact]
    public async Task NewMappingsAndGuestPageMetadataChangesWaitForReaders()
    {
        using var module = Load(FakeGuest.Create(withImport: false, withEntry: false));
        var gate = module.Environment.StableReads!;
        GuestBuffer? allocated = null;

        await AssertWriterWaitsForReader(gate, () => allocated = module.Memory.Allocate(4096));
        Assert.NotNull(allocated);
        await AssertWriterWaitsForReader(gate, () => module.Memory.SetGuestExecutable(allocated!.Address, 4096, true));
        allocated!.Dispose();
    }

    [Fact]
    public void LateConfigurationDoesNotEnableGateAfterModuleLoad()
    {
        var fake = FakeGuest.Create(withImport: false, withEntry: false);
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        using var module = GuestModule.Load(
            fake.Artifact,
            fake.Image,
            RuntimeLoaderTests.Resolver(),
            new GuestModuleOptions { RunEntryPoint = false },
            environment,
            "late-config");

        Assert.Null(environment.StableReads);
        environment.ConfigureStableReads(RuntimeHash);
        Assert.Null(environment.StableReads);
    }

    [Theory]
    [InlineData(0, 16, false)]
    [InlineData(32, 31, false)]
    [InlineData(32, 32, true)]
    public void RememberCachesOnlyPositiveNativeSizeMatchingMappedHeader(uint nativeSize, uint headerSize, bool shouldCache)
    {
        using var module = LoadBankSizeFixture(nativeSize);
        using var bank = module.Memory.Allocate(16);
        using var data = module.Memory.Allocate(32);
        data.Write(0, Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        module.Memory.WriteUInt32(bank.Address, 0x12345678); // opaque native bank object/vtable
        module.Memory.WriteUInt32(bank.Address + 4, data.Address);
        module.Memory.WriteUInt32(bank.Address + 8, headerSize);
        module.Memory.WriteUInt32(bank.Address + 12, 32);
        module.InvokeRva(0x2F9E0, "BankSize", GuestReturn.Eax, [bank.Address]); // Warm the TEB before measuring the validation call.
        var reader = new VerifiedBankReader();

        reader.Remember(module, bank.Address);

        var hit = reader.TryRead(module, bank.Address, 5, out var value);
        Assert.Equal(shouldCache, hit);
        if (shouldCache) Assert.Equal(6u, value);
    }

    private static async Task AssertWriterWaitsForReader(StableMemoryReadGate gate, Action mutation)
    {
        Assert.True(gate.TryEnterRead(out var reader));
        var before = reader.Epoch;
        var work = Task.Run(mutation);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != before, TimeSpan.FromSeconds(2)));
            Assert.False(work.Wait(TimeSpan.FromMilliseconds(40)));
            var admitted = gate.TryEnterRead(out var unexpectedReader);
            if (admitted) unexpectedReader.Dispose();
            Assert.False(admitted);
        }
        finally
        {
            reader.Dispose();
        }

        await work.WaitAsync(TimeSpan.FromSeconds(2));
        var delta = gate.Epoch - before;
        Assert.True(delta >= 2);
        Assert.Equal(0, delta & 1);
    }

    private static GuestModule LoadBankSizeFixture(uint returnSize)
    {
        var fake = FakeGuest.Create(withImport: false, withEntry: false);
        var (host, codeSize, contextOffset, enterOffset, bankSizeOffset) = CreateBankSizeHost(returnSize);
        var artifact = fake.Artifact with
        {
            HostImage = host,
            CodeSize = codeSize,
            ImportThunkTableOffset = checked((uint)contextOffset + 64),
            ContextOffset = (uint)contextOffset,
            EnterGuestOffset = (uint)enterOffset,
            AddressMap = [new AddressMapEntry(0x2F9E0, (uint)bankSizeOffset)],
            TargetOs = OperatingSystem.IsWindows() ? BinaryTargets.WindowsX64 : BinaryTargets.LinuxSysV,
        };
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        return GuestModule.Load(artifact, fake.Image, RuntimeLoaderTests.Resolver(), new GuestModuleOptions { RunEntryPoint = false }, environment, "bank-size-test");
    }

    private static (byte[] Host, int CodeSize, int ContextOffset, int EnterOffset, int BankSizeOffset) CreateBankSizeHost(uint returnSize)
    {
        var win = OperatingSystem.IsWindows();
        (byte[] Host, int Context, int Enter, int BankSize) Assemble(int contextPadding)
        {
            var assembler = new Assembler(64);
            var labels = new Dictionary<string, Label>();
            Label LabelFor(string name) => labels.TryGetValue(name, out var label) ? label : labels[name] = assembler.CreateLabel(name);
            void Mark(string name)
            {
                var label = LabelFor(name);
                assembler.Label(ref label);
                labels[name] = label;
            }
            AssemblerMemoryOperand Context() => __qword_ptr[LabelFor("context")];

            Mark("enter");
            assembler.push(rbx); assembler.push(rbp);
            if (win) { assembler.push(rdi); assembler.push(rsi); }
            assembler.push(r12); assembler.push(r13); assembler.push(r14); assembler.push(r15);
            assembler.push(Context());
            assembler.mov(Context(), rsp);
            if (win) { assembler.mov(esp, edx); assembler.jmp(rcx); }
            else { assembler.mov(esp, esi); assembler.jmp(rdi); }

            Mark("exit");
            assembler.mov(rsp, Context());
            assembler.pop(Context());
            assembler.pop(r15); assembler.pop(r14); assembler.pop(r13); assembler.pop(r12);
            if (win) { assembler.pop(rsi); assembler.pop(rdi); }
            assembler.pop(rbp); assembler.pop(rbx);
            assembler.ret();

            Mark("bank-size");
            assembler.mov(eax, returnSize);
            assembler.jmp(LabelFor("exit"));

            assembler.db(new byte[contextPadding]);
            Mark("context");
            assembler.db(new byte[0x1000]);
            var stream = new MemoryStream();
            var result = assembler.Assemble(new StreamCodeWriter(stream), 0, BlockEncoderOptions.ReturnNewInstructionOffsets);
            return (stream.ToArray(), checked((int)result.GetLabelRIP(LabelFor("context"))), checked((int)result.GetLabelRIP(LabelFor("enter"))),
                checked((int)result.GetLabelRIP(LabelFor("bank-size"))));
        }
        var first = Assemble(0);
        if (first.Context >= 4096) throw new InvalidOperationException("Synthetic host code unexpectedly exceeds one page.");
        var final = Assemble(4096 - first.Context);
        return (final.Host, 4096, final.Context, final.Enter, final.BankSize);
    }

    [Fact]
    public void HostMutationDuringPausedGuestWaitStillAcquiresWriter()
    {
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(RuntimeHash);
        var gate = Assert.IsType<StableMemoryReadGate>(environment.StableReads);
        using var readerPinned = new ManualResetEventSlim();
        using var releaseReader = new ManualResetEventSlim();
        GuestModule? module = null;
        uint address = 0;
        var mutationEpochDelta = 0L;
        using var loaded = Load(FakeGuest.Create(withImport: true, withEntry: false), environment, value =>
        {
            if (value != 77) return value;
            using (GuestModule.Current!.SuspendMemoryWriterForHostWait())
            {
                var readerTask = Task.Run(() =>
                {
                    if (!gate.TryEnterRead(out var reader)) return;
                    using (reader)
                    {
                        readerPinned.Set();
                        releaseReader.Wait();
                    }
                });
                Assert.True(readerPinned.Wait(TimeSpan.FromSeconds(2)));
                var before = gate.Epoch;
                var releaseTask = Task.Run(() =>
                {
                    Thread.Sleep(50);
                    releaseReader.Set();
                });
                module!.Memory.WriteByte(address, 0xA5);
                mutationEpochDelta = gate.Epoch - before;
                Assert.True(readerTask.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(releaseTask.Wait(TimeSpan.FromSeconds(2)));
            }
            return value;
        });
        module = loaded;
        using var buffer = module.Memory.Allocate(4);
        address = buffer.Address;

        Assert.Equal(77u, module.Bind("callimp", GuestCallingConvention.Cdecl, 1).Call(77));
        Assert.Equal(0xA5u, module.Memory.ReadByte(address));
        Assert.Equal(2, mutationEpochDelta);
    }
}
