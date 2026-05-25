using H264Decoder.Encoder;
using Xunit.Abstractions;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 5e (B_8x8 stage 1): B-slice macroblocks with four 8x8 quadrants, each
/// independently using L0_8x8 / L1_8x8 / Bi_8x8 (sub_mb_types 1..3). Fixtures use 4-quadrant
/// split-motion content so each 8x8 quadrant of each MB matches a different reference best.
/// Verifies round-trip through our decoder and ffmpeg for both CAVLC and CABAC paths.</summary>
public class Phase5eP8x8Tests
{
    private readonly ITestOutputHelper _output;
    public Phase5eP8x8Tests(ITestOutputHelper output) { _output = output; }

    /// <summary>4-quadrant motion split: per 16x16 MB, top-left scrolls right, top-right scrolls
    /// down, bottom-left scrolls left, bottom-right scrolls up. Within each 8x8 quadrant the
    /// content moves uniformly so one direction wins per quadrant.</summary>
    private static byte[] MakeQuadrantSplitMotion(int W, int H, int frames)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        int frameSize = ySize + 2 * cSize;
        var buf = new byte[frames * frameSize];
        for (int f = 0; f < frames; f++)
        {
            int shift = f * 2;
            int yOff = f * frameSize;
            int uOff = yOff + ySize;
            int vOff = uOff + cSize;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int mbRelX = x % 16, mbRelY = y % 16;
                    // Per-8x8-quadrant direction.
                    int qx = mbRelX / 8, qy = mbRelY / 8;
                    int sx, sy;
                    if (qx == 0 && qy == 0)        // TL: scroll right
                    { sx = (x + shift) % W; sy = y; }
                    else if (qx == 1 && qy == 0)   // TR: scroll down
                    { sx = x; sy = (y + shift) % H; }
                    else if (qx == 0 && qy == 1)   // BL: scroll left
                    { sx = (x - shift + 4 * W) % W; sy = y; }
                    else                            // BR: scroll up
                    { sx = x; sy = (y - shift + 4 * H) % H; }
                    byte v = (byte)((sx * 4 + sy * 3) & 0xFF);
                    buf[yOff + y * W + x] = v;
                }
            for (int i = 0; i < cSize; i++) { buf[uOff + i] = 128; buf[vOff + i] = 128; }
        }
        return buf;
    }

    private static double ComputeYPsnr(byte[] src, int srcOff, byte[] dec, int decStride, int W, int H)
    {
        double mse = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int d = src[srcOff + y * W + x] - dec[y * decStride + x];
                mse += d * d;
            }
        mse /= (W * H);
        if (mse <= 0) return 99.0;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Fact]
    public void P8x8_QuadrantSplit_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC P8x8 frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void P8x8_QuadrantSplit_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC P8x8 frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void P8x8_LargerFrame_RoundTrip_Cabac()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
    }

    [Fact]
    public void P8x8_FfmpegCrossDecodes_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void P8x8_FfmpegCrossDecodes_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void P8x8_FfmpegCrossDecodes_LargerFrame_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 64, H = 48;
        byte[] yuv = MakeQuadrantSplitMotion(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\FFMPEG\bin\ffmpeg.exe",
            @"C:\FFMPEG-CURRENT\bin\ffmpeg.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    private static void AssertFfmpegDecodesSilently(string ffmpeg, byte[] h264, int W, int H)
    {
        string tmpIn = Path.GetTempFileName() + ".h264";
        string tmpOut = Path.GetTempFileName() + ".yuv";
        try
        {
            File.WriteAllBytes(tmpIn, h264);
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg,
                $"-v error -i \"{tmpIn}\" -f rawvideo -pix_fmt yuv420p \"{tmpOut}\" -y")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            Assert.True(p.ExitCode == 0, $"ffmpeg failed: {err}");
            Assert.True(string.IsNullOrWhiteSpace(err), $"ffmpeg complained: {err}");
        }
        finally
        {
            if (File.Exists(tmpIn)) File.Delete(tmpIn);
            if (File.Exists(tmpOut)) File.Delete(tmpOut);
        }
    }
}
