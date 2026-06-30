using System.Diagnostics;
using System.Text;
using H264Sharp.Decoder;
using H264Sharp.Decoder.Picture;
using H264Sharp.Tests.Fixtures;

namespace H264Sharp.Tests.EndToEnd;

/// <summary>
/// Diverse corpus of x264-encoded clips reflecting typical real-world encoder settings.
/// Each clip is generated lazily via ffmpeg, decoded, and compared byte-wise against the
/// ffmpeg-decoded YUV reference. Results are categorized as PASS / CLOSE / DESYNC / THROW
/// and written to a structured report file. The test asserts only a coarse passage-rate
/// threshold so failure damage is bounded — the report is the primary deliverable.
/// </summary>
[Trait("Category", "Ffmpeg")]
public sealed class CorpusDecodeTests
{
    /// <summary>One axis-tuple in the corpus.</summary>
    public sealed record Clip(
        string Name,
        int Width,
        int Height,
        int FrameCount,
        string Profile,
        string Entropy,
        int BFrames,
        int Refs,
        string Partitions,
        int Dct8x8,
        int Subme,
        int Qp,
        string Content,
        string InputFilter,
        string ExtraX264Params);

    private const string CorpusSubdir = "Corpus";

    public static List<Clip> BuildCorpus()
    {
        var clips = new List<Clip>();

        // Small reusable input filters keyed by content tag.
        string TestSrc(int w, int h, double d, int r) =>
            $"testsrc=size={w}x{h}:d={d}:r={r},format=yuv420p";
        string Solid(int w, int h, double d, int r, string c) =>
            $"color=c={c}:s={w}x{h}:d={d}:r={r},format=yuv420p";
        string Gradient(int w, int h) =>
            $"color=s={w}x{h}:d=1:r=1,geq='X*255/{w}':128:128,format=yuv420p";
        string Smpte(int w, int h, double d, int r) =>
            $"smptebars=size={w}x{h}:d={d}:r={r},format=yuv420p";
        string Mandel(int w, int h) =>
            $"mandelbrot=s={w}x{h},format=yuv420p";
        string Noise(int w, int h) =>
            $"color=s={w}x{h}:d=1:r=1,geq='random(1)*255':128:128,format=yuv420p";
        string Shifted(int w, int h)
        {
            // Two-frame shifted testsrc (forces P inter MVs).
            return $"testsrc=size={w}x{h}:d=0.5:r=2[a];testsrc=size={w}x{h}:d=0.5:r=2,crop={w - 8}:{h}:8:0,pad={w}:{h}:0:0[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p";
        }

        // ---- Baseline / CAVLC family ----
        // 1. Mobile camera profile: Baseline + CAVLC + bf=0 + ref=1 - solid red.
        clips.Add(new Clip("base_cavlc_solid_red_16x16", 16, 16, 1,
            "baseline", "cavlc", 0, 1, "none", 0, 7, 18, "solid",
            Solid(16, 16, 1, 1, "red"), ""));
        // 2. Baseline CAVLC gradient.
        clips.Add(new Clip("base_cavlc_gradient_32x32", 32, 32, 1,
            "baseline", "cavlc", 0, 1, "none", 0, 7, 18, "gradient",
            Gradient(32, 32), ""));
        // 3. Baseline CAVLC testsrc - exercises Intra_4x4.
        clips.Add(new Clip("base_cavlc_testsrc_32x32", 32, 32, 1,
            "baseline", "cavlc", 0, 1, "i4x4", 0, 7, 18, "testsrc",
            TestSrc(32, 32, 1, 1), ""));
        // 4. Baseline CAVLC smptebars (typical 'low-detail real-world').
        clips.Add(new Clip("base_cavlc_smpte_64x48", 64, 48, 1,
            "baseline", "cavlc", 0, 1, "none", 0, 7, 18, "smpte",
            Smpte(64, 48, 1, 1), ""));
        // 5. Mobile-camera-like: shifted testsrc, Baseline CAVLC, bf=0, ref=1 - P inter MC.
        clips.Add(new Clip("base_cavlc_shifted_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "none", 0, 7, 18, "shifted",
            Shifted(64, 48), "no-deblock=1"));
        // 6. Baseline CAVLC i4x4+p8x8 - common mobile encoder partition set.
        clips.Add(new Clip("base_cavlc_p8x8_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "i4x4,p8x8", 0, 7, 18, "shifted",
            Shifted(64, 48), "no-deblock=1"));
        // 7. Baseline CAVLC ref=3 multi-ref.
        clips.Add(new Clip("base_cavlc_multiref_64x48", 64, 48, 3,
            "baseline", "cavlc", 0, 3, "none", 0, 7, 18, "testsrc",
            TestSrc(64, 48, 1.5, 2), "no-deblock=1"));
        // 8. Baseline CAVLC high quality qp=8 all partitions.
        clips.Add(new Clip("base_cavlc_allparts_qp8_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "all", 0, 8, 8, "shifted",
            Shifted(64, 48), "no-deblock=1"));
        // 9. Baseline CAVLC low quality qp=30.
        clips.Add(new Clip("base_cavlc_qp30_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "none", 0, 7, 30, "shifted",
            Shifted(64, 48), "no-deblock=1"));
        // 10. Baseline CAVLC fastpath subme=1.
        clips.Add(new Clip("base_cavlc_subme1_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "none", 0, 1, 18, "shifted",
            Shifted(64, 48), "no-deblock=1"));

        // ---- Main / CABAC family ----
        // 11. Main CABAC solid red (IDR + P_Skip).
        clips.Add(new Clip("main_cabac_solid_red_16x16", 16, 16, 2,
            "main", "cabac", 0, 1, "none", 0, 7, 18, "solid",
            Solid(16, 16, 1, 2, "red"), "no-deblock=1"));
        // 12. Main CABAC testsrc (Intra_4x4 CABAC I-slice).
        clips.Add(new Clip("main_cabac_testsrc_32x32", 32, 32, 1,
            "main", "cabac", 0, 1, "i4x4", 0, 7, 18, "testsrc",
            TestSrc(32, 32, 1, 1), "no-deblock=1"));
        // 13. Main CABAC shifted 32x16 (the known-good minimum reproducer).
        clips.Add(new Clip("main_cabac_shifted_32x16", 32, 16, 2,
            "main", "cabac", 0, 1, "none", 0, 7, 18, "shifted",
            Shifted(32, 16), "no-deblock=1"));
        // 14. Main CABAC shifted 64x48 (multi-row CABAC P).
        clips.Add(new Clip("main_cabac_shifted_64x48", 64, 48, 2,
            "main", "cabac", 0, 1, "none", 0, 7, 18, "shifted",
            Shifted(64, 48), "no-deblock=1:partitions=none:8x8dct=0"));
        // 15. Main CABAC shifted 128x96 (larger, like real screen recording).
        clips.Add(new Clip("main_cabac_shifted_128x96", 128, 96, 2,
            "main", "cabac", 0, 1, "none", 0, 7, 18, "shifted",
            Shifted(128, 96), "no-deblock=1:partitions=none:8x8dct=0"));
        // 16. Main CABAC qp=8 high quality shifted.
        clips.Add(new Clip("main_cabac_qp8_64x48", 64, 48, 2,
            "main", "cabac", 0, 1, "none", 0, 8, 8, "shifted",
            Shifted(64, 48), "no-deblock=1:partitions=none:8x8dct=0"));
        // 17. Main CABAC qp=30 low quality.
        clips.Add(new Clip("main_cabac_qp30_64x48", 64, 48, 2,
            "main", "cabac", 0, 1, "none", 0, 7, 30, "shifted",
            Shifted(64, 48), "no-deblock=1:partitions=none:8x8dct=0"));
        // 18. Main CABAC ref=3 multi-ref.
        clips.Add(new Clip("main_cabac_multiref_64x48", 64, 48, 3,
            "main", "cabac", 0, 3, "none", 0, 7, 18, "testsrc",
            TestSrc(64, 48, 1.5, 2), "no-deblock=1:partitions=none:8x8dct=0"));
        // 19. Main CABAC p8x8 partitions - typical encoder.
        clips.Add(new Clip("main_cabac_p8x8_64x48", 64, 48, 2,
            "main", "cabac", 0, 1, "p8x8", 0, 7, 18, "shifted",
            Shifted(64, 48), "no-deblock=1:partitions=p8x8:8x8dct=0"));
        // 20. Main CABAC all partitions (known-fail family - record as representative).
        clips.Add(new Clip("main_cabac_allparts_128x96", 128, 96, 2,
            "main", "cabac", 0, 1, "all", 0, 8, 8, "shifted",
            Shifted(128, 96), "no-deblock=1:partitions=all:subme=8:8x8dct=0"));

        // ---- High profile / 8x8 transform ----
        // 21. High CAVLC + 8x8dct + i8x8 - mandelbrot.
        clips.Add(new Clip("high_cavlc_8x8dct_mandel_64x48", 64, 48, 1,
            "high", "cavlc", 0, 1, "i8x8", 1, 7, 10, "mandel",
            Mandel(64, 48), "no-deblock=1:8x8dct=1:partitions=i8x8"));
        // 22. High CABAC + 8x8dct + i8x8 (known skip family).
        clips.Add(new Clip("high_cabac_8x8dct_mandel_64x48", 64, 48, 1,
            "high", "cabac", 0, 1, "i8x8", 1, 7, 10, "mandel",
            Mandel(64, 48), "no-deblock=1:8x8dct=1:partitions=i8x8"));
        // 23. High CABAC + 8x8dct on testsrc.
        clips.Add(new Clip("high_cabac_8x8dct_testsrc_32x32", 32, 32, 1,
            "high", "cabac", 0, 1, "i8x8", 1, 7, 18, "testsrc",
            TestSrc(32, 32, 1, 1), "no-deblock=1:8x8dct=1:partitions=i8x8"));
        // 24. High CAVLC + 8x8dct on smptebars.
        clips.Add(new Clip("high_cavlc_8x8dct_smpte_64x48", 64, 48, 1,
            "high", "cavlc", 0, 1, "i8x8", 1, 7, 18, "smpte",
            Smpte(64, 48, 1, 1), "no-deblock=1:8x8dct=1:partitions=i8x8"));

        // ---- B-frames family ----
        // 25. Main CAVLC + bf=1 - typical streaming.
        clips.Add(new Clip("main_cavlc_bf1_64x48", 64, 48, 3,
            "main", "cavlc", 1, 1, "none", 0, 7, 18, "testsrc",
            TestSrc(64, 48, 1.5, 2), "no-deblock=1"));
        // 26. Main CAVLC + bf=2 (IBBP) - typical default.
        clips.Add(new Clip("main_cavlc_bf2_64x48", 64, 48, 4,
            "main", "cavlc", 2, 1, "none", 0, 7, 18, "testsrc",
            TestSrc(64, 48, 1, 4), "no-deblock=1"));
        // 27. Main CABAC + bf=1 32x16 small.
        clips.Add(new Clip("main_cabac_bf1_32x16", 32, 16, 3,
            "main", "cabac", 1, 1, "none", 0, 7, 18, "solid",
            Solid(32, 16, 1.5, 2, "red"), "no-deblock=1:8x8dct=0"));
        // 28. Main CABAC + bf=2 16x16 red.
        clips.Add(new Clip("main_cabac_bf2_red_16x16", 16, 16, 3,
            "main", "cabac", 2, 1, "none", 0, 7, 18, "solid",
            Solid(16, 16, 1.5, 2, "red"), ""));

        // ---- OBS / screen recording typical: Main CABAC bf=2 ref=3 ----
        // 29. OBS-like Main CABAC bf=2 ref=3 testsrc (small).
        clips.Add(new Clip("obs_main_cabac_bf2_ref3_32x32", 32, 32, 4,
            "main", "cabac", 2, 3, "none", 0, 7, 23, "testsrc",
            TestSrc(32, 32, 1, 4), "no-deblock=1:8x8dct=0"));

        // ---- ffmpeg-default-ish: High, CABAC, bf=3 (default), all partitions, subme=7 ----
        // 30. ffmpeg default-like on testsrc.
        clips.Add(new Clip("ffmpeg_default_testsrc_32x32", 32, 32, 4,
            "high", "cabac", 2, 3, "all", 1, 7, 23, "testsrc",
            TestSrc(32, 32, 1, 4), ""));

        // ---- Subme variations ----
        // 31. Baseline CAVLC subme=1 (fastpath) - shifted.
        clips.Add(new Clip("base_cavlc_subme1_shifted_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "none", 0, 1, 18, "shifted",
            Shifted(64, 48), "no-deblock=1:subme=1"));
        // 32. Baseline CAVLC subme=8 (best) - shifted all parts.
        clips.Add(new Clip("base_cavlc_subme8_allparts_64x48", 64, 48, 2,
            "baseline", "cavlc", 0, 1, "all", 0, 8, 8, "shifted",
            Shifted(64, 48), "no-deblock=1:subme=8:partitions=all"));

        // ---- Noise content (worst case for prediction) ----
        // 33. Baseline CAVLC noise.
        clips.Add(new Clip("base_cavlc_noise_32x32", 32, 32, 1,
            "baseline", "cavlc", 0, 1, "i4x4", 0, 7, 18, "noise",
            Noise(32, 32), ""));
        // 34. Main CABAC noise.
        clips.Add(new Clip("main_cabac_noise_32x32", 32, 32, 1,
            "main", "cabac", 0, 1, "i4x4", 0, 7, 18, "noise",
            Noise(32, 32), "no-deblock=1"));

        // ---- High profile no-8x8dct (effectively-Main flavor) ----
        // 35. High CABAC no 8x8dct on shifted - check High profile parses cleanly.
        clips.Add(new Clip("high_cabac_no8x8_shifted_64x48", 64, 48, 2,
            "high", "cabac", 0, 1, "none", 0, 7, 18, "shifted",
            Shifted(64, 48), "no-deblock=1:partitions=none:8x8dct=0"));

        // ---- Single-MB special edges ----
        // 36. Baseline CAVLC single 16x16 testsrc.
        clips.Add(new Clip("base_cavlc_testsrc_16x16", 16, 16, 1,
            "baseline", "cavlc", 0, 1, "i4x4", 0, 7, 18, "testsrc",
            TestSrc(16, 16, 1, 1), ""));

        // ---- iPhone-recording-like clips: deblocking ON, mixed transform sizes ----
        // 37. High + CABAC + 8x8dct + deblocking on + mixed i4x4/i8x8 + testsrc (mixed transform sizes within MB rows).
        clips.Add(new Clip("iphone_high_cabac_mixed_deblock_64x48", 64, 48, 1,
            "high", "cabac", 0, 1, "i4x4,i8x8", 1, 7, 18, "testsrc",
            TestSrc(64, 48, 1, 1), "8x8dct=1:partitions=i4x4,i8x8"));
        // 38. Same as above with stronger content (mandelbrot).
        clips.Add(new Clip("iphone_high_cabac_mixed_deblock_mandel_64x48", 64, 48, 1,
            "high", "cabac", 0, 1, "i4x4,i8x8", 1, 7, 18, "mandel",
            Mandel(64, 48), "8x8dct=1:partitions=i4x4,i8x8"));
        // 39. Apple-VideoToolbox-like: High + CABAC + 8x8dct + bf=2 + ref=3 + deblock on + all partitions.
        clips.Add(new Clip("iphone_high_cabac_full_64x48", 64, 48, 4,
            "high", "cabac", 2, 3, "all", 1, 7, 23, "testsrc",
            TestSrc(64, 48, 1, 4), "8x8dct=1:partitions=all"));
        // 40. iPhone-like with smptebars (high-contrast edges, hits deblocking strength branches).
        clips.Add(new Clip("iphone_high_cabac_smpte_deblock_64x48", 64, 48, 1,
            "high", "cabac", 0, 1, "i4x4,i8x8", 1, 7, 18, "smpte",
            Smpte(64, 48, 1, 1), "8x8dct=1:partitions=i4x4,i8x8"));
        // 41. Larger mixed-transform-size iPhone-like clip for cumulative drift testing.
        clips.Add(new Clip("iphone_high_cabac_mixed_deblock_128x96", 128, 96, 2,
            "high", "cabac", 1, 2, "all", 1, 7, 23, "shifted",
            Shifted(128, 96), "8x8dct=1:partitions=all"));
        // 42. VideoToolbox-like with non-zero deblock offsets (Apple often uses alpha/beta != 0).
        clips.Add(new Clip("apple_vt_deblock_offset_64x48", 64, 48, 2,
            "high", "cabac", 1, 2, "all", 1, 7, 23, "shifted",
            Shifted(64, 48), "8x8dct=1:partitions=all:deblock=-1,-1"));
        // 43. Apple-like with stronger deblock offsets.
        clips.Add(new Clip("apple_vt_deblock_strong_64x48", 64, 48, 2,
            "high", "cabac", 1, 2, "all", 1, 7, 23, "testsrc",
            TestSrc(64, 48, 1, 2), "8x8dct=1:partitions=all:deblock=-2,-2"));
        // 44. VideoToolbox-like: chroma_qp_index_offset != 0 (Apple uses -2 sometimes).
        clips.Add(new Clip("apple_vt_chroma_qp_offset_64x48", 64, 48, 2,
            "high", "cabac", 1, 2, "all", 1, 7, 23, "shifted",
            Shifted(64, 48), "8x8dct=1:partitions=all:chroma-qp-offset=-2"));
        // 45. Higher QP variance: cqm/aq enabled (forces per-MB qp_delta).
        clips.Add(new Clip("apple_vt_aq_qp_var_128x96", 128, 96, 2,
            "high", "cabac", 1, 2, "all", 1, 7, 28, "shifted",
            Shifted(128, 96), "8x8dct=1:partitions=all:aq-mode=1:aq-strength=1.5"));
        // 46. Mixed 8x8 + 4x4 transform sizes on adjacent MBs: deblocking at MB boundary
        //   between transform_size_8x8_flag=1 and =0 MBs is a specific edge case.
        clips.Add(new Clip("mixed_xform_boundary_128x96", 128, 96, 1,
            "high", "cabac", 0, 1, "i4x4,i8x8", 1, 7, 28, "smpte",
            Smpte(128, 96, 1, 1), "8x8dct=1:partitions=i4x4,i8x8"));

        return clips;
    }

