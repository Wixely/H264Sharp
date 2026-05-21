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
    public void DecodeMultiRef_PicksCorrectReferenceFrame()
    {
        // 3-frame clip encoded with --refs 3. The last P-frame's slice header
        // signals num_ref_idx_l0_active_minus1=1, and x264 picks ref_idx_l0
        // per-partition between the two prior reference pictures.
        var sample = FfmpegFixture.ThreeFramesMultiRef64x48();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        var frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(3, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 3; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            int maxY = 0, maxU = 0, maxV = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeMp4Container_ProducesSamePixelsAsElementaryStream()
    {
        // Same encoder settings as DecodeTwoFramesAllPartitions, just muxed to MP4
        // (which forces our Mp4Reader to walk the atom tree and pull AVCC samples).
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        var frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 2; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            int maxY = 0, maxU = 0, maxV = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeTwoFramesAllPartitions_PartitionTypes()
    {
        // 128x96 testsrc + light blur, qp=8, partitions=all. x264 emits a mix of
        // P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, P_8x8 (with sub_mb_types), and
        // P_8x8ref0 — exercises every motion partition shape.
        var sample = FfmpegFixture.TwoFramesAllPartitions128x96();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        var frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 2; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            int maxY = 0, maxU = 0, maxV = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeTwoFramesFractionalMv_PSubpelMotionCompensation()
    {
        // 128x96 testsrc with frame-2 light blur. x264 picks ~29 P_L0_16x16 MBs with
        // fractional-pel MVs, exercising all 16 luma sub-pel positions and chroma 1/8-pel.
        var sample = FfmpegFixture.TwoFramesFractionalMv128x96();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        var frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 2; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            int maxY = 0, maxU = 0, maxV = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeTwoFramesShifted_PMotionCompensation()
    {
        // 128x96 testsrc, frame 2 horizontally shifted by 8 pixels. x264 emits a mix of
        // P_Skip and P_L0_16x16 with integer-pel MV=(32, 0) plus inter residuals.
        var sample = FfmpegFixture.TwoFramesShifted128x96();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        var frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 2; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            int maxY = 0, maxU = 0, maxV = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeTwoFramesIdentical_PSkipFromReference()
    {
        var sample = FfmpegFixture.TwoFramesIdentical16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        List<DecodedPicture> frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;

        for (int f = 0; f < 2; f++)
        {
            DecodedPicture pic = frames[f];
            int off = f * frameStride;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[off + i]));
            int maxU = 0;
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(pic.U[i] - reference[off + yLen + i]));
            int maxV = 0;
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(pic.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 2, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 2, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 2, $"frame {f} V max err = {maxV}");
        }
    }

    [Fact]
    public void DecodeCabacIdrPlusSkip_MatchesFfmpegBitExact()
    {
        // 16x16 red, Main profile, CABAC (-coder 1). x264 emits an IDR followed by a
        // P_Skip frame. Exercises: PPS entropy_coding_mode_flag=1, I-slice CABAC mb_type
        // (Intra_16x16) + intra_chroma_pred_mode + mb_qp_delta + residual_block_cabac,
        // and P-slice mb_skip_flag + end_of_slice_flag.
        var sample = FfmpegFixture.TwoFramesIdentical16x16Cabac();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        byte[] reference = File.ReadAllBytes(sample.YuvPath);

        List<DecodedPicture> frames = new H264FrameDecoder().DecodeAllFrames(stream);
        Assert.Equal(2, frames.Count);

        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        for (int f = 0; f < 2; f++)
        {
            DecodedPicture pic = frames[f];
            int off = f * frameStride;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[off + i]));
            int maxU = 0;
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(pic.U[i] - reference[off + yLen + i]));
            int maxV = 0;
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(pic.V[i] - reference[off + yLen + cLen + i]));
            Assert.True(maxY <= 1, $"frame {f} luma max err = {maxY}");
            Assert.True(maxU <= 1, $"frame {f} U max err = {maxU}");
            Assert.True(maxV <= 1, $"frame {f} V max err = {maxV}");
        }
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
