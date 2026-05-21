using H264Decoder.Prediction;
using H264Decoder.Syntax;

namespace H264Decoder.Tests.Prediction;

public sealed class IntraPredictionTests
{
    // ----- Intra_16x16 -----
    [Fact]
    public void I16_DC_NoNeighbors_Returns128()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[16];
        Span<byte> output = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            Intra16x16PredMode.Dc,
            top, topAvail: false, left, leftAvail: false,
            topLeft: 0, topLeftAvail: false, output);
        foreach (byte b in output) Assert.Equal(128, b);
    }

    [Fact]
    public void I16_DC_BothNeighbors_AveragesTopAndLeft()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[16];
        for (int i = 0; i < 16; i++) { top[i] = 100; left[i] = 50; }
        Span<byte> output = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            Intra16x16PredMode.Dc, top, true, left, true, 0, false, output);
        // sum = 16*100 + 16*50 = 2400; (2400 + 16) >> 5 = 75
        Assert.Equal(75, output[0]);
        Assert.Equal(75, output[255]);
    }

    [Fact]
    public void I16_Vertical_CopiesTopRow()
    {
        Span<byte> top = stackalloc byte[16];
        for (int i = 0; i < 16; i++) top[i] = (byte)(i * 16);
        Span<byte> output = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            Intra16x16PredMode.Vertical, top, true, [], false, 0, false, output);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(top[x], output[y * 16 + x]);
    }

    [Fact]
    public void I16_Horizontal_CopiesLeftColumn()
    {
        Span<byte> left = stackalloc byte[16];
        for (int i = 0; i < 16; i++) left[i] = (byte)(i * 16);
        Span<byte> output = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            Intra16x16PredMode.Horizontal, [], false, left, true, 0, false, output);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(left[y], output[y * 16 + x]);
    }

    // ----- Chroma 8x8 -----
    [Fact]
    public void Chroma_DC_NoNeighbors_Returns128()
    {
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictChroma8x8(
            IntraChromaPredMode.Dc, [], false, [], false, 0, false, output);
        foreach (byte b in output) Assert.Equal(128, b);
    }

    [Fact]
    public void Chroma_Horizontal_CopiesLeft()
    {
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 8; i++) left[i] = (byte)(i * 32);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictChroma8x8(
            IntraChromaPredMode.Horizontal, [], false, left, true, 0, false, output);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(left[y], output[y * 8 + x]);
    }

    // ----- Intra_4x4 -----
    [Fact]
    public void I4x4_DC_NoNeighbors_Returns128()
    {
        Span<byte> output = stackalloc byte[16];
        IntraPrediction.PredictIntra4x4(
            IntraPrediction.Intra4x4Mode.Dc,
            [], topAvail: false, topRightAvail: false,
            [], leftAvail: false, 0, false, output);
        foreach (byte b in output) Assert.Equal(128, b);
    }

    [Fact]
    public void I4x4_Vertical_CopiesTopRow()
    {
        Span<byte> top = stackalloc byte[8];
        top[0] = 10; top[1] = 20; top[2] = 30; top[3] = 40;
        Span<byte> output = stackalloc byte[16];
        IntraPrediction.PredictIntra4x4(
            IntraPrediction.Intra4x4Mode.Vertical,
            top, topAvail: true, topRightAvail: false,
            [], leftAvail: false, 0, false, output);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(top[x], output[y * 4 + x]);
    }

    [Fact]
    public void I4x4_Horizontal_CopiesLeftCol()
    {
        Span<byte> left = stackalloc byte[4];
        left[0] = 10; left[1] = 20; left[2] = 30; left[3] = 40;
        Span<byte> output = stackalloc byte[16];
        IntraPrediction.PredictIntra4x4(
            IntraPrediction.Intra4x4Mode.Horizontal,
            [], topAvail: false, topRightAvail: false,
            left, leftAvail: true, 0, false, output);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(left[y], output[y * 4 + x]);
    }

    [Fact]
    public void I4x4_DC_BothNeighbors_AveragesEight()
    {
        Span<byte> top = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        for (int i = 0; i < 4; i++) { top[i] = 100; left[i] = 60; }
        Span<byte> output = stackalloc byte[16];
        IntraPrediction.PredictIntra4x4(
            IntraPrediction.Intra4x4Mode.Dc,
            top, true, false, left, true, 0, false, output);
        // (4*100 + 4*60 + 4) >> 3 = 80
        Assert.Equal(80, output[0]);
        Assert.Equal(80, output[15]);
    }
}
