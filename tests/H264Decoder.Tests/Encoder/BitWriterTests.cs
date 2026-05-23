using H264Decoder.Bitstream;
using H264Decoder.Encoder.Bitstream;

namespace H264Decoder.Tests.Encoder;

public class BitWriterTests
{
    [Fact]
    public void WriteBits_SingleByte_MsbFirst()
    {
        var w = new BitWriter();
        w.WriteBits(0b10101100, 8);
        byte[] bytes = w.ToByteArray();
        Assert.Single(bytes);
        Assert.Equal((byte)0b10101100, bytes[0]);
    }

    [Fact]
    public void WriteBits_AcrossByteBoundary_PacksCorrectly()
    {
        var w = new BitWriter();
        w.WriteBits(0b101, 3);
        w.WriteBits(0b1111_0000, 8);
        w.WriteBit(1);
        byte[] bytes = w.ToByteArray();
        // 12 bits: 101_11110000_1 -> pad to 16 bits with one trailing zero.
        Assert.Equal(2, bytes.Length);
        Assert.Equal(0b10111110, bytes[0]);
        Assert.Equal(0b00010000, bytes[1]);
    }

    [Fact]
    public void RoundTrip_Random_BitsMatch()
    {
        var rng = new Random(42);
        var w = new BitWriter();
        var values = new List<(uint val, int len)>();
        for (int i = 0; i < 100; i++)
        {
            int len = rng.Next(1, 20);
            uint val = (uint)rng.NextInt64() & ((1u << len) - 1);
            values.Add((val, len));
            w.WriteBits(val, len);
        }
        byte[] bytes = w.ToByteArray();
        var r = new BitReader(bytes);
        foreach (var (val, len) in values)
        {
            Assert.Equal(val, r.ReadBits(len));
        }
    }

    [Fact]
    public void WriteRbspTrailingBits_AddsStopBitAndPadsToByteBoundary()
    {
        var w = new BitWriter();
        w.WriteBits(0b10101, 5);
        w.WriteRbspTrailingBits();
        byte[] bytes = w.ToByteArray();
        Assert.Single(bytes);
        Assert.Equal((byte)0b10101_100, bytes[0]);
    }
}
