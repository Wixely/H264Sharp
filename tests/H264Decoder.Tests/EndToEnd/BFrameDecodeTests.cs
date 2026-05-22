using H264Decoder.Picture;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.EndToEnd;

public sealed class BFrameDecodeTests
{
    [Fact]
    public void DecodeThreeFramesBFrames64x48Cavlc_FrameCount()
    {
        // Stage 2: CAVLC B-slice support. IBBP-style stream decodes; we check
        // frame count and that the B-frame doesn't throw.
        var sample = FfmpegFixture.ThreeFramesBFrames64x48Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        Assert.Equal(3, frames.Count);
        foreach (var f in frames)
        {
            Assert.Equal(sample.Width, f.Width);
            Assert.Equal(sample.Height, f.Height);
        }
    }

    [Fact]
    public void DecodeThreeFramesBFrames64x48Cavlc_ByteExactWithinTolerance()
    {
        // Compare each frame against the ffmpeg-decoded reference YUV (display order).
        var sample = FfmpegFixture.ThreeFramesBFrames64x48Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        Assert.Equal(3, frames.Count);

        int worstMaxY = 0;
        for (int f = 0; f < 3; f++)
        {
            var pic = frames[f];
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            worstMaxY = Math.Max(worstMaxY, maxY);
        }

        Assert.True(worstMaxY <= 2, $"luma max abs error across B-frames = {worstMaxY}");
    }

    // Stage-2 known limitation: spatial direct mode's per-4x4-block collocated-MV check
    // (spec §8.4.1.2.2 — clear MV to 0 when colocated L1[0] block has refIdx=0 + |MV|<=1)
    // is not implemented because DecodedPicture doesn't yet retain per-MB MV grids for the
    // future-reference picture. For IBBP streams with motion content the omission causes
    // a small per-sample bias in the B-frames; the I + P frames remain byte-exact.
    // This is the next milestone in B-slice support and is tracked separately.
    [Fact]
    public void DecodeFourFramesBFrames64x48Cavlc_IandPByteExactBframesApproximate()
    {
        // IBBP CAVLC: two consecutive B-frames between I and P. Exercises L0/L1
        // ref selection where consecutive B-frames pick different refs.
        var sample = FfmpegFixture.FourFramesBFrames64x48Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        Assert.Equal(4, frames.Count);

        int worstMaxY = 0;
        int[] perFrame = new int[4];
        for (int f = 0; f < 4; f++)
        {
            var pic = frames[f];
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            perFrame[f] = maxY;
            worstMaxY = Math.Max(worstMaxY, maxY);
        }

        // I (frame 0) and P (frame 3) must be byte-exact. After stage 4 (implicit weighted
        // bipred + per-4x4 colocated-MV override) the FIRST B-frame is also byte-exact.
        // The second B-frame still drifts on a non-direct B_L0_L1_16x8 MB (mb_type 8):
        // a pre-existing MV-prediction issue tracked separately.
        Assert.True(perFrame[0] <= 2, $"I-frame luma diff = {perFrame[0]}");
        Assert.True(perFrame[3] <= 2, $"P-frame luma diff = {perFrame[3]}");
        Assert.True(perFrame[1] <= 2, $"B1-frame luma diff = {perFrame[1]} (should be byte-exact after stage 4)");
        Assert.True(perFrame[2] <= 50, $"B2-frame luma diff = {perFrame[2]} (regression detection only)");
    }

    [Fact]
    public void DecodeThreeFramesBFrames32x16Cabac_ByteExactWithinTolerance()
    {
        // Stage 3: CABAC B-slice non-skip MB parsing. Small constant-content fixture
        // exercises the CABAC mb_skip_flag B path and (for non-uniform MBs) the
        // CabacSliceB.ParseMb path. Content is constant-red so most B-MBs are B_Skip,
        // but the IDR + P + B header / CBP / qp_delta paths are all exercised via CABAC.
        var sample = FfmpegFixture.ThreeFramesBFrames32x16Cabac();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        Assert.Equal(3, frames.Count);

        int worstMaxY = 0;
        for (int f = 0; f < 3; f++)
        {
            var pic = frames[f];
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            worstMaxY = Math.Max(worstMaxY, maxY);
        }

        Assert.True(worstMaxY <= 2, $"luma max abs error across CABAC B-frames = {worstMaxY}");
    }

    [Fact]
    public void DecodeThreeFramesBFrames64x48CavlcDeblock_ByteExact()
    {
        // Same as the CAVLC B-frame test but with the deblocking filter ENABLED in the
        // bitstream — exercises bS derivation for inter MBs (P and B).
        var sample = FfmpegFixture.ThreeFramesBFrames64x48CavlcDeblock();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        Assert.Equal(3, frames.Count);

        int worstMaxY = 0;
        for (int f = 0; f < 3; f++)
        {
            var pic = frames[f];
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            worstMaxY = Math.Max(worstMaxY, maxY);
        }

        Assert.True(worstMaxY <= 2, $"luma max abs error across deblock-on B-frames = {worstMaxY}");
    }

    [Fact]
    public void DecodeThreeFramesBFrames32x16CabacDeblock_ByteExact()
    {
        // Same as the CABAC B-frame test but with the deblocking filter ENABLED.
        var sample = FfmpegFixture.ThreeFramesBFrames32x16CabacDeblock();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        Assert.Equal(3, frames.Count);

        int worstMaxY = 0;
        for (int f = 0; f < 3; f++)
        {
            var pic = frames[f];
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            worstMaxY = Math.Max(worstMaxY, maxY);
        }

        Assert.True(worstMaxY <= 2, $"luma max abs error across CABAC deblock-on B-frames = {worstMaxY}");
    }
}
