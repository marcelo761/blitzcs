using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class AudioDecoderTests
{
    [Fact]
    public void Decodes_generated_vorbis_fixture_to_finite_mono_samples()
    {
        var decoded = AudioDecoder.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generated-tone.ogg"));

        Assert.Equal(22050, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.True(decoded.Samples.Length > 1000);
        Assert.All(decoded.Samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.Contains(decoded.Samples, sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void Decodes_pcm_wave_with_metadata_and_interleaved_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-audio-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, MakePcm16Wave());
            var decoded = AudioDecoder.Decode(path);

            Assert.Equal(8000, decoded.SampleRate);
            Assert.Equal(2, decoded.Channels);
            Assert.Equal(4, decoded.Samples.Length);
            Assert.InRange(decoded.Samples[0], 0.49f, 0.51f);
            Assert.InRange(decoded.Samples[1], -0.51f, -0.49f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("unsupported.mp3")]
    public void Rejects_empty_or_unsupported_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => AudioDecoder.Decode(path));
    }

    [Fact]
    public void Reports_missing_audio_file()
    {
        Assert.Throws<FileNotFoundException>(() => AudioDecoder.Decode(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ogg")));
    }

    [Fact]
    public void Reports_malformed_supported_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blitz-audio-{Guid.NewGuid():N}.ogg");
        try
        {
            File.WriteAllText(path, "not an audio file");
            Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] MakePcm16Wave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(44 - 8);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(8000);
        writer.Write(32000);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(8);
        writer.Write((short)16384);
        writer.Write((short)-16384);
        writer.Write((short)8192);
        writer.Write((short)-8192);
        return stream.ToArray();
    }
}
