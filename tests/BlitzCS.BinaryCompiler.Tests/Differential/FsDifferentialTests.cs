using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests.Differential;

/// <summary>
/// FS/TEB differential tests. The oracle is a native ELF32 program that installs a real fs segment (set_thread_area) over a TEB laid out
/// per docs/binary-teb-contract.md and runs libraries whose inline asm uses genuine %fs: accesses; the translated guest must return
/// the same values (address-independent facts only) and leave the same data section.
///   fs_seh: ExceptionList chain push/pop/unwind like an MSVC SEH prolog, StackBase/StackLimit relations, Self pointer
///   fs_tls: TLS slots with static and run-time offsets, LastError, ids, PEB, TLS pointer
///   fs_ops: sized accesses, read-modify-write, xchg/cmpxchg, flag consumers with run-time offsets, push/pop memory, SSE moves, lea
/// </summary>
[Collection("GuestRuntime")]
public sealed class FsDifferentialTests
{
    public static TheoryData<string, OptimizationProfile> Cases()
    {
        var d = new TheoryData<string, OptimizationProfile>();
        foreach (var lib in new[] { "fs_seh", "fs_tls", "fs_ops" })
            foreach (var p in TranslatorHarness.Profiles) d.Add(lib, p);
        return d;
    }

    [OracleTheory, MemberData(nameof(Cases))]
    public void Translated_guest_matches_the_x86_oracle_with_a_real_fs_segment(string lib, OptimizationProfile profile)
    {
        var outcome = OracleDifferential.Run(lib, profile);
        Assert.True(outcome.Passed, outcome.ToString());
        Assert.True(outcome.Vectors > 0);
    }
}
