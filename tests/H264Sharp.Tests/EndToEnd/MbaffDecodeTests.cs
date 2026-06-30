using H264Sharp.Decoder;
using H264Sharp.Tests.Fixtures;

namespace H264Sharp.Tests.EndToEnd;

/// <summary>Stage 3a of interlaced support: MBAFF I-slice CAVLC where every MB pair is
/// frame-coded (mb_field_decoding_flag=0 throughout). x264 always emits MBAFF for interlaced
/// content, and for static intra-only material it consistently picks frame coding within MB
/// pairs — so these clips exercise the MBAFF MB-pair iteration / address mapping without
/// requiring field-coded MB support (which is Stage 3b).</summary>
[Trait("Category", "Ffmpeg")]
public sealed class MbaffDecodeTests
{
    [Theory]
    [InlineData(16, 32, "testsrc")]     // smallest possible MBAFF: 1 MB pair, no in-MB neighbors
    [InlineData(32, 32, "testsrc")]     // 2 MB pairs across one pair-row → tests horizontal pair iteration
    [InlineData(64, 64, "testsrc")]     // 8 MB pairs across 2 pair-rows → tests pair-row transitions
    [InlineData(64, 64, "smpte")]       // strong horizontal edges → harder intra prediction
    [InlineData(128, 96, "testsrc")]    // larger frame, mixed I_16x16 + I_4x4 inside MBAFF pairs
    public void MbaffIFrame_FrameCodedPairs_DecodesByteExact(int width, int height, string content)
    {
        var sample = GenerateMbaffFixture(width, height, content);
        byte[] h264 = File.ReadAllBytes(sample.H264Path);
        byte[] refYuv = File.ReadAllBytes(sample.YuvPath);

        var pics = new H264FrameDecoder().DecodeAllFrames(h264);
        Assert.Single(pics);

        int frameSize = width * height + 2 * (width / 2) * (height / 2);
        int sw = pics[0].BufferWidth;
        int csw = pics[0].ChromaBufferWidth;
        int worst = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                worst = Math.Max(worst, Math.Abs(pics[0].Y[y * sw + x] - refYuv[y * width + x]));
        for (int y = 0; y < height / 2; y++)
            for (int x = 0; x < width / 2; x++)
            {
                int du = Math.Abs(pics[0].U[y * csw + x] - refYuv[width * height + y * (width / 2) + x]);
                int dv = Math.Abs(pics[0].V[y * csw + x] - refYuv[width * height + (width / 2) * (height / 2) + y * (width / 2) + x]);
                worst = Math.Max(worst, Math.Max(du, dv));
            }
        Assert.Equal(0, worst);
    }

    [Fact]
    public void Mbaff_PSlice_RejectedWithClearError()
    {
        // MBAFF P-slice is Stage 3b; the dispatch must reject it with a parameterized error.
        // gop=2 ensures the second frame is a P-slice rather than another IDR.
        var sample = GenerateMbaffFixture(32, 32, "testsrc", frames: 2, gopSize: 2);
        byte[] h264 = File.ReadAllBytes(sample.H264Path);
        var dec = new H264FrameDecoder();
        var ex = Assert.Throws<NotSupportedException>(() => dec.DecodeAllFrames(h264));
        Assert.Contains("MBAFF", ex.Message);
        Assert.Contains("P-slice", ex.Message);
    }

    [Fact]
    public void Mbaff_Cabac_RejectedWithClearError()
    {
        // MBAFF CABAC is Stage 3b; the dispatch must reject it with a parameterized error.
        var sample = GenerateMbaffFixture(32, 32, "testsrc", cabac: true);
        byte[] h264 = File.ReadAllBytes(sample.H264Path);
        var dec = new H264FrameDecoder();
        var ex = Assert.Throws<NotSupportedException>(() => dec.DecodeAllFrames(h264));
        Assert.Contains("MBAFF", ex.Message);
        Assert.Contains("CABAC", ex.Message);
    }

    private readonly record struct MbaffSample(string H264Path, string YuvPath, int Width, int Height);

    private static MbaffSample GenerateMbaffFixture(int width, int height, string content,
        int frames = 1, bool cabac = false, int gopSize = 1)
    {
        string dir = Path.Combine(FfmpegFixture.SamplesDirectory, "Mbaff");
        Directory.CreateDirectory(dir);
        string suffix = $"{content}_{width}x{height}_f{frames}_g{gopSize}" + (cabac ? "_cabac" : "_cavlc");
        string h264 = Path.Combine(dir, $"mbaff_{suffix}.h264");
        string yuv = Path.Combine(dir, $"mbaff_{suffix}.yuv");
        if (File.Exists(h264) && File.Exists(yuv)) return new MbaffSample(h264, yuv, width, height);

        string input = content switch
        {
            "testsrc" => $"testsrc=s={width}x{height}:r=30:d={frames / 30.0 + 0.05}",
            "smpte" => $"smptebars=s={width}x{height}:r=30:d={frames / 30.0 + 0.05}",
            _ => throw new ArgumentException($"unknown content '{content}'"),
        };
        string coder = cabac ? "1" : "0";
        string x264Params = $"interlaced=tff:tff=1:cabac={coder}";
        string args = $"-y -f lavfi -i \"{input}\" -pix_fmt yuv420p -frames:v {frames} " +
            $"-c:v libx264 -profile:v main -x264-params \"{x264Params}\" " +
            $"-bf 0 -g {gopSize} -keyint_min {gopSize} -coder {coder} -an -f h264 \"{h264}\"";
        RunFfmpeg(args);
        string yuvArgs = $"-y -i \"{h264}\" -frames:v {frames} -f rawvideo -pix_fmt yuv420p \"{yuv}\"";
        RunFfmpeg(yuvArgs);
        return new MbaffSample(h264, yuv, width, height);
    }

    private static void RunFfmpeg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(FfmpegFixture.FfmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        string err = p.StandardError.ReadToEnd();
        _ = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exit {p.ExitCode}\nargs: {args}\nstderr:\n{err}");
    }
}
