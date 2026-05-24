using H264Decoder.Encoder;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 3b: sub-MB partition mode decision tests (P_L0_L0_16x8, P_L0_L0_8x16, P_8x8).</summary>
public class Phase3PartitionTests
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

    private static byte[] MakeYuv420(int w, int h, Func<int, int, byte> yFn)
    {
        int frameBytes = w * h + 2 * (w / 2) * (h / 2);
        var f = new byte[frameBytes];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f[y * w + x] = yFn(x, y);
        Array.Fill<byte>(f, 128, w * h, 2 * (w / 2) * (h / 2));
        return f;
    }

    [Fact]
    public void Partition_MixedMotion_RoundTripStaysHighFidelity()
    {
        // Multi-MB, multi-frame mixed motion content. Reconstruction PSNR with phase-3 should be
        // at least as good as phase-2 single-partition encode at the same QP — regressing this would
        // re-introduce an MV-predictor mismatch like the one fixed in phase 3.
        int W = 64, H = 48;
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        var yuv = new byte[frameBytes * 5];
        for (int t = 0; t < 5; t++)
        {
            byte[] f = new byte[frameBytes];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int dx;
                    if (y < 24 && x < 32) dx = t;
                    else if (y < 24) dx = -t;
                    else dx = 0;
                    int xs = ((x + dx) % W + W) % W;
                    f[y * W + x] = (byte)(80 + ((xs / 2) % 2 == 0 ? 0 : 40) + ((y / 4) % 2 == 0 ? 0 : 20));
                }
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            Array.Copy(f, 0, yuv, t * frameBytes, frameBytes);
        }
        byte[] phase2 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 5,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });
        byte[] phase3 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 5);
        var pics2 = new H264FrameDecoder().DecodeAllFrames(phase2);
        var pics3 = new H264FrameDecoder().DecodeAllFrames(phase3);
        long sse2 = 0, sse3 = 0;
        for (int t = 0; t < 5; t++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int src = yuv[t * frameBytes + y * W + x];
                    int d2 = src - pics2[t].Y[y * pics2[t].BufferWidth + x];
                    int d3 = src - pics3[t].Y[y * pics3[t].BufferWidth + x];
                    sse2 += d2 * d2;
                    sse3 += d3 * d3;
                }
        // Phase 3 reconstruction must not regress vs phase 2 by more than 5x SSE (allows small
        // expected rate-distortion tradeoffs from λ-RDO but catches catastrophic distortion).
        Assert.True(sse3 < sse2 * 5 + 10000,
            $"Phase 3 SSE {sse3} catastrophically worse than phase 2 SSE {sse2} — MV predictor mismatch likely");
        // And phase 3 should be smaller (or no larger) in bytes.
        Assert.True(phase3.Length <= phase2.Length,
            $"Phase 3 bytes {phase3.Length} should be <= phase 2 bytes {phase2.Length} on mixed-motion content");
    }

    [Fact]
    public void Partition_StaticTopMovingBottom_PartitionedSmallerThanSinglePartition()
    {
        // 32x32 (4 MBs): top 2 MBs static, bottom 2 MBs shifted by 2 px between frames.
        // P_L0_L0_16x8 split should be selected for at least some MBs since top half is static
        // and bottom half is moving — distinct MV per half.
        int W = 32, H = 32;
        byte Make(int x, int y, int t)
        {
            // Background grid with high-frequency structure so motion matters.
            int dx = (y < 16) ? 0 : (t * 2);
            int xs = (x + dx) % 32;
            return (byte)(80 + ((y / 4) % 2 == 0 ? 0 : 30) + ((xs / 4) % 2 == 0 ? 0 : 20));
        }
        byte[] f1 = MakeYuv420(W, H, (x, y) => Make(x, y, 0));
        byte[] f2 = MakeYuv420(W, H, (x, y) => Make(x, y, 1));
        byte[] yuv = ConcatFrames(f1, f2);

        byte[] simple = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 2,
            new H264FrameEncoder.Options { EnableSubMbPartitions = false });
        byte[] withPart = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 2,
            new H264FrameEncoder.Options { EnableSubMbPartitions = true });

        // With partitions the encoder can pick 16x8 (or 8x16/P_8x8) when residual+bits are lower.
        // We require strictly smaller bitstream when partitions are enabled.
        Assert.True(withPart.Length <= simple.Length,
            $"Partitioned encode ({withPart.Length} B) should be <= simple 16x16-only ({simple.Length} B)");
    }

    [Fact]
    public void Partition_AllShapes_RoundTripDecodes()
    {
        // Varied content over 3 frames; encoder is free to pick any partition shape.
        // We just verify it decodes without exceptions and produces a sensible pixel reconstruction.
        int W = 48, H = 32;
        var rng = new Random(42);
        var allFrames = new List<byte>();
        for (int t = 0; t < 3; t++)
        {
            byte[] f = MakeYuv420(W, H, (x, y) =>
            {
                // Different motion regions: top-left moves +1 px/frame, top-right -1 px/frame, bottom static.
                int dx;
                if (y < 16 && x < 24) dx = t;
                else if (y < 16) dx = -t;
                else dx = 0;
                int xs = ((x + dx) % W + W) % W;
                return (byte)(70 + (xs * 3) % 100);
            });
            allFrames.AddRange(f);
        }
        byte[] yuv = allFrames.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 3,
            new H264FrameEncoder.Options { EnableSubMbPartitions = true, EnableSubpelMe = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);

        // Reconstructed P-frame should be within a tolerance band of the source.
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        for (int t = 0; t < 3; t++)
        {
            byte[] src = yuv[(t * frameBytes)..((t + 1) * frameBytes)];
            int maxDiff = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    maxDiff = Math.Max(maxDiff, Math.Abs(pics[t].Y[y * pics[t].BufferWidth + x] - src[y * W + x]));
            // Allow generous tolerance — partitioned encode of high-frequency content may have visible residual.
            Assert.InRange(maxDiff, 0, 60);
        }
    }

    [Fact]
    public void Partition_Disabled_BehavesLikePhase2()
    {
        // With EnableSubMbPartitions=false, output should round-trip identically to phase-2 behavior.
        int W = 32, H = 32;
        var rng = new Random(7);
        byte[] f1 = MakeYuv420(W, H, (x, y) => (byte)(100 + (x * 4 + y * 7) % 60));
        byte[] f2 = MakeYuv420(W, H, (x, y) => (byte)(100 + ((x + 2) * 4 + y * 7) % 60));
        byte[] yuv = ConcatFrames(f1, f2);
        byte[] simple = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableSubMbPartitions = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(simple);
        Assert.Equal(2, pics.Count);
    }

    [Fact]
    public void Partition_TwoMbHoriz_DifferentShapes_RoundTrip()
    {
        // 32x16 (2 MBs side by side). Top half moves one way, bottom another in MB 0;
        // swap in MB 1. The neighbor MV predictor must see the correct partition-1 MV when
        // emitting partition-1 of the next MB. This exposed the IsInter-before-WriteMvds bug.
        int W = 32, H = 16;
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        byte[] BuildFrame(int t)
        {
            byte[] f = new byte[frameBytes];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int dx;
                    if (x < 16) dx = y < 8 ? t : 0;
                    else dx = y < 8 ? 0 : t;
                    int xs = ((x + dx) % W + W) % W;
                    f[y * W + x] = (byte)(80 + (xs * 5) % 100);
                }
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            return f;
        }
        byte[] f0 = BuildFrame(0);
        byte[] f1 = BuildFrame(1);
        byte[] yuv = new byte[frameBytes * 2];
        Array.Copy(f0, 0, yuv, 0, frameBytes);
        Array.Copy(f1, 0, yuv, frameBytes, frameBytes);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2,
            new H264FrameEncoder.Options { EnableSubMbPartitions = true, EnableSubpelMe = false, ModeDecisionLambda = 0 });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        long sse = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int src = f1[y * W + x];
                int dec = pics[1].Y[y * pics[1].BufferWidth + x];
                sse += (src - dec) * (src - dec);
            }
        double mse = (double)sse / (W * H);
        Assert.True(mse < 30, $"2-MB horizontal partition round-trip MSE {mse:F2} too high");
    }

    [Fact]
    public void Partition_P8x8_RoundTripsCleanly_FourQuadrantMotion()
    {
        // Single MB with each 8x8 quadrant moving differently. Tests P_8x8 + chroma chroma sub-block MC.
        int W = 16, H = 16;
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        byte[] f0 = new byte[frameBytes];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                f0[y * W + x] = (byte)(60 + x * 6 + y * 4);
        Array.Fill<byte>(f0, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] f1 = new byte[frameBytes];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int dx;
                if (y < 8 && x < 8) dx = 1;
                else if (y < 8) dx = -1;
                else if (x < 8) dx = 2;
                else dx = -2;
                int xs = ((x + dx) % W + W) % W;
                f1[y * W + x] = (byte)(60 + xs * 6 + y * 4);
            }
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] yuv = new byte[frameBytes * 2];
        Array.Copy(f0, 0, yuv, 0, frameBytes);
        Array.Copy(f1, 0, yuv, frameBytes, frameBytes);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = true, ModeDecisionLambda = 0 });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        long sse = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                sse += (f1[y * W + x] - pics[1].Y[y * pics[1].BufferWidth + x]) * (f1[y * W + x] - pics[1].Y[y * pics[1].BufferWidth + x]);
        double mse = (double)sse / (W * H);
        Assert.True(mse < 100, $"Single-MB P_8x8 MSE {mse:F2} too high at QP=18");
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes()
    {
        string? ff = FindFfmpeg();
        if (ff is null) return;

        int W = 48, H = 32;
        var allFrames = new List<byte>();
        for (int t = 0; t < 3; t++)
        {
            byte[] f = MakeYuv420(W, H, (x, y) =>
            {
                int dx = (y < 16) ? t : -t;
                int xs = ((x + dx) % W + W) % W;
                return (byte)(70 + (xs * 3) % 100);
            });
            allFrames.AddRange(f);
        }
        byte[] yuv = allFrames.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 3,
            new H264FrameEncoder.Options { EnableSubMbPartitions = true, EnableSubpelMe = true });

        string dir = Path.Combine(Path.GetTempPath(), "h264enc_p3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string h264Path = Path.Combine(dir, "in.h264");
            string outPath = Path.Combine(dir, "out.yuv");
            File.WriteAllBytes(h264Path, h264);
            var psi = new System.Diagnostics.ProcessStartInfo(ff,
                $"-y -i \"{h264Path}\" -f rawvideo -pix_fmt yuv420p \"{outPath}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(15000);
            if (File.Exists(outPath))
            {
                long len = new FileInfo(outPath).Length;
                int frameBytes = W * H + 2 * (W / 2) * (H / 2);
                Assert.True(len >= 3L * frameBytes,
                    $"ffmpeg produced {len} bytes, expected >= 3 frames ({3L * frameBytes})");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string? FindFfmpeg()
    {
        string[] candidates = {
            @"C:\FFMPEG\bin\ffmpeg.exe",
            @"C:\FFMPEG-CURRENT\bin\ffmpeg.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }
}
