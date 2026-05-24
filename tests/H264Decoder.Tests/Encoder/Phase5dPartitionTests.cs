using H264Decoder.Encoder;
using Xunit.Abstractions;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 5d: B-slice 16x8/8x16 partitioned macroblocks. Fixtures use split-motion
/// content (top half moves one way, bottom half another — or left/right split) so 16x8 or
/// 8x16 partitions out-cost a single 16x16 MV. Verifies round-trip through our decoder + ffmpeg
/// for both CAVLC and CABAC paths.</summary>
public class Phase5dPartitionTests
{
    private readonly ITestOutputHelper _output;
    public Phase5dPartitionTests(ITestOutputHelper output) { _output = output; }

    /// <summary>16x8-friendly fixture: top half scrolls right at +2 px/frame, bottom half scrolls
    /// left at -2 px/frame. A single 16x16 MV can fit only one half; 16x8 fits both.</summary>
    private static byte[] MakeSplitVerticalMotion(int W, int H, int frames)
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
                    int sx = (y < H / 2)
                        ? ((x + shift) % W)
                        : ((x - shift + 4 * W) % W);
                    byte v = (byte)((sx * 4 + y) & 0xFF);
                    buf[yOff + y * W + x] = v;
                }
            for (int i = 0; i < cSize; i++) { buf[uOff + i] = 128; buf[vOff + i] = 128; }
        }
        return buf;
    }

    /// <summary>8x16-friendly fixture: left half scrolls down, right half scrolls up.</summary>
    private static byte[] MakeSplitHorizontalMotion(int W, int H, int frames)
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
                    int sy = (x < W / 2)
                        ? ((y + shift) % H)
                        : ((y - shift + 4 * H) % H);
                    byte v = (byte)((x + sy * 4) & 0xFF);
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
    public void Partition_16x8_VerticalSplit_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Partition_16x8_VerticalSplit_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Partition_8x16_HorizontalSplit_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeSplitHorizontalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Partition_8x16_HorizontalSplit_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeSplitHorizontalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Partition_LargerFrame_VerticalSplit_RoundTrip_Cabac()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes_VerticalSplit_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes_VerticalSplit_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes_HorizontalSplit_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeSplitHorizontalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes_HorizontalSplit_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeSplitHorizontalMotion(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Partition_FfmpegCrossDecodes_LargerFrame_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 64, H = 48;
        byte[] yuv = MakeSplitVerticalMotion(W, H, 3);
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
