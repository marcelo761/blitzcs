using System.Buffers.Binary;
using System.Text;
using BlitzCS.BinaryCompiler.Runtime;

namespace BlitzCS.BinaryCompiler.Win32;

/// <summary>Which waveOut backend a <see cref="Win32Context"/> uses. <see cref="Default"/>: HostWinmm on a Windows host (including Wine), None elsewhere. The environment variable BLITZCS_AUDIO (host|virtual|none) overrides the option.</summary>
public enum WaveOutBackendKind { Default, HostWinmm, VirtualSink, None }

/// <summary>mmsystem.h constants used by the waveOut layer.</summary>
public static class WaveOutConst
{
    public const uint MmsyserrNoError = 0, MmsyserrError = 1, MmsyserrBadDeviceId = 2, MmsyserrNotEnabled = 3, MmsyserrAllocated = 4, MmsyserrInvalHandle = 5, MmsyserrNoDriver = 6,
        MmsyserrNoMem = 7, MmsyserrNotSupported = 8, MmsyserrBadErrNum = 9, MmsyserrInvalFlag = 10, MmsyserrInvalParam = 11, MmsyserrHandleBusy = 12,
        WaverrBadFormat = 32, WaverrStillPlaying = 33, WaverrUnprepared = 34, WaverrSync = 35;
    public const uint WaveMapper = 0xFFFFFFFF;
    public const uint WhdrDone = 1, WhdrPrepared = 2, WhdrBeginLoop = 4, WhdrEndLoop = 8, WhdrInQueue = 0x10;
    public const uint CallbackTypeMask = 0x70000, CallbackNull = 0, CallbackWindow = 0x10000, CallbackThread = 0x20000, CallbackFunction = 0x30000, CallbackEvent = 0x50000;
    public const uint WaveFormatQuery = 1, WaveAllowSync = 2, WaveMapped = 4, WaveFormatDirect = 8, WaveMappedDefaultCommunicationDevice = 0x10;
    public const uint WomOpen = 0x3BB, WomClose = 0x3BC, WomDone = 0x3BD;
    public const uint TimeMs = 1, TimeSamples = 2, TimeBytes = 4, TimeSmpte = 8, TimeMidi = 0x10, TimeTicks = 0x20;
    public const int HeaderSize32 = 32, MmTimeSize = 12, CapsSizeA = 52, CapsSizeW = 84;
    public const ushort FormatPcm = 1, FormatExtensible = 0xFFFE;
}

/// <summary>A WAVEFORMATEX read from the guest (the layouts of 32-bit and 64-bit Windows are identical: packed, no pointers). <see cref="Raw"/> holds the bytes exactly as the guest wrote them.</summary>
public sealed record WaveOutFormat(ushort Tag, ushort Channels, uint SamplesPerSec, uint AvgBytesPerSec, ushort BlockAlign, ushort BitsPerSample, byte[] Raw)
{
    /// <summary>True for WAVE_FORMAT_PCM and for WAVE_FORMAT_EXTENSIBLE whose sub-format is KSDATAFORMAT_SUBTYPE_PCM.</summary>
    public bool IsPcm => Tag == WaveOutConst.FormatPcm || (Tag == WaveOutConst.FormatExtensible && Raw.Length >= 40 && ExtensiblePcm());

    private bool ExtensiblePcm() =>
        BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(16)) >= 22 && BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(18)) == BitsPerSample
        && BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(24)) == 1 && Raw.AsSpan(28, 12).SequenceEqual(new byte[] { 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });

    /// <summary>Reads the format at <paramref name="address"/> with bounds checks. Returns null when the memory is not mapped.</summary>
    public static WaveOutFormat? Read(IWin32Memory memory, uint address)
    {
        if (address == 0 || !memory.IsMapped(address, 16)) return null;
        var head = memory.ReadBytes(address, 16);
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(head);
        var total = 16;
        if (tag != WaveOutConst.FormatPcm)
        {
            if (!memory.IsMapped(address, 18)) return null;
            total = 18 + memory.ReadUInt16(address + 16);
            if (!memory.IsMapped(address, (uint)total)) return null;
        }
        var raw = memory.ReadBytes(address, total);
        return new WaveOutFormat(tag, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2)), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)), BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(14)), raw);
    }
}

/// <summary>Device capabilities (WAVEOUTCAPS). Written to the guest as the A (ANSI, 52 bytes) or W (UTF-16, 84 bytes) layout.</summary>
public sealed record WaveOutCaps(ushort Mid, ushort Pid, uint DriverVersion, string Name, uint Formats, ushort Channels, uint Support)
{
    public byte[] ToBytes(bool wide)
    {
        var b = new byte[wide ? WaveOutConst.CapsSizeW : WaveOutConst.CapsSizeA];
        BinaryPrimitives.WriteUInt16LittleEndian(b, Mid);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), Pid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), DriverVersion);
        var n = Name.Length > 31 ? Name[..31] : Name;
        for (var i = 0; i < n.Length; i++)
            if (wide) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8 + 2 * i), n[i]); else b[8 + i] = n[i] < 256 ? (byte)n[i] : (byte)'?';
        var o = wide ? 72 : 40;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), Formats);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), Support);
        return b;
    }

    /// <summary>Parses a native WAVEOUTCAPSW (84 bytes).</summary>
    public static WaveOutCaps FromWide(ReadOnlySpan<byte> b)
    {
        var chars = new StringBuilder();
        for (var i = 0; i < 32; i++) { var c = BinaryPrimitives.ReadUInt16LittleEndian(b[(8 + 2 * i)..]); if (c == 0) break; chars.Append((char)c); }
        return new WaveOutCaps(BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), chars.ToString(),
            BinaryPrimitives.ReadUInt32LittleEndian(b[72..]), BinaryPrimitives.ReadUInt16LittleEndian(b[76..]), BinaryPrimitives.ReadUInt32LittleEndian(b[80..]));
    }
}

