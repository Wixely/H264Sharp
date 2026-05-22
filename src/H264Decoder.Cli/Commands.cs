using H264Decoder;
using H264Decoder.Bitstream;
using H264Decoder.Picture;
using H264Decoder.Syntax;

namespace H264Decoder.Cli;

/// <summary>
/// CLI command implementations. Kept separate from Program.Main so tests can exercise
/// the logic directly (in-process) without spawning a subprocess.
/// </summary>
public static class Commands
{
    /// <summary>Parse argv and dispatch. Returns a process exit code.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        // --info <in.mp4>
        if (args.Length == 2 && args[0] == "--info")
        {
            return Info(args[1], stdout, stderr);
        }
        // <in> <out> --at <seconds>
        if (args.Length == 4 && args[2] == "--at")
        {
            return ThumbnailAt(args[0], args[1], args[3], stderr);
        }
        // <in> <out> --at-pct <0..1>
        if (args.Length == 4 && args[2] == "--at-pct")
        {
            return ThumbnailAtPercent(args[0], args[1], args[3], stderr);
        }
        if (args.Length == 2)
        {
            return DecodeFirstIFrameToFile(args[0], args[1], stderr);
        }
        stderr.WriteLine("Usage:");
        stderr.WriteLine("  H264Decoder.Cli <in.h264|in.mp4> <out.yuv|out.png>");
        stderr.WriteLine("  H264Decoder.Cli <in.mp4> <out.png> --at <seconds>");
        stderr.WriteLine("  H264Decoder.Cli <in.mp4> <out.png> --at-pct <0..1>");
        stderr.WriteLine("  H264Decoder.Cli --info <in.mp4>");
        return 1;
    }

    /// <summary>Legacy default — decode the first I-frame.</summary>
    public static int DecodeFirstIFrameToFile(string inPath, string outPath, TextWriter stderr)
    {
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        byte[] stream = File.ReadAllBytes(inPath);
        var decoder = new H264FrameDecoder();
        DecodedPicture pic;
        try { pic = decoder.DecodeFirstIFrame(stream); }
        catch (Exception ex) { stderr.WriteLine($"decode failed: {ex.Message}"); return 3; }
        WritePicture(pic, outPath, stderr);
        return 0;
    }

    /// <summary>Thumbnail at a specific composition timestamp. Requires MP4 (needs timing info).</summary>
    public static int ThumbnailAt(string inPath, string outPath, string timestamp, TextWriter stderr)
    {
        if (!TryParseTimestamp(timestamp, out double t))
        {
            stderr.WriteLine($"invalid --at value: '{timestamp}' (expected seconds or mm:ss[.ms])");
            return 4;
        }
        return ThumbnailAtSeconds(inPath, outPath, t, stderr);
    }

    /// <summary>Thumbnail at a percentage of the video duration. <paramref name="percent"/>
    /// is in [0, 1]; e.g. "0.44" maps to 44% of the way through. Requires MP4.</summary>
    public static int ThumbnailAtPercent(string inPath, string outPath, string percent, TextWriter stderr)
    {
        if (!double.TryParse(percent, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pct)
            || pct < 0 || pct > 1)
        {
            stderr.WriteLine($"invalid --at-pct value: '{percent}' (expected a number in [0, 1])");
            return 4;
        }
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        if (!FileLooksLikeMp4(inPath))
        {
            stderr.WriteLine("--at-pct requires an MP4 container (Annex-B has no timestamps)");
            return 5;
        }
        using var fs = File.OpenRead(inPath);
        Mp4SampleStream stream;
        try { stream = Mp4Reader.ExtractH264WithTiming(fs); }
        catch (Exception ex) { stderr.WriteLine($"MP4 parse failed: {ex.Message}"); return 3; }
        if (stream.Samples.Count == 0) { stderr.WriteLine("MP4 has no video samples"); return 3; }

        // Prefer mvhd duration; fall back to the last sample's composition time.
        double duration = stream.DurationSeconds > 0
            ? stream.DurationSeconds
            : stream.Samples[^1].CompositionTimeSeconds;
        double t = duration * pct;
        return ThumbnailAtTimeForStream(stream, outPath, t, stderr);
    }

    private static int ThumbnailAtSeconds(string inPath, string outPath, double t, TextWriter stderr)
    {
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        if (!FileLooksLikeMp4(inPath))
        {
            stderr.WriteLine("--at requires an MP4 container (Annex-B has no timestamps)");
            return 5;
        }
        using var fs = File.OpenRead(inPath);
        Mp4SampleStream stream;
        try { stream = Mp4Reader.ExtractH264WithTiming(fs); }
        catch (Exception ex) { stderr.WriteLine($"MP4 parse failed: {ex.Message}"); return 3; }
        if (stream.Samples.Count == 0) { stderr.WriteLine("MP4 has no video samples"); return 3; }
        return ThumbnailAtTimeForStream(stream, outPath, t, stderr);
    }

    private static int ThumbnailAtTimeForStream(Mp4SampleStream stream, string outPath, double t, TextWriter stderr)
    {
        // Pick the sample whose composition time is closest to the target.
        int target = 0;
        double best = double.MaxValue;
        for (int i = 0; i < stream.Samples.Count; i++)
        {
            double d = Math.Abs(stream.Samples[i].CompositionTimeSeconds - t);
            if (d < best) { best = d; target = i; }
        }

        int idr = target;
        while (idr > 0 && !stream.Samples[idr].IsSyncSample) idr--;

        var nals = new List<NalUnit>(stream.AvcCConfigNalUnits);
        for (int i = idr; i <= target; i++) nals.AddRange(stream.ResolveNalUnits(i));

        var decoder = new H264FrameDecoder();
        List<DecodedPicture> frames;
        try { frames = decoder.DecodeAllFrames(nals); }
        catch (Exception ex) { stderr.WriteLine($"decode failed: {ex.Message}"); return 3; }

        int displayIdx = Math.Min(target - idr, frames.Count - 1);
        var pic = frames[displayIdx];
        WritePicture(pic, outPath, stderr);
        stderr.WriteLine($"at t={stream.Samples[target].CompositionTimeSeconds:F3}s (sample {target}, sync@{idr})");
        return 0;
    }

    /// <summary>Print metadata: duration, resolution, frame count, fps, profile.</summary>
    public static int Info(string inPath, TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        if (!FileLooksLikeMp4(inPath))
        {
            // Annex-B / AVCC raw: parse SPS for resolution + profile, count slices for frames.
            byte[] bytes = File.ReadAllBytes(inPath);
            return InfoAnnexB(bytes, stdout, stderr);
        }
        using var fs = File.OpenRead(inPath);
        Mp4SampleStream stream;
        try { stream = Mp4Reader.ExtractH264WithTiming(fs); }
        catch (Exception ex) { stderr.WriteLine($"MP4 parse failed: {ex.Message}"); return 3; }

        int frames = stream.Samples.Count;
        double dur = stream.DurationSeconds;
        double fps = dur > 0 ? frames / dur : 0;

        string profile = "unknown";
        if (stream.AvcCConfigNalUnits.Count > 0)
        {
            var sps = stream.AvcCConfigNalUnits.First(n => n.NalUnitType == NalUnitType.Sps);
            try
            {
                var s = SequenceParameterSet.Parse(sps.Rbsp.Span);
                profile = ProfileName(s.ProfileIdc);
            }
            catch { /* leave as unknown */ }
        }

        stdout.WriteLine($"duration: {dur:F3} s");
        stdout.WriteLine($"resolution: {stream.Width}x{stream.Height}");
        stdout.WriteLine($"frames: {frames} ({fps:F2} fps)");
        stdout.WriteLine($"profile: {profile}");
        return 0;
    }

    private static int InfoAnnexB(byte[] bytes, TextWriter stdout, TextWriter stderr)
    {
        List<NalUnit> nals;
        try
        {
            nals = LooksLikeAnnexBHeader(bytes)
                ? AnnexBReader.SplitNalUnits(bytes)
                : AvccReader.SplitNalUnits(bytes);
        }
        catch (Exception ex) { stderr.WriteLine($"parse failed: {ex.Message}"); return 3; }

        SequenceParameterSet? sps = null;
        int slices = 0;
        foreach (var n in nals)
        {
            if (n.NalUnitType == NalUnitType.Sps && sps is null)
                sps = SequenceParameterSet.Parse(n.Rbsp.Span);
            if (n.NalUnitType is NalUnitType.SliceIdr or NalUnitType.SliceNonIdr) slices++;
        }
        if (sps is null) { stderr.WriteLine("no SPS found"); return 3; }

        // Duration: try VUI num_units_in_tick / time_scale; otherwise unknown.
        double? duration = null;
        double? fps = null;
        if (sps.Vui is { } vui && vui.NumUnitsInTick > 0 && vui.TimeScale > 0)
        {
            double frameRate = vui.TimeScale / (2.0 * vui.NumUnitsInTick);
            fps = frameRate;
            if (frameRate > 0) duration = slices / frameRate;
        }

        stdout.WriteLine(duration.HasValue ? $"duration: {duration.Value:F3} s" : "duration: unknown (no timing info)");
        stdout.WriteLine($"resolution: {sps.CroppedWidth}x{sps.CroppedHeight}");
        stdout.WriteLine(fps.HasValue ? $"frames: {slices} ({fps.Value:F2} fps)" : $"frames: {slices}");
        stdout.WriteLine($"profile: {ProfileName(sps.ProfileIdc)}");
        return 0;
    }

    private static string ProfileName(byte idc) => idc switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High10",
        122 => "High4:2:2",
        244 => "High4:4:4",
        _ => $"profile_idc={idc}",
    };

    /// <summary>Accepts plain seconds ("5.0", "12.345") and mm:ss[.ms] ("1:23.5").</summary>
    public static bool TryParseTimestamp(string s, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int mm)) return false;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ss)) return false;
            seconds = mm * 60 + ss;
            return seconds >= 0;
        }
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
    }

    private static void WritePicture(DecodedPicture pic, string outPath, TextWriter stderr)
    {
        if (outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            byte[] rgb = YuvToRgb.Convert(pic);
            byte[] png = PngEncoder.EncodeRgb(pic.Width, pic.Height, rgb);
            File.WriteAllBytes(outPath, png);
            stderr.WriteLine($"decoded {pic.Width}x{pic.Height} -> {outPath} ({png.Length} bytes PNG)");
        }
        else
        {
            using var fs = File.Create(outPath);
            Yuv420Frame.Write(pic, fs);
            fs.Flush();
            stderr.WriteLine($"decoded {pic.Width}x{pic.Height} YUV 4:2:0 -> {outPath}");
        }
    }

    // ----- Local container sniffing copies (H264FrameDecoder's versions are private) -----

    private static bool FileLooksLikeMp4(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = File.OpenRead(path);
        int read = 0;
        while (read < head.Length)
        {
            int n = fs.Read(head[read..]);
            if (n == 0) break;
            read += n;
        }
        return read >= 8 && LooksLikeMp4(head);
    }

    private static bool LooksLikeMp4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        ReadOnlySpan<byte> ty = bytes.Slice(4, 4);
        return Match(ty, "ftyp") || Match(ty, "moov") || Match(ty, "mdat")
            || Match(ty, "free") || Match(ty, "skip") || Match(ty, "wide");
        static bool Match(ReadOnlySpan<byte> a, string b) =>
            a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
    }

    private static bool LooksLikeAnnexBHeader(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < Math.Min(4, bytes.Length); i++)
        {
            if (bytes[i] == 0) continue;
            return bytes[i] == 1;
        }
        return false;
    }
}