    private static string CorpusDirectory => Path.Combine(FfmpegFixture.SamplesDirectory, CorpusSubdir);

    /// <summary>Generates ffmpeg .h264 + reference .yuv for a clip if not cached.</summary>
    private static (string h264, string yuv) EnsureGenerated(Clip clip)
    {
        Directory.CreateDirectory(CorpusDirectory);
        string h264 = Path.Combine(CorpusDirectory, clip.Name + ".h264");
        string yuv = Path.Combine(CorpusDirectory, clip.Name + ".yuv");
        if (!File.Exists(h264) || !File.Exists(yuv))
        {
            string coder = clip.Entropy == "cabac" ? "1" : "0";
            string profile = clip.Profile;
            // Extra x264 params - merge partitions, 8x8dct, subme if not already in ExtraX264Params.
            var extras = new StringBuilder(clip.ExtraX264Params ?? "");
            if (extras.Length > 0 && !extras.ToString().EndsWith(":")) extras.Append(":");
            if (!extras.ToString().Contains("partitions=") && !string.IsNullOrEmpty(clip.Partitions) && clip.Partitions != "default")
                extras.Append($"partitions={clip.Partitions}:");
            if (!extras.ToString().Contains("8x8dct=") && clip.Profile == "high")
                extras.Append($"8x8dct={clip.Dct8x8}:");
            if (!extras.ToString().Contains("subme="))
                extras.Append($"subme={clip.Subme}:");
            string ex = extras.ToString().TrimEnd(':');
            string x264Args = string.IsNullOrEmpty(ex) ? "" : $"-x264-params \"{ex}\" ";

            string genArgs =
                $"-y -f lavfi -i \"{clip.InputFilter}\" -frames:v {clip.FrameCount} -pix_fmt yuv420p " +
                $"-c:v libx264 -profile:v {profile} -bf {clip.BFrames} -refs {clip.Refs} " +
                $"-keyint_min 99 -g 99 -sc_threshold 0 -coder {coder} -an -qp {clip.Qp} " +
                $"{x264Args}-f h264 \"{h264}\"";
            string yuvArgs =
                $"-y -i \"{h264}\" -frames:v {clip.FrameCount} -vsync passthrough " +
                $"-f rawvideo -pix_fmt yuv420p \"{yuv}\"";
            RunFfmpeg(genArgs);
            RunFfmpeg(yuvArgs);
        }
        return (h264, yuv);
    }

