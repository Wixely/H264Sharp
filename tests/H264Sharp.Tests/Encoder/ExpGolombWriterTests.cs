using H264Sharp.Decoder.Bitstream;
using H264Sharp.Encoder.Bitstream;

namespace H264Sharp.Tests.Encoder;

public class ExpGolombWriterTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(7u)]
    [InlineData(255u)]
    [InlineData(65535u)]
    public void WriteUe_RoundTrip(uint v)
    {
        var w = new BitWriter();
        ExpGolombWriter.WriteUe(w, v);
        w.WriteRbspTrailingBits();
        var r = new BitReader(w.ToByteArray());
        Assert.Equal(v, ExpGolomb.ReadUe(ref r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(123)]
    [InlineData(-321)]
    public void WriteSe_RoundTrip(int v)
    {
        var w = new BitWriter();
        ExpGolombWriter.WriteSe(w, v);
        w.WriteRbspTrailingBits();
        var r = new BitReader(w.ToByteArray());
        Assert.Equal(v, ExpGolomb.ReadSe(ref r));
    }

    [Fact]
    public void WriteUe_Zero_IsSingleBit1()
    {
        var w = new BitWriter();
        ExpGolombWriter.WriteUe(w, 0);
        w.WriteRbspTrailingBits();
        // ue(0) = "1" then trailing stop bit makes "1" already followed by zero pad — single 1 bit then RBSP trailing bit (1) then 6 zeros.
        Assert.Equal((byte)0b1_1_000000, w.ToByteArray()[0]);
    }
}
