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
        // encode <in.yuv> <out.h264|out.mp4> --size <W>x<H> [--frames N] [--qp 18]
        if (args.Length >= 5 && args[0] == "encode")
        {
            return EncodeYuv(args, stderr);
        }
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
        // <in> <out_dir> --frames <spec>
        if (args.Length == 4 && args[2] == "--frames")
        {
            return ExtractFrames(args[0], args[1], args[3], stderr);
        }
        if (args.Length == 2)
        {
            return DecodeFirstIFrameToFile(args[0], args[1], stderr);
        }
        stderr.WriteLine("Usage:");
        stderr.WriteLine("  H264Decoder.Cli encode <in.yuv> <out.h264> --size <W>x<H> [--frames N] [--qp 18]");
        stderr.WriteLine("  H264Decoder.Cli <in.h264|in.mp4> <out.yuv|out.png>");
        stderr.WriteLine("  H264Decoder.Cli <in.mp4> <out.png> --at <seconds>");
        stderr.WriteLine("  H264Decoder.Cli <in.mp4> <out.png> --at-pct <0..1>");
        stderr.WriteLine("  H264Decoder.Cli <in.mp4> <out_dir> --frames <spec>");
        stderr.WriteLine("    spec: 'all', '<N>', '<N>-<M>', or comma-separated mix (e.g. '5,10-15,20')");
        stderr.WriteLine("  H264Decoder.Cli --info <in.mp4>");
        return 1;
    }

    /// <summary>Encode raw YUV 4:2:0 (planar Y/U/V) into Annex-B H.264. Args layout:
    /// encode &lt;in.yuv&gt; &lt;out.h264&gt; --size WxH [--frames N] [--qp Q]</summary>
    public static int EncodeYuv(string[] args, TextWriter stderr)
    {
        // args[0]="encode", args[1]=in, args[2]=out, then --size WxH, optional --frames N, optional --qp Q.
        string inPath = args[1];
        string outPath = args[2];
        int width = 0, height = 0, frames = 1, qp = 18;
        for (int i = 3; i + 1 < args.Length; i += 2)
        {
            string k = args[i], v = args[i + 1];
            if (k == "--size")
            {
                int sep = v.IndexOf('x');
                if (sep <= 0
                    || !int.TryParse(v.AsSpan(0, sep), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out width)
                    || !int.TryParse(v.AsSpan(sep + 1), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out height))
                {
                    stderr.WriteLine($"invalid --size '{v}' (expected WxH)");
                    return 4;
                }
            }
            else if (k == "--frames")
            {
                if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out frames) || frames <= 0)
                {
                    stderr.WriteLine($"invalid --frames '{v}'");
                    return 4;
                }
            }
            else if (k == "--qp")
            {
                if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out qp) || qp < 0 || qp > 51)
                {
                    stderr.WriteLine($"invalid --qp '{v}'");
                    return 4;
                }
            }
            else
            {
                stderr.WriteLine($"unknown option '{k}'");
                return 4;
            }
        }
        if (width <= 0 || height <= 0)
        {
            stderr.WriteLine("--size WxH required");
            return 4;
        }
        if (!File.Exists(inPath))
        {
            stderr.WriteLine($"input not found: {inPath}");
            return 2;
        }
        byte[] yuv = File.ReadAllBytes(inPath);
        int frameBytes = width * height + 2 * (width / 2) * (height / 2);
        if (yuv.Length < frameBytes * frames)
        {
            stderr.WriteLine($"YUV file too small: expected {frameBytes * frames}, got {yuv.Length}");
            return 3;
        }
        byte[] annexB = H264Decoder.Encoder.H264FrameEncoder.EncodeAnnexB(yuv, width, height, qp, frames);
        File.WriteAllBytes(outPath, annexB);
        stderr.WriteLine($"encoded {width}x{height} x{frames} @ qp={qp} -> {outPath} ({annexB.Length} bytes)");
        return 0;
    }

    /// <summary>Legacy default — decode the first I-frame.</summary>
    public static int DecodeFirstIFrameToFile(string inPath, string outPath, TextWriter stderr)
    {
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        byte[] stream = File.ReadAllBytes(inPath);
        var decoder = new H264FrameDecoder();
        DecodedPicture pic;
        try { pic = decoder.DecodeFirstIFrame(stream); }
        catch (Exception ex) { PrintException("decode failed", ex, stderr); return 3; }
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
        catch (Exception ex) { PrintException("MP4 parse failed", ex, stderr); return 3; }
        if (stream.Samples.Count == 0) { stderr.WriteLine("MP4 has no video samples"); return 3; }

        // Prefer mvhd duration; fall back to the last sample's composition time.
        double duration = stream.DurationSeconds > 0
            ? stream.DurationSeconds
            : stream.Samples[^1].CompositionTimeSeconds;
        double t = duration * pct;
        return ThumbnailAtTimeForStream(stream, outPath, t, stderr);
    }

    /// <summary>Extract one or more frames in display order. <paramref name="spec"/> accepts:
    /// "all", a single index "89", a range "12-39", or a comma-separated mix like "5,10-15,20".
    /// Frame N is written to <paramref name="outDir"/>/frame_NNNNN.png (5-digit zero-padded).
    /// MP4 input only (Annex-B has no usable frame ordering).</summary>
    public static int ExtractFrames(string inPath, string outDir, string spec, TextWriter stderr)
    {
        if (!File.Exists(inPath)) { stderr.WriteLine($"input not found: {inPath}"); return 2; }
        if (!FileLooksLikeMp4(inPath))
        {
            stderr.WriteLine("--frames requires an MP4 container");
            return 5;
        }
        using var fs = File.OpenRead(inPath);
        Mp4SampleStream stream;
        try { stream = Mp4Reader.ExtractH264WithTiming(fs); }
        catch (Exception ex) { PrintException("MP4 parse failed", ex, stderr); return 3; }
        if (stream.Samples.Count == 0) { stderr.WriteLine("MP4 has no video samples"); return 3; }

        if (!TryParseFrameSpec(spec, stream.Samples.Count, out var indices, out string? err))
        {
            stderr.WriteLine($"invalid --frames value: {err}");
            return 4;
        }
        bool isAll = spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

        // Group requested indices by the GOP they belong to. Each GOP starts at a sync sample (IDR)
        // and is decode-independent — parallelizable across GOPs. Within a GOP, frames depend on each
        // other (P/B reference earlier frames) so decode is sequential there.
        var syncSamples = new List<int>();
        for (int i = 0; i < stream.Samples.Count; i++)
            if (stream.Samples[i].IsSyncSample) syncSamples.Add(i);
        if (syncSamples.Count == 0) syncSamples.Add(0); // tolerate streams with no stss

        // Closed-GOP assumption: display index N maps to GOP K where syncSamples[K] <= N < syncSamples[K+1].
        // True for typical content; open-GOP streams could mis-bucket boundary B-frames but won't crash.
        var byGop = new Dictionary<int, List<int>>();
        foreach (int idx in indices)
        {
            int gopIdr = syncSamples[0];
            for (int k = 0; k < syncSamples.Count; k++)
            {
                if (syncSamples[k] <= idx) gopIdr = syncSamples[k];
                else break;
            }
            if (!byGop.TryGetValue(gopIdr, out var list)) byGop[gopIdr] = list = new List<int>();
            list.Add(idx);
        }

        Directory.CreateDirectory(outDir);
        int totalSamples = isAll ? stream.Samples.Count : Math.Min(stream.Samples.Count, indices[^1] + 9);
        if (totalSamples > 200) stderr.WriteLine($"decoding {totalSamples} samples across {byGop.Count} GOP(s)...");

        // One task per GOP. Each task re-opens the MP4 (parallel-safe), decodes its slice, writes PNGs.
        // Within a GOP, PNG encoding is also done in parallel since YuvToRgb + PngEncoder are pure.
        int totalWritten = 0;
        int failedGops = 0;
        System.Threading.Tasks.Parallel.ForEach(byGop, gop =>
        {
            int idrSample = gop.Key;
            var requested = gop.Value;
            int highest = 0;
            foreach (int r in requested) if (r > highest) highest = r;
            // Cap decode at the next sync sample so we don't spill into the next GOP's IDR,
            // which would reset POC and mix two GOPs' frames in the POC-sorted output.
            int nextIdr = stream.Samples.Count;
            for (int i = idrSample + 1; i < stream.Samples.Count; i++)
            {
                if (stream.Samples[i].IsSyncSample) { nextIdr = i; break; }
            }
            int gopEnd = Math.Min(nextIdr, highest + 9);

            using var gopFs = File.OpenRead(inPath);
            Mp4SampleStream localStream;
            try { localStream = Mp4Reader.ExtractH264WithTiming(gopFs); }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref failedGops);
                lock (stderr) PrintException($"MP4 parse failed (GOP starting at {idrSample})", ex, stderr);
                return;
            }

            var nals = new List<NalUnit>(localStream.AvcCConfigNalUnits);
            for (int i = idrSample; i < gopEnd; i++) nals.AddRange(localStream.ResolveNalUnits(i));

            var decoder = new H264FrameDecoder();
            List<DecodedPicture> frames;
            try { frames = decoder.DecodeAllFrames(nals); }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref failedGops);
                lock (stderr) PrintException($"decode failed (GOP starting at {idrSample})", ex, stderr);
                return;
            }

            // Within a GOP, encode + write PNGs in parallel.
            int wrote = 0;
            System.Threading.Tasks.Parallel.ForEach(requested, idx =>
            {
                int local = idx - idrSample;
                if (local < 0 || local >= frames.Count) return;
                var pic = frames[local];
                string outPath = Path.Combine(outDir, $"frame_{idx:D5}.png");
                byte[] rgb = YuvToRgb.Convert(pic, pic.Vui);
                byte[] png = PngEncoder.EncodeRgb(pic.Width, pic.Height, rgb);
                File.WriteAllBytes(outPath, png);
                System.Threading.Interlocked.Increment(ref wrote);
            });
            System.Threading.Interlocked.Add(ref totalWritten, wrote);
        });

        stderr.WriteLine($"wrote {totalWritten}/{indices.Count} frame(s) to {outDir} ({byGop.Count} GOP(s), {Environment.ProcessorCount} cores)");
        return failedGops == 0 ? 0 : 3;
    }

    /// <summary>Parse a frame-selection spec into a sorted, deduplicated set of display-order indices.</summary>
    public static bool TryParseFrameSpec(string spec, int totalFrames, out List<int> indices, out string? error)
    {
        indices = new List<int>();
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) { error = "empty spec"; return false; }
        var set = new SortedSet<int>();
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < totalFrames; i++) set.Add(i);
            indices = set.ToList();
            return true;
        }
        foreach (string raw in spec.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int n))
                {
                    error = $"'{part}' is not a number"; return false;
                }
                if (n < 0 || n >= totalFrames)
                {
                    error = $"frame {n} out of range [0, {totalFrames - 1}]"; return false;
                }
                set.Add(n);
            }
            else
            {
                string lo = part[..dash].Trim();
                string hi = part[(dash + 1)..].Trim();
                if (!int.TryParse(lo, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int a)
                 || !int.TryParse(hi, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int b))
                {
                    error = $"invalid range '{part}'"; return false;
                }
                if (a > b) (a, b) = (b, a);
                if (a < 0 || b >= totalFrames)
                {
                    error = $"range {a}-{b} out of [0, {totalFrames - 1}]"; return false;
                }
                for (int i = a; i <= b; i++) set.Add(i);
            }
        }
        if (set.Count == 0) { error = "no frames selected"; return false; }
        indices = set.ToList();
        return true;
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
        catch (Exception ex) { PrintException("MP4 parse failed", ex, stderr); return 3; }
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
        catch (Exception ex) { PrintException("decode failed", ex, stderr); return 3; }

        // frames is sorted by POC (display order); the MP4 sample table indexes decode order.
        // Look up by DecodeOrderIndex so B-pyramid clips don't pick the wrong frame.
        int targetDecodeIdx = target - idr;
        var pic = frames.FirstOrDefault(f => f.DecodeOrderIndex == targetDecodeIdx) ?? frames[^1];
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
        catch (Exception ex) { PrintException("MP4 parse failed", ex, stderr); return 3; }

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
        catch (Exception ex) { PrintException("parse failed", ex, stderr); return 3; }

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
            byte[] rgb = YuvToRgb.Convert(pic, pic.Vui);
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

    /// <summary>Print exception with type + first stack frame; full stack trace when H264_VERBOSE is set.</summary>
    private static void PrintException(string label, Exception ex, TextWriter stderr)
    {
        string typeName = ex.GetType().Name;
        stderr.WriteLine($"{label}: [{typeName}] {ex.Message}");
        bool verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("H264_VERBOSE"));
        if (verbose)
        {
            stderr.WriteLine(ex.ToString());
        }
        else if (ex.StackTrace is { } trace)
        {
            // First frame only — usually points to the throw site.
            string? firstFrame = trace.Split('\n').FirstOrDefault()?.Trim();
            if (firstFrame is not null) stderr.WriteLine($"  {firstFrame}");
            stderr.WriteLine("  (set H264_VERBOSE=1 for full stack trace)");
        }
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
