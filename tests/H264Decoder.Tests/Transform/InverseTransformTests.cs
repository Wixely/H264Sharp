using H264Decoder.Transform;

namespace H264Decoder.Tests.Transform;

public sealed class InverseTransformTests
{
    [Fact]
    public void Inverse4x4_AllZero_IsZero()
    {
        Span<int> b = stackalloc int[16];
        InverseTransform.Inverse4x4(b);
        foreach (int v in b) Assert.Equal(0, v);
    }

    [Fact]
    public void Inverse4x4_PureDcCoeff64_Yields1Everywhere()
    {
        Span<int> b = stackalloc int[16];
        b[0] = 64;
        InverseTransform.Inverse4x4(b);
        // (64 + 32) >> 6 == 1
        foreach (int v in b) Assert.Equal(1, v);
    }

    [Fact]
    public void Inverse4x4_PureDcCoeff128_Yields2Everywhere()
    {
        Span<int> b = stackalloc int[16];
        b[0] = 128;
        InverseTransform.Inverse4x4(b);
        foreach (int v in b) Assert.Equal(2, v);
    }

    [Fact]
    public void InverseHadamard4x4_PureDc16_YieldsAll16()
    {
        Span<int> b = stackalloc int[16];
        b[0] = 16;
        InverseTransform.InverseHadamard4x4(b);
        foreach (int v in b) Assert.Equal(16, v);
    }

    [Fact]
    public void Inverse8x8_AllZero_IsZero()
    {
        Span<int> b = stackalloc int[64];
        InverseTransform.Inverse8x8(b);
        foreach (int v in b) Assert.Equal(0, v);
    }

    [Fact]
    public void Inverse8x8_PureDc64_Yields1()
    {
        // DC=64: row pass produces 64 across all 8 row entries; col pass produces 64 again,
        // then (64+32)>>6 = 1 per sample (analogous to the 4x4 DC=64 -> 1 round-trip).
        Span<int> b = stackalloc int[64];
        b[0] = 64;
        InverseTransform.Inverse8x8(b);
        foreach (int v in b) Assert.Equal(1, v);
    }

    [Fact]
    public void Inverse8x8_PureDc4096_Yields64()
    {
        // 4096 = 64*64, the magnitude needed for output exactly 64 everywhere.
        Span<int> b = stackalloc int[64];
        b[0] = 4096;
        InverseTransform.Inverse8x8(b);
        foreach (int v in b) Assert.Equal(64, v);
    }

    [Fact]
    public void Inverse8x8_OffDcCoeffsSymmetric()
    {
        // Sanity: any single non-DC coefficient should produce a non-constant output that
        // sums (approximately) to zero — the basis functions are zero-mean.
        Span<int> b = stackalloc int[64];
        b[1] = 4096;
        InverseTransform.Inverse8x8(b);
        long sum = 0;
        foreach (int v in b) sum += v;
        // Allow small bias from the +32 rounding term over 64 samples.
        Assert.InRange(sum, -64, 64);
    }

    [Fact]
    public void Dequant8x8_DcAtQpZero_AppliesFirstScale()
    {
        Span<int> c = stackalloc int[64];
        c[0] = 1;
        Quantization.Dequant8x8(c, 0);
        // Spec §8.5.12.2, qP<36 branch: (1 * 20 * 16 + (1<<5)) >> 6 = 352 >> 6 = 5.
        Assert.Equal(5, c[0]);
    }

    [Fact]
    public void Dequant8x8_QpShifts()
    {
        Span<int> c = stackalloc int[64];
        c[0] = 1;
        Quantization.Dequant8x8(c, 6);
        // qP<36 branch: (1 * 20 * 16 + (1<<4)) >> 5 = 336 >> 5 = 10.
        Assert.Equal(10, c[0]);
    }

    [Fact]
    public void Dequant8x8_QpAt36_PureLeftShift()
    {
        Span<int> c = stackalloc int[64];
        c[0] = 1;
        Quantization.Dequant8x8(c, 36);
        // qP>=36 branch with qP/6=6, shift=0: 1 * 20 * 16 << 0 = 320.
        Assert.Equal(320, c[0]);
    }

    [Fact]
    public void Dequant8x8_QpAbove36_AppliesLeftShift()
    {
        Span<int> c = stackalloc int[64];
        c[0] = 1;
        Quantization.Dequant8x8(c, 42);
        // qP>=36, qP/6=7, shift=1: 1 * 20 * 16 << 1 = 640.
        Assert.Equal(640, c[0]);
    }

    [Fact]
    public void InverseHadamard2x2_Sample()
    {
        Span<int> b = stackalloc int[4];
        b[0] = 1; b[1] = 2; b[2] = 3; b[3] = 4;
        InverseTransform.InverseHadamard2x2(b);
        Assert.Equal(10, b[0]);
        Assert.Equal(-2, b[1]);
        Assert.Equal(-4, b[2]);
        Assert.Equal(0, b[3]);
    }
}
