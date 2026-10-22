using Blitz3DCS.Audio;
using Xunit;

namespace Blitz3DCS.Audio.Tests;

public sealed class NativeAudioTests
{
    [Fact]
    public void Native_asset_and_channel_failures_use_zero_sentinel()
    {
        Assert.Equal(0, NativeAudio.HandleOrZero(-1));
        Assert.Equal(0, NativeAudio.HandleOrZero(0));
        Assert.Equal(4097, NativeAudio.HandleOrZero(4097));
        Assert.Equal(0, NativeAudio.PlaySound(int.MaxValue));
    }
}
