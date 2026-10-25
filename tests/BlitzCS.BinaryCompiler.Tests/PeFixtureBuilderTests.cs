using System.Buffers.Binary;
using BlitzCS.BinaryCompiler.Tests.Support;
using Xunit;

namespace BlitzCS.BinaryCompiler.Tests;

public class PeFixtureBuilderTests
{
    [Fact]
    public void BuildsPe32Header()
    {
        var bytes = new PeFixtureBuilder().Export("f", 0).Build();
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal(0x014C, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x44)));
    }
}
