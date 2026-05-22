using System.Buffers.Binary;

namespace H264Decoder.Bitstream;

/// <summary>
/// Minimal MP4 (ISOBMFF) reader. Walks the atom tree just enough to extract the
/// H.264 elementary stream from an MP4 video track:
///   - SPS + PPS from the avcC (AVCDecoderConfigurationRecord)
///   - All video samples from mdat, sliced per the stbl sample table
///
/// Out of scope: fragmented MP4 (moof/traf), edit lists, multiple stsd entries,
/// audio tracks. We accept 32-bit and 64-bit chunk offsets (stco / co64).
/// </summary>
public static class Mp4Reader
{
    /// <summary>
    /// Treat <paramref name="mp4"/> as an MP4 file and return the H.264 NAL stream
    /// in decode order: SPS NALs, PPS NALs, then every video sample NAL.
    /// </summary>
    public static List<NalUnit> ExtractH264NalUnits(ReadOnlySpan<byte> mp4)
    {
        var stream = ExtractH264WithTiming(mp4);
        var results = new List<NalUnit>();
        results.AddRange(stream.AvcCConfigNalUnits);
        foreach (var s in stream.Samples) results.AddRange(s.NalUnits);
        return results;
    }

    /// <summary>
    /// Parse an MP4 fully — avcC configuration NALs, per-sample NAL units, and the
    /// timing tables (stts / ctts / stss / mvhd / tkhd) needed to seek to a timestamp.
    /// </summary>
    public static Mp4SampleStream ExtractH264WithTiming(ReadOnlySpan<byte> mp4)
    {
        if (!TryFindTopBox(mp4, "moov", out int moovStart, out int moovLen))
            throw new InvalidDataException("MP4: no 'moov' box");
        var moov = mp4.Slice(moovStart, moovLen);

        // Movie header — global timescale + duration on the master timeline.
        (uint movieTimescale, ulong movieDuration) = ReadMvhd(moov);

        if (!TryFindVideoTrak(moov, out int trakStart, out int trakLen))
            throw new InvalidDataException("MP4: no video track");
        var trak = moov.Slice(trakStart, trakLen);

        // Track header — width/height (16.16 fixed point) for the video track.
        (int tkhdWidth, int tkhdHeight) = ReadTkhdSize(trak);

        if (!TryFindChildBox(trak, "mdia", out int mdiaS, out int mdiaL))
            throw new InvalidDataException("MP4: no mdia");
        var mdia = trak.Slice(mdiaS, mdiaL);

        // Media header — per-track timescale (used for stts/ctts deltas).
        uint mediaTimescale = ReadMdhdTimescale(mdia);

        if (!TryFindChildBox(mdia, "minf", out int minfS, out int minfL))
            throw new InvalidDataException("MP4: no minf");
        var minf = mdia.Slice(minfS, minfL);
        if (!TryFindChildBox(minf, "stbl", out int stblS, out int stblL))
            throw new InvalidDataException("MP4: no stbl");
        var stbl = minf.Slice(stblS, stblL);

        var (sps, pps, lengthSize) = ReadAvcConfigFromStbl(stbl);
        if (sps.Count == 0 || pps.Count == 0)
            throw new InvalidDataException("MP4: avcC missing SPS or PPS");

        var sampleOffsets = BuildSampleOffsetTable(stbl);
        int sampleCount = sampleOffsets.Count;

        // Per-sample decode-time deltas (stts) — sum across runs to get cumulative time.
        uint[] sttsDeltas = ReadStts(stbl, sampleCount);
        // Per-sample composition offsets (ctts), or zero if absent.
        int[] cttsOffsets = ReadCtts(stbl, sampleCount);
        // 1-based indices of sync samples (stss); if absent all samples are sync.
        bool[] isSync = ReadStss(stbl, sampleCount);

        var avcConfig = new List<NalUnit>(sps.Count + pps.Count);
        avcConfig.AddRange(sps);
        avcConfig.AddRange(pps);

        var samples = new List<Mp4Sample>(sampleCount);
        long cumulativeDt = 0;
        double tsInv = mediaTimescale > 0 ? 1.0 / mediaTimescale : 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            var (offset, size) = sampleOffsets[i];
            if (offset < 0 || (long)offset + size > mp4.Length)
                throw new InvalidDataException($"MP4: sample at {offset}+{size} exceeds file size {mp4.Length}");
            var sample = mp4.Slice(offset, size);
            var nals = new List<NalUnit>();
            int pos = 0;
            while (pos + lengthSize <= sample.Length)
            {
                int nalLen = ReadBE(sample, pos, lengthSize);
                pos += lengthSize;
                if (nalLen < 1 || pos + nalLen > sample.Length)
                    throw new InvalidDataException($"MP4: NAL length {nalLen} overflows sample at offset {offset}");
                nals.Add(BuildNalUnit(sample.Slice(pos, nalLen)));
                pos += nalLen;
            }
            double dtSec = cumulativeDt * tsInv;
            double ctSec = (cumulativeDt + cttsOffsets[i]) * tsInv;
            samples.Add(new Mp4Sample(nals, dtSec, ctSec, isSync[i]));
            cumulativeDt += sttsDeltas[i];
        }

