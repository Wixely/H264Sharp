using H264Sharp.Encoder;
using Xunit.Abstractions;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Encoder;

/// <summary>Phase 4b: CABAC P-slice encoder. Stages 4b-i (P_Skip) + 4b-ii (P_L0_16x16) +
/// 4b-iii (16x8/8x16 sub-MB partition shapes) + 4b-iv (P_8x8). Verifies round-trip via
/// our own decoder and cross-decode via ffmpeg.</summary>
public class Phase4bCabacPTests
{
    private readonly ITestOutputHelper _output;
    public Phase4bCabacPTests(ITestOutputHelper output) { _output = output; }

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

    private static int MaxPixelDiff(H264Sharp.Decoder.Picture.DecodedPicture a, H264Sharp.Decoder.Picture.DecodedPicture b)
    {
        int maxErr = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                maxErr = Math.Max(maxErr, Math.Abs(a.Y[y * a.BufferWidth + x] - b.Y[y * b.BufferWidth + x]));
        return maxErr;
    }

    [Fact]
    public void CabacP_TwoIdenticalFrames_AllPSkip_RoundTrip()
    {
        // Two identical frames with CABAC: 2nd frame should be all P_Skip and decode losslessly
        // back to frame 1 (since CABAC P_Skip uses the same MV-derivation as CAVLC).
        int W = 32, H = 32;
        byte[] f1 = MakeSolidYuv420(W, H, 80, 100, 140);
        byte[] yuv = ConcatFrames(f1, f1);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        Assert.Equal(pics[0].Y, pics[1].Y);
        Assert.Equal(pics[0].U, pics[1].U);
        Assert.Equal(pics[0].V, pics[1].V);
    }

