using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 5b: CABAC + B-frame combined path. Uses the same per-MB decision as Phase 5a
/// (B_L0/L1/Bi_16x16) but emits the macroblock layer through CABAC instead of CAVLC.
/// Verifies round-trip through our decoder and silent ffmpeg cross-decode.</summary>
public class Phase5bCabacBFrameTests
{
    private readonly ITestOutputHelper _output;
    public Phase5bCabacBFrameTests(ITestOutputHelper output) { _output = output; }

    private static byte[] MakeSolidYuv(int W, int H, byte y, byte u, byte v)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        var data = new byte[ySize + 2 * cSize];
        Array.Fill(data, y, 0, ySize);
        Array.Fill(data, u, ySize, cSize);
        Array.Fill(data, v, ySize + cSize, cSize);
        return data;
    }

    private static byte[] MakeMotionSequence(int W, int H, int frames)
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
                    int sx = ((x + shift) % W);
                    byte v = (byte)((sx * 4 + y) & 0xFF);
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
    public void Cabac_BFrames_SolidColor_3Frames_RoundTrip()
    {
        int W = 32, H = 32;
        var combined = new List<byte>();
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        byte[] yuv = combined.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        foreach (var pic in pics)
        {
            int maxErr = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    maxErr = Math.Max(maxErr, Math.Abs(100 - pic.Y[y * pic.BufferWidth + x]));
            Assert.InRange(maxErr, 0, 20);
        }
    }

    [Fact]
    public void Cabac_BFrames_5Frames_RoundTripPocMonotonic()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        for (int i = 1; i < pics.Count; i++)
            Assert.True(pics[i].PicOrderCnt > pics[i - 1].PicOrderCnt);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 30.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Cabac_BFrames_SmallerThanCavlcOnPredictableMotion()
    {
        // On the same content with the same mode decisions, CABAC should typically produce
        // a smaller bitstream than CAVLC. (B-frame residuals are usually low-entropy, which
        // is exactly CABAC's sweet spot.)
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] cavlc = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        byte[] cabac = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        _output.WriteLine($"CAVLC={cavlc.Length} bytes, CABAC={cabac.Length} bytes");
        // Be generous — within 1.2x is the soft pass; we want to catch catastrophic bloat,
        // not enforce a tight ratio on the small fixtures we have here.
        Assert.True(cabac.Length <= cavlc.Length * 12 / 10,
            $"CABAC bitstream ({cabac.Length}) is significantly larger than CAVLC ({cavlc.Length})");
    }

    [Fact]
    public void Cabac_BFrames_LargerFrame_RoundTrip()
    {
        int W = 64, H = 48; // 12 MBs per frame
        byte[] yuv = MakeMotionSequence(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
    }

    [Fact]
    public void Cabac_BFrames_FfmpegCrossDecodes_3Frames_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Cabac_BFrames_FfmpegCrossDecodes_5Frames_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
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