        double durationSec = movieTimescale > 0 ? (double)movieDuration / movieTimescale : 0.0;

        // Prefer the SPS-derived cropped size (matches what the decoder emits); fall back to tkhd.
        int width = tkhdWidth, height = tkhdHeight;
        if (sps.Count > 0)
        {
            try
            {
                var s0 = Syntax.SequenceParameterSet.Parse(sps[0].Rbsp.Span);
                width = (int)s0.CroppedWidth;
                height = (int)s0.CroppedHeight;
            }
            catch { /* fall back to tkhd dims */ }
        }

        return new Mp4SampleStream(avcConfig, samples, mediaTimescale, durationSec, width, height);
    }

    // ---------- timing atoms ----------

    private static (uint timescale, ulong duration) ReadMvhd(ReadOnlySpan<byte> moov)
    {
        if (!TryFindChildBox(moov, "mvhd", out int s, out int l)) return (0, 0);
        var b = moov.Slice(s, l);
        byte version = b[0];
        if (version == 1)
        {
            // v1: 8B ctime, 8B mtime, 4B timescale, 8B duration
            uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 8 + 8, 4));
            ulong dur = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(4 + 8 + 8 + 4, 8));
            return (ts, dur);
        }
        else
        {
            // v0: 4B ctime, 4B mtime, 4B timescale, 4B duration
            uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 4 + 4, 4));
            uint dur = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 4 + 4 + 4, 4));
            return (ts, dur);
        }
    }

    private static uint ReadMdhdTimescale(ReadOnlySpan<byte> mdia)
    {
        if (!TryFindChildBox(mdia, "mdhd", out int s, out int l)) return 0;
        var b = mdia.Slice(s, l);
        byte version = b[0];
        if (version == 1) return BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 8 + 8, 4));
        return BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 4 + 4, 4));
    }

    private static (int w, int h) ReadTkhdSize(ReadOnlySpan<byte> trak)
    {
        if (!TryFindChildBox(trak, "tkhd", out int s, out int l)) return (0, 0);
        var b = trak.Slice(s, l);
        byte version = b[0];
        // version 0: 4B v/f + 4B ctime + 4B mtime + 4B track_id + 4B reserved + 4B duration
        //          + 8B reserved + 2B layer + 2B alt + 2B vol + 2B reserved
        //          + 36B matrix + 4B width (16.16) + 4B height (16.16)
        // version 1: same with 8B ctime/mtime + 8B duration.
        int offToWH = version == 1 ? (4 + 8 + 8 + 4 + 4 + 8 + 8 + 2 + 2 + 2 + 2 + 36) : (4 + 4 + 4 + 4 + 4 + 4 + 8 + 2 + 2 + 2 + 2 + 36);
        if (b.Length < offToWH + 8) return (0, 0);
        uint w16 = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offToWH, 4));
        uint h16 = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offToWH + 4, 4));
        return ((int)(w16 >> 16), (int)(h16 >> 16));
    }

    private static uint[] ReadStts(ReadOnlySpan<byte> stbl, int sampleCount)
    {
        var deltas = new uint[sampleCount];
        if (!TryFindChildBox(stbl, "stts", out int s, out int l)) return deltas;
        var b = stbl.Slice(s, l);
        int entries = BinaryPrimitives.ReadInt32BigEndian(b.Slice(4, 4));
        int idx = 0;
        for (int e = 0; e < entries && idx < sampleCount; e++)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(b.Slice(8 + e * 8, 4));
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8 + e * 8 + 4, 4));
            for (int k = 0; k < count && idx < sampleCount; k++) deltas[idx++] = delta;
        }
        return deltas;
    }

    private static int[] ReadCtts(ReadOnlySpan<byte> stbl, int sampleCount)
    {
        var offsets = new int[sampleCount];
        if (!TryFindChildBox(stbl, "ctts", out int s, out int l)) return offsets;
        var b = stbl.Slice(s, l);
        // ctts v1 carries signed offsets; v0 unsigned. We coerce to int either way.
        int entries = BinaryPrimitives.ReadInt32BigEndian(b.Slice(4, 4));
        int idx = 0;
        for (int e = 0; e < entries && idx < sampleCount; e++)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(b.Slice(8 + e * 8, 4));
            int off = BinaryPrimitives.ReadInt32BigEndian(b.Slice(8 + e * 8 + 4, 4));
            for (int k = 0; k < count && idx < sampleCount; k++) offsets[idx++] = off;
        }
        return offsets;
    }

    private static bool[] ReadStss(ReadOnlySpan<byte> stbl, int sampleCount)
    {
        var flags = new bool[sampleCount];
        if (!TryFindChildBox(stbl, "stss", out int s, out int l))
        {
            // No stss -> every sample is a sync sample (per ISOBMFF).
            for (int i = 0; i < sampleCount; i++) flags[i] = true;
            return flags;
        }
        var b = stbl.Slice(s, l);
        int entries = BinaryPrimitives.ReadInt32BigEndian(b.Slice(4, 4));
        for (int e = 0; e < entries; e++)
        {
            int oneBased = BinaryPrimitives.ReadInt32BigEndian(b.Slice(8 + e * 4, 4));
            int zeroBased = oneBased - 1;
            if ((uint)zeroBased < (uint)sampleCount) flags[zeroBased] = true;
        }
        return flags;
    }

    // ---------- atom walking ----------

    private static bool TryFindTopBox(ReadOnlySpan<byte> file, string fourcc, out int dataStart, out int dataLen)
    {
        int p = 0;
        while (p + 8 <= file.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(file.Slice(p, 4));
            string ty = Fourcc(file, p + 4);
            if (sz < 8 || p + sz > file.Length) break;
            if (ty == fourcc) { dataStart = p + 8; dataLen = sz - 8; return true; }
            p += sz;
        }
        dataStart = 0; dataLen = 0; return false;
    }

    private static bool TryFindChildBox(ReadOnlySpan<byte> parent, string fourcc, out int dataStart, out int dataLen)
    {
        int p = 0;
        while (p + 8 <= parent.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(parent.Slice(p, 4));
            string ty = Fourcc(parent, p + 4);
            if (sz < 8 || p + sz > parent.Length) break;
            if (ty == fourcc) { dataStart = p + 8; dataLen = sz - 8; return true; }
            p += sz;
        }
        dataStart = 0; dataLen = 0; return false;
    }

    private static bool TryFindNestedBox(ReadOnlySpan<byte> root, string a, string b, string c, out int dataStart, out int dataLen)
    {
        dataStart = 0; dataLen = 0;
        if (!TryFindChildBox(root, a, out int aS, out int aL)) return false;
        var aSpan = root.Slice(aS, aL);
        if (!TryFindChildBox(aSpan, b, out int bS, out int bL)) return false;
        var bSpan = aSpan.Slice(bS, bL);
        if (!TryFindChildBox(bSpan, c, out int cS, out int cL)) return false;
        // Translate cS, cL back to root coordinates.
        dataStart = aS + bS + cS;
        dataLen = cL;
        return true;
    }

    private static bool TryFindVideoTrak(ReadOnlySpan<byte> moov, out int trakStart, out int trakLen)
    {
        int p = 0;
        while (p + 8 <= moov.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(moov.Slice(p, 4));
            string ty = Fourcc(moov, p + 4);
            if (sz < 8 || p + sz > moov.Length) break;
            if (ty == "trak")
            {
                var trak = moov.Slice(p + 8, sz - 8);
                if (IsVideoTrak(trak))
                {
                    trakStart = p + 8; trakLen = sz - 8; return true;
                }
            }
            p += sz;
        }
        trakStart = 0; trakLen = 0; return false;
    }

    private static bool IsVideoTrak(ReadOnlySpan<byte> trak)
    {
        if (!TryFindChildBox(trak, "mdia", out int mdiaS, out int mdiaL)) return false;
        var mdia = trak.Slice(mdiaS, mdiaL);
        if (!TryFindChildBox(mdia, "hdlr", out int hdlrS, out int hdlrL)) return false;
        var hdlr = mdia.Slice(hdlrS, hdlrL);
        // hdlr payload: 4B v/f + 4B pre_defined + 4B handler_type + ...
        return hdlr.Length >= 12 && Fourcc(hdlr, 8) == "vide";
    }

    // ---------- avcC ----------

    private static (List<NalUnit> sps, List<NalUnit> pps, int lengthSize) ReadAvcConfigFromStbl(ReadOnlySpan<byte> stbl)
    {
        if (!TryFindChildBox(stbl, "stsd", out int stsdS, out int stsdL))
            return (new(), new(), 4);
        var stsd = stbl.Slice(stsdS, stsdL);

        // stsd: 4B version/flags + 4B entry_count + N sample entries (themselves boxes).
        int entries = BinaryPrimitives.ReadInt32BigEndian(stsd.Slice(4, 4));
        int p = 8;
        for (int e = 0; e < entries && p + 8 <= stsd.Length; e++)
        {
            int entrySize = BinaryPrimitives.ReadInt32BigEndian(stsd.Slice(p, 4));
            if (entrySize < 8 || p + entrySize > stsd.Length) break;
            string entryType = Fourcc(stsd, p + 4);
            if (entryType is "avc1" or "avc3")
            {
                // VisualSampleEntry: 78 bytes of fixed header after the box header, then child boxes.
                int childStart = p + 8 + 78;
                int childEnd = p + entrySize;
                if (childStart > childEnd) break;
                var children = stsd[childStart..childEnd];
                if (TryFindChildBox(children, "avcC", out int avccS, out int avccL))
                {
                    return ParseAvcC(children.Slice(avccS, avccL));
                }
            }
            p += entrySize;
        }
        return (new(), new(), 4);
    }

    private static (List<NalUnit> sps, List<NalUnit> pps, int lengthSize) ParseAvcC(ReadOnlySpan<byte> avcc)
    {
        if (avcc.Length < 7) throw new InvalidDataException("avcC too short");
        int lengthSize = (avcc[4] & 0x03) + 1;
        int numSps = avcc[5] & 0x1F;
        int o = 6;
        var sps = new List<NalUnit>();
        for (int i = 0; i < numSps; i++)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(o, 2));
            o += 2;
            sps.Add(BuildNalUnit(avcc.Slice(o, len)));
            o += len;
        }
        int numPps = avcc[o++];
        var pps = new List<NalUnit>();
        for (int i = 0; i < numPps; i++)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(o, 2));
            o += 2;
            pps.Add(BuildNalUnit(avcc.Slice(o, len)));
            o += len;
        }
        return (sps, pps, lengthSize);
    }

    // ---------- sample table ----------

    private static List<(int Offset, int Size)> BuildSampleOffsetTable(ReadOnlySpan<byte> stbl)
    {
        if (!TryFindChildBox(stbl, "stsz", out int stszS, out int stszL)) throw new InvalidDataException("MP4: no stsz");
        if (!TryFindChildBox(stbl, "stsc", out int stscS, out int stscL)) throw new InvalidDataException("MP4: no stsc");
        bool isCo64 = TryFindChildBox(stbl, "co64", out int co64S, out int co64L);
        bool isCo32 = TryFindChildBox(stbl, "stco", out int stcoS, out int stcoL);
        if (!isCo64 && !isCo32) throw new InvalidDataException("MP4: no stco/co64");

        var stsz = stbl.Slice(stszS, stszL);
        var stsc = stbl.Slice(stscS, stscL);
        var stco = isCo64 ? stbl.Slice(co64S, co64L) : stbl.Slice(stcoS, stcoL);

        int defaultSize = BinaryPrimitives.ReadInt32BigEndian(stsz.Slice(4, 4));
        int sampleCount = BinaryPrimitives.ReadInt32BigEndian(stsz.Slice(8, 4));
        int[] sampleSizes = new int[sampleCount];
        if (defaultSize != 0)
        {
            for (int i = 0; i < sampleCount; i++) sampleSizes[i] = defaultSize;
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
                sampleSizes[i] = BinaryPrimitives.ReadInt32BigEndian(stsz.Slice(12 + i * 4, 4));
        }

        int chunkCount = BinaryPrimitives.ReadInt32BigEndian(stco.Slice(4, 4));
        long[] chunkOffsets = new long[chunkCount];
        if (isCo64)
        {
            for (int i = 0; i < chunkCount; i++)
                chunkOffsets[i] = BinaryPrimitives.ReadInt64BigEndian(stco.Slice(8 + i * 8, 8));
        }
        else
        {
            for (int i = 0; i < chunkCount; i++)
                chunkOffsets[i] = BinaryPrimitives.ReadInt32BigEndian(stco.Slice(8 + i * 4, 4));
        }

        int stscCount = BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(4, 4));
        var stscEntries = new (int FirstChunk, int SamplesPerChunk)[stscCount];
        for (int i = 0; i < stscCount; i++)
        {
            stscEntries[i] = (
                BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(8 + i * 12, 4)),
                BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(8 + i * 12 + 4, 4)));
        }

        var result = new List<(int, int)>(sampleCount);
        int sampleIdx = 0;
        for (int e = 0; e < stscCount && sampleIdx < sampleCount; e++)
        {
            int firstChunk = stscEntries[e].FirstChunk;
            int samplesPerChunk = stscEntries[e].SamplesPerChunk;
            int lastChunk = (e + 1 < stscCount) ? stscEntries[e + 1].FirstChunk - 1 : chunkCount;
            for (int chunk = firstChunk; chunk <= lastChunk && sampleIdx < sampleCount; chunk++)
            {
                long pos = chunkOffsets[chunk - 1];
                for (int s = 0; s < samplesPerChunk && sampleIdx < sampleCount; s++)
                {
                    int sz = sampleSizes[sampleIdx];
                    result.Add(((int)pos, sz));
                    pos += sz;
                    sampleIdx++;
                }
            }
        }
        return result;
    }

    // ---------- helpers ----------

    private static NalUnit BuildNalUnit(ReadOnlySpan<byte> headerPlusEbsp)
    {
        byte header = headerPlusEbsp[0];
        if ((header & 0x80) != 0)
            throw new InvalidDataException("MP4: forbidden_zero_bit set in NAL header");
        byte nalRefIdc = (byte)((header >> 5) & 0x03);
        var nalUnitType = (NalUnitType)(header & 0x1F);
        byte[] rbsp = AnnexBReader.StripEmulationPreventionBytes(headerPlusEbsp[1..]);
        return new NalUnit(nalRefIdc, nalUnitType, rbsp);
    }

    private static int ReadBE(ReadOnlySpan<byte> s, int pos, int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | s[pos + i];
        return v;
    }

    private static string Fourcc(ReadOnlySpan<byte> s, int pos) =>
        new string(new[] { (char)s[pos], (char)s[pos + 1], (char)s[pos + 2], (char)s[pos + 3] });
}

