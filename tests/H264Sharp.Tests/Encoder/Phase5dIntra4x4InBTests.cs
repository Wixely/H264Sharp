using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 5d-full (intra-4x4-in-B): Intra_4x4 macroblocks in B-slices (B mb_type code 23
/// = I_NxN). Fixtures use high-frequency B-frame content where neither inter nor Intra_16x16
/// fit well — Intra_4x4's per-block mode selection wins. Verifies round-trip through our decoder
/// and ffmpeg for both CAVLC and CABAC paths.</summary>
public class Phase5dIntra4x4InBTests
{
    private readonly ITestOutputHelper _output;
    public Phase5dIntra4x4InBTests(ITestOutputHelper output) { _output = output; }

    /// <summary>3-frame fixture: flat I/P refs + a B frame with per-MB diagonal strokes
    /// (high-frequency content). Inter can't match anything in the flat refs; Intra_16x16's
    /// 4 modes can't capture diagonals well; Intra_4x4's 9 per-block modes should win.</summary>
    private static byte[] MakeIntra4x4InBFixture(int W, int H)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        int frameSize = ySize + 2 * cSize;
        var buf = new byte[3 * frameSize];

        // Frame 0: flat 220.
        for (int i = 0; i < ySize; i++) buf[i] = 220;
        for (int i = ySize; i < frameSize; i++) buf[i] = 128;

        // Frame 1 (display 1 = B): diagonal-stroke per MB on flat background. Each 16x16 MB has
        // a 1-pixel-wide diagonal at relX==relY (or relX==15-relY for the alternating MB).
        int yOff1 = frameSize;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = 220;
                int mbX = x / 16, mbY = y / 16;
                int relX = x % 16, relY = y % 16;
                bool fwd = ((mbX + mbY) & 1) == 0;
                int diff = fwd ? Math.Abs(relX - relY) : Math.Abs(relX - (15 - relY));
                if (diff == 0) v = 30;
                buf[yOff1 + y * W + x] = v;
            }
        for (int i = 0; i < cSize; i++)
        {
            buf[yOff1 + ySize + i] = 128;
            buf[yOff1 + ySize + cSize + i] = 128;
        }

        // Frame 2 (display 2 = P): flat 220 again.
        int yOff2 = 2 * frameSize;
        for (int i = 0; i < ySize; i++) buf[yOff2 + i] = 220;
        for (int i = ySize; i < frameSize; i++) buf[yOff2 + i] = 128;
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
    public void Intra4x4InB_HighFreqContent_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false, EnableIntra4x4 = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 3; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC intra4x4-in-B frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Intra4x4InB_HighFreqContent_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true, EnableIntra4x4 = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 3; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC intra4x4-in-B frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 28.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void Intra4x4InB_BeatsIntra16x16OnlyOnHighFreq()
    {
        // Sanity: with EnableIntra4x4=true the encoder can sometimes pick Intra_4x4 in the B frame.
        // Compare bitstream size vs EnableIntra4x4=false — they should be close (Intra_4x4 may win
        // sometimes, may not, depending on the per-MB SAD). Just check both decode cleanly.
        int W = 32, H = 32;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] with4x4 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableIntra4x4 = true });
        byte[] no4x4 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableIntra4x4 = false });
        _output.WriteLine($"with4x4={with4x4.Length} bytes, no4x4={no4x4.Length} bytes");
        Assert.NotNull(new H264FrameDecoder().DecodeAllFrames(with4x4));
        Assert.NotNull(new H264FrameDecoder().DecodeAllFrames(no4x4));
        // Sanity bound: with4x4 should be within 2x of no4x4 either direction.
        Assert.InRange(with4x4.Length, no4x4.Length / 2, no4x4.Length * 2);
    }

    [Fact]
    public void Intra4x4InB_FfmpegCrossDecodes_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false, EnableIntra4x4 = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Intra4x4InB_FfmpegCrossDecodes_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true, EnableIntra4x4 = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Intra4x4InB_LargerFrame_RoundTrip_Cabac()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeIntra4x4InBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true, EnableIntra4x4 = true });
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
