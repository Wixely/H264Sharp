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