/// <summary>
/// MP4 video track parsed into NAL units plus timing metadata: per-sample decode time,
/// composition time, sync-sample flag, plus container-level duration and dimensions.
/// </summary>
public sealed class Mp4SampleStream
{
    public IReadOnlyList<NalUnit> AvcCConfigNalUnits { get; }
    public IReadOnlyList<Mp4Sample> Samples { get; }
    public uint Timescale { get; }
    public double DurationSeconds { get; }
    public int Width { get; }
    public int Height { get; }

    public Mp4SampleStream(
        IReadOnlyList<NalUnit> avcConfig, IReadOnlyList<Mp4Sample> samples,
        uint timescale, double durationSeconds, int width, int height)
    {
        AvcCConfigNalUnits = avcConfig;
        Samples = samples;
        Timescale = timescale;
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
    }
}

/// <summary>One MP4 video sample (typically an access unit / coded picture).</summary>
public sealed class Mp4Sample
{
    public IReadOnlyList<NalUnit> NalUnits { get; }
    public double DecodeTimeSeconds { get; }
    public double CompositionTimeSeconds { get; }
    public bool IsSyncSample { get; }

    public Mp4Sample(IReadOnlyList<NalUnit> nalUnits, double decodeTime, double compositionTime, bool isSync)
    {
        NalUnits = nalUnits;
        DecodeTimeSeconds = decodeTime;
        CompositionTimeSeconds = compositionTime;
        IsSyncSample = isSync;
    }
}