/// <summary>PCM bytes a backend consumed (VirtualSink: when the buffer finished playing) or forwarded (HostWinmm: when written).</summary>
/// <param name="Format">The stream format.</param>
/// <param name="StreamTimeMs">Playback position of the first byte (VirtualSink) or the time since the stream was opened (HostWinmm).</param>
/// <param name="Data">A private copy of the bytes.</param>
public sealed record WaveOutWrite(uint DeviceId, uint HeaderAddress, WaveOutFormat Format, long StreamTimeMs, byte[] Data);

/// <summary>Receives every <see cref="WaveOutWrite"/>. Called on the backend's thread (a timer or winmm callback thread), never while a backend lock is held; it must not call back into waveOut.</summary>
public delegate void WaveOutObserver(WaveOutWrite write);

/// <summary>One prepared guest WAVEHDR as seen by a backend. All fields are host-side copies; no guest pointer is dereferenced except through <see cref="Memory"/>.</summary>
public sealed class WaveOutBuffer
{
    internal WaveOutBuffer(IWin32Memory memory, uint header) { Memory = memory; HeaderAddress = header; }
    public IWin32Memory Memory { get; }
    /// <summary>Address of the guest WAVEHDR (the key of the buffer).</summary>
    public uint HeaderAddress { get; }
    /// <summary>lpData as of waveOutPrepareHeader.</summary>
    public uint DataAddress { get; internal set; }
    /// <summary>dwBufferLength as of waveOutPrepareHeader.</summary>
    public uint PreparedLength { get; internal set; }
    /// <summary>dwBufferLength as of the latest waveOutWrite.</summary>
    public uint Length { get; internal set; }
    /// <summary>dwFlags of the guest header at waveOutWrite (WHDR_BEGINLOOP/ENDLOOP are the ones that matter).</summary>
    public uint Flags { get; internal set; }
    public uint Loops { get; internal set; }
    public uint User { get; internal set; }
    /// <summary>Backend-private state (the HostWinmm native header mirror).</summary>
    public object? BackendState { get; set; }

    /// <summary>Copies the buffer contents out of the guest. Throws GuestMemoryException for unmapped memory.</summary>
    public byte[] ReadData() => Memory.ReadBytes(DataAddress, checked((int)Length));
}

/// <summary>Notified by a backend when a written buffer is finished (played, or returned by a reset). May be called from any thread but never with a backend lock held.</summary>
public interface IWaveOutSink
{
    void BufferDone(WaveOutBuffer buffer);
}

/// <summary>An open waveOut stream on a backend. Error codes are MMSYSERR_*/WAVERR_* values.</summary>
public interface IWaveOutDevice : IDisposable
{
    /// <summary>The numeric device id the stream is on (WAVE_MAPPER resolved).</summary>
    uint DeviceId { get; }
    uint Prepare(WaveOutBuffer buffer);
    uint Unprepare(WaveOutBuffer buffer);
    uint Write(WaveOutBuffer buffer);
    uint Reset();
    uint Pause();
    uint Restart();
    uint BreakLoop();
    /// <summary>The requested type when supported, else TIME_BYTES (as Windows does); <paramref name="typeReturned"/> says which. The value is the 32-bit union field.</summary>
    uint GetPosition(uint type, out uint typeReturned, out uint value);
    uint GetVolume(out uint volume);
    uint SetVolume(uint volume);
    /// <summary>WAVERR_STILLPLAYING while buffers are queued.</summary>
    uint Close();
}

/// <summary>A source of waveOut devices.</summary>
public interface IWaveOutBackend : IDisposable
{
    string Name { get; }
    uint DeviceCount { get; }
    uint GetCaps(uint deviceId, out WaveOutCaps? caps);
    /// <summary>Opens a stream, or only checks the format when <paramref name="flags"/> contains WAVE_FORMAT_QUERY (then <paramref name="device"/> is null).</summary>
    uint Open(uint deviceId, WaveOutFormat format, uint flags, IWaveOutSink sink, out IWaveOutDevice? device);
    /// <summary>Volume of a device addressed by id (no open handle).</summary>
    uint GetVolume(uint deviceId, out uint volume);
    uint SetVolume(uint deviceId, uint volume);
    string? GetErrorText(uint code);
}

/// <summary>mmsystem.h error texts, English (the same strings winmm returns).</summary>
internal static class WaveOutErrors
{
    public static string? Text(uint code) => code switch
    {
        0 => "The specified command was carried out.",
        1 => "Undefined external error.",
        2 => "A device ID has been used that is out of range for your system.",
        3 => "The driver was not enabled.",
        4 => "The specified device is already in use.  Wait until it is free, and then try again.",
        5 => "The specified device handle is invalid.",
        6 => "There is no driver installed on your system !\r\n",
        7 => "Not enough memory available for this task.  Quit one or more applications to increase available memory, and then try again.",
        8 => "This function is not supported.  Use the Capabilities function to determine which functions and messages the driver supports.",
        9 => "An error number was specified that is not defined in the system.",
        10 => "An invalid flag was passed to a system function.",
        11 => "An invalid parameter was passed to a system function.",
        12 => "The handle being used is simultaneously in use by another thread (e.g. callback).",
        32 => "The specified format is not supported or cannot be translated. Use the Capabilities function to determine the supported formats",
        33 => "Cannot perform this operation while media data is still playing. Reset the device, or wait until the data is finished playing.",
        34 => "The wave header was not prepared.  Use the Prepare function to prepare the header, and then try again.",
        35 => "Cannot open the specified device.  The device is synchronous and does not support more than one open at a time.",
        _ => null,
    };
}
