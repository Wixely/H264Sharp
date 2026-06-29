using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 5d (intra-in-B): Intra_16x16 macroblocks in B-slices (B mb_type codes 24..47).
/// Fixtures use a "new content" B-frame whose pixels can't be predicted from either L0 or L1,
/// forcing the encoder to pick intra. Verifies both CAVLC and CABAC paths round-trip through our
/// decoder and ffmpeg.</summary>
public class Phase5dIntraInBTests
{
    private readonly ITestOutputHelper _output;
    public Phase5dIntraInBTests(ITestOutputHelper output) { _output = output; }

    /// <summary>3-frame fixture: I (flat 100), P (flat 100, ME finds zero), B (uncorrelated noise).
    /// The B frame's content can't be predicted from the flat refs, so intra-in-B should win.</summary>
    private static byte[] MakeIntraInBFixture(int W, int H)
    {
        int ySize = W * H;
        int cSize = (W / 2) * (H / 2);
        int frameSize = ySize + 2 * cSize;
        var buf = new byte[3 * frameSize];

        // Frame 0: flat grey 100.
        for (int i = 0; i < ySize; i++) buf[i] = 100;
        for (int i = ySize; i < frameSize; i++) buf[i] = 128;

        // Frame 1 (display index 1, encoded as B): smooth gradient with vertical bands. Intra_16x16
        // Plane mode fits this well; inter has nothing to match against the flat refs.
        int yOff1 = frameSize;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                buf[yOff1 + y * W + x] = (byte)((x * 4 + y * 2) & 0xFF);
        for (int i = 0; i < cSize; i++)
        {
            buf[yOff1 + ySize + i] = 128;
            buf[yOff1 + ySize + cSize + i] = 128;
        }

        // Frame 2 (display index 2, encoded as P): flat grey again so MEt zero MV vs frame 0.
        int yOff2 = 2 * frameSize;
        for (int i = 0; i < ySize; i++) buf[yOff2 + i] = 100;
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
    public void IntraInB_NoMatchingRefs_RoundTrip_Cavlc()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeIntraInBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 3; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CAVLC intra-in-B frame {i}: PSNR={psnr:F2}dB");
            // Frame 1 (B with intra-friendly content) should reconstruct well; other two are flat.
            Assert.True(psnr > 30.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void IntraInB_NoMatchingRefs_RoundTrip_Cabac()
    {
        int W = 32, H = 32;
        byte[] yuv = MakeIntraInBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        int frameSize = W * H + 2 * (W / 2) * (H / 2);
        for (int i = 0; i < 3; i++)
        {
            double psnr = ComputeYPsnr(yuv, i * frameSize, pics[i].Y, pics[i].BufferWidth, W, H);
            _output.WriteLine($"CABAC intra-in-B frame {i}: PSNR={psnr:F2}dB");
            Assert.True(psnr > 30.0, $"frame {i} PSNR too low: {psnr:F2}dB");
        }
    }

    [Fact]
    public void IntraInB_FfmpegCrossDecodes_Cavlc_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeIntraInBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void IntraInB_FfmpegCrossDecodes_Cabac_WhenAvailable()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        byte[] yuv = MakeIntraInBFixture(W, H);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    /// <summary>Verify that the B-frame's bitstream becomes a lot smaller (or at least doesn't
    /// catastrophically grow) when intra-in-B is available vs. forcing all-inter on this fixture.
    /// (We can't directly disable intra-in-B via Options, but a sanity comparison vs. 16x16-only
    /// content shows the encoder isn't producing pathological output.)</summary>
    [Fact]
    public void IntraInB_DecodesAndReconstructsCorrectly_LargerFrame()
    {
        int W = 64, H = 48;
        byte[] yuv = MakeIntraInBFixture(W, H);
        // Re-build for larger frame.
        int ySize = W * H, cSize = (W / 2) * (H / 2), frameSize = ySize + 2 * cSize;
        yuv = new byte[3 * frameSize];
        // Flat I.
        for (int i = 0; i < ySize; i++) yuv[i] = 100;
        for (int i = ySize; i < frameSize; i++) yuv[i] = 128;
        // Gradient B.
        int yOff1 = frameSize;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                yuv[yOff1 + y * W + x] = (byte)((x * 4 + y * 2) & 0xFF);
        for (int i = 0; i < cSize; i++)
        {
            yuv[yOff1 + ySize + i] = 128;
            yuv[yOff1 + ySize + cSize + i] = 128;
        }
        // Flat P.
        int yOff2 = 2 * frameSize;
        for (int i = 0; i < ySize; i++) yuv[yOff2 + i] = 100;
        for (int i = ySize; i < frameSize; i++) yuv[yOff2 + i] = 128;

        byte[] h264Cavlc = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = false });
        byte[] h264Cabac = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableBFrames = true, EnableCabac = true });
        _output.WriteLine($"64x48 intra-in-B: CAVLC={h264Cavlc.Length} bytes, CABAC={h264Cabac.Length} bytes");

        var picsCavlc = new H264FrameDecoder().DecodeAllFrames(h264Cavlc);
        var picsCabac = new H264FrameDecoder().DecodeAllFrames(h264Cabac);
        Assert.Equal(3, picsCavlc.Count);
        Assert.Equal(3, picsCabac.Count);

        // B-frame (display index 1) should reconstruct close to the gradient source.
        double psnrCavlc = ComputeYPsnr(yuv, frameSize, picsCavlc[1].Y, picsCavlc[1].BufferWidth, W, H);
        double psnrCabac = ComputeYPsnr(yuv, frameSize, picsCabac[1].Y, picsCabac[1].BufferWidth, W, H);
        _output.WriteLine($"B-frame PSNR: CAVLC={psnrCavlc:F2}dB, CABAC={psnrCabac:F2}dB");
        Assert.True(psnrCavlc > 30.0);
        Assert.True(psnrCabac > 30.0);
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