    private static void RunFfmpeg(string args)
    {
        var psi = new ProcessStartInfo(FfmpegFixture.FfmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        string se = p.StandardError.ReadToEnd();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exit {p.ExitCode}\nargs: {args}\nstderr:\n{se}");
    }

    public enum Result { PASS, CLOSE, DESYNC, THROW, FFMPEG_GEN_FAIL }

    public sealed record ClipResult(Clip Clip, Result Result, int MaxY, int MaxU, int MaxV, string? Error);

    private static ClipResult RunOne(Clip clip)
    {
        string h264, yuv;
        try
        {
            (h264, yuv) = EnsureGenerated(clip);
        }
        catch (Exception ex)
        {
            return new ClipResult(clip, Result.FFMPEG_GEN_FAIL, -1, -1, -1, ex.Message);
        }

        byte[] stream;
        byte[] reference;
        try
        {
            stream = File.ReadAllBytes(h264);
            reference = File.ReadAllBytes(yuv);
        }
        catch (Exception ex)
        {
            return new ClipResult(clip, Result.FFMPEG_GEN_FAIL, -1, -1, -1, ex.Message);
        }

        List<DecodedPicture> frames;
        try
        {
            frames = new H264FrameDecoder().DecodeAllFrames(stream);
        }
        catch (Exception ex)
        {
            return new ClipResult(clip, Result.THROW, -1, -1, -1, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (frames.Count != clip.FrameCount)
        {
            return new ClipResult(clip, Result.THROW, -1, -1, -1,
                $"FrameCountMismatch: expected {clip.FrameCount} got {frames.Count}");
        }

        int yLen = clip.Width * clip.Height;
        int cLen = yLen / 4;
        int frameStride = yLen + 2 * cLen;
        int maxY = 0, maxU = 0, maxV = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            var p = frames[f];
            int off = f * frameStride;
            if (p.Y.Length < yLen || reference.Length < off + frameStride)
            {
                return new ClipResult(clip, Result.THROW, -1, -1, -1, "Short plane / reference");
            }
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(p.Y[i] - reference[off + i]));
            for (int i = 0; i < cLen; i++) maxU = Math.Max(maxU, Math.Abs(p.U[i] - reference[off + yLen + i]));
            for (int i = 0; i < cLen; i++) maxV = Math.Max(maxV, Math.Abs(p.V[i] - reference[off + yLen + cLen + i]));
        }

        int worst = Math.Max(maxY, Math.Max(maxU, maxV));
        Result r = worst <= 2 ? Result.PASS : worst <= 20 ? Result.CLOSE : Result.DESYNC;
        return new ClipResult(clip, r, maxY, maxU, maxV, null);
    }

    [Fact]
    public void DecodeCorpus_MeasuresAndReports()
    {
        var corpus = BuildCorpus();
        var results = new List<ClipResult>(corpus.Count);
        foreach (var clip in corpus)
        {
            results.Add(RunOne(clip));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# H.264 Decoder Corpus Report");
        sb.AppendLine($"Generated: {DateTime.UtcNow:o}");
        sb.AppendLine($"Corpus size: {corpus.Count}");
        sb.AppendLine();
        int pass = results.Count(r => r.Result == Result.PASS);
        int close = results.Count(r => r.Result == Result.CLOSE);
        int desync = results.Count(r => r.Result == Result.DESYNC);
        int thrw = results.Count(r => r.Result == Result.THROW);
        int genFail = results.Count(r => r.Result == Result.FFMPEG_GEN_FAIL);
        sb.AppendLine($"PASS:       {pass}");
        sb.AppendLine($"CLOSE:      {close}");
        sb.AppendLine($"DESYNC:     {desync}");
        sb.AppendLine($"THROW:      {thrw}");
        sb.AppendLine($"GEN_FAIL:   {genFail}");
        sb.AppendLine();
        sb.AppendLine("| Name | Profile | Entropy | bf | refs | parts | 8x8 | qp | Result | maxY | maxU | maxV | Error |");
        sb.AppendLine("|------|---------|---------|----|------|-------|-----|----|--------|------|------|------|-------|");
        foreach (var r in results)
        {
            var c = r.Clip;
            string err = r.Error?.Replace("|", "/").Replace("\n", " ").Replace("\r", " ") ?? "";
            if (err.Length > 200) err = err.Substring(0, 200) + "...";
            sb.AppendLine($"| {c.Name} | {c.Profile} | {c.Entropy} | {c.BFrames} | {c.Refs} | {c.Partitions} | {c.Dct8x8} | {c.Qp} | {r.Result} | {r.MaxY} | {r.MaxU} | {r.MaxV} | {err} |");
        }

        string reportPath = Path.Combine(CorpusDirectory, "_corpus_report.md");
        Directory.CreateDirectory(CorpusDirectory);
        File.WriteAllText(reportPath, sb.ToString());

        // Console output for easy inspection from the test runner.
        Console.WriteLine(sb.ToString());

        // Sanity-only assertion: don't crash entire suite. At least 1 PASS proves the harness runs.
        Assert.True(pass + close + desync + thrw + genFail == corpus.Count, "Result count mismatch");
        Assert.True(pass >= 1, $"Expected at least 1 PASS, got pass={pass} close={close} desync={desync} throw={thrw} genFail={genFail}");
    }

    /// <summary>
    /// Regression guard for the CABAC P-slice mb_type bin0 ctxIdxInc bug (spec Table 9-39).
    /// The smallest reproducer: 32x16 (2 MB) shifted CABAC P-slice — used to THROW
    /// "Intra_16x16 Vertical: top not available" because mb_type bin0 incorrectly used
    /// (condA+condB)-derived ctxIdxInc instead of the spec-mandated fixed 0.
    /// </summary>
    [Fact]
    public void DecodeShiftedCabacPSlice32x16_DecodesWithoutThrowing()
    {
        var clip = BuildCorpus().Single(c => c.Name == "main_cabac_shifted_32x16");
        var result = RunOne(clip);
        Assert.True(result.Result == Result.PASS || result.Result == Result.CLOSE,
            $"Expected PASS/CLOSE, got {result.Result}: {result.Error}");
    }
}
