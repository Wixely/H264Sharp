using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 4a-i: Intra_4x4 in the encoder. High-frequency intra content should benefit
/// from per-4x4 mode selection vs. forcing Intra_16x16 only.</summary>
public class Phase4Intra4x4Tests
{
    private readonly ITestOutputHelper _output;
    public Phase4Intra4x4Tests(ITestOutputHelper output) { _output = output; }

    /// <summary>Build a YUV420 fixture with text-on-flat-background content: flat 16x16
    /// regions (where Intra_16x16 is fine) plus thin diagonal "letter strokes" within
    /// individual MBs (where Intra_4x4 catches the edge cleanly).</summary>
    private static byte[] MakeHighFreqYuv(int W, int H)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        var yuv = new byte[ySize + 2 * cSize];
        // White background with sharp diagonal "stroke" pixels.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = 220;
                // Thin diagonal lines at MB-internal positions, alternating direction.
                int mbX = x / 16, mbY = y / 16;
                int relX = x % 16, relY = y % 16;
                bool fwd = ((mbX + mbY) & 1) == 0;
                int diff = fwd ? Math.Abs(relX - relY) : Math.Abs(relX - (15 - relY));
                if (diff == 0) v = 30; // 1-pixel-wide diagonal stroke per MB
                yuv[y * W + x] = v;
            }
        Array.Fill<byte>(yuv, 128, ySize, 2 * cSize);
        return yuv;
    }

    [Fact]
    public void Intra4x4_HighFreqContent_SmallerOrBetterThanIntra16x16()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] bytes16only = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = false });
        byte[] bytes4x4 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = true });

        var dec16 = new H264FrameDecoder().DecodeFirstIFrame(bytes16only);
        var dec4 = new H264FrameDecoder().DecodeFirstIFrame(bytes4x4);
        double psnr16 = ComputeYPsnr(yuv, dec16.Y, dec16.BufferWidth, W, H);
        double psnr4 = ComputeYPsnr(yuv, dec4.Y, dec4.BufferWidth, W, H);
        _output.WriteLine($"Intra_16x16 PSNR={psnr16:F2}dB, Intra_4x4 PSNR={psnr4:F2}dB");
        _output.WriteLine($"Intra_16x16 bytes={bytes16only.Length}, Intra_4x4 bytes={bytes4x4.Length}");
        // On high-frequency content Intra_4x4 should be selected for at least some MBs, producing
        // a smaller bitstream (smaller residual due to better-fit per-block prediction).
        // PSNR may be slightly lower because Intra_4x4 quantizes 16 small DC values directly
        // (no 16x16 Hadamard chain), but the encoder picks it when SAD savings outweigh bit cost.
        Assert.True(bytes4x4.Length < bytes16only.Length,
            $"Intra_4x4 should produce a smaller bitstream than Intra_16x16-only on high-freq content " +
            $"({bytes4x4.Length} vs {bytes16only.Length} bytes)");
        // Reconstruction quality should remain high (>= 40 dB at qp=22).
        Assert.True(psnr4 > 40.0, $"Intra_4x4 PSNR too low: {psnr4:F2}dB");
    }

    [Fact]
    public void Intra4x4_RoundTrip_DecodesCleanly()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        Assert.Equal(W, pic.Width);
        Assert.Equal(H, pic.Height);
        // Reconstruction should be close to source — within reasonable distortion at qp=22.
        int maxErr = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                maxErr = Math.Max(maxErr, Math.Abs(yuv[y * W + x] - pic.Y[y * pic.BufferWidth + x]));
        Assert.InRange(maxErr, 0, 60);
    }

    [Fact]
    public void Intra4x4_SolidColorMb_PicksIntra16x16()
    {
        // Solid color is best encoded as Intra_16x16 DC. Validates that the SAD comparator
        // doesn't force Intra_4x4 when 16x16 is clearly better.
        int W = 32, H = 32;
        byte[] yuv = MakeSolidYuv(W, H, y: 100, u: 128, v: 128);
        byte[] bytes16only = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = false });
        byte[] bytes4x4 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 24, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = true });
        // With Intra_4x4 enabled, encoder should still pick 16x16 for solid color
        // (since its SAD is essentially equal but Intra_16x16 has lower bit cost),
        // so output sizes should be very close.
        _output.WriteLine($"16x16-only={bytes16only.Length}, 4x4-enabled={bytes4x4.Length}");
        Assert.True(bytes4x4.Length <= bytes16only.Length + 2,
            "Intra_4x4 should not be picked over Intra_16x16 for solid color");
    }

    [Fact]
    public void Intra4x4_FfmpegCrossDecodes_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Intra4x4_MultiMb_PrevFlagPath_RoundTrip()
    {
        // 64x48 fixture (12 MBs) with the high-freq pattern: should trigger Intra_4x4 in many MBs
        // and exercise the prev_flag-matching-predictor optimization path across neighbors.
        int W = 64, H = 48;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        Assert.Equal(W, pic.Width);
        Assert.Equal(H, pic.Height);
    }

    // ----- helpers -----
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

    private static double ComputeYPsnr(byte[] src, byte[] dec, int decStride, int W, int H)
    {
        double mse = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int d = src[y * W + x] - dec[y * decStride + x];
                mse += d * d;
            }
        mse /= (W * H);
        if (mse <= 0) return 100.0;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
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
            byte[] decoded = File.ReadAllBytes(tmpOut);
            int expected = W * H + 2 * (W / 2) * (H / 2);
            Assert.Equal(expected, decoded.Length);
        }
        finally
        {
            if (File.Exists(tmpIn)) File.Delete(tmpIn);
            if (File.Exists(tmpOut)) File.Delete(tmpOut);
        }
    }
}
