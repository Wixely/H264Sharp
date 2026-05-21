using H264Decoder.Bitstream;

namespace H264Decoder.Tests.Bitstream;

public sealed class BitReaderTests
{
    [Fact]
    public void ReadsIndividualBitsMsbFirst()
    {
        byte[] data = [0b1011_0100];
        var r = new BitReader(data);
        Assert.Equal(1u, r.ReadBit());
        Assert.Equal(0u, r.ReadBit());
        Assert.Equal(1u, r.ReadBit());
        Assert.Equal(1u, r.ReadBit());
        Assert.Equal(0u, r.ReadBit());
        Assert.Equal(1u, r.ReadBit());
        Assert.Equal(0u, r.ReadBit());
        Assert.Equal(0u, r.ReadBit());
    }

    [Fact]
    public void ReadBitsCrossesByteBoundary()
    {
        byte[] data = [0b1010_1010, 0b1100_1100];
        var r = new BitReader(data);
        Assert.Equal(0b1010u, r.ReadBits(4));
        Assert.Equal(0b1010_1100u, r.ReadBits(8));   // 4 bits from byte 0, 4 from byte 1
        Assert.Equal(0b1100u, r.ReadBits(4));
    }

    [Fact]
    public void ReadBitsZeroIsNoOp()
    {
        byte[] data = [0xFF];
        var r = new BitReader(data);
        Assert.Equal(0u, r.ReadBits(0));
        Assert.Equal(0, r.BitPosition);
    }

    [Fact]
    public void ReadBeyondEndThrows()
    {
        byte[] data = [0x00];
        var r = new BitReader(data);
        r.ReadBits(8);
        Assert.Throws<InvalidDataException>(() =>
        {
            var rr = new BitReader([0x00]);
            rr.ReadBits(9);
        });
    }

    [Fact]
    public void ByteAlignAdvancesToNextByte()
    {
        byte[] data = [0xFF, 0x55];
        var r = new BitReader(data);
        r.ReadBits(3);
        r.ByteAlign();
        Assert.Equal(8, r.BitPosition);
        Assert.Equal(0x55u, r.ReadBits(8));
    }

    [Fact]
    public void ByteAlignOnBoundaryIsNoOp()
    {
        byte[] data = [0xAB, 0xCD];
        var r = new BitReader(data);
        r.ReadBits(8);
        r.ByteAlign();
        Assert.Equal(8, r.BitPosition);
    }

    [Fact]
    public void MoreRbspData_ReturnsFalseWhenOnlyTrailingBitsRemain()
    {
        // payload ends with stop bit 1 followed by alignment zeros
        // bits: 1010 | 1000_0000 -> after reading 4 bits, remainder is "1000_0000" which is just rbsp_trailing_bits
        byte[] data = [0b1010_1000];
        var r = new BitReader(data);
        r.ReadBits(4);
        Assert.False(r.MoreRbspData());
    }

    [Fact]
    public void MoreRbspData_ReturnsTrueWhenPayloadFollows()
    {
        // After reading 4 bits we still have "1100" then "1000_0000" trailing — there is more data.
        byte[] data = [0b1010_1100, 0b1000_0000];
        var r = new BitReader(data);
        r.ReadBits(4);
        Assert.True(r.MoreRbspData());
    }

    [Fact]
    public void MoreRbspData_StateRestoredAfterPeek()
    {
        byte[] data = [0b1010_1100, 0b1000_0000];
        var r = new BitReader(data);
        r.ReadBits(4);
        int before = r.BitPosition;
        _ = r.MoreRbspData();
        Assert.Equal(before, r.BitPosition);
    }
}