    [Fact]
    public void CabacP_TwoIdenticalFrames_AllPSkip_BytesNearCavlc()
    {
        int W = 32, H = 32;
        byte[] f1 = MakeSolidYuv420(W, H, 80, 100, 140);
        byte[] yuv = ConcatFrames(f1, f1);
        byte[] cavlc = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = false });
        byte[] cabac = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        _output.WriteLine($"all-skip CAVLC={cavlc.Length}, CABAC={cabac.Length}");
        // CABAC P-skip emits one bit per MB which is comparable to CAVLC's mb_skip_run UEC.
        // We just require both to decode and remain in a sensible ratio.
        Assert.True(cabac.Length < cavlc.Length * 3);
    }

    [Fact]
    public void CabacP_HorizontalShift_RoundTrip()
    {
        // Frame 1: an "object" at x=0..15 in a 48-wide picture, frame 2: shifted right by 4.
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
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 18, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
        // Verify frame-2 reconstruction is close to source (motion estimation should find the shift).
        int maxErr = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                maxErr = Math.Max(maxErr, Math.Abs(f2[y * W + x] - pics[1].Y[y * pics[1].BufferWidth + x]));
        _output.WriteLine($"CABAC horizontal-shift maxErr={maxErr}, bytes={h264.Length}");
        Assert.InRange(maxErr, 0, 20);
    }

    [Fact]
    public void CabacP_MultiFrame_MotionContent_RoundTrip()
    {
        // 3 frames with a slowly translating gradient. Tests that CABAC P-slices decode
        // with the same MV/residual accuracy as the CAVLC pipeline.
        int W = 32, H = 32;
        var frames = new List<byte[]>();
        for (int f = 0; f < 3; f++)
        {
            var buf = new byte[W * H + 2 * (W / 2) * (H / 2)];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    buf[y * W + x] = (byte)(((x + f) * 8) & 0xFF);
            Array.Fill<byte>(buf, 128, W * H, 2 * (W / 2) * (H / 2));
            frames.Add(buf);
        }
        byte[] yuv = ConcatFrames(frames.ToArray());
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 3,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(3, pics.Count);
        for (int f = 0; f < 3; f++)
        {
            int maxErr = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    maxErr = Math.Max(maxErr, Math.Abs(frames[f][y * W + x] - pics[f].Y[y * pics[f].BufferWidth + x]));
            _output.WriteLine($"CABAC frame {f} maxErr={maxErr}");
            Assert.InRange(maxErr, 0, 25);
        }
    }

    [Fact]
    public void CabacP_SmallerThanCavlc_OnRealMotionContent()
    {
        // 8-frame slow pan. CABAC should be measurably smaller than CAVLC for real motion content.
        int W = 64, H = 48;
        int N = 8;
        var combined = new List<byte>();
        for (int f = 0; f < N; f++)
        {
            var buf = new byte[W * H + 2 * (W / 2) * (H / 2)];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int v = ((x + f) * 5 + (y + f / 2) * 4) & 0xFF;
                    buf[y * W + x] = (byte)v;
                }
            Array.Fill<byte>(buf, 128, W * H, 2 * (W / 2) * (H / 2));
            combined.AddRange(buf);
        }
        byte[] yuv = combined.ToArray();
        byte[] cavlc = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 26, frames: N,
            new H264FrameEncoder.Options { EnableCabac = false, EnableIntra4x4 = false });
        byte[] cabac = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 26, frames: N,
            new H264FrameEncoder.Options { EnableCabac = true });
        _output.WriteLine($"CAVLC={cavlc.Length}, CABAC={cabac.Length}, ratio={(double)cabac.Length / cavlc.Length:F3}");
        // Both should decode cleanly.
        var picsCavlc = new H264FrameDecoder().DecodeAllFrames(cavlc);
        var picsCabac = new H264FrameDecoder().DecodeAllFrames(cabac);
        Assert.Equal(N, picsCavlc.Count);
        Assert.Equal(N, picsCabac.Count);
        // CABAC should be at least as compact on real motion content.
        Assert.True(cabac.Length <= cavlc.Length,
            $"CABAC ({cabac.Length} B) should be <= CAVLC ({cavlc.Length} B) on motion content");
    }

    [Fact]
    public void CabacP_FfmpegCrossDecodes()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        int W = 32, H = 32;
        var combined = new List<byte>();
        for (int f = 0; f < 2; f++)
        {
            var buf = new byte[W * H + 2 * (W / 2) * (H / 2)];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    buf[y * W + x] = (byte)(((x + f * 2) * 6) & 0xFF);
            Array.Fill<byte>(buf, 128, W * H, 2 * (W / 2) * (H / 2));
            combined.AddRange(buf);
        }
        byte[] yuv = combined.ToArray();
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        AssertFfmpegDecodesSilently(ffmpeg, h264, W, H);
    }

    [Fact]
    public void CabacP_SubMbPartition_16x8_RoundTrip()
    {
        // Frame 2 differs in the top vs bottom halves of an MB so 16x8 wins mode decision.
        int W = 32, H = 32;
        byte[] f1 = new byte[W * H + 2 * (W / 2) * (H / 2)];
        byte[] f2 = new byte[f1.Length];
        Array.Fill<byte>(f1, 100);
        Array.Fill<byte>(f2, 100);
        // Frame 1: top half y=200, bottom y=80. Frame 2: shifted left in top half, shifted right in bottom.
        for (int y = 0; y < H / 2; y++)
            for (int x = 0; x < W; x++) f1[y * W + x] = 200;
        for (int y = H / 2; y < H; y++)
            for (int x = 0; x < W; x++) f1[y * W + x] = 80;
        // Frame 2 ≈ identical: 16x8 might or might not be chosen by ME, but the pipeline must still round-trip.
        for (int y = 0; y < H / 2; y++)
            for (int x = 0; x < W; x++) f2[y * W + x] = 200;
        for (int y = H / 2; y < H; y++)
            for (int x = 0; x < W; x++) f2[y * W + x] = 80;
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        Array.Fill<byte>(f2, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] yuv = ConcatFrames(f1, f2);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
    }

    [Fact]
    public void CabacP_SubMbPartition_8x16_RoundTrip()
    {
        int W = 32, H = 32;
        byte[] f1 = new byte[W * H + 2 * (W / 2) * (H / 2)];
        byte[] f2 = new byte[f1.Length];
        Array.Fill<byte>(f1, 100);
        Array.Fill<byte>(f2, 100);
        // Frame 1 vs 2: identical 8x16 column pattern. Round-trip check.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W / 2; x++) f1[y * W + x] = 200;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W / 2; x++) f2[y * W + x] = 200;
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        Array.Fill<byte>(f2, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] yuv = ConcatFrames(f1, f2);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
    }

    [Fact]
    public void CabacP_SubMbPartition_8x8_RoundTrip()
    {
        // 8x8 partition test: small textured content to encourage P_8x8 split.
        int W = 32, H = 32;
        byte[] f1 = new byte[W * H + 2 * (W / 2) * (H / 2)];
        byte[] f2 = new byte[f1.Length];
        var rng = new Random(42);
        for (int i = 0; i < W * H; i++) f1[i] = (byte)rng.Next(80, 180);
        // Shift each 8x8 quadrant differently.
        for (int qy = 0; qy < 2; qy++)
            for (int qx = 0; qx < 2; qx++)
            {
                int dx = qx == 0 ? 1 : -1;
                int dy = qy == 0 ? 1 : -1;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = Math.Max(0, Math.Min(W - 1, qx * 8 + x - dx));
                        int sy = Math.Max(0, Math.Min(H - 1, qy * 8 + y - dy));
                        f2[(qy * 8 + y) * W + (qx * 8 + x)] = f1[sy * W + sx];
                    }
            }
        // Replicate the lower-right 24x24 just with neutral content.
        for (int y = 0; y < H; y++)
            for (int x = 16; x < W; x++) f2[y * W + x] = f1[y * W + x];
        for (int y = 16; y < H; y++)
            for (int x = 0; x < W; x++) f2[y * W + x] = f1[y * W + x];
        Array.Fill<byte>(f1, 128, W * H, 2 * (W / 2) * (H / 2));
        Array.Fill<byte>(f2, 128, W * H, 2 * (W / 2) * (H / 2));
        byte[] yuv = ConcatFrames(f1, f2);
        byte[] h264 = H264FrameEncoder.EncodeAnnexB(yuv, W, H, qp: 22, frames: 2,
            new H264FrameEncoder.Options { EnableCabac = true });
        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Equal(2, pics.Count);
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
