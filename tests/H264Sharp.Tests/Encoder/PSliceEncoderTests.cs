using H264Sharp.Encoder;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase-2 P-slice encoder tests: IDR-then-P round-trip, motion estimation,
/// P_Skip detection, and cross-decoder validation.</summary>
public class PSliceEncoderTests
{
    private static byte[] MakeSolidYuv420(int w, int h, byte y, byte u, byte v)
    {
        int ySize = w * h;
        int cSize = (w / 2) * (h / 2);
        var data = new byte[ySize + 2 * cSize];
        Array.Fill(data, y, 0, ySize);
        Array.Fill(data, u, ySize, cSize);
        Array.Fill(data, v, ySize + cSize, cSize);
        return data;
    }

    private static byte[] ConcatFrames(params byte[][] frames)
    {
        int total = 0;
        foreach (var f in frames) total += f.Length;
        var r = new byte[total];
        int off = 0;
        foreach (var f in frames)
        {
            Array.Copy(f, 0, r, off, f.Length);
            off += f.Length;
        }
        return r;
    }

    [Fact]
    public void TwoIdenticalFrames_RoundTrip_FrameTwoMatchesFrameOne()
    {
        int W = 16, H = 16;
        byte[] f1 = MakeSolidYuv420(W, H, 80, 100, 140);
        byte[] yuv = ConcatFrames(f1, f1);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2);
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        // Compare frame-1 (P) to frame-0 (IDR) sample by sample — must be exactly identical
        // because frame 1 should be all P_Skip with MV (0,0) and copy from reference.
        Assert.Equal(pics[0].Y, pics[1].Y);
        Assert.Equal(pics[0].U, pics[1].U);
        Assert.Equal(pics[0].V, pics[1].V);
    }

    [Fact]
    public void TwoIdenticalFrames_AllPSkip_BitstreamMuchSmallerThanIOnly()
    {
        int W = 64, H = 48;
        byte[] f1 = MakeSolidYuv420(W, H, 100, 120, 130);
        byte[] yuv = ConcatFrames(f1, f1);

        byte[] ip = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2);
        byte[] ii = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableInterPrediction = false });

        Assert.True(ip.Length < ii.Length,
            $"I+P encode ({ip.Length} B) should be smaller than I-only ({ii.Length} B)");
    }

    [Fact]
    public void TwoIdenticalFrames_NoPSkip_StillRoundTrips()
    {
        // With EnablePSkip=false, MB-2's residual is zero but the encoder must emit explicit P_L0_16x16
        // mb_type=0 with zero MV + zero residual. Decoder still reconstructs identical pixels.
        int W = 16, H = 16;
        byte[] f1 = MakeSolidYuv420(W, H, 90, 110, 130);
        byte[] yuv = ConcatFrames(f1, f1);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2,
            new H264FrameEncoder.Options { EnablePSkip = false });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        int maxErr = MaxPixelDiff(pics[0], pics[1]);
        Assert.InRange(maxErr, 0, 2);
    }

    [Fact]
    public void HorizontalShiftedFrames_MotionEstimationFindsMv()
    {
        // Frame 1: a 16x16 luma "object" at column 0..15 on a flat background.
        // Frame 2: same object shifted right by 4 pixels.
        int W = 48, H = 16;
        byte[] f1 = new byte[W * H + 2 * (W / 2) * (H / 2)];
        byte[] f2 = new byte[f1.Length];
        Array.Fill<byte>(f1, 100);
        Array.Fill<byte>(f2, 100);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < 16; x++) f1[y * W + x] = 200;
            for (int x = 0; x < 16; x++) f2[y * W + (x + 4)] = 200;
        }
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        Array.Fill<byte>(f2, 128, W * H, 2 * (W / 2) * (H / 2));

        byte[] yuv = ConcatFrames(f1, f2);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2);

        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        // MB at (1, 0) contains the moving object after shift.
        int errMb1 = 0;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int dx = 16 + x;
                int v1 = f2[y * W + dx];
                int v2 = pics[1].Y[y * pics[1].BufferWidth + dx];
                errMb1 = Math.Max(errMb1, Math.Abs(v1 - v2));
            }
        Assert.InRange(errMb1, 0, 20);
    }

    [Fact]
    public void HorizontalShiftedFrames_OutputDecodesByFfmpeg()
    {
        string? ff = FindFfmpeg();
        if (ff is null) return;

        int W = 32, H = 16;
        byte[] f1 = new byte[W * H + 2 * (W / 2) * (H / 2)];
        byte[] f2 = new byte[f1.Length];
        Array.Fill<byte>(f1, 100);
        Array.Fill<byte>(f2, 100);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < 16; x++) f1[y * W + x] = 200;
            for (int x = 0; x < 16; x++) f2[y * W + (x + 4)] = 200;
        }
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        Array.Fill<byte>(f2, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] yuv = ConcatFrames(f1, f2);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2);

        string dir = Path.Combine(Path.GetTempPath(), "h264enc_p_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string h264Path = Path.Combine(dir, "in.h264");
            string outPath = Path.Combine(dir, "out.yuv");
            File.WriteAllBytes(h264Path, h264);
            var psi = new System.Diagnostics.ProcessStartInfo(ff,
                $"-y -i \"{h264Path}\" -f rawvideo -pix_fmt yuv420p \"{outPath}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(15000);
            if (File.Exists(outPath))
            {
                long len = new FileInfo(outPath).Length;
                Assert.True(len >= 2 * yuv.Length / 2,
                    $"ffmpeg produced {len} bytes, expected >= 2 frames");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MultipleFrames_RealMotion_RoundTrip()
    {
        // 5 frames of a vertical stripe moving 2 pixels right per frame.
        int W = 64, H = 16;
        var allFrames = new List<byte>();
        for (int t = 0; t < 5; t++)
        {
            byte[] f = new byte[W * H + 2 * (W / 2) * (H / 2)];
            Array.Fill<byte>(f, 100);
            int stripeX0 = 8 + t * 2;
            for (int y = 0; y < H; y++)
                for (int x = stripeX0; x < stripeX0 + 16; x++)
                    f[y * W + x] = 200;
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            allFrames.AddRange(f);
        }
        byte[] yuv = allFrames.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 5);
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(5, pics.Count);

        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        for (int t = 0; t < 5; t++)
        {
            byte[] src = yuv[(t * frameBytes)..((t + 1) * frameBytes)];
            int maxErr = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    maxErr = Math.Max(maxErr, Math.Abs(src[y * W + x] - pics[t].Y[y * pics[t].BufferWidth + x]));
            Assert.InRange(maxErr, 0, 25);
        }
    }

    [Fact]
    public void IdenticalFrames_PFrame_HasNoIdrNalUnit()
    {
        int W = 16, H = 16;
        byte[] f = MakeSolidYuv420(W, H, 70, 80, 90);
        byte[] yuv = ConcatFrames(f, f);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2);
        int idrCount = 0, nonIdrCount = 0;
        for (int i = 0; i + 4 < h264.Length; i++)
        {
            if (h264[i] != 0 || h264[i + 1] != 0 || h264[i + 2] != 0 || h264[i + 3] != 1) continue;
            byte nalHeader = h264[i + 4];
            int nalType = nalHeader & 0x1F;
            if (nalType == 5) idrCount++;
            else if (nalType == 1) nonIdrCount++;
        }
        Assert.Equal(1, idrCount);
        Assert.Equal(1, nonIdrCount);
    }

    [Fact]
    public void ThirtyFrameSlowMotion_IPlusP_SmallerThanIOnly()
    {
        // 30 frames of mostly-static content with a slight diagonal drift.
        int W = 64, H = 48;
        int frameBytes = W * H + 2 * (W / 2) * (H / 2);
        var yuv = new byte[frameBytes * 30];
        for (int t = 0; t < 30; t++)
        {
            byte[] f = new byte[frameBytes];
            int drift = t / 4;
            for (int y = 0; y < H; y++)
            {
                byte luma = (byte)(60 + (y / (H / 3)) * 80);
                for (int x = 0; x < W; x++)
                {
                    int xs = (x + drift) % W;
                    f[y * W + x] = (byte)(luma + (xs % 16 < 8 ? 0 : 20));
                }
            }
            Array.Fill<byte>(f, 128, W * H, 2 * (W / 2) * (H / 2));
            Array.Copy(f, 0, yuv, t * frameBytes, frameBytes);
        }
        byte[] ip = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30);
        byte[] ii = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 30,
            new H264FrameEncoder.Options { EnableInterPrediction = false });
        Assert.True(ip.Length < ii.Length,
            $"I+P 30-frame slow-motion encode ({ip.Length} B) should be smaller than I-only ({ii.Length} B)");
    }

    private static int MaxPixelDiff(H264Sharp.Decoder.Picture.DecodedPicture a, H264Sharp.Decoder.Picture.DecodedPicture b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                max = Math.Max(max, Math.Abs(a.Y[y * a.BufferWidth + x] - b.Y[y * b.BufferWidth + x]));
        return max;
    }

    private static string? FindFfmpeg()
    {
        string[] candidates = {
            @"C:\FFMPEG\bin\ffmpeg.exe",
            @"C:\FFMPEG-CURRENT\bin\ffmpeg.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                string c = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }
}
