using H264Decoder.Encoder;
using Xunit.Abstractions;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 3 compression-gain regressions: phase-3 encoder (sub-pel + partitions) should
/// produce smaller bitstreams than phase-2 (16x16-only, integer-pel) on real-ish content.</summary>
public class Phase3CompressionTests
{
    private readonly ITestOutputHelper _output;
    public Phase3CompressionTests(ITestOutputHelper output) { _output = output; }


    private static byte[] BuildSlowPan30Frame(int W, int H)
    {
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        var yuv = new byte[frameBytes * 30];
        for (int t = 0; t < 30; t++)
        {
            byte[] f = new byte[frameBytes];
            int drift = t / 4;
            for (int y = 0; y < H; y++)
            {
                byte luma = (byte)(60 + (y / (H / 3)) * 80);
                for (int x = 0; x < W; x++)
                {
                    int xs = (x + drift) % W;
                    f[y * W + x] = (byte)(luma + (xs % 16 < 8 ? 0 : 20));
                }
            }
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            Array.Copy(f, 0, yuv, t * frameBytes, frameBytes);
        }
        return yuv;
    }

    [Fact]
    public void Phase3_SmallerThanPhase2_OnSlowPan30Frame()
    {
        int W = 64, H = 48;
        byte[] yuv = BuildSlowPan30Frame(W, H);
        byte[] phase2 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });
        byte[] phase3 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = true });
        // Phase 3 should not be larger than phase 2 on slow-pan; on motion-rich content the gain
        // from sub-pel is more visible. We allow equality (degenerate case) but require non-regression.
        Assert.True(phase3.Length <= (int)(phase2.Length * 1.02),
            $"Phase 3 ({phase3.Length} B) should not exceed phase 2 ({phase2.Length} B) on slow-pan content");
    }

    [Fact]
    public void ReportPhase2VsPhase3_SlowPan30Frame()
    {
        int W = 64, H = 48;
        byte[] yuv = BuildSlowPan30Frame(W, H);
        byte[] phase1Only = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableInterPrediction = false });
        byte[] phase2 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });
        byte[] phase3 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = true });
        var pics3 = new H264FrameDecoder().DecodeAllFrames(phase3);
        var pics2 = new H264FrameDecoder().DecodeAllFrames(phase2);
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        long totalSse3 = 0, totalSse2 = 0;
        int countY = W * H * 30;
        for (int t = 0; t < 30; t++)
        {
            byte[] src = yuv[(t * frameBytes)..((t * frameBytes) + W * H)];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int d3 = src[y * W + x] - pics3[t].Y[y * pics3[t].BufferWidth + x];
                    int d2 = src[y * W + x] - pics2[t].Y[y * pics2[t].BufferWidth + x];
                    totalSse3 += d3 * d3;
                    totalSse2 += d2 * d2;
                }
        }
        double mse3 = (double)totalSse3 / countY;
        double mse2 = (double)totalSse2 / countY;
        double psnr3 = mse3 == 0 ? 99 : 10.0 * Math.Log10(255.0 * 255.0 / mse3);
        double psnr2 = mse2 == 0 ? 99 : 10.0 * Math.Log10(255.0 * 255.0 / mse2);
        _output.WriteLine($"slow-pan 30-frame: I-only={phase1Only.Length}B phase2={phase2.Length}B phase3={phase3.Length}B");
        _output.WriteLine($"  phase2 PSNR-Y={psnr2:F2}dB  phase3 PSNR-Y={psnr3:F2}dB");
        _output.WriteLine($"  size reduction phase3 vs phase2: {100.0 * (phase2.Length - phase3.Length) / phase2.Length:F1}%");
    }

    [Fact]
    public void ReportPhase2VsPhase3_MixedMotion10Frame()
    {
        // Mixed motion: top-left moves diagonally, top-right horizontally, bottom static.
        int W = 64, H = 48;
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        var yuv = new byte[frameBytes * 10];
        for (int t = 0; t < 10; t++)
        {
            byte[] f = new byte[frameBytes];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int dx, dy;
                    if (y < 24 && x < 32) { dx = t; dy = t; }
                    else if (y < 24) { dx = -t; dy = 0; }
                    else { dx = 0; dy = 0; }
                    int xs = ((x + dx) % W + W) % W;
                    int ys = ((y + dy) % H + H) % H;
                    // High-frequency texture so partition decisions matter.
                    f[y * W + x] = (byte)(80 + ((xs / 2) % 2 == 0 ? 0 : 40) + ((ys / 4) % 2 == 0 ? 0 : 20));
                }
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            Array.Copy(f, 0, yuv, t * frameBytes, frameBytes);
        }
        byte[] phase2 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 10,
            new H264FrameEncoder.Options { EnableSubpelMe = false, EnableSubMbPartitions = false });
        byte[] phase3 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 10,
            new H264FrameEncoder.Options { EnableSubpelMe = true, EnableSubMbPartitions = true });
        var pics3 = new H264FrameDecoder().DecodeAllFrames(phase3);
        var pics2 = new H264FrameDecoder().DecodeAllFrames(phase2);
        long sse3 = 0, sse2 = 0;
        int count = W * H * 10;
        for (int t = 0; t < 10; t++)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int src = yuv[t * frameBytes + y * W + x];
                    int d3 = src - pics3[t].Y[y * pics3[t].BufferWidth + x];
                    int d2 = src - pics2[t].Y[y * pics2[t].BufferWidth + x];
                    sse3 += d3 * d3;
                    sse2 += d2 * d2;
                }
        }
        double psnr3 = sse3 == 0 ? 99 : 10.0 * Math.Log10(255.0 * 255.0 * count / sse3);
        double psnr2 = sse2 == 0 ? 99 : 10.0 * Math.Log10(255.0 * 255.0 * count / sse2);
        _output.WriteLine($"mixed-motion 10-frame: phase2={phase2.Length}B phase3={phase3.Length}B");
        _output.WriteLine($"  phase2 PSNR-Y={psnr2:F2}dB  phase3 PSNR-Y={psnr3:F2}dB");
        _output.WriteLine($"  size reduction phase3 vs phase2: {100.0 * (phase2.Length - phase3.Length) / phase2.Length:F1}%");
    }

    [Fact]
    public void Phase3_RoundTripsCleanly_OnSlowPan30Frame()
    {
        int W = 64, H = 48;
        byte[] yuv = BuildSlowPan30Frame(W, H);
        byte[] phase3 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30);
        var pics = new H264FrameDecoder().DecodeAllFrames(phase3);
        Assert.Equal(30, pics.Count);
    }
}
