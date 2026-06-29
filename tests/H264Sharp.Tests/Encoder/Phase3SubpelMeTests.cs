using H264Sharp.Encoder;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 3a: sub-pel ME (half- and quarter-pel refinement) tests.</summary>
public class Phase3SubpelMeTests
{
    private static byte[] ConcatFrames(params byte[][] frames)
    {
        int total = 0;
        foreach (var f in frames) total += f.Length;
        var r = new byte[total];
        int off = 0;
        foreach (var f in frames) { Array.Copy(f, 0, r, off, f.Length); off += f.Length; }
        return r;
    }

    /// <summary>Build a YUV frame where a 16x16 high-frequency luma "object" is positioned at
    /// integer X = baseX + (subOffset/4) — i.e., subOffset is in quarter-pel units (0..7 typically).
    /// We simulate a half-pel-shifted source by averaging integer-pel rows.</summary>
    private static byte[] MakeShiftedHfFrame(int W, int H, int xShiftQpel)
    {
        // Use a smooth gradient image so sub-pel interpolation is well-defined.
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        var f = new byte[frameBytes];
        // Generate a 2x super-resolution image, then sample at the shifted position.
        // We treat the image as a smooth function of x: pixel = clamp(80 + 6 * (x + xShiftPel))
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                // Continuous-x position with sub-pel shift.
                double cx = x + xShiftQpel / 4.0;
                int v = (int)Math.Round(80 + 6.0 * cx);
                if (v < 16) v = 16; if (v > 235) v = 235;
                f[y * W + x] = (byte)v;
            }
        }
        Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
        return f;
    }

    private static int Sad(byte[] decY, int decStride, byte[] orig, int origStride, int W, int H)
    {
        int s = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                s += Math.Abs(decY[y * decStride + x] - orig[y * origStride + x]);
        return s;
    }

    [Fact]
    public void SubpelMe_HorizontalShiftHalfPel_ProducesSmallerBitstream()
    {
        int W = 32, H = 16;
        byte[] f1 = MakeShiftedHfFrame(W, H, xShiftQpel: 0);
        byte[] f2 = MakeShiftedHfFrame(W, H, xShiftQpel: 2); // half-pel shift
        byte[] yuv = ConcatFrames(f1, f2);

        // Encode with sub-pel ME enabled.
        byte[] withSubpel = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = false });

        // Encode with sub-pel ME disabled (integer-pel only).
        byte[] noSubpel = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });

        // Sub-pel should produce strictly fewer (or equal in degenerate corners) bytes.
        Assert.True(withSubpel.Length < noSubpel.Length,
            $"sub-pel ME encode ({withSubpel.Length} B) should be smaller than int-pel-only ({noSubpel.Length} B)");
    }

    [Fact]
    public void SubpelMe_RoundTripDistortion_BetterThanIntPelOnly()
    {
        int W = 32, H = 16;
        byte[] f1 = MakeShiftedHfFrame(W, H, xShiftQpel: 0);
        byte[] f2 = MakeShiftedHfFrame(W, H, xShiftQpel: 2); // half-pel shift in luma
        byte[] yuv = ConcatFrames(f1, f2);

        byte[] withSubpel = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = false });
        byte[] noSubpel = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });

        var picsSub = new H264FrameDecoder().DecodeAllFrames(withSubpel);
        var picsInt = new H264FrameDecoder().DecodeAllFrames(noSubpel);
        // P-frame index 1 reconstructed luma vs original.
        int distSub = Sad(picsSub[1].Y, picsSub[1].BufferWidth, f2, W, W, H);
        int distInt = Sad(picsInt[1].Y, picsInt[1].BufferWidth, f2, W, W, H);
        Assert.True(distSub <= distInt,
            $"sub-pel ME distortion ({distSub}) should be <= int-pel-only distortion ({distInt})");
    }

    [Fact]
    public void SubpelMe_RoundTripsCleanly_AnyHalfPelShift()
    {
        // Encoder + decoder round trip for a half-pel shifted source must still decode without errors
        // and produce reasonable pixel reconstruction.
        int W = 32, H = 16;
        byte[] f1 = MakeShiftedHfFrame(W, H, xShiftQpel: 0);
        byte[] f2 = MakeShiftedHfFrame(W, H, xShiftQpel: 2);
        byte[] yuv = ConcatFrames(f1, f2);

        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        // Reconstructed P-frame within a tolerance band of the original.
        int maxDiff = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                maxDiff = Math.Max(maxDiff, Math.Abs(pics[1].Y[y * pics[1].BufferWidth + x] - f2[y * W + x]));
        Assert.InRange(maxDiff, 0, 25);
    }
}
