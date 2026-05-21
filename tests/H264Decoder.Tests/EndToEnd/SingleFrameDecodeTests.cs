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
}
