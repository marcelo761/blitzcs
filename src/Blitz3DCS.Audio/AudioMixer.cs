namespace Blitz3DCS.Audio;

/// <summary>Pull-driven software mixer for interleaved mono/stereo float PCM.</summary>
public sealed class AudioMixer
{
    private sealed class Channel
    {
        public DecodedAudio? Audio;
        public double Position;
        public int Volume;
        public int Pan;
        public int Frequency;
        public int LoopStart;
        public int LoopEnd;
        public bool Loop;
        public bool Paused;

        public bool IsPlaying => Audio is not null;
    }

    private readonly Channel[] _channels;
    private readonly int _outputSampleRate;

    public AudioMixer(int outputSampleRate, int channelCount)
    {
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        if (channelCount < 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        _outputSampleRate = outputSampleRate;
        _channels = new Channel[channelCount];
        for (var i = 0; i < channelCount; i++) _channels[i] = new Channel();
    }

    public int ChannelCount => _channels.Length;

    /// <summary>Starts a sound and returns its physical channel, or -1 when none is free.</summary>
    public int Play(DecodedAudio audio, int volume = 255, int pan = 128, int frequency = 0,
        int channel = -1, bool loop = false, bool paused = false)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ValidateAudio(audio);
        if (frequency < 0) throw new ArgumentOutOfRangeException(nameof(frequency));

        var index = channel == -1 ? FindFreeChannel() : ValidateChannel(channel);
        if (index < 0) return -1;

        var state = _channels[index];
        state.Audio = audio;
        state.Position = 0;
        state.Volume = ClampByte(volume);
        state.Pan = ClampByte(pan);
        state.Frequency = frequency;
        state.LoopStart = 0;
        state.LoopEnd = audio.Samples.Length / audio.Channels;
        state.Loop = loop;
        state.Paused = paused;
        return index;
    }

    /// <summary>Mixes the next interleaved stereo frame block into the supplied buffer.</summary>
    public void Render(Span<float> stereo)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Stereo output must contain complete frames.", nameof(stereo));
        stereo.Clear();
        var frames = stereo.Length / 2;
        for (var channelIndex = 0; channelIndex < _channels.Length; channelIndex++)
        {
            var channel = _channels[channelIndex];
            var audio = channel.Audio;
            if (audio is null || channel.Paused) continue;

            var samples = audio.Samples;
            var sourceChannels = audio.Channels;
            var sourceFrames = samples.Length / sourceChannels;
            var loopStart = channel.LoopStart;
            var loopEnd = channel.LoopEnd;
            var step = (double)(channel.Frequency == 0 ? audio.SampleRate : channel.Frequency) / _outputSampleRate;
            var leftGain = channel.Volume / 255f * (channel.Pan > 128 ? (255 - channel.Pan) / 127f : 1f);
            var rightGain = channel.Volume / 255f * (channel.Pan < 128 ? channel.Pan / 128f : 1f);
            var position = channel.Position;

            for (var frame = 0; frame < frames; frame++)
            {
                if (channel.Loop && position >= loopEnd)
                    position = loopStart + (position - loopStart) % (loopEnd - loopStart);
                else if (!channel.Loop && position >= sourceFrames)
                {
                    channel.Audio = null;
                    break;
                }

                var sourceFrame = (int)position;
                var nextFrame = sourceFrame + 1;
                if (channel.Loop && nextFrame >= loopEnd) nextFrame = loopStart;
                else if (nextFrame >= sourceFrames) nextFrame = sourceFrames - 1;
                var fraction = (float)(position - sourceFrame);
                var sampleOffset = sourceFrame * sourceChannels;
                var nextOffset = nextFrame * sourceChannels;
                var left = Interpolate(samples[sampleOffset], samples[nextOffset], fraction);
                var right = sourceChannels == 1
                    ? left
                    : Interpolate(samples[sampleOffset + 1], samples[nextOffset + 1], fraction);
                var outputOffset = frame * 2;
                stereo[outputOffset] += left * leftGain;
                stereo[outputOffset + 1] += right * rightGain;
                position += step;
            }

            if (channel.Audio is not null)
            {
                if (channel.Loop && position >= loopEnd)
                    position = loopStart + (position - loopStart) % (loopEnd - loopStart);
                else if (!channel.Loop && position >= sourceFrames)
                {
                    channel.Audio = null;
                    position = sourceFrames;
                }
                channel.Position = position;
            }
        }

