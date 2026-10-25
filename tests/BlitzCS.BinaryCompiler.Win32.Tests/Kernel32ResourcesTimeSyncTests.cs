using System.Text;
using BlitzCS.BinaryCompiler.Win32;
using Xunit;

namespace BlitzCS.BinaryCompiler.Win32.Tests;

/// <summary>Resources, SRW locks / condition variables and time functions of KERNEL32 (docs/binary-win32.md).</summary>
public sealed class Kernel32ResourcesTimeSyncTests
{
    // ---------- resources ----------

    private sealed class Dir
    {
        public readonly List<(object Key, object Child)> Entries = [];
    }

    /// <summary>A mapped PE32 image (at the first FakeMemory allocation) whose resource directory is built from a type -> name -> language -> data tree.</summary>
    private static (World World, uint Base) ImageWorld(Dictionary<object, Dictionary<object, Dictionary<ushort, byte[]>>>? tree, bool withDirectory = true, Win32Options? options = null)
    {
        var w = new World(options ?? new Win32Options { MainModuleBase = 0x30000000 });
        const uint rsrcRva = 0x200;
        var rs = new List<byte>();
        void Pad() { while (rs.Count % 4 != 0) rs.Add(0); }
        byte[] Bytes() => [.. rs];

        void Patch(int at, uint v) { var b = BitConverter.GetBytes(v); for (var i = 0; i < 4; i++) rs[at + i] = b[i]; }
        uint WriteName(string s) { Pad(); var off = (uint)rs.Count; rs.AddRange(BitConverter.GetBytes((ushort)s.Length)); rs.AddRange(Encoding.Unicode.GetBytes(s)); return off; }

        int WriteDir(IReadOnlyList<(object Key, Action<int> Fill)> children)
        {
            Pad();
            var at = rs.Count;
            var named = children.Count(c => c.Key is string);
            rs.AddRange(new byte[12]);
            rs.AddRange(BitConverter.GetBytes((ushort)named)); rs.AddRange(BitConverter.GetBytes((ushort)(children.Count - named)));
            var ordered = children.Where(c => c.Key is string).Concat(children.Where(c => c.Key is not string)).ToList();
            var slots = new List<int>();
            foreach (var _ in ordered) { slots.Add(rs.Count); rs.AddRange(new byte[8]); }
            for (var i = 0; i < ordered.Count; i++)
            {
                Patch(slots[i], ordered[i].Key is string s ? 0x80000000 | WriteName(s) : Convert.ToUInt32(ordered[i].Key));
                ordered[i].Fill(slots[i] + 4);
            }
            return at;
        }

        if (tree is not null)
        {
            WriteDir([.. tree.Select(t => (t.Key, (Action<int>)(slot =>
            {
                var d = WriteDir([.. t.Value.Select(n => (n.Key, (Action<int>)(slot2 =>
                {
                    var d2 = WriteDir([.. n.Value.Select(l => ((object)(uint)l.Key, (Action<int>)(slot3 =>
                    {
                        Pad();
                        var blobAt = rs.Count + 16;
                        // data entry: RVA, size, codepage, reserved; then the bytes
                        var entryAt = rs.Count;
                        rs.AddRange(new byte[16]);
                        Pad(); var dataAt = rs.Count; rs.AddRange(l.Value);
                        Patch(entryAt, rsrcRva + (uint)dataAt); Patch(entryAt + 4, (uint)l.Value.Length);
                        Patch(slot3, (uint)entryAt);
                    }))).ToList()]);
                    Patch(slot2, 0x80000000 | (uint)d2);
                }))).ToList()]);
                Patch(slot, 0x80000000 | (uint)d);
            }))).ToList()]);
        }
        var rsrc = Bytes();
        var image = w.Mem.Allocate(0x200 + Math.Max(rsrc.Length, 16));
        Assert.Equal(0x30000000u, image.Address);
        var hdr = new byte[0x200];
        hdr[0] = (byte)'M'; hdr[1] = (byte)'Z';
        BitConverter.GetBytes(0x80u).CopyTo(hdr, 0x3C);
        BitConverter.GetBytes(0x4550u).CopyTo(hdr, 0x80);
        BitConverter.GetBytes((ushort)0x10B).CopyTo(hdr, 0x80 + 24);
        BitConverter.GetBytes(16u).CopyTo(hdr, 0x80 + 24 + 92);
        if (withDirectory && tree is not null)
        {
            BitConverter.GetBytes(rsrcRva).CopyTo(hdr, 0x80 + 24 + 96 + 16);
            BitConverter.GetBytes((uint)rsrc.Length).CopyTo(hdr, 0x80 + 24 + 96 + 20);
        }
        w.Mem.Write(image.Address, hdr);
        if (rsrc.Length > 0) w.Mem.Write(image.Address + rsrcRva, rsrc);
        return (w, image.Address);
    }

