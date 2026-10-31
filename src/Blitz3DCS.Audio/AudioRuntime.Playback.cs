namespace Blitz3DCS.Audio;

public sealed record AudioStatistics(int Assets, int Channels, long SubmittedFrames, long NonSilentFrames, double PcmEnergy)
{
    public long StreamUnderruns { get; init; }
    public long StreamBufferBytes { get; init; }
}

public sealed partial class AudioRuntime
{
    private sealed class Asset(DecodedAudio? audio, BufferedAudioStream? buffered, bool stream, bool loop)
    {
        internal readonly DecodedAudio? Audio = audio;
        internal readonly BufferedAudioStream? Buffered = buffered;
        internal readonly bool Stream = stream;
        internal bool Loop = loop;
        internal int Volume = stream ? 128 : 255;
        internal int Pan = 128;
        internal int Frequency;
    }
    private readonly Dictionary<int, Asset> _assets = [];
    private readonly Dictionary<int, int> _channels = [];
    private AudioMixer? _mixer;
    private int[] _slotTokens = [];
    private long _nextToken = 4096;
    private int _errorCode;
    private long _session;
    private SdlPlayback? _playback;
    private Thread? _pumpThread;
    private string? _pumpFailure;
    private ManualResetEventSlim? _pumpStop;
    private long _submittedFrames, _nonSilentFrames;
    private double _pcmEnergy;

