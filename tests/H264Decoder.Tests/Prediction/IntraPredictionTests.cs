using H264Decoder.Prediction;
using H264Decoder.Syntax;

namespace H264Decoder.Tests.Prediction;

public sealed class IntraPredictionTests
{
    // ----- Intra_8x8 -----
    [Fact]
    public void I8x8_Filter_AllConstantNeighbors_RemainsConstant()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 128;
        for (int i = 0; i < 8; i++) left[i] = 128;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(
            top, true, true, left, true, 128, true,
            ft, fl, out byte ftl);
        for (int i = 0; i < 16; i++) Assert.Equal(128, ft[i]);
        for (int i = 0; i < 8; i++) Assert.Equal(128, fl[i]);
        Assert.Equal(128, ftl);
    }

    [Fact]
    public void I8x8_DC_NoNeighbors_Returns128()
    {
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.Dc,
            ft, false, fl, false, 0, false, output);
        foreach (byte b in output) Assert.Equal(128, b);
    }

    [Fact]
    public void I8x8_DC_All128Neighbors_Returns128()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 128;
        for (int i = 0; i < 8; i++) left[i] = 128;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, true, true, left, true, 128, true, ft, fl, out byte ftl);

        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.Dc,
            ft, true, fl, true, ftl, true, output);
        foreach (byte b in output) Assert.Equal(128, b);
    }

    [Fact]
    public void I8x8_Vertical_All77Top_Returns77Everywhere()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 77;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, true, true, left, false, 77, true, ft, fl, out byte ftl);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.Vertical,
            ft, true, fl, false, ftl, true, output);
        foreach (byte b in output) Assert.Equal(77, b);
    }

    [Fact]
    public void I8x8_Horizontal_All55Left_Returns55Everywhere()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 8; i++) left[i] = 55;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, false, false, left, true, 55, true, ft, fl, out byte ftl);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.Horizontal,
            ft, false, fl, true, ftl, true, output);
        foreach (byte b in output) Assert.Equal(55, b);
    }

    [Fact]
    public void I8x8_DiagDownLeft_AllConstantTop_ReturnsConstant()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 100;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, true, true, left, false, 100, true, ft, fl, out byte ftl);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.DiagDownLeft,
            ft, true, fl, false, ftl, false, output);
        foreach (byte b in output) Assert.Equal(100, b);
    }

    [Fact]
    public void I8x8_DiagDownRight_AllConstantNeighbors_ReturnsConstant()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 80;
        for (int i = 0; i < 8; i++) left[i] = 80;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, true, true, left, true, 80, true, ft, fl, out byte ftl);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.DiagDownRight,
            ft, true, fl, true, ftl, true, output);
        foreach (byte b in output) Assert.Equal(80, b);
    }

    [Fact]
    public void I8x8_VerticalLeft_AllConstantTop_ReturnsConstant()
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        for (int i = 0; i < 16; i++) top[i] = 90;
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        IntraPrediction.Intra8x8PredFilter(top, true, true, left, false, 90, true, ft, fl, out byte ftl);
        Span<byte> output = stackalloc byte[64];
        IntraPrediction.PredictIntra8x8(
            IntraPrediction.Intra8x8Mode.VerticalLeft,
            ft, true, fl, false, ftl, true, output);
        foreach (byte b in output) Assert.Equal(90, b);
    }

    [Fact]
    public void I8x8_VR_HD_HU_AreStubbed_Throw()
    {
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        byte[] ftArr = ft.ToArray();
        byte[] flArr = fl.ToArray();
        var output = new byte[64];
        Assert.Throws<NotSupportedException>(() =>
            IntraPrediction.PredictIntra8x8(
                IntraPrediction.Intra8x8Mode.VerticalRight,
                ftArr, true, flArr, true, (byte)0, true, output));
        Assert.Throws<NotSupportedException>(() =>
            IntraPrediction.PredictIntra8x8(
                IntraPrediction.Intra8x8Mode.HorizontalDown,
                ftArr, true, flArr, true, (byte)0, true, output));
        Assert.Throws<NotSupportedException>(() =>
            IntraPrediction.PredictIntra8x8(
                IntraPrediction.Intra8x8Mode.HorizontalUp,
                ftArr, true, flArr, true, (byte)0, true, output));
    }

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
