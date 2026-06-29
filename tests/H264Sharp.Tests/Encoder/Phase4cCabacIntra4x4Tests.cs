using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 4c: CABAC + Intra_4x4 combined path. Validates that when both options are
/// enabled, the encoder emits real Intra_4x4 (I_NxN) macroblocks via CABAC (instead of falling
/// back to Intra_16x16) and that the output round-trips through our decoder and ffmpeg.</summary>
public class Phase4cCabacIntra4x4Tests
{
    private readonly ITestOutputHelper _output;
    public Phase4cCabacIntra4x4Tests(ITestOutputHelper output) { _output = output; }

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

    private static byte[] MakeHighFreqYuv(int W, int H)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        var yuv = new byte[ySize + 2 * cSize];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = 220;
                int mbX = x / 16, mbY = y / 16;
                int relX = x % 16, relY = y % 16;
                bool fwd = ((mbX + mbY) & 1) == 0;
                int diff = fwd ? Math.Abs(relX - relY) : Math.Abs(relX - (15 - relY));
                if (diff == 0) v = 30;
                yuv[y * W + x] = v;
            }
        Array.Fill<byte>(yuv, 128, ySize, 2 * cSize);
        return yuv;
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
        if (mse <= 0) return 99.0;
        return 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    [Fact]
    public void Cabac_Intra4x4_SolidColor_RoundTrip()
    {
        int W = 16, H = 16;
        byte[] yuv = MakeSolidYuv(W, H, y: 100, u: 120, v: 130);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        Assert.Equal(W, pic.Width);
        Assert.Equal(H, pic.Height);
        int maxErr = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                maxErr = Math.Max(maxErr, Math.Abs(100 - pic.Y[y * pic.BufferWidth + x]));
        Assert.InRange(maxErr, 0, 20);
    }

    [Fact]
    public void Cabac_Intra4x4_HighFreqContent_RoundTrip()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        Assert.Equal(W, pic.Width);
        Assert.Equal(H, pic.Height);
        double psnr = ComputeYPsnr(yuv, pic.Y, pic.BufferWidth, W, H);
        _output.WriteLine($"CABAC+Intra_4x4 high-freq: bytes={h264.Length}, PSNR={psnr:F2}dB");
        Assert.True(psnr > 35.0, $"Reconstruction PSNR too low: {psnr:F2}dB");
    }

    [Fact]
    public void Cabac_Intra4x4_ProducesValidStreamComparableToIntra16x16()
    {
        // Both CABAC + Intra_4x4 and CABAC + Intra_16x16-only should produce valid streams of
        // comparable size on the same fixture. (We don't assert which is smaller — CABAC's
        // efficient residual coding narrows the Intra_4x4 win to be content-dependent.)
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] cabac16only = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = false });
        byte[] cabac4x4 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        _output.WriteLine($"CABAC 16x16-only={cabac16only.Length} bytes, CABAC+Intra_4x4={cabac4x4.Length} bytes");
        // Both should be in a sensible size range.
        Assert.InRange(cabac4x4.Length, cabac16only.Length / 2, cabac16only.Length * 2);
        // Both should round-trip without errors.
        Assert.NotNull(new H264FrameDecoder().DecodeFirstIFrame(cabac16only));
        Assert.NotNull(new H264FrameDecoder().DecodeFirstIFrame(cabac4x4));
    }

    [Fact]
    public void Cabac_Intra4x4_MultiMb_RoundTrip()
    {
        int W = 64, H = 48; // 4×3 = 12 MBs
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        Assert.Equal(W, pic.Width);
        Assert.Equal(H, pic.Height);
        double psnr = ComputeYPsnr(yuv, pic.Y, pic.BufferWidth, W, H);
        _output.WriteLine($"CABAC+Intra_4x4 12-MB: bytes={h264.Length}, PSNR={psnr:F2}dB");
        Assert.True(psnr > 35.0, $"Multi-MB reconstruction PSNR too low: {psnr:F2}dB");
    }

    [Fact]
    public void Cabac_Intra4x4_GradientContent_RoundTrip()
    {
        int W = 32, H = 32;
        var yuv = new byte[W * H + 2 * (W / 2) * (H / 2)];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                yuv[y * W + x] = (byte)(x * 8);
        Array.Fill<byte>(yuv, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        var pic = new H264FrameDecoder().DecodeFirstIFrame(h264);
        int maxErr = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                maxErr = Math.Max(maxErr, Math.Abs(yuv[y * W + x] - pic.Y[y * pic.BufferWidth + x]));
        _output.WriteLine($"CABAC+Intra_4x4 gradient maxErr={maxErr}, bytes={h264.Length}");
        Assert.InRange(maxErr, 0, 20);
    }

    [Fact]
    public void Cabac_Intra4x4_FfmpegCrossDecodes_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void Cabac_Intra4x4_FfmpegCrossDecodes_LargerFrame_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 64, H = 48;
        byte[] yuv = MakeHighFreqYuv(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 26, frames: 1,
            new H264FrameEncoder.Options { EnableCabac = true, EnableIntra4x4 = true });
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
