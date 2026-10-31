using BlitzCS.BinaryCompiler.Runtime;
using BlitzCS.BinaryCompiler.Tests.Support;
using Iced.Intel;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace BlitzCS.BinaryCompiler.Tests;

[Collection("GuestRuntime")]
public sealed class VerifiedBankReaderTests
{
    private static GuestModule Load()
    {
        var fake = FakeGuest.Create(withImport: false, withEntry: false);
        var host = (byte[])fake.Artifact.HostImage.Clone();
        const uint offset = 0xF80;
        // Synthetic validator: return the fixture header size through the normal guest-entry path.
        var a = new Assembler(64);
        a.mov(eax, __dword_ptr[rsp + 4]);
        a.mov(eax, __dword_ptr[rax + 8]);
        a.jmp((ulong)fake.Artifact.Exports.Single(e => e.Name == "mystery").HostOffset + 2);
        using var code = new MemoryStream();
        a.Assemble(new StreamCodeWriter(code), offset);
        code.ToArray().CopyTo(host, (int)offset);
        var artifact = fake.Artifact with
        {
            HostImage = host,
            AddressMap = [new AddressMapEntry(0x2F9E0, offset)],
        };
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(VerifiedBankReader.RuntimeSha256);
        var module = GuestModule.Load(artifact, fake.Image, RuntimeLoaderTests.Resolver(),
            new GuestModuleOptions { RunEntryPoint = false }, environment, "bank-cache-test");
        module.Bind("add", GuestCallingConvention.Cdecl, 2).Call(0, 0);
        return module;
    }

    private static (GuestBuffer Header, GuestBuffer Data) Bank(GuestModule module)
    {
        var header = module.Memory.Allocate(16);
        var data = module.Memory.Allocate(4);
        module.Memory.WriteUInt32(header.Address + 4, data.Address);
        module.Memory.WriteUInt32(header.Address + 8, 4);
        data.Write(0, [10, 20, 30, 40]);
        return (header, data);
    }

    [Fact]
    public void ValidatedBytesAreReusedOnlyWithinTheValidatedRange()
    {
        using var module = Load();
        var (header, data) = Bank(module);
        using (header) using (data)
        {
            var reader = new VerifiedBankReader();
            reader.Remember(module, header.Address);
            Assert.True(reader.TryRead(module, header.Address, 3, out var value));
            Assert.Equal(40u, value);
            Assert.False(reader.TryRead(module, header.Address, 4, out _));
            Assert.False(reader.TryRead(module, header.Address, uint.MaxValue, out _));
            Assert.False(reader.TryRead(module, header.Address + 1, 0, out _));
        }
    }

    [Fact]
    public void NativeValidatorHostWritesAreCoveredByItsOuterGuestWriterLease()
    {
        var fake = FakeGuest.Create(withImport: true, withEntry: false);
        var host = (byte[])fake.Artifact.HostImage.Clone();
        const uint offset = 0xF80;
        var a = new Assembler(64);
        a.jmp((ulong)fake.Artifact.Exports.Single(e => e.Name == "callimp").HostOffset);
        using var code = new MemoryStream();
        a.Assemble(new StreamCodeWriter(code), offset);
        code.ToArray().CopyTo(host, (int)offset);
        var artifact = fake.Artifact with
        {
            HostImage = host,
            AddressMap = [new AddressMapEntry(0x2F9E0, offset)],
        };
        var environment = new GuestProcessEnvironment(ownedBySet: false);
        environment.ConfigureStableReads(VerifiedBankReader.RuntimeSha256);
        GuestModule? module = null;
        var writes = 0;
        var allReadersRejected = true;
        using var loaded = GuestModule.Load(artifact, fake.Image, RuntimeLoaderTests.Resolver(bank =>
        {
            var current = module!;
            var gate = current.Environment.StableReads!;
            allReadersRejected &= !gate.TryEnterRead(out var reader);
            if (!allReadersRejected) reader.Dispose();
            for (uint i = 0; i < 18; i++)
            {
                current.Memory.WriteUInt32(bank + 12, i);
                writes++;
            }
            return current.Memory.ReadUInt32(bank + 8);
        }), new GuestModuleOptions { RunEntryPoint = false }, environment, "bank-cache-nested-write-test");
        module = loaded;
        module.Bind("add", GuestCallingConvention.Cdecl, 2).Call(0, 0);
        var (header, data) = Bank(module);
        using (header) using (data)
        {
            var reader = new VerifiedBankReader();
            var before = environment.StableReads!.Epoch;
            reader.Remember(module, header.Address);
            Assert.Equal(before + 2, environment.StableReads.Epoch);
            Assert.Equal(18, writes);
            Assert.True(allReadersRejected);
            Assert.True(reader.TryRead(module, header.Address, 0, out var value));
            Assert.Equal(10u, value);
        }
    }

    [Fact]
    public void HostWriteAndGuestEntryInvalidateTheSnapshot()
    {
        using var module = Load();
        var (header, data) = Bank(module);
        using (header) using (data)
        {
            var reader = new VerifiedBankReader();
            reader.Remember(module, header.Address);
            module.Memory.WriteByte(data.Address, 99);
            Assert.False(reader.TryRead(module, header.Address, 0, out _));
            reader.Remember(module, header.Address);
            Assert.True(reader.TryRead(module, header.Address, 0, out var value));
            Assert.Equal(99u, value);
            module.Bind("add", GuestCallingConvention.Cdecl, 2).Call(1, 2);
            Assert.False(reader.TryRead(module, header.Address, 0, out _));
        }
    }

    [Fact]
    public void ReleasedPayloadCannotBeDereferenced()
    {
        using var module = Load();
        var (header, data) = Bank(module);
        using (header)
        {
            var reader = new VerifiedBankReader();
            reader.Remember(module, header.Address);
            data.Dispose();
            Assert.False(reader.TryRead(module, header.Address, 0, out _));
            reader.Remember(module, header.Address);
            Assert.False(reader.TryRead(module, header.Address, 0, out _));
        }
    }

    [Fact]
    public void BufferDisposeAfterConfiguredEnvironmentTeardownRemainsIdempotent()
    {
        using var module = Load();
        var buffer = module.Memory.Allocate(8);
        module.Dispose();
        buffer.Dispose();
    }

    [Fact]
    public async Task ProcessReadPinAlonePreventsModuleTeardown()
    {
        var module = Load();
        var gate = module.Environment.StableReads!;
        Assert.True(gate.TryEnterRead(out var pin));
        var epoch = pin.Epoch;
        var teardown = Task.Run(module.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => gate.Epoch != epoch, TimeSpan.FromSeconds(2)));
            Assert.False(teardown.IsCompleted);
            Assert.False(module.IsDisposed);
        }
        finally { pin.Dispose(); }
        await teardown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(module.IsDisposed);
    }

    [Fact]
    public async Task FreshThreadMustTakeNormalGuestEntryBeforeCacheUse()
    {
        using var module = Load();
        var (header, data) = Bank(module);
        using (header) using (data)
        {
            var reader = new VerifiedBankReader();
            reader.Remember(module, header.Address);
            var hit = await Task.Run(() => reader.TryRead(module, header.Address, 0, out _));
            Assert.False(hit);
        }
    }
}
