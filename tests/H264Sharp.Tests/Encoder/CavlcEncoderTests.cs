using H264Sharp.Decoder.Bitstream;
using H264Sharp.Encoder.Bitstream;
using H264Sharp.Encoder.Cavlc;

namespace H264Sharp.Tests.Encoder;

public class CavlcEncoderTests
{
    private static void RoundTripBlock(int[] coeffs, int nC, int maxNumCoeff = 16, bool chromaDc = false)
    {
        var w = new BitWriter();
        CavlcEncoder.EncodeResidualBlock(w, coeffs, maxNumCoeff, nC, chromaDc);
        // RBSP trailing bits not needed for residual-only round-trip but pad to byte for safety.
        w.WriteRbspTrailingBits();
        byte[] bytes = w.ToByteArray();
        var r = new BitReader(bytes);
        Span<int> decoded = stackalloc int[maxNumCoeff];
        int nz = H264Sharp.Decoder.Cavlc.CavlcResidualPublic.ReadResidualBlock(
            ref r, decoded, maxNumCoeff, nC, chromaDc);
        int expectedNz = 0;
        for (int i = 0; i < maxNumCoeff; i++) if (coeffs[i] != 0) expectedNz++;
        Assert.Equal(expectedNz, nz);
        for (int i = 0; i < maxNumCoeff; i++)
        {
            Assert.Equal(coeffs[i], decoded[i]);
        }
    }

    [Fact]
    public void EmptyBlock_RoundTrip()
    {
        RoundTripBlock(new int[16], nC: 0);
    }

    [Fact]
    public void SingleNonZero_RoundTrip_AtDC()
    {
        int[] block = new int[16];
        block[0] = 5;
        RoundTripBlock(block, nC: 0);
    }

    [Fact]
    public void SingleTrailingOne_RoundTrip()
    {
        int[] block = new int[16];
        block[3] = 1;
        RoundTripBlock(block, nC: 0);
    }

    [Fact]
    public void MultipleCoefficientsWithTrailingOnes_RoundTrip()
    {
        int[] block = { 3, -2, 0, 1, 0, -1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
        RoundTripBlock(block, nC: 2);
    }

    [Fact]
    public void FullBlock_NoZeros_RoundTrip()
    {
        int[] block = new int[16];
        for (int i = 0; i < 16; i++) block[i] = 1;
        RoundTripBlock(block, nC: 4);
    }

    [Fact]
    public void LargeMagnitudes_RoundTrip()
    {
        int[] block = { 100, -50, 30, 0, -20, 10, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0 };
        RoundTripBlock(block, nC: 3);
    }

    [Fact]
    public void ChromaDc_2x2_RoundTrip()
    {
        int[] block = { 3, -1, 0, 2 };
        RoundTripBlock(block, nC: 0, maxNumCoeff: 4, chromaDc: true);
    }

    [Fact]
    public void Ac15_RoundTrip()
    {
        int[] block = new int[15];
        block[0] = 4; block[2] = -3; block[5] = 1;
        RoundTripBlock(block, nC: 1, maxNumCoeff: 15);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void RoundTrip_VariousNc(int nC)
    {
        int[] block = { 7, -3, 0, 2, 0, -1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        RoundTripBlock(block, nC);
    }

    /// <summary>Regression: encoder used to misclassify levelPrefix=14 as "escape" when
    /// suffixLength > 0, corrupting medium-magnitude levels that need the prefix-suffix form.
    /// This block produces levelCode=59 at suffixLength=2 (prefix=14, suffix=3) for the
    /// second coefficient — exactly the edge that broke before.</summary>
    [Fact]
    public void RoundTrip_TwoMediumMagnitudes_PrefixFourteen()
    {
        // After collection (high-freq first): levels = [-30, -30]. WriteLevels:
        // i=0 (no trailing ones): adjustedAbs=29, levelCode=57, prefix=15 (escape) at suffixLength=0.
        // i=1: suffixLength becomes 2 (since absLevel 30 > threshold 3). adjustedAbs=30,
        //      levelCode=59. prefix = 59>>2 = 14, suffix = 59 & 3 = 3 — must NOT be escape.
        int[] block = new int[16];
        block[0] = -30;
        block[1] = -30;
        RoundTripBlock(block, nC: 0);
    }
}