    private static Dictionary<object, Dictionary<object, Dictionary<ushort, byte[]>>> Sample() => new()
    {
        [(uint)10] = new() // RT_RCDATA
        {
            [(uint)101] = new() { [0x0409] = [1, 2, 3, 4, 5], [0x0407] = [9, 9] },
            ["Banner"] = new() { [0] = Encoding.ASCII.GetBytes("neutral") },
            [(uint)7] = new() { [0x0407] = [7] },
        },
        ["MYTYPE"] = new() { [(uint)5] = new() { [0x0409] = [0xAA, 0xBB, 0xCC] } },
    };

    private static void AssertData(World w, uint h, byte[] expected)
    {
        var p = w.Kernel("LoadResource", 0, h);
        Assert.NotEqual(0u, p);
        Assert.Equal((uint)expected.Length, w.Kernel("SizeofResource", 0, h));
        Assert.Equal(p, w.Kernel("LockResource", p));
        Assert.Equal(expected, w.Bytes(p, expected.Length));
    }

    [Fact]
    public void FindLoadLockSizeofByIdAgainstMainModule()
    {
        var (w, _) = ImageWorld(Sample());
        using (w)
        {
            var h = w.Kernel("FindResourceA", 0, 101, 10);
            Assert.NotEqual(0u, h);
            AssertData(w, h, [1, 2, 3, 4, 5]);                       // neutral request prefers en-US over the German entry
            Assert.Equal(h, w.Kernel("FindResourceW", 0x30000000, 101, 10)); // explicit handle = main module
            Assert.Equal(0u, w.Kernel("FreeResource", w.Kernel("LoadResource", 0, h)));
        }
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndHashStringsAreIds()
    {
        var (w, _) = ImageWorld(Sample());
        using (w)
        {
            AssertData(w, w.Kernel("FindResourceA", 0, 5, w.Str("mytype")), [0xAA, 0xBB, 0xCC]);
            AssertData(w, w.Kernel("FindResourceW", 0, w.Wide("#5"), w.Wide("MyType")), [0xAA, 0xBB, 0xCC]);
            AssertData(w, w.Kernel("FindResourceA", 0, w.Str("BANNER"), w.Str("#10")), Encoding.ASCII.GetBytes("neutral"));
            AssertData(w, w.Kernel("FindResourceW", 0, w.Wide("banner"), 10), Encoding.ASCII.GetBytes("neutral"));
        }
    }

    [Fact]
    public void LanguageSelection()
    {
        var (w, _) = ImageWorld(Sample());
        using (w)
        {
            AssertData(w, w.Kernel("FindResourceExA", 0, 10, 101, 0x0407), [9, 9]);
            AssertData(w, w.Kernel("FindResourceExW", 0, 10, 101, 0x0409), [1, 2, 3, 4, 5]);
            AssertData(w, w.Kernel("FindResourceExA", 0, 10, w.Str("Banner"), 0x0409), Encoding.ASCII.GetBytes("neutral")); // falls back to LANG_NEUTRAL
            Assert.Equal(0u, w.Kernel("FindResourceExA", 0, 10, 101, 0x040C));        // French: exact only, no neutral entry
            Assert.Equal(Win32Error.ResourceLangNotFound, w.LastError);
            AssertData(w, w.Kernel("FindResourceA", 0, 7, 10), [7]);                 // neutral request with no en-US: first language present
        }
    }

    [Fact]
    public void ErrorsLikeWindows()
    {
        var (w, _) = ImageWorld(Sample());
        using (w)
        {
            Assert.Equal(0u, w.Kernel("FindResourceA", 0, 101, 99));
            Assert.Equal(Win32Error.ResourceTypeNotFound, w.LastError);
            Assert.Equal(0u, w.Kernel("FindResourceA", 0, 101, w.Str("NOPE")));
            Assert.Equal(Win32Error.ResourceTypeNotFound, w.LastError);
            Assert.Equal(0u, w.Kernel("FindResourceA", 0, 999, 10));
            Assert.Equal(Win32Error.ResourceNameNotFound, w.LastError);
            Assert.Equal(0u, w.Kernel("FindResourceW", 0, w.Wide("#abc"), 10));
            Assert.Equal(Win32Error.ResourceNameNotFound, w.LastError);
            Assert.Equal(0u, w.Kernel("FindResourceA", 0x12345678, 101, 10));
            Assert.Equal(Win32Error.InvalidHandle, w.LastError);
            Assert.Equal(0u, w.Kernel("FindResourceA", w.Kernel("GetModuleHandleA", w.Str("KERNEL32.dll")), 101, 10)); // emulated DLL: no resource section
            Assert.Equal(Win32Error.ResourceDataNotFound, w.LastError);
            // a handle that is not an HRSRC of this module
            Assert.Equal(0u, w.Kernel("LoadResource", 0, 0x30000010));
            Assert.Equal(Win32Error.InvalidParameter, w.LastError);
            Assert.Equal(0u, w.Kernel("SizeofResource", 0, 0));
            Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        }
    }

    [Fact]
    public void ModuleWithoutResourceDirectory()
    {
        var (w, _) = ImageWorld(null);
        using (w)
        {
            Assert.Equal(0u, w.Kernel("FindResourceA", 0, 1, 10));
            Assert.Equal(Win32Error.ResourceDataNotFound, w.LastError);
        }
    }

    [Fact]
    public void CorruptDirectoryIsBadExeFormat()
    {
        var (w, b) = ImageWorld(Sample());
        using (w)
        {
            w.Mem.WriteUInt32(b + 0x200 + 12, 0xFFFFFFFF); // absurd entry counts
            Assert.Equal(0u, w.Kernel("FindResourceA", 0, 101, 10));
            Assert.Equal(Win32Error.BadExeFormat, w.LastError);
        }
    }

    [Fact]
    public void ResourceApisAreAbsentFromTheUnimplementedList()
    {
        foreach (var n in new[] { "FindResourceA", "FindResourceW", "FindResourceExA", "FindResourceExW", "LoadResource", "LockResource", "SizeofResource", "FreeResource" })
            Assert.Contains(Win32ImportResolver.Coverage.Implemented, x => x.Name == n);
        Assert.DoesNotContain(Win32ImportResolver.Coverage.Implemented, x => x.Name == "EnumResourceNamesA");
    }

    // ---------- SRW locks and condition variables ----------

    [Fact]
    public void SrwExclusiveSharedAndTry()
    {
        using var w = new World();
        var l = w.Buf(4);
        w.Kernel("InitializeSRWLock", l);
        Assert.Equal(1u, w.Kernel("TryAcquireSRWLockExclusive", l));
        Assert.NotEqual(0u, w.Mem.ReadUInt32(l));
        Assert.Equal(0u, w.Kernel("TryAcquireSRWLockShared", l));
        Assert.Equal(0u, w.Kernel("TryAcquireSRWLockExclusive", l));
        Assert.Throws<Win32UsageException>(() => w.Kernel("AcquireSRWLockExclusive", l)); // would deadlock
        w.Kernel("ReleaseSRWLockExclusive", l);
        Assert.Equal(0u, w.Mem.ReadUInt32(l));
        Assert.Equal(1u, w.Kernel("TryAcquireSRWLockShared", l));
        Assert.Equal(1u, w.Kernel("TryAcquireSRWLockShared", l));
        Assert.Equal(0u, w.Kernel("TryAcquireSRWLockExclusive", l));
        w.Kernel("ReleaseSRWLockShared", l); w.Kernel("ReleaseSRWLockShared", l);
        Assert.Equal(0u, w.Mem.ReadUInt32(l));
        Assert.Throws<Win32UsageException>(() => w.Kernel("ReleaseSRWLockShared", l));
        Assert.Throws<Win32UsageException>(() => w.Kernel("ReleaseSRWLockExclusive", l));
    }

    [Fact]
    public void SrwStaticallyInitialisedLockWorksWithoutInitialize()
    {
        using var w = new World();
        var l = w.Buf(4);
        w.Kernel("AcquireSRWLockShared", l);
        w.Kernel("ReleaseSRWLockShared", l);
        w.Kernel("AcquireSRWLockExclusive", l);
        w.Kernel("ReleaseSRWLockExclusive", l);
        w.Kernel("AcquireSRWLockExclusive", l);
        Assert.Throws<Win32UsageException>(() => w.Kernel("InitializeSRWLock", l));
        w.Kernel("ReleaseSRWLockExclusive", l);
    }

    [Fact]
    public void SrwExclusiveBlocksAnotherThreadUntilReleased()
    {
        using var w = new World();
        var l = w.Buf(4);
        var counter = w.Buf(4);
        w.Kernel("AcquireSRWLockExclusive", l);
        var t = new Thread(() => { w.Kernel("AcquireSRWLockShared", l); w.Mem.WriteUInt32(counter, 1); w.Kernel("ReleaseSRWLockShared", l); });
        t.Start();
        Thread.Sleep(80);
        Assert.Equal(0u, w.Mem.ReadUInt32(counter));
        w.Kernel("ReleaseSRWLockExclusive", l);
        Assert.True(t.Join(5000));
        Assert.Equal(1u, w.Mem.ReadUInt32(counter));
    }

    [Fact]
    public void ConditionVariableWithSrwProducerConsumer()
    {
        using var w = new World();
        var l = w.Buf(4); var cv = w.Buf(4); var flag = w.Buf(4);
        w.Kernel("InitializeSRWLock", l); w.Kernel("InitializeConditionVariable", cv);
        uint slept = 99;
        var consumer = new Thread(() =>
        {
            w.Kernel("AcquireSRWLockExclusive", l);
            while (w.Mem.ReadUInt32(flag) == 0) slept = w.Kernel("SleepConditionVariableSRW", cv, l, 0xFFFFFFFF, 0);
            w.Kernel("ReleaseSRWLockExclusive", l);
        });
        consumer.Start();
        Thread.Sleep(80);
        w.Kernel("AcquireSRWLockExclusive", l);   // proves the sleeper released the lock
        w.Mem.WriteUInt32(flag, 1);
        w.Kernel("ReleaseSRWLockExclusive", l);
        w.Kernel("WakeConditionVariable", cv);
        Assert.True(consumer.Join(5000));
        Assert.Equal(1u, slept);
    }

    [Fact]
    public void ConditionVariableTimeoutReacquiresTheLock()
    {
        using var w = new World();
        var l = w.Buf(4); var cv = w.Buf(4);
        w.Kernel("AcquireSRWLockShared", l);
        Assert.Equal(0u, w.Kernel("SleepConditionVariableSRW", cv, l, 30, 1));   // CONDITION_VARIABLE_LOCKMODE_SHARED
        Assert.Equal(Win32Error.Timeout, w.LastError);
        Assert.Equal(0u, w.Kernel("TryAcquireSRWLockExclusive", l));              // shared lock was re-acquired
        w.Kernel("ReleaseSRWLockShared", l);
        Assert.Equal(0u, w.Kernel("SleepConditionVariableSRW", cv, l, 0, 2));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Throws<Win32UsageException>(() => w.Kernel("SleepConditionVariableSRW", cv, l, 10, 0)); // lock not held
        Assert.Equal(0u, w.Mem.ReadUInt32(cv));
    }

    [Fact]
    public void WakeAllWakesEveryWaiterAndWakeIsLostWithoutWaiters()
    {
        using var w = new World();
        var cs = w.Buf(24); var cv = w.Buf(4); var woken = w.Buf(4);
        w.Kernel("InitializeCriticalSection", cs); w.Kernel("InitializeConditionVariable", cv);
        w.Kernel("WakeConditionVariable", cv); w.Kernel("WakeAllConditionVariable", cv);   // nobody waits
        var results = new uint[3];
        var threads = Enumerable.Range(0, 3).Select(i => new Thread(() =>
        {
            w.Kernel("EnterCriticalSection", cs);
            results[i] = w.Kernel("SleepConditionVariableCS", cv, cs, 10_000);
            w.Mem.WriteUInt32(woken, w.Mem.ReadUInt32(woken) + 1);   // still under the re-acquired section
            w.Kernel("LeaveCriticalSection", cs);
        })).ToList();
        threads.ForEach(t => t.Start());
        Thread.Sleep(150);
        w.Kernel("WakeAllConditionVariable", cv);
        Assert.All(threads, t => Assert.True(t.Join(5000)));
        Assert.Equal([1u, 1u, 1u], results);
        Assert.Equal(3u, w.Mem.ReadUInt32(woken));
    }

    [Fact]
    public void SleepConditionVariableCsRequiresTheSectionHeldOnce()
    {
        using var w = new World();
        var cs = w.Buf(24); var cv = w.Buf(4);
        w.Kernel("InitializeCriticalSection", cs);
        Assert.Throws<Win32UsageException>(() => w.Kernel("SleepConditionVariableCS", cv, cs, 10));
        w.Kernel("EnterCriticalSection", cs); w.Kernel("EnterCriticalSection", cs);
        Assert.Throws<Win32UsageException>(() => w.Kernel("SleepConditionVariableCS", cv, cs, 10));
        w.Kernel("LeaveCriticalSection", cs);
        Assert.Equal(0u, w.Kernel("SleepConditionVariableCS", cv, cs, 20));
        Assert.Equal(Win32Error.Timeout, w.LastError);
        Assert.Equal(1u, w.Mem.ReadUInt32(cs + 8));   // owned again
        w.Kernel("LeaveCriticalSection", cs);
    }

    // ---------- time ----------

    private static TimeZoneInfo Berlin() => TimeZoneInfo.CreateCustomTimeZone("Test Standard", TimeSpan.FromHours(1), "Test Standard Time", "Test Standard Time", "Test Daylight Time",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),   // last Sunday of March, 02:00 standard
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)), // last Sunday of October, 03:00 daylight
    ]);

    private static void St(World w, uint p, int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0)
    {
        var v = new ushort[] { (ushort)y, (ushort)mo, 0, (ushort)d, (ushort)h, (ushort)mi, (ushort)s, (ushort)ms };
        w.Mem.Write(p, v.SelectMany(BitConverter.GetBytes).ToArray());
    }

    private static int[] ReadSt(World w, uint p) => Enumerable.Range(0, 8).Select(i => (int)BitConverter.ToUInt16(w.Bytes(p, 16), i * 2)).ToArray();

    [Fact]
    public void FileTimeAndSystemTimeRoundTrip()
    {
        using var w = new World();
        var st = w.Buf(16); var ft = w.Buf(8); var back = w.Buf(16);
        St(w, st, 2024, 2, 29, 23, 59, 58, 123);
        Assert.Equal(1u, w.Kernel("SystemTimeToFileTime", st, ft));
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 58, 123, DateTimeKind.Utc).ToFileTimeUtc(), BitConverter.ToInt64(w.Bytes(ft, 8)));
        Assert.Equal(1u, w.Kernel("FileTimeToSystemTime", ft, back));
        Assert.Equal([2024, 2, 4, 29, 23, 59, 58, 123], ReadSt(w, back));   // Thursday
        // epoch and beyond DateTime's range
        w.Mem.WriteUInt64(ft, 0);
        Assert.Equal(1u, w.Kernel("FileTimeToSystemTime", ft, back));
        Assert.Equal([1601, 1, 1, 1, 0, 0, 0, 0], ReadSt(w, back));
        St(w, st, 30827, 12, 14, 2, 48, 5, 477);
        Assert.Equal(1u, w.Kernel("SystemTimeToFileTime", st, ft));
        Assert.Equal(1u, w.Kernel("FileTimeToSystemTime", ft, back));
        Assert.Equal([30827, 12, 0, 14, 2, 48, 5, 477], ReadSt(w, back).Select((v, i) => i == 2 ? 0 : v).ToArray());
        // invalid inputs
        St(w, st, 2023, 2, 29);
        Assert.Equal(0u, w.Kernel("SystemTimeToFileTime", st, ft));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        St(w, st, 1600, 12, 31);
        Assert.Equal(0u, w.Kernel("SystemTimeToFileTime", st, ft));
        w.Mem.WriteUInt64(ft, ulong.MaxValue);
        Assert.Equal(0u, w.Kernel("FileTimeToSystemTime", ft, back));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void SystemAndLocalTimeFollowTheConfiguredZone()
    {
        using var w = new World(new Win32Options { TimeZone = TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromHours(5.5), "x", "x") });
        var a = w.Buf(16); var b = w.Buf(16);
        w.Kernel("GetSystemTime", a); w.Kernel("GetLocalTime", b);
        var utc = ReadSt(w, a); var local = ReadSt(w, b);
        var delta = new DateTime(local[0], local[1], local[3], local[4], local[5], local[6]) - new DateTime(utc[0], utc[1], utc[3], utc[4], utc[5], utc[6]);
        Assert.InRange(delta.TotalMinutes, 329, 331);
        Assert.InRange((DateTime.UtcNow - new DateTime(utc[0], utc[1], utc[3], utc[4], utc[5], utc[6], DateTimeKind.Utc)).TotalSeconds, -2, 5);
    }

    [Fact]
    public void GetTimeZoneInformationDescribesTheZone()
    {
        using var w = new World(new Win32Options { TimeZone = Berlin() });
        var p = w.Buf(172);
        var id = w.Kernel("GetTimeZoneInformation", p);
        var tzi = w.Bytes(p, 172);
        Assert.Equal(-60, BitConverter.ToInt32(tzi, 0));
        Assert.Equal("Test Standard Time", Encoding.Unicode.GetString(tzi, 4, 64).TrimEnd('\0'));
        Assert.Equal("Test Daylight Time", Encoding.Unicode.GetString(tzi, 88, 64).TrimEnd('\0'));
        Assert.Equal([0, 10, 0, 5, 3, 0, 0, 0], Enumerable.Range(0, 8).Select(i => (int)BitConverter.ToUInt16(tzi, 68 + i * 2)));   // last Sunday of October 03:00
        Assert.Equal([0, 3, 0, 5, 2, 0, 0, 0], Enumerable.Range(0, 8).Select(i => (int)BitConverter.ToUInt16(tzi, 152 + i * 2)));
        Assert.Equal(0, BitConverter.ToInt32(tzi, 84));
        Assert.Equal(-60, BitConverter.ToInt32(tzi, 168));
        Assert.Equal(Berlin().IsDaylightSavingTime(DateTime.UtcNow) ? 2u : 1u, id);
    }

    [Fact]
    public void GetTimeZoneInformationWithoutDaylightSaving()
    {
        using var w = new World(new Win32Options { TimeZone = TimeZoneInfo.Utc });
        var p = w.Buf(172);
        Assert.Equal(0u, w.Kernel("GetTimeZoneInformation", p));
        var tzi = w.Bytes(p, 172);
        Assert.Equal(0, BitConverter.ToInt32(tzi, 0));
        Assert.All(tzi.Skip(68).Take(16), x => Assert.Equal(0, x));
        Assert.All(tzi.Skip(152).Take(16), x => Assert.Equal(0, x));
    }

    [Fact]
    public void SystemTimeToTzSpecificLocalTimeUsesTheGivenZoneOrTheActiveOne()
    {
        using var w = new World(new Win32Options { TimeZone = Berlin() });
        var tzi = w.Buf(172); var utc = w.Buf(16); var local = w.Buf(16);
        w.Kernel("GetTimeZoneInformation", tzi);
        St(w, utc, 2024, 1, 15, 12);       // winter: UTC+1
        Assert.Equal(1u, w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local));
        Assert.Equal([2024, 1, 1, 15, 13, 0, 0, 0], ReadSt(w, local));
        St(w, utc, 2024, 7, 15, 12);       // summer: UTC+2
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal([2024, 7, 1, 15, 14, 0, 0, 0], ReadSt(w, local));
        St(w, utc, 2024, 3, 31, 0, 59);    // before 01:00 UTC on the last Sunday of March
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal([2024, 3, 0, 31, 1, 59, 0, 0], ReadSt(w, local));
        St(w, utc, 2024, 3, 31, 1, 0);     // exactly at the switch
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal([2024, 3, 0, 31, 3, 0, 0, 0], ReadSt(w, local));
        St(w, utc, 2024, 10, 27, 0, 59);   // still daylight
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal(2, ReadSt(w, local)[4]);
        St(w, utc, 2024, 10, 27, 1, 0);    // daylight ended
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal(2, ReadSt(w, local)[4]);
        St(w, utc, 2024, 10, 27, 2, 0);
        w.Kernel("SystemTimeToTzSpecificLocalTime", tzi, utc, local);
        Assert.Equal(3, ReadSt(w, local)[4]);
        // NULL zone = the active one, exact for that instant
        St(w, utc, 2024, 7, 15, 12);
        Assert.Equal(1u, w.Kernel("SystemTimeToTzSpecificLocalTime", 0, utc, local));
        Assert.Equal(14, ReadSt(w, local)[4]);
        St(w, utc, 2024, 13, 1);
        Assert.Equal(0u, w.Kernel("SystemTimeToTzSpecificLocalTime", 0, utc, local));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
    }

    [Fact]
    public void SouthernHemisphereZoneDaylightWrapsTheYear()
    {
        using var w = new World();
        var tzi = new byte[172];
        BitConverter.GetBytes(180).CopyTo(tzi, 0);            // UTC-3
        BitConverter.GetBytes((ushort)2).CopyTo(tzi, 68 + 2); BitConverter.GetBytes((ushort)0).CopyTo(tzi, 68 + 4); BitConverter.GetBytes((ushort)3).CopyTo(tzi, 68 + 6); BitConverter.GetBytes((ushort)0).CopyTo(tzi, 68 + 8); // 3rd Sunday of Feb, 00:00
        BitConverter.GetBytes((ushort)11).CopyTo(tzi, 152 + 2); BitConverter.GetBytes((ushort)0).CopyTo(tzi, 152 + 4); BitConverter.GetBytes((ushort)1).CopyTo(tzi, 152 + 6); BitConverter.GetBytes((ushort)0).CopyTo(tzi, 152 + 8); // 1st Sunday of Nov
        BitConverter.GetBytes(-60).CopyTo(tzi, 168);
        var p = w.Buf(172); w.Mem.Write(p, tzi);
        var utc = w.Buf(16); var local = w.Buf(16);
        St(w, utc, 2024, 1, 10, 12);   // January: daylight (UTC-2)
        w.Kernel("SystemTimeToTzSpecificLocalTime", p, utc, local);
        Assert.Equal(10, ReadSt(w, local)[4]);
        St(w, utc, 2024, 6, 10, 12);   // June: standard (UTC-3)
        w.Kernel("SystemTimeToTzSpecificLocalTime", p, utc, local);
        Assert.Equal(9, ReadSt(w, local)[4]);
    }

    [Fact]
    public void DateAndTimeFormatting()
    {
        using var w = new World();
        var st = w.Buf(16); var o = w.Buf(200);
        St(w, st, 2024, 3, 7, 15, 4, 9);
        string Out(uint r) => w.Mem.ReadWideString(o);
        Assert.Equal(9u, w.Kernel("GetDateFormatW", 0x409, 0, st, 0, o, 50)); Assert.Equal("3/7/2024", Out(0));
        Assert.Equal(24u, w.Kernel("GetDateFormatW", 0x409, 2, st, 0, o, 50)); Assert.Equal("Thursday, March 7, 2024", Out(0));
        w.Kernel("GetDateFormatW", 0x7F, 0, st, 0, o, 50); Assert.Equal("03/07/2024", Out(0));
        w.Kernel("GetDateFormatW", 0x400, 0, st, w.Wide("ddd dd MMM yy 'on' y"), o, 50); Assert.Equal("Thu 07 Mar 24 on 24", Out(0));
        Assert.Equal(9u, w.Kernel("GetDateFormatW", 0x409, 0, st, 0, 0, 0));          // size query: "3/7/2024" + NUL
        Assert.Equal(0u, w.Kernel("GetDateFormatW", 0x409, 0, st, 0, o, 3));
        Assert.Equal(Win32Error.InsufficientBuffer, w.LastError);
        Assert.Equal(0u, w.Kernel("GetDateFormatW", 0x409, 1, st, w.Wide("d"), o, 50));
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);
        Assert.Equal(0u, w.Kernel("GetDateFormatW", 0x407, 0, st, 0, o, 50));        // no locale database
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.Equal(0u, w.Kernel("GetDateFormatW", 0x409, 0, st, w.Wide("gg"), o, 50)); // eras unsupported
        Assert.Equal(Win32Error.InvalidFlags, w.LastError);

        w.Kernel("GetTimeFormatW", 0x409, 0, st, 0, o, 50); Assert.Equal("3:04:09 PM", Out(0));
        w.Kernel("GetTimeFormatW", 0x409, 2, st, 0, o, 50); Assert.Equal("3:04 PM", Out(0));
        w.Kernel("GetTimeFormatW", 0x409, 1, st, 0, o, 50); Assert.Equal("3 PM", Out(0));
        w.Kernel("GetTimeFormatW", 0x409, 4, st, 0, o, 50); Assert.Equal("3:04:09", Out(0));
        w.Kernel("GetTimeFormatW", 0x409, 8, st, 0, o, 50); Assert.Equal("15:04:09", Out(0));
        w.Kernel("GetTimeFormatW", 0x7F, 0, st, 0, o, 50); Assert.Equal("15:04:09", Out(0));
        w.Kernel("GetTimeFormatW", 0x409, 0, st, w.Wide("hh:mm t"), o, 50); Assert.Equal("03:04 P", Out(0));
        St(w, st, 2024, 3, 7, 0, 0, 0);
        w.Kernel("GetTimeFormatW", 0x409, 0, st, 0, o, 50); Assert.Equal("12:00:00 AM", Out(0));
        St(w, st, 2024, 3, 7, 24);
        Assert.Equal(0u, w.Kernel("GetTimeFormatW", 0x409, 0, st, 0, o, 50));
        Assert.Equal(Win32Error.InvalidParameter, w.LastError);
        Assert.NotEqual(0u, w.Kernel("GetTimeFormatW", 0x409, 0, 0, 0, o, 50));        // NULL = now
    }
}
