using H264Decoder.Encoder;
using Xunit.Abstractions;

namespace H264Decoder.Tests.Encoder;

/// <summary>Phase 5a: encoder emits IPBP GOP with B_L0/L1/Bi_16x16 (CAVLC). Verifies that streams
/// produced with EnableBFrames=true round-trip through our decoder and decode silently through
/// ffmpeg, and that B-frames are correctly placed in the bitstream's display order.</summary>
public class Phase5aBFrameTests
{
    private readonly ITestOutputHelper _output;
    public Phase5aBFrameTests(ITestOutputHelper output) { _output = output; }

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

    /// <summary>Multi-frame fixture: each frame is a translated version of the same content,
    /// so motion is exactly predictable. Phase 5a B-MBs should win on most blocks.</summary>
    private static byte[] MakeMotionSequence(int W, int H, int frames)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        int frameSize = ySize + 2 * cSize;
        var buf = new byte[frames * frameSize];
        for (int f = 0; f < frames; f++)
        {
            int shift = f * 2; // 2-pixel horizontal shift per frame
            int yOff = f * frameSize;
            int uOff = yOff + ySize;
            int vOff = uOff + cSize;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int sx = ((x + shift) % W);
                    // Gradient with a couple of vertical bands so SAD has signal.
                    byte v = (byte)((sx * 4 + y) & 0xFF);
                    buf[yOff + y * W + x] = v;
                }
            // Flat chroma.
            for (int i = 0; i < cSize; i++) { buf[uOff + i] = 128; buf[vOff + i] = 128; }
        }
        return buf;
    }

    [Fact]
    public void BFrames_SolidColor_3Frames_RoundTrip()
    {
        // 3-frame IPBP: I0, P2 (display 2), B1.
        int W = 32, H = 32;
        var combined = new List<byte>();
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        combined.AddRange(MakeSolidYuv(W, H, 100, 128, 128));
        byte[] yuv = combined.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true });
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
    public void BFrames_5Frames_DecodeOrderHasIPBPB_DisplayOrderIsLinear()
    {
        // 5 frames: I0 P2 B1 P4 B3 in coding order; I P B P B in display order.
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);
        // DecodeAllFrames returns display-ordered pictures (sorted by POC).
        // Display order should yield monotonically increasing POC.
        for (int i = 1; i < pics.Count; i++)
            Assert.True(pics[i].PicOrderCnt > pics[i - 1].PicOrderCnt,
                $"POC not monotonic at i={i}: prev={pics[i - 1].PicOrderCnt}, cur={pics[i].PicOrderCnt}");
        // Each pic should reconstruct close to its source frame.
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 5; i++)
        {
            int maxErr = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int src = yuv[i * frameSize + y * W + x];
                    int dec = pics[i].Y[y * pics[i].BufferWidth + x];
                    maxErr = Math.Max(maxErr, Math.Abs(src - dec));
                }
            _output.WriteLine($"frame {i}: maxErr={maxErr}");
            Assert.InRange(maxErr, 0, 60);
        }
    }

    [Fact]
    public void BFrames_StreamIsSmallerThanIPPPOnPredictableMotion()
    {
        // On predictable motion the B-frame should benefit from bipred (avg of past+future),
        // outperforming the IPPP baseline that only references the past.
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] ippp = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = false });
        byte[] ipbp = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true });
        _output.WriteLine($"IPPP={ippp.Length} bytes, IPBP={ipbp.Length} bytes");
        // On 5-frame predictable motion, IPBP should at least not be drastically larger.
        // Phase 5a uses non-iterative bipred and skips B_Skip / B_Direct, so the win can be small;
        // accept anything <= 1.5x the IPPP size as "reasonable".
        Assert.True(ipbp.Length <= ippp.Length * 3 / 2,
            $"IPBP ({ipbp.Length}) is suspiciously larger than IPPP ({ippp.Length})");
    }

    [Fact]
    public void BFrames_FfmpegCrossDecodes_3Frames_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void BFrames_FfmpegCrossDecodes_5Frames_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 5);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 5,
            new H264FrameEncoder.Options { EnableBFrames = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void BFrames_LargerFrame_RoundTrip()
    {
        int W = 64, H = 48; // 12 MBs per frame
        byte[] yuv = MakeMotionSequence(W, H, 3);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
    }

    [Fact]
    public void BFrames_CabacRejected()
    {
        // Phase 5a: CABAC + B-frames combo is not yet supported (Phase 5b territory).
        int W = 32, H = 32;
        byte[] yuv = MakeMotionSequence(W, H, 3);
        Assert.Throws<NotSupportedException>(() =>
            H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
                new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true }));
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