    public AudioStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                PruneChannels();
                return new(_assets.Count, _channels.Count, _submittedFrames, _nonSilentFrames, _pcmEnergy)
                {
                    StreamUnderruns = _mixer?.StreamUnderruns ?? 0,
                    StreamBufferBytes = _assets.Values.Sum(static asset => asset.Buffered?.RetainedBufferBytes ?? 0),
                };
            }
        }
    }
    public int GetError() { lock (_gate) return _errorCode; }
    private int Fail(int code, string message, int value = 0) { _errorCode = code; LastError = message; return value; }
    private int Success(int value = 1) { _errorCode = 0; LastError = null; return value; }
    private int Token()
    {
        if (_nextToken > int.MaxValue) throw new InvalidOperationException("Audio handle token space is exhausted.");
        return (int)_nextToken++;
    }

    public int StreamOpen(string path, int mode, int offset = 0, int length = 0) => Load(path, mode, offset, length, true);
    public int SampleLoad(string path, int mode = 0) => Load(path, mode, 0, 0, false);
    private int Load(string path, int mode, int offset, int length, bool stream)
    {
        long session;
        lock (_gate)
        {
            session = _session;
            if (_mixer is null) return Fail(2, "Audio is not initialized.");
            if ((mode & ~3) != 0 || mode == 3 || offset != 0 || length is not (0 or -1))
                return Fail(18, "Only file-backed LOOP_OFF(1)/LOOP_NORMAL(2), zero offset and full file are implemented.");
        }
        // File IO and codec allocations run outside the render/control gate.
        DecodedAudio? decoded = null;
        BufferedAudioStream? buffered = null;
        try
        {
            var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar);
            if (stream) buffered = _streamLoader(normalizedPath);
            else decoded = AudioDecoder.Decode(normalizedPath);
        }
        catch (FileNotFoundException error) { lock (_gate) return session == _session ? Fail(9, error.Message) : 0; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { lock (_gate) return session == _session ? Fail(10, error.Message) : 0; }

        var disposeStream = false;
        var result = 0;
        lock (_gate)
        {
            if (_mixer is null || session != _session)
            {
                disposeStream = buffered is not null;
                result = session == _session ? Fail(2, "Audio session is no longer initialized.") : 0;
            }
            else if (!stream && decoded?.Channels is not (1 or 2))
            {
                result = Fail(10, "Only mono/stereo audio is supported.");
            }
            else if (_nextToken > int.MaxValue)
            {
                disposeStream = buffered is not null;
                result = Fail(12, "Audio handle token space is exhausted.");
            }
            else
            {
                var token = Token();
                _assets.Add(token, new(decoded, buffered, stream, (mode & 2) != 0));
                result = stream ? token : Success(token);
            }
        }
        if (disposeStream) buffered!.Dispose();
        return result;
    }

    public int StreamPlay(int channel, int handle) => Play(handle, channel, true);
    public int PlaySample(int handle) => Play(handle, -1, false);
    public int PlaySample3D(int handle, AudioEmitterState emitter) => Play(handle, -1, false, emitter);
    private int Play(int handle, int channel, bool stream, AudioEmitterState? emitter = null)
    {
        lock (_gate)
        {
            if (_mixer is null) return Fail(2, "Audio is not initialized.", -1);
            if (!_assets.TryGetValue(handle, out var asset) || asset.Stream != stream)
                return Fail(14, "Invalid or deleted audio asset handle.", -1);
            if (_nextToken > int.MaxValue) return Fail(12, "Audio handle token space is exhausted.", -1);
            PruneChannels();
            var slot = -1;
            if (channel != -1 && !_channels.TryGetValue(channel, out slot))
                return Fail(14, "Invalid channel handle.", -1);
            slot = stream
                ? _mixer.Play(asset.Buffered!, asset.Volume, asset.Pan, asset.Frequency, slot, asset.Loop)
                : _mixer.Play(asset.Audio!, asset.Volume, asset.Pan, asset.Frequency, slot, asset.Loop);
            if (slot < 0) return Fail(16, "All software audio channels are allocated.", -1);
            if (emitter is { } spatialEmitter)
            {
                try { _mixer.SetChannel3D(slot, spatialEmitter); }
                catch (ArgumentException error)
                {
                    _mixer.Stop(slot);
                    return Fail(14, error.Message, -1);
                }
            }
            if (_slotTokens[slot] != 0) _channels.Remove(_slotTokens[slot]);
            var token = Token();
            _slotTokens[slot] = token;
            _channels[token] = slot;
            PublishChannels();
            try { EnsurePump(); }
            catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { _mixer.Stop(slot); PruneChannels(); return Fail(3, error.Message, -1); }
            return Success(token);
        }
    }

    public int StreamStop(int handle)
    {
        lock (_gate)
        {
            if (_mixer is null || !_assets.TryGetValue(handle, out var asset) || !asset.Stream)
                return Fail(14, "Invalid stream handle.");
            _mixer.StopByAsset(asset.Buffered!); PruneChannels(); return 1;
        }
    }
    public int StreamClose(int handle) => Free(handle, true);
    public int SampleFree(int handle) => Free(handle, false);
    private int Free(int handle, bool stream)
    {
        BufferedAudioStream? disposeStream;
        var result = 0;
        lock (_gate)
        {
            if (_mixer is null || !_assets.TryGetValue(handle, out var asset) || asset.Stream != stream)
                return Fail(14, "Invalid or deleted audio asset handle.");
            if (stream)
            {
                disposeStream = asset.Buffered;
                _mixer.StopByAsset(asset.Buffered!);
            }
            else
            {
                disposeStream = null;
                _mixer.StopByAsset(asset.Audio!);
            }
            _assets.Remove(handle);
            PruneChannels();
            result = Success();
        }
        disposeStream?.Dispose();
        return result;
    }
    public int StopSound(int channel)
    {
        lock (_gate)
        {
            PruneChannels();
            if (_mixer is null || !_channels.Remove(channel, out var slot)) return Fail(14, "Invalid or stopped channel.");
            _mixer.Stop(slot); _paused.Remove(channel); _slotTokens[slot] = 0; PublishChannels(); return Success();
        }
    }
    /// <summary>
    /// Lock-free: reads the channel-token snapshot republished (under the gate) after every change to the live
    /// channel set, so a game-thread poll never waits for the mixer section that holds the gate.
    /// </summary>
    public int IsPlaying(int channel)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.Ready) return FailUnlocked(2, "Audio is not initialized.");
        return snapshot.Tokens.Contains(channel) ? SuccessUnlocked() : FailUnlocked(14, "Invalid or stopped channel.");
    }
    private sealed class ChannelSnapshot(bool ready, HashSet<int> tokens)
    {
        internal readonly bool Ready = ready;
        internal readonly HashSet<int> Tokens = tokens;
    }
    private ChannelSnapshot _snapshot = new(false, []);
    private void PublishChannels() =>
        Volatile.Write(ref _snapshot, new ChannelSnapshot(_mixer is not null, new HashSet<int>(_channels.Keys)));
    private int FailUnlocked(int code, string message) { Volatile.Write(ref _errorCode, code); LastError = message; return 0; }
    private int SuccessUnlocked() { Volatile.Write(ref _errorCode, 0); LastError = null; return 1; }
    public int SetVolume(int channel, int volume) => Control(channel, mixer => mixer.SetVolume(Slot(channel), volume is >= 0 and <= 255 ? volume : 0));
    public int SetPan(int channel, int pan) => pan is < 0 or > 255 ? InvalidParameter("Pan must be between 0 and 255.")
        : Control(channel, mixer => mixer.SetPan(Slot(channel), pan));
    public int SetFrequency(int channel, int frequency) => frequency <= 0 ? InvalidParameter("Frequency must be positive.")
        : Control(channel, mixer => mixer.SetFrequency(Slot(channel), frequency));
    public int SetPaused(int channel, int paused) => Control(channel, mixer => { mixer.SetPaused(Slot(channel), paused != 0); _paused[channel] = paused != 0; });
    public int GetVolume(int channel) => Query(channel, mixer => mixer.GetVolume(Slot(channel)));
    public int GetPan(int channel) => Query(channel, mixer => mixer.GetPan(Slot(channel)));
    public int GetFrequency(int channel) => Query(channel, mixer => mixer.GetFrequency(Slot(channel)));
    public int GetPaused(int channel) { lock (_gate) return _paused.TryGetValue(channel, out var value) && _channels.ContainsKey(channel) ? Success(value ? 1 : 0) : Query(channel, _ => 0); }
    private readonly Dictionary<int, bool> _paused = [];
    private int Slot(int token) => _channels[token];
    private int InvalidParameter(string message) { lock (_gate) return Fail(14, message); }
    private int Control(int channel, Action<AudioMixer> action)
    {
        lock (_gate)
        {
            PruneChannels();
            if (channel == -3) return Fail(18, "FSOUND_ALL controls are not implemented.");
            if (_mixer is null) return Fail(2, "Audio is not initialized.");
            if (!_channels.ContainsKey(channel)) return Fail(14, "Invalid or stopped channel.");
            try { action(_mixer); return Success(); }
            catch (ArgumentException error) { return Fail(14, error.Message); }
        }
    }
    private int Query(int channel, Func<AudioMixer, int> action)
    {
        lock (_gate)
        {
            PruneChannels();
            return _mixer is null ? Fail(2, "Audio is not initialized.") : _channels.ContainsKey(channel) ? Success(action(_mixer)) : Fail(14, "Invalid or stopped channel.");
        }
    }
    public int SetPosition(int channel, int frame) => Control(channel, mixer => mixer.SetPosition(Slot(channel), frame));
    public int GetPosition(int channel) => Query(channel, mixer => mixer.GetPosition(Slot(channel)));
    public int SetLoop(int channel, bool loop) => Control(channel, mixer => mixer.SetLoop(Slot(channel), loop));
    public int SampleLoop(int handle, bool loop) => EditSample(handle, asset => asset.Loop = loop);
    public int SampleVolume(int handle, float volume) => EditSample(handle, asset => asset.Volume = (int)(Math.Clamp(volume, 0, 1) * 255));
    public int SamplePan(int handle, float pan) => EditSample(handle, asset => asset.Pan = (int)((Math.Clamp(pan, -1, 1) + 1) * 127.5f));
    public int SampleFrequency(int handle, int frequency) => frequency <= 0 ? InvalidParameter("Frequency must be positive.") : EditSample(handle, asset => asset.Frequency = frequency);
    private int EditSample(int handle, Action<Asset> edit)
    {
        lock (_gate)
        {
            if (!_assets.TryGetValue(handle, out var asset) || asset.Stream) return Fail(14, "Invalid sample handle.");
            edit(asset); return Success();
        }
    }

    /// <summary>Deterministic PCM inspection; the device pump calls the same mixer.</summary>
    public void Render(Span<float> stereo)
    {
        lock (_gate)
        {
            if (_mixer is null) throw new InvalidOperationException("Audio is not initialized.");
            _mixer.Render(stereo); PruneChannels();
        }
    }
    private void PruneChannels()
    {
        var changed = false;
        for (var slot = 0; slot < _slotTokens.Length; slot++)
        {
            var token = _slotTokens[slot];
            if (token != 0 && (_mixer is null || !_mixer.IsPlaying(slot)))
            {
                _channels.Remove(token); _paused.Remove(token); _slotTokens[slot] = 0; changed = true;
            }
        }
        if (changed) PublishChannels();
    }
    private void EnsurePump()
    {
        if (_pumpFailure is not null) throw new InvalidOperationException(_pumpFailure);
        if (_sdl is not SdlAudio || _pumpThread is not null) return;
        _playback = new SdlPlayback(_device, _mixRate);
        var stop = new ManualResetEventSlim();
        _pumpStop = stop;
        var playback = _playback;
        _pumpThread = new Thread(() => Pump(stop, playback)) { IsBackground = true, Name = "Blitz3DCS audio producer" };
        _pumpThread.Start();
    }
    private void Pump(ManualResetEventSlim stop, SdlPlayback playback)
    {
        try
        {
            while (!stop.Wait(4))
            {
                lock (_gate)
                {
                    if (stop.IsSet || _mixer is null) break;
                    var queued = playback.QueuedBytes;
                    if (queued < 0) throw new InvalidOperationException("SDL audio queue query failed.");
                    if (queued >= 8192) continue;
                    _mixer.Render(playback.Buffer);
                    playback.Submit();
                    _submittedFrames += playback.Buffer.Length / 2;
                    for (var i = 0; i < playback.Buffer.Length; i += 2)
                    {
                        var left = playback.Buffer[i]; var right = playback.Buffer[i + 1];
                        _pcmEnergy += left * left + right * right;
                        if (left != 0 || right != 0) _nonSilentFrames++;
                    }
                    PruneChannels();
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            lock (_gate)
            {
                _pumpFailure = "Audio producer failed: " + error.Message;
                Fail(3, _pumpFailure);
                _mixer?.Stop(-2);
                PruneChannels();
            }
        }

    }
}
