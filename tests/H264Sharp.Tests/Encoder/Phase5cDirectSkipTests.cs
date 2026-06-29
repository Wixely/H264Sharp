using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 5c: B_Direct_16x16 + B_Skip + spatial direct mode. Verifies that the encoder
/// picks Direct/Skip on no-motion content (where direct prediction is near-perfect), and that
/// streams round-trip through our decoder + ffmpeg for both CAVLC and CABAC paths.</summary>
public class Phase5cDirectSkipTests
{
    private readonly ITestOutputHelper _output;
    public Phase5cDirectSkipTests(ITestOutputHelper output) { _output = output; }

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

    private static byte[] RepeatFrame(byte[] frame, int frames)
    {
        var buf = new byte[frame.Length * frames];
        for (int f = 0; f < frames; f++) Buffer.BlockCopy(frame, 0, buf, f * frame.Length, frame.Length);
        return buf;
    }

    /// <summary>Solid-color frame in motion: each frame shifts the pattern by a small offset so
    /// L0/L1 ME find good matches. Used to compare bitstream sizes.</summary>
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

    // ---------- Round-trip tests ----------

    [Fact]
    public void DirectSkip_NoMotion_3Frames_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] frame = MakeSolidYuv(W, H, 100, 128, 128);
        byte[] yuv = RepeatFrame(frame, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
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
    public void DirectSkip_NoMotion_3Frames_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] frame = MakeSolidYuv(W, H, 100, 128, 128);
        byte[] yuv = RepeatFrame(frame, 3);
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
    public void DirectSkip_Motion_5Frames_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            Assert.True(psnr > 30.0, $"frame {i} PSNR={psnr:F2}dB");
        }
    }

    [Fact]
    public void DirectSkip_Motion_5Frames_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            Assert.True(psnr > 30.0, $"frame {i} PSNR={psnr:F2}dB");
        }
    }

    // ---------- Compression tests ----------

    [Fact]
    public void DirectSkip_OnRepeatedFrames_ShrinksBStream()
    {
        // On repeated identical frames, every B-MB should pick B_Skip (zero bits).
        // Phase 5c's IPBP should be much smaller than Phase 5a's (no skip): the B-frame
        // payload collapses to mb_skip_run with all-MBs-skipped.
        int W = 32, H = 32;
        byte[] frame = MakeSolidYuv(W, H, 100, 128, 128);
        byte[] yuv = RepeatFrame(frame, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true });
        _output.WriteLine($"5-frame repeated IPBP CAVLC: {h264.Length} bytes");
        // 5 MBs per frame × 5 frames = 25 MBs total. With Skip working, B-frames should
        // be very small (one mb_skip_run per B-slice).
        Assert.True(h264.Length < 400, $"Expected <400 bytes, got {h264.Length} — Skip not engaging");
    }

    // ---------- ffmpeg cross-decode ----------

    [Fact]
    public void DirectSkip_FfmpegCrossDecodes_NoMotion_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = RepeatFrame(MakeSolidYuv(W, H, 100, 128, 128), 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void DirectSkip_FfmpegCrossDecodes_NoMotion_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = RepeatFrame(MakeSolidYuv(W, H, 100, 128, 128), 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void DirectSkip_FfmpegCrossDecodes_Motion_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void DirectSkip_FfmpegCrossDecodes_Motion_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void DirectSkip_LargerFrame_RoundTrip_Cavlc()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeMotionSequence(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
    }

    [Fact]
    public void DirectSkip_LargerFrame_RoundTrip_Cabac()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeMotionSequence(W, H, 3);
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
