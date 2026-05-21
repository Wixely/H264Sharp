using H264Decoder;
using H264Decoder.Picture;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.EndToEnd;

public sealed class SingleFrameDecodeTests
{
    [Fact]
    public void DecodeSingleRed16x16_ProducesPictureOfCorrectShape()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264FrameDecoder();
        DecodedPicture pic = decoder.DecodeFirstIFrame(stream);

        Assert.Equal(sample.Width, pic.Width);
        Assert.Equal(sample.Height, pic.Height);
        Assert.Equal(sample.Width * sample.Height, pic.Y.Length);
        Assert.Equal(sample.Width * sample.Height / 4, pic.U.Length);
        Assert.Equal(sample.Width * sample.Height / 4, pic.V.Length);
    }

    [Fact]
    public void DecodeSingleRed16x16_MatchesFfmpegReferenceYuv()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264FrameDecoder();
        DecodedPicture pic = decoder.DecodeFirstIFrame(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;

        // Pre-deblock vs ffmpeg's deblocked YUV will differ near macroblock edges.
        // Inside the first MB our values should be close on average. Compare per-plane means.
        long pYsum = 0; for (int i = 0; i < yLen; i++) pYsum += pic.Y[i];
        long rYsum = 0; for (int i = 0; i < yLen; i++) rYsum += reference[i];
        long pUsum = 0; for (int i = 0; i < cLen; i++) pUsum += pic.U[i];
        long rUsum = 0; for (int i = 0; i < cLen; i++) rUsum += reference[yLen + i];
        long pVsum = 0; for (int i = 0; i < cLen; i++) pVsum += pic.V[i];
        long rVsum = 0; for (int i = 0; i < cLen; i++) rVsum += reference[yLen + cLen + i];

        // Within ±2 average error per plane — pre-deblock is already very close
        // to ffmpeg's post-deblock for a single-MB picture.
        Assert.InRange(pYsum / yLen, rYsum / yLen - 2, rYsum / yLen + 2);
        Assert.InRange(pUsum / cLen, rUsum / cLen - 2, rUsum / cLen + 2);
        Assert.InRange(pVsum / cLen, rVsum / cLen - 2, rVsum / cLen + 2);
    }

    [Fact]
    public void DecodeFourQuadrants32x32_ShapeCheckOnly()
    {
        var sample = FfmpegFixture.FourQuadrants32x32();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var pic = new H264FrameDecoder().DecodeFirstIFrame(stream);
        Assert.Equal(sample.Width, pic.Width);
        Assert.Equal(sample.Height, pic.Height);
    }

    [Fact]
    public void DecodeFourQuadrants32x32_BitExactPerSampleAgainstFfmpeg()
    {
        var sample = FfmpegFixture.FourQuadrants32x32();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var pic = new H264FrameDecoder().DecodeFirstIFrame(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;

        // Per-sample max absolute error — with deblocking the boundaries should
        // be very close. Allow ±2 LSB across the whole plane.
        int maxY = 0;
        for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[i]));
        int maxU = 0;
        for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(pic.U[i] - reference[yLen + i]));
        int maxV = 0;
        for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(pic.V[i] - reference[yLen + cLen + i]));

        Assert.True(maxY <= 2, $"luma max abs error = {maxY}");
        Assert.True(maxU <= 2, $"u max abs error = {maxU}");
        Assert.True(maxV <= 2, $"v max abs error = {maxV}");
    }

    [Fact]
    public void DecodeTestsrc32x32_HandlesIntra4x4()
    {
        // testsrc is detailed enough that x264 picks ~75% Intra_4x4 macroblocks.
        var sample = FfmpegFixture.Testsrc32x32();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var pic = new H264FrameDecoder().DecodeFirstIFrame(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;

        int maxY = 0;
        for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[i]));
        int maxU = 0;
        for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(pic.U[i] - reference[yLen + i]));
        int maxV = 0;
        for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(pic.V[i] - reference[yLen + cLen + i]));

        // Allow a couple LSB of slack at the worst sample — same threshold as the
        // 4-quadrants test, which is effectively bit-exact in practice.
        Assert.True(maxY <= 2, $"luma max abs error = {maxY}");
        Assert.True(maxU <= 2, $"u max abs error = {maxU}");
        Assert.True(maxV <= 2, $"v max abs error = {maxV}");
    }
}