        for (var i = 0; i < stereo.Length; i++)
            stereo[i] = Math.Clamp(stereo[i], -1f, 1f);
    }

    public void Stop(int channel)
    {
        ForChannels(channel, static state => state.Audio = null);
    }

    public void StopByAsset(DecodedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        foreach (var channel in _channels)
            if (ReferenceEquals(channel.Audio, audio)) channel.Audio = null;
    }

    public bool IsPlaying(int channel) => GetChannel(channel).IsPlaying;
    public int GetVolume(int channel) => GetChannel(channel).Volume;
    public int GetPan(int channel) => GetChannel(channel).Pan;
    public int GetFrequency(int channel)
    {
        var state = GetChannel(channel);
        return state.Frequency == 0 && state.Audio is not null ? state.Audio.SampleRate : state.Frequency;
    }
    public int GetPosition(int channel) => (int)GetChannel(channel).Position;

    public void SetVolume(int channel, int volume) => ForChannels(channel, state => state.Volume = ClampByte(volume));
    public void SetPan(int channel, int pan) => ForChannels(channel, state => state.Pan = ClampByte(pan));
    public void SetPaused(int channel, bool paused) => ForChannels(channel, state => state.Paused = paused);
    public void SetLoop(int channel, bool loop) => ForChannels(channel, state => state.Loop = loop);

    public void SetFrequency(int channel, int frequency)
    {
        if (frequency < 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        ForChannels(channel, state => state.Frequency = frequency);
    }

    public void SetPosition(int channel, int frame)
    {
        ForChannels(channel, state =>
        {
            var audio = state.Audio ?? throw new InvalidOperationException("Cannot seek a channel that is not playing.");
            var totalFrames = audio.Samples.Length / audio.Channels;
            if ((uint)frame >= (uint)totalFrames) throw new ArgumentOutOfRangeException(nameof(frame));
            state.Position = frame;
        });
    }

    public void SetLoopPoints(int channel, int startFrame, int endFrame)
    {
        ForChannels(channel, state =>
        {
            var audio = state.Audio ?? throw new InvalidOperationException("Cannot set loop points on a channel that is not playing.");
            var totalFrames = audio.Samples.Length / audio.Channels;
            if (startFrame < 0 || endFrame <= startFrame || endFrame > totalFrames)
                throw new ArgumentOutOfRangeException(nameof(endFrame), "Loop points must describe a nonempty range inside the sound.");
            state.LoopStart = startFrame;
            state.LoopEnd = endFrame;
        });
    }

    private int FindFreeChannel()
    {
        for (var i = 0; i < _channels.Length; i++)
            if (!_channels[i].IsPlaying) return i;
        return -1;
    }

    private Channel GetChannel(int index) => _channels[ValidateChannel(index)];

    private int ValidateChannel(int index)
    {
        if (index == -2) throw new ArgumentException("Use -2 only with a channel control operation.", nameof(index));
        if ((uint)index >= (uint)_channels.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return index;
    }

    private void ForChannels(int index, Action<Channel> action)
    {
        if (index == -2)
        {
            foreach (var channel in _channels) action(channel);
            return;
        }
        action(_channels[ValidateChannel(index)]);
    }

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);

    private static float Interpolate(float first, float second, float fraction) => first + (second - first) * fraction;

    private static void ValidateAudio(DecodedAudio audio)
    {
        if (audio.SampleRate <= 0) throw new ArgumentException("Audio sample rate must be positive.", nameof(audio));
        if (audio.Channels is not (1 or 2)) throw new ArgumentException("Only mono and stereo PCM are supported.", nameof(audio));
        if (audio.Samples.Length == 0 || audio.Samples.Length % audio.Channels != 0)
            throw new ArgumentException("Audio must contain complete, nonempty frames.", nameof(audio));
    }
}
