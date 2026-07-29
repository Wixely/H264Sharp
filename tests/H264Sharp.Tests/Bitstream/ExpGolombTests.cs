using H264Sharp.Decoder.Bitstream;

namespace H264Sharp.Tests.Bitstream;

public sealed class ExpGolombTests
{
    // Canonical ue(v) mapping from spec table 9-1:
    //   codeNum 0 -> "1"
    //   codeNum 1 -> "010"
    //   codeNum 2 -> "011"
    //   codeNum 3 -> "00100"
    //   codeNum 4 -> "00101"
    //   codeNum 5 -> "00110"
    //   codeNum 6 -> "00111"
    //   codeNum 7 -> "0001000"
    //   codeNum 8 -> "0001001"
    [Theory]
    [InlineData("1", 0u)]
    [InlineData("010", 1u)]
    [InlineData("011", 2u)]
    [InlineData("00100", 3u)]
    [InlineData("00101", 4u)]
    [InlineData("00110", 5u)]
    [InlineData("00111", 6u)]
    [InlineData("0001000", 7u)]
    [InlineData("0001001", 8u)]
    [InlineData("000010000", 15u)]
    [InlineData("00000000100000000", 255u)]
    public void ReadUe_MatchesSpecMapping(string bitString, uint expected)
    {
        byte[] data = BitsToBytes(bitString);
        var r = new BitReader(data);
        Assert.Equal(expected, ExpGolomb.ReadUe(ref r));
    }

    // se(v) mapping (spec table 9-3):
    //   codeNum 0 -> 0,   1 -> 1,   2 -> -1,   3 -> 2,   4 -> -2,   5 -> 3,   6 -> -3
    [Theory]
    [InlineData("1", 0)]
    [InlineData("010", 1)]
    [InlineData("011", -1)]
    [InlineData("00100", 2)]
    [InlineData("00101", -2)]
    [InlineData("00110", 3)]
    [InlineData("00111", -3)]
    public void ReadSe_MatchesSpecMapping(string bitString, int expected)
    {
        byte[] data = BitsToBytes(bitString);
        var r = new BitReader(data);
        Assert.Equal(expected, ExpGolomb.ReadSe(ref r));
    }

    [Theory]
    [InlineData("1", 1u, 0u)]   // te with x=1: read 1 -> 0
    [InlineData("0", 1u, 1u)]   // te with x=1: read 0 -> 1
    [InlineData("00100", 5u, 3u)] // te with x>1 falls through to ue
    public void ReadTe_MatchesSpecMapping(string bitString, uint x, uint expected)
    {
        byte[] data = BitsToBytes(bitString);
        var r = new BitReader(data);
        Assert.Equal(expected, ExpGolomb.ReadTe(ref r, x));
    }

    [Fact]
    public void ReadUe_31LeadingZeros_DecodesMaxRepresentable()
    {
        // 31 leading zeros is the largest legal ue(v) (codeNum up to 2^32-2). With a zero suffix
        // the value is (1<<31) - 1. This must decode, not be rejected.
        string bits = new string('0', 31) + "1" + new string('0', 31);
        var r = new BitReader(BitsToBytes(bits));
        Assert.Equal((1u << 31) - 1u, ExpGolomb.ReadUe(ref r));
    }

    [Fact]
    public void ReadUe_32LeadingZeros_Throws()
    {
        // 32 leading zeros would need codeNum >= 2^32-1, which is not representable; C# shift
        // masking would otherwise alias it to a small value, so it must be rejected.
        byte[] data = BitsToBytes(new string('0', 32) + "1");
        // BitReader is a ref struct (can't be captured), so construct it inside the delegate.
        Assert.Throws<InvalidDataException>(() => { var r = new BitReader(data); ExpGolomb.ReadUe(ref r); });
    }

    [Fact]
    public void ConsecutiveUeCallsAdvancePosition()
    {
        // "1" "010" "00101" -> codeNums 0, 1, 4
        byte[] data = BitsToBytes("1010001011");
        var r = new BitReader(data);
        Assert.Equal(0u, ExpGolomb.ReadUe(ref r));
        Assert.Equal(1u, ExpGolomb.ReadUe(ref r));
        Assert.Equal(4u, ExpGolomb.ReadUe(ref r));
    }

    private static byte[] BitsToBytes(string bits)
    {
        int n = (bits.Length + 7) / 8;
        byte[] result = new byte[n];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }
        return result;
    }
}
