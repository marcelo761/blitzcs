using Blitz3DCS.Audio;
using System.Runtime.InteropServices;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioRuntimeTests
{
    [Fact]
    public void Init_opens_device_and_close_releases_resources_once()
    {
        var sdl = new FakeSdl { Device = 17 };
        using var runtime = new AudioRuntime(sdl);

        Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 32, 0));
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 32, 0));
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(48000, 0, 0));
        Assert.Equal("Audio is initialized with different settings; close it before reinitializing.", runtime.LastError);
        Assert.True(runtime.IsInitialized);
        Assert.Equal(44100, sdl.Spec.Frequency);
        Assert.Equal(2, sdl.Spec.Channels);

        runtime.FSOUND_Close();
        runtime.FSOUND_Close();
        Assert.False(runtime.IsInitialized);
        Assert.Equal(new uint[] { 17 }, sdl.ClosedDevices);
        Assert.Equal(1, sdl.QuitCount);
    }

    [Fact]
    public void Init_rejects_negative_channels()
    {
        var sdl = new FakeSdl { Device = 1 };
        using var runtime = new AudioRuntime(sdl);
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(44100, -1, 0));
        Assert.Equal(0, sdl.InitCount);
    }

    [Fact]
    public void Different_settings_can_be_used_after_close()
    {
        var sdl = new FakeSdl { Device = 21 };
        using var runtime = new AudioRuntime(sdl);
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 32, 0));
        runtime.FSOUND_Close();
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(48000, 0, 0));
        Assert.Equal(48000, sdl.Spec.Frequency);
        Assert.Equal(2, sdl.InitCount);
    }

    [Fact]
    public void Init_rejects_unimplemented_flags_and_preserves_sdl_error()
    {
        var sdl = new FakeSdl { InitResult = false };
        using var runtime = new AudioRuntime(sdl);
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(44100, 1, 0x80));
        Assert.Equal("Unsupported FSOUND_Init flags; only flags=0 is currently implemented.", runtime.LastError);
        Assert.Equal(0, sdl.InitCount);
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(44100, 1, 0));
        Assert.Equal("fake SDL error", runtime.LastError);
    }

    [Fact]
    public void Sdl_audio_spec_matches_native_abi_layout()
    {
        Assert.Equal(12, Marshal.SizeOf<AudioRuntime.SdlAudioSpec>());
        Assert.Equal((nint)0, Marshal.OffsetOf<AudioRuntime.SdlAudioSpec>(nameof(AudioRuntime.SdlAudioSpec.Format)));
        Assert.Equal((nint)4, Marshal.OffsetOf<AudioRuntime.SdlAudioSpec>(nameof(AudioRuntime.SdlAudioSpec.Channels)));
        Assert.Equal((nint)8, Marshal.OffsetOf<AudioRuntime.SdlAudioSpec>(nameof(AudioRuntime.SdlAudioSpec.Frequency)));
    }

    [Fact]
    public void Native_loader_failure_becomes_failure_result_and_diagnostic()
    {
        var sdl = new FakeSdl { InitException = new DllNotFoundException("SDL3 test loader failure") };
        using var runtime = new AudioRuntime(sdl);
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(44100, 32, 0));
        Assert.Contains("SDL3 audio initialization failed: SDL3 test loader failure", runtime.LastError);
        Assert.Equal(0, sdl.QuitCount);
        Assert.False(runtime.IsInitialized);
    }

    [Fact]
    public void Handle_tokens_never_wrap_and_reuse_old_ids()
    {
        using var table = new ManagedHandleTable(int.MaxValue);
        var releases = new List<nint>();
        var token = table.Add((nint)1, releases.Add);
        Assert.Equal(int.MaxValue, token);
        Assert.Throws<InvalidOperationException>(() => table.Add((nint)2, releases.Add));
        table.Clear();
        Assert.Equal(new nint[] { 1 }, releases);
    }

    [Fact]
    public void Clear_releases_remaining_resources_even_when_one_release_throws()
    {
        using var table = new ManagedHandleTable();
        var released = new List<nint>();
        table.Add((nint)1, _ => throw new InvalidOperationException("release failed"));
        table.Add((nint)2, released.Add);
        Assert.Throws<AggregateException>(table.Clear);
        Assert.Equal(new nint[] { 2 }, released);
        Assert.False(table.TryGet(1, out _));
        Assert.False(table.TryGet(2, out _));
    }

    [Fact]
    public void Init_failure_releases_subsystem_and_allows_retry()
    {
        var sdl = new FakeSdl { Device = 0 };
        using var runtime = new AudioRuntime(sdl);

        Assert.Equal((sbyte)0, runtime.FSOUND_Init(48000, 16, 0));
        Assert.False(runtime.IsInitialized);
        Assert.Equal(1, sdl.QuitCount);
        sdl.Device = 18;
        Assert.Equal((sbyte)1, runtime.FSOUND_Init(48000, 16, 0));
    }

    [Fact]
    public void Invalid_inputs_do_not_touch_sdl_and_init_failure_is_clean()
    {
        var sdl = new FakeSdl { InitResult = false };
        using var runtime = new AudioRuntime(sdl);
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(0, 1, 0));
        Assert.Equal((sbyte)0, runtime.FSOUND_Init(44100, 1, 0));
        Assert.Equal(1, sdl.InitCount);
        Assert.Equal(0, sdl.QuitCount);
    }

    [Fact]
    public void Managed_handles_preserve_native_width_and_invalidate_on_remove()
    {
        var table = new ManagedHandleTable();
        var pointer = IntPtr.Size == 8 ? unchecked((nint)0x1234567887654321L) : (nint)0x12345678;
        var releases = new List<nint>();
        var token = table.Add(pointer, releases.Add);
        Assert.NotEqual(0, token);
        Assert.True(table.TryGet(token, out var actual));
        Assert.Equal(pointer, actual);
        Assert.True(table.Remove(token));
        Assert.False(table.TryGet(token, out _));
        Assert.Equal(new[] { pointer }, releases);
    }

    [Fact]
    public void Managed_handles_dispose_all_resources_exactly_once()
    {
        var table = new ManagedHandleTable();
        var releases = new List<nint>();
        var first = table.Add((nint)1, releases.Add);
        var second = table.Add((nint)2, releases.Add);
        Assert.True(table.Remove(first));
        table.Dispose();
        table.Clear();
        Assert.False(table.Remove(second));
        Assert.Equal(new nint[] { 1, 2 }, releases);
    }

    [Fact]
    public void Native_sdl3_dummy_driver_initializes_and_closes()
    {
        var previousDriver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        try
        {
            using var runtime = new AudioRuntime();
            Assert.Equal((sbyte)1, runtime.FSOUND_Init(44100, 32, 0));
            Assert.True(runtime.IsInitialized);
            runtime.FSOUND_Close();
            Assert.False(runtime.IsInitialized);
        }
        finally { Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", previousDriver); }
    }

    private sealed class FakeSdl : ISdlAudio
    {
        public bool InitResult { get; set; } = true;
        public Exception? InitException { get; set; }
        public uint Device { get; set; }
        public int InitCount { get; private set; }
        public int QuitCount { get; private set; }
        public AudioRuntime.SdlAudioSpec Spec { get; private set; }
        public List<uint> ClosedDevices { get; } = [];
        public bool InitAudio() { InitCount++; if (InitException is not null) throw InitException; return InitResult; }
        public uint OpenDefaultPlayback(in AudioRuntime.SdlAudioSpec spec) { Spec = spec; return Device; }
        public string GetError() => "fake SDL error";
        public void CloseAudioDevice(uint device) => ClosedDevices.Add(device);
        public void QuitAudio() => QuitCount++;
    }
}
