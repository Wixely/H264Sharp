using H264Decoder.Encoder;
using Xunit.Abstractions;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 5e stage 2: B_8x8 with sub-8x8 partitions (sub_mb_types 4..12 — 8x4, 4x8, 4x4
/// variants per quadrant). Fixtures use fine-grained motion patterns that benefit from per-sub-
/// partition MVs within each 8x8 quadrant.</summary>
public class Phase5eP8x8Stage2Tests
{
    private readonly ITestOutputHelper _output;
    public Phase5eP8x8Stage2Tests(ITestOutputHelper output) { _output = output; }

    /// <summary>Per-8x8-quadrant horizontal-banded motion: within each quadrant, the top 8x4 band
    /// moves one direction and the bottom 8x4 moves the opposite. Encourages 8x4 sub-partitions
    /// (sub_mb_types 4, 6, 8).</summary>
    private static byte[] MakeBandedMotion(int W, int H, int frames)
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
                    int qBandY = (y % 8); // within quadrant
                    bool topBand = qBandY < 4;
                    int sx = topBand ? (x + shift) % W : (x - shift + 4 * W) % W;
                    byte v = (byte)((sx * 4 + y * 3) & 0xFF);
                    buf[yOff + y * W + x] = v;
                }
            for (int i = 0; i < cSize; i++) { buf[uOff + i] = 128; buf[vOff + i] = 128; }
        }
        return buf;
    }

    /// <summary>Per-4x4-block motion: a checkerboard pattern where each 4x4 block within a
    /// quadrant moves differently. Encourages 4x4 sub-partitions (sub_mb_types 10/11/12).</summary>
    private static byte[] MakeChecker4x4Motion(int W, int H, int frames)
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
                    int bx = (x % 8) / 4;
                    int by = (y % 8) / 4;
                    bool diag = (bx == by);
                    int sx = diag ? (x + shift) % W : x;
                    int sy = diag ? y : (y + shift) % H;
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
    public void P8x8_BandedMotion_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeBandedMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC banded frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 26.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void P8x8_BandedMotion_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeBandedMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC banded frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 26.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void P8x8_Checker4x4_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeChecker4x4Motion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC checker4x4 frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 26.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void P8x8_BandedMotion_FfmpegCrossDecodes_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeBandedMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void P8x8_BandedMotion_FfmpegCrossDecodes_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeBandedMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void P8x8_Checker4x4_FfmpegCrossDecodes_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeChecker4x4Motion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void P8x8_Stage2_LargerFrame_Cabac()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeBandedMotion(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
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
