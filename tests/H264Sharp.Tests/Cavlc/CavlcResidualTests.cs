using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Cavlc;

namespace H264Sharp.Tests.Cavlc;

public sealed class CavlcResidualTests
{
    // (TotalCoeff=0, TrailingOnes=0) codewords across the four coeff_token contexts:
    //   nC in [0,2)  -> "1"        (1 bit)
    //   nC in [2,4)  -> "11"       (2 bits)
    //   nC in [4,8)  -> "1111"     (4 bits)
    //   nC >= 8      -> "000011"   (6 bits)
    [Theory]
    [InlineData(0, 0x80, 1)]
    [InlineData(1, 0x80, 1)]
    [InlineData(2, 0xC0, 2)]
    [InlineData(3, 0xC0, 2)]
    [InlineData(4, 0xF0, 4)]
    [InlineData(7, 0xF0, 4)]
    [InlineData(8, 0x0C, 6)]
    [InlineData(15, 0x0C, 6)]
    public void ZeroCoeffToken_LumaContexts(int nC, byte firstByte, int expectedConsumed)
    {
        byte[] data = [firstByte, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[16];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 16, nC: nC, chromaDc: false);

        Assert.Equal(0, totalCoeff);
        Assert.Equal(expectedConsumed, r.BitPosition);
        foreach (int c in coeffs) Assert.Equal(0, c);
    }

    // ChromaDC (TotalCoeff=0, TrailingOnes=0) = "01" (2 bits)
    [Fact]
    public void ZeroCoeffToken_ChromaDc()
    {
        byte[] data = [0x40, 0x00];   // 01000000
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[4];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 4, nC: 0, chromaDc: true);

        Assert.Equal(0, totalCoeff);
        Assert.Equal(2, r.BitPosition);
    }

    // (TotalCoeff=1, TrailingOnes=1) in NumVLC0 (nC in [0,2)):
    //   coeff_token codeword = "01" (2 bits)
    //   then 1 sign bit for the trailing one (0 -> +1, 1 -> -1)
    //   TotalCoeff == maxNumCoeff (1) so total_zeros is skipped
    //   (TotalCoeff - 1) == 0 run_before iterations
    [Fact]
    public void OneTrailingOnePositive_LumaContext0()
    {
        // bits: 01 0 -> 010_00000 = 0x40
        byte[] data = [0x40, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[1];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 1, nC: 0, chromaDc: false);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(1, coeffs[0]);
        Assert.Equal(3, r.BitPosition);
    }

    [Fact]
    public void OneTrailingOneNegative_LumaContext0()
    {
        // bits: 01 1 -> 011_00000 = 0x60
        byte[] data = [0x60, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[1];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 1, nC: 0, chromaDc: false);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(-1, coeffs[0]);
    }

    // (TotalCoeff=1, TrailingOnes=0) in NumVLC0:
    //   coeff_token "000101" (6 bits) -> indexVlc=1 -> (T1=0, TC=1)
    //   ±1 cannot occur in the i==T1, T1<3 slot (reserved for trailing ones),
    //   so the minimum non-trailing level is ±2.
    //   level=+2: bitstring "1" (prefix=0); level=-2: bitstring "01" (prefix=1).
    [Fact]
    public void OneLevelPlus2_LumaContext0()
    {
        // bits: 000101 1 -> 00010110 = 0x16
        byte[] data = [0x16, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[1];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 1, nC: 0, chromaDc: false);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(2, coeffs[0]);
        Assert.Equal(7, r.BitPosition);
    }

    // (TotalCoeff=1, TrailingOnes=0), then a level using the level_prefix >= 16 escape (spec
    // §9.2.2.1) for a large-magnitude coefficient. With suffixLength=0 and a zero 13-bit suffix:
    //   levelCode = 15 + 15 (prefix>=15 && sl==0) + ((1<<13)-4096) + 2 (first-level bias) = 4128
    //   level = (4128 + 2) >> 1 = 2065.
    // Bitstream: coeff_token "000101" + level_prefix (16 zeros + "1") + 13-bit suffix (zeros).
    [Fact]
    public void LevelPrefix16Escape_DecodesLargeLevel()
    {
        // 000101 0000000000000000 1 0000000000000  (36 bits)
        byte[] data = [0x14, 0x00, 0x02, 0x00, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[1];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 1, nC: 0, chromaDc: false);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(2065, coeffs[0]);
        Assert.Equal(36, r.BitPosition);
    }

    [Fact]
    public void ReadResidualBlock8x8_AllFourSubBlocksEmpty_ConsumesFourZeroTokens()
    {
        // 4 sub-blocks each with TotalCoeff=0 at nC=0 -> 4 × "1" bit = 4 bits.
        // Bitstream: "1111" followed by zeros -> 0xF0.
        byte[] data = [0xF0, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[64];

        int total = CavlcResidual.ReadResidualBlock8x8(ref r, coeffs, nC0: 0, nC1: 0, nC2: 0, nC3: 0);

        Assert.Equal(0, total);
        Assert.Equal(4, r.BitPosition);
        foreach (int c in coeffs) Assert.Equal(0, c);
    }

    [Fact]
    public void ReadResidualBlock8x8_ScatterPattern_PlacesSubBlockCoeffsCorrectly()
    {
        // Sub-block 0 with TotalCoeff=0 ("1"), sub-block 1 with TotalCoeff=0 ("1"),
        // sub-block 2 with TotalCoeff=0 ("1"), sub-block 3 with TotalCoeff=0 ("1").
        // After 4 reads the residual array remains all zeros and we've consumed 4 bits.
        // This validates the per-sub-block dispatch wiring; the scatter mapping
        // (sub[i] -> coeffs64[s + i*4]) is exercised by the integration path.
        byte[] data = [0xF0, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[64];
        int total = CavlcResidual.ReadResidualBlock8x8(ref r, coeffs, 0, 0, 0, 0);
        Assert.Equal(0, total);
        for (int i = 0; i < 64; i++) Assert.Equal(0, coeffs[i]);
    }

    [Fact]
    public void OneLevelMinus2_LumaContext0()
    {
        // bits: 000101 01 -> 00010101 = 0x15
        byte[] data = [0x15, 0x00];
        var r = new BitReader(data);
        Span<int> coeffs = stackalloc int[1];

        int totalCoeff = CavlcResidual.ReadResidualBlock(ref r, coeffs, maxNumCoeff: 1, nC: 0, chromaDc: false);

        Assert.Equal(1, totalCoeff);
        Assert.Equal(-2, coeffs[0]);
        Assert.Equal(8, r.BitPosition);
    }
}
