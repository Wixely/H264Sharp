using System.Buffers.Binary;

namespace H264Decoder.Bitstream;

/// <summary>
/// Minimal MP4 (ISOBMFF) reader. Walks the atom tree just enough to extract the
/// H.264 elementary stream from an MP4 video track:
///   - SPS + PPS from the avcC (AVCDecoderConfigurationRecord)
///   - All video samples from mdat, sliced per the stbl sample table (non-fragmented)
///     OR from moof/traf/trun runs (fragmented MP4, fMP4).
///
/// Streams the file: only the moov atom (and each individual moof) is loaded into
/// memory, so arbitrary-size inputs are supported as long as moov itself fits in
/// a byte[] (~2 GiB) and each moof fits in ~10 MiB.
/// Out of scope: edit lists, multiple stsd entries, audio tracks. We accept 32-bit
/// and 64-bit chunk offsets (stco / co64).
/// </summary>
public static class Mp4Reader
{
    /// <summary>
    /// Treat <paramref name="mp4"/> as an MP4 file and return the H.264 NAL stream
    /// in decode order: SPS NALs, PPS NALs, then every video sample NAL.
    /// </summary>
    public static List<NalUnit> ExtractH264NalUnits(ReadOnlySpan<byte> mp4)
    {
        // The span overload owns its own MemoryStream; sample resolution needs it alive.
        var ms = new MemoryStream(mp4.ToArray(), writable: false);
        var stream = ExtractH264WithTiming(ms);
        var results = new List<NalUnit>();
        results.AddRange(stream.AvcCConfigNalUnits);
        for (int i = 0; i < stream.Samples.Count; i++)
            results.AddRange(stream.ResolveNalUnits(i));
        return results;
    }

    /// <summary>
    /// Span-based wrapper kept for backwards compatibility. Internally wraps the span
    /// in a MemoryStream and dispatches to the streaming entry point.
    /// </summary>
    public static Mp4SampleStream ExtractH264WithTiming(ReadOnlySpan<byte> mp4)
    {
        var ms = new MemoryStream(mp4.ToArray(), writable: false);
        return ExtractH264WithTiming(ms);
    }

    /// <summary>
    /// Streaming entry point. Walks top-level boxes via Seek/Read, loads only the
    /// moov atom into memory, and returns a <see cref="Mp4SampleStream"/> carrying
    /// sample metadata plus a reference to <paramref name="stream"/> for lazy NAL
    /// resolution. Caller owns the stream lifetime; we do not dispose it.
    /// </summary>
    public static Mp4SampleStream ExtractH264WithTiming(Stream stream) =>
        ExtractH264WithTiming(stream, stderr: null);

    /// <summary>Streaming entry point with a writer for diagnostic warnings (e.g. unsupported edit lists).</summary>
    public static Mp4SampleStream ExtractH264WithTiming(Stream stream, TextWriter? stderr)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("MP4: stream must be seekable", nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("MP4: stream must be readable", nameof(stream));

        // Walk top-level boxes once: pull moov into memory and remember positions of moof atoms.
        byte[]? moovBytes = null;
        var moofRegions = new List<(long Start, long PayloadStart, long PayloadEnd)>();
        long fileLength = stream.Length;
        long pos = 0;
        Span<byte> header = stackalloc byte[16];
        while (pos + 8 <= fileLength)
        {
            stream.Position = pos;
            int read = ReadExact(stream, header[..8]);
            if (read < 8) break;
            uint sz32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string ty = AsFourcc(header[4..8]);

            long boxSize;
            long payloadStart;
            if (sz32 == 1)
            {
                if (ReadExact(stream, header[8..16]) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                payloadStart = pos + 16;
            }
            else if (sz32 == 0)
            {
                boxSize = fileLength - pos;
                payloadStart = pos + 8;
            }
            else
            {
                boxSize = sz32;
                payloadStart = pos + 8;
            }

            if (boxSize < 8 || pos + boxSize > fileLength) break;

            if (ty == "moov" && moovBytes is null)
            {
                long payloadLen = pos + boxSize - payloadStart;
                if (payloadLen > int.MaxValue)
                    throw new InvalidDataException("MP4: moov atom too large to load (>2 GiB)");
                moovBytes = new byte[(int)payloadLen];
                stream.Position = payloadStart;
                if (ReadExact(stream, moovBytes) != moovBytes.Length)
                    throw new InvalidDataException("MP4: truncated moov atom");
            }
            else if (ty == "moof")
            {
                moofRegions.Add((pos, payloadStart, pos + boxSize));
            }
            pos += boxSize;
        }

        if (moovBytes is null) throw new InvalidDataException("MP4: no 'moov' box");
        return ParseMoov(moovBytes, stream, moofRegions, stderr);
    }

    private static Mp4SampleStream ParseMoov(byte[] moovBytes, Stream source,
        List<(long Start, long PayloadStart, long PayloadEnd)> moofRegions, TextWriter? stderr)
    {
        ReadOnlySpan<byte> moov = moovBytes;

        // Movie header — global timescale + duration on the master timeline.
        (uint movieTimescale, ulong movieDuration) = ReadMvhd(moov);

        if (!TryFindVideoTrak(moov, out int trakStart, out int trakLen))
            throw new InvalidDataException("MP4: no video track");
        var trak = moov.Slice(trakStart, trakLen);
        uint videoTrackId = ReadTkhdTrackId(trak);

        // Track header — width/height (16.16 fixed point) for the video track.
        (int tkhdWidth, int tkhdHeight) = ReadTkhdSize(trak);

        if (!TryFindChildBox(trak, "mdia", out int mdiaS, out int mdiaL))
            throw new InvalidDataException("MP4: no mdia");
        var mdia = trak.Slice(mdiaS, mdiaL);

        // Media header — per-track timescale (used for stts/ctts deltas).
        uint mediaTimescale = ReadMdhdTimescale(mdia);

        // Edit list — most commonly a single non-empty edit with media_time>0 that shifts
        // composition times so the displayed timeline starts at 0 (B-pyramid compensation).
        // We support that common case; anything more elaborate falls back to no offset.
        long editMediaTimeOffset = ReadElstMediaTimeOffset(trak, movieTimescale, mediaTimescale, stderr);

        if (!TryFindChildBox(mdia, "minf", out int minfS, out int minfL))
            throw new InvalidDataException("MP4: no minf");
        var minf = mdia.Slice(minfS, minfL);
        if (!TryFindChildBox(minf, "stbl", out int stblS, out int stblL))
            throw new InvalidDataException("MP4: no stbl");
        var stbl = minf.Slice(stblS, stblL);

        var (sps, pps, lengthSize) = ReadAvcConfigFromStbl(stbl);
        if (sps.Count == 0 || pps.Count == 0)
            throw new InvalidDataException("MP4: avcC missing SPS or PPS");

        // Read trex defaults for the video track (used by fragmented MP4).
        var trex = ReadTrexForTrack(moov, videoTrackId);

        var avcConfig = new List<NalUnit>(sps.Count + pps.Count);
        avcConfig.AddRange(sps);
        avcConfig.AddRange(pps);

        double tsInv = mediaTimescale > 0 ? 1.0 / mediaTimescale : 0.0;
        List<Mp4Sample> samples;

        // Build samples from stbl as before — may be empty for fragmented (empty_moov) files.
        var sampleOffsets = BuildSampleOffsetTable(stbl);
        int sampleCount = sampleOffsets.Count;
        uint[] sttsDeltas = ReadStts(stbl, sampleCount);
        int[] cttsOffsets = ReadCtts(stbl, sampleCount);
        bool[] isSync = ReadStss(stbl, sampleCount);

        samples = new List<Mp4Sample>(sampleCount);
        long cumulativeDt = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            var (offset, size) = sampleOffsets[i];
            double dtSec = (cumulativeDt - editMediaTimeOffset) * tsInv;
            double ctSec = (cumulativeDt + cttsOffsets[i] - editMediaTimeOffset) * tsInv;
            samples.Add(new Mp4Sample(offset, size, dtSec, ctSec, isSync[i], lengthSize));
            cumulativeDt += sttsDeltas[i];
        }

        // If fragments exist, prefer them. Some files have a placeholder stbl (sample_count=0)
        // plus real samples across moof/mdat pairs — others have a small stbl init alongside
        // real fragment data. In both cases the fragmented data is authoritative.
        if (moofRegions.Count > 0)
        {
            var fragSamples = ParseFragments(source, moofRegions, videoTrackId, trex, lengthSize, tsInv, editMediaTimeOffset);
            if (fragSamples.Count > 0) samples = fragSamples;
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

        return new Mp4SampleStream(avcConfig, samples, mediaTimescale, durationSec, width, height,
            (byte)(lengthSize - 1), source);
    }

    // ---------- timing atoms ----------

    private static (uint timescale, ulong duration) ReadMvhd(ReadOnlySpan<byte> moov)
    {
        if (!TryFindChildBox(moov, "mvhd", out int s, out int l)) return (0, 0);
        var b = moov.Slice(s, l);
        byte version = b[0];
        if (version == 1)
        {
            uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 8 + 8, 4));
            ulong dur = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(4 + 8 + 8 + 4, 8));
            return (ts, dur);
        }
        else
        {
            uint ts = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 4 + 4, 4));
            uint dur = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4 + 4 + 4 + 4, 4));
            return (ts, dur);
        }
    }

    // ISO/IEC 14496-12 §8.6.6: edts/elst. Returns the media_time offset (in media-timescale units)
    // to subtract from every sample's decode/composition time so the timeline starts at 0.
    // Empty edits (media_time == -1) are ignored. We support the common case of a single non-empty
    // edit with media_rate == 1.0; anything more elaborate emits a warning and returns 0.
    private static long ReadElstMediaTimeOffset(ReadOnlySpan<byte> trak, uint movieTimescale, uint mediaTimescale, TextWriter? stderr)
    {
        if (!TryFindChildBox(trak, "edts", out int edtsS, out int edtsL)) return 0;
        var edts = trak.Slice(edtsS, edtsL);
        if (!TryFindChildBox(edts, "elst", out int elstS, out int elstL)) return 0;
        var b = edts.Slice(elstS, elstL);
        if (b.Length < 8) return 0;
        byte version = b[0];
        int entryCount = BinaryPrimitives.ReadInt32BigEndian(b.Slice(4, 4));
        if (entryCount <= 0) return 0;
        int entrySize = version == 1 ? 20 : 12;
        if (b.Length < 8 + entryCount * entrySize) return 0;

        // Find the first non-empty entry (media_time != -1).
        long firstMediaTime = -1;
        int firstNonEmptyIdx = -1;
        int nonEmptyCount = 0;
        for (int i = 0; i < entryCount; i++)
        {
            int off = 8 + i * entrySize;
            long mediaTime;
            uint mediaRateRaw;
            if (version == 1)
            {
                mediaTime = BinaryPrimitives.ReadInt64BigEndian(b.Slice(off + 8, 8));
                mediaRateRaw = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(off + 16, 4));
            }
            else
            {
                mediaTime = BinaryPrimitives.ReadInt32BigEndian(b.Slice(off + 4, 4));
                mediaRateRaw = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(off + 8, 4));
            }
            if (mediaTime < 0) continue; // empty edit — gap, no media presented
            nonEmptyCount++;
            // media_rate is 16.16 fixed point; 0x00010000 == 1.0. Anything else: unsupported.
            if (mediaRateRaw != 0x00010000)
            {
                stderr?.WriteLine($"MP4: elst entry {i} has media_rate != 1.0 (0x{mediaRateRaw:X8}); ignoring edit list");
                return 0;
            }
            if (firstNonEmptyIdx < 0) { firstNonEmptyIdx = i; firstMediaTime = mediaTime; }
        }
        if (firstNonEmptyIdx < 0) return 0;
        if (nonEmptyCount > 1)
        {
            stderr?.WriteLine($"MP4: elst has {nonEmptyCount} non-empty entries; using first media_time={firstMediaTime} and approximating");
        }
        return firstMediaTime;
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
        int offToWH = version == 1 ? (4 + 8 + 8 + 4 + 4 + 8 + 8 + 2 + 2 + 2 + 2 + 36) : (4 + 4 + 4 + 4 + 4 + 4 + 8 + 2 + 2 + 2 + 2 + 36);
        if (b.Length < offToWH + 8) return (0, 0);
        uint w16 = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offToWH, 4));
        uint h16 = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offToWH + 4, 4));
        return ((int)(w16 >> 16), (int)(h16 >> 16));
    }

    // tkhd track_ID lives right after version+flags(4) + creation/modification times.
    private static uint ReadTkhdTrackId(ReadOnlySpan<byte> trak)
    {
        if (!TryFindChildBox(trak, "tkhd", out int s, out int l)) return 0;
        var b = trak.Slice(s, l);
        byte version = b[0];
        int off = version == 1 ? (4 + 8 + 8) : (4 + 4 + 4);
        if (b.Length < off + 4) return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(b.Slice(off, 4));
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

    private static bool TryFindChildBox(ReadOnlySpan<byte> parent, string fourcc, out int dataStart, out int dataLen)
    {
        int p = 0;
        while (p + 8 <= parent.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(parent.Slice(p, 4));
            string ty = AsFourcc(parent.Slice(p + 4, 4));
            if (sz < 8 || p + sz > parent.Length) break;
            if (ty == fourcc) { dataStart = p + 8; dataLen = sz - 8; return true; }
            p += sz;
        }
        dataStart = 0; dataLen = 0; return false;
    }

    private static bool TryFindVideoTrak(ReadOnlySpan<byte> moov, out int trakStart, out int trakLen)
    {
        int p = 0;
        while (p + 8 <= moov.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(moov.Slice(p, 4));
            string ty = AsFourcc(moov.Slice(p + 4, 4));
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
        return hdlr.Length >= 12 && AsFourcc(hdlr.Slice(8, 4)) == "vide";
    }

    // ---------- avcC ----------

    private static (List<NalUnit> sps, List<NalUnit> pps, int lengthSize) ReadAvcConfigFromStbl(ReadOnlySpan<byte> stbl)
    {
        if (!TryFindChildBox(stbl, "stsd", out int stsdS, out int stsdL))
            return (new(), new(), 4);
        var stsd = stbl.Slice(stsdS, stsdL);

        int entries = BinaryPrimitives.ReadInt32BigEndian(stsd.Slice(4, 4));
        int p = 8;
        for (int e = 0; e < entries && p + 8 <= stsd.Length; e++)
        {
            int entrySize = BinaryPrimitives.ReadInt32BigEndian(stsd.Slice(p, 4));
            if (entrySize < 8 || p + entrySize > stsd.Length) break;
            string entryType = AsFourcc(stsd.Slice(p + 4, 4));
            if (entryType is "avc1" or "avc3")
            {
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

    private static List<(long Offset, int Size)> BuildSampleOffsetTable(ReadOnlySpan<byte> stbl)
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
            // stco entries are unsigned 32-bit; widen via uint before storing.
            for (int i = 0; i < chunkCount; i++)
                chunkOffsets[i] = BinaryPrimitives.ReadUInt32BigEndian(stco.Slice(8 + i * 4, 4));
        }

        int stscCount = BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(4, 4));
        var stscEntries = new (int FirstChunk, int SamplesPerChunk)[stscCount];
        for (int i = 0; i < stscCount; i++)
        {
            stscEntries[i] = (
                BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(8 + i * 12, 4)),
                BinaryPrimitives.ReadInt32BigEndian(stsc.Slice(8 + i * 12 + 4, 4)));
        }

        var result = new List<(long, int)>(sampleCount);
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
                    result.Add((pos, sz));
                    pos += sz;
                    sampleIdx++;
                }
            }
        }
        return result;
    }

    // ---------- helpers ----------

    internal static NalUnit BuildNalUnit(ReadOnlySpan<byte> headerPlusEbsp)
    {
        byte header = headerPlusEbsp[0];
        if ((header & 0x80) != 0)
            throw new InvalidDataException("MP4: forbidden_zero_bit set in NAL header");
        byte nalRefIdc = (byte)((header >> 5) & 0x03);
        var nalUnitType = (NalUnitType)(header & 0x1F);
        byte[] rbsp = AnnexBReader.StripEmulationPreventionBytes(headerPlusEbsp[1..]);
        return new NalUnit(nalRefIdc, nalUnitType, rbsp);
    }

    internal static int ReadBE(ReadOnlySpan<byte> s, int pos, int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | s[pos + i];
        return v;
    }

    private static string AsFourcc(ReadOnlySpan<byte> s) =>
        new string(new[] { (char)s[0], (char)s[1], (char)s[2], (char)s[3] });

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ---------- fragmented MP4 ----------

    // Per-track defaults from mvex/trex. All fields can be overridden by tfhd at fragment scope.
    private readonly struct TrexDefaults
    {
        public readonly uint SampleDescriptionIndex;
        public readonly uint SampleDuration;
        public readonly uint SampleSize;
        public readonly uint SampleFlags;
        public TrexDefaults(uint sdi, uint dur, uint size, uint flags)
        { SampleDescriptionIndex = sdi; SampleDuration = dur; SampleSize = size; SampleFlags = flags; }
    }

    // Scan moov for an mvex/trex matching the given track_ID. Missing trex => all-zero defaults.
    private static TrexDefaults ReadTrexForTrack(ReadOnlySpan<byte> moov, uint trackId)
    {
        if (!TryFindChildBox(moov, "mvex", out int mvexS, out int mvexL)) return default;
        var mvex = moov.Slice(mvexS, mvexL);
        int p = 0;
        while (p + 8 <= mvex.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(mvex.Slice(p, 4));
            string ty = AsFourcc(mvex.Slice(p + 4, 4));
            if (sz < 8 || p + sz > mvex.Length) break;
            if (ty == "trex" && sz >= 8 + 4 + 4 + 4 + 4 + 4 + 4)
            {
                var b = mvex.Slice(p + 8, sz - 8);
                // version+flags(4), track_ID(4), default_sample_description_index(4),
                // default_sample_duration(4), default_sample_size(4), default_sample_flags(4).
                uint tid = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
                if (tid == trackId)
                {
                    return new TrexDefaults(
                        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8, 4)),
                        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12, 4)),
                        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4)),
                        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4)));
                }
            }
            p += sz;
        }
        return default;
    }

    // Iterate every moof region, parse traf/trun for the video track, and accumulate samples.
    private static List<Mp4Sample> ParseFragments(
        Stream source, List<(long Start, long PayloadStart, long PayloadEnd)> moofRegions,
        uint videoTrackId, TrexDefaults trex, int lengthSize, double tsInv, long editMediaTimeOffset)
    {
        var samples = new List<Mp4Sample>();
        // Cumulative decode time across all fragments (in media timescale units). tfdt resets this.
        long cumulativeDt = 0;
        foreach (var region in moofRegions)
        {
            long payloadLen = region.PayloadEnd - region.PayloadStart;
            if (payloadLen <= 0) continue;
            if (payloadLen > 10L * 1024 * 1024)
                throw new InvalidDataException($"MP4: moof atom too large ({payloadLen} bytes > 10 MiB) at offset {region.Start}");
            byte[] moofBuf = new byte[(int)payloadLen];
            source.Position = region.PayloadStart;
            if (ReadExact(source, moofBuf) != moofBuf.Length)
                throw new InvalidDataException($"MP4: truncated moof at offset {region.Start}");

            ParseMoof(moofBuf, region.Start, videoTrackId, trex, lengthSize, tsInv, samples, ref cumulativeDt, editMediaTimeOffset);
        }
        return samples;
    }

    // Parse one moof: iterate traf children, locating one whose tfhd.track_ID matches videoTrackId.
    private static void ParseMoof(byte[] moofBytes, long moofStart, uint videoTrackId,
        TrexDefaults trex, int lengthSize, double tsInv,
        List<Mp4Sample> samples, ref long cumulativeDt, long editMediaTimeOffset)
    {
        ReadOnlySpan<byte> moof = moofBytes;
        int p = 0;
        while (p + 8 <= moof.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(moof.Slice(p, 4));
            string ty = AsFourcc(moof.Slice(p + 4, 4));
            if (sz < 8 || p + sz > moof.Length) break;
            if (ty == "traf")
            {
                ParseTraf(moof.Slice(p + 8, sz - 8), moofStart, videoTrackId, trex, lengthSize, tsInv,
                    samples, ref cumulativeDt, editMediaTimeOffset);
            }
            // mfhd: ignored (sequence_number not needed for decoding correctness).
            p += sz;
        }
    }

    private static void ParseTraf(ReadOnlySpan<byte> traf, long moofStart, uint videoTrackId,
        TrexDefaults trex, int lengthSize, double tsInv,
        List<Mp4Sample> samples, ref long cumulativeDt, long editMediaTimeOffset)
    {
        // tfhd is mandatory and first in well-formed traf.
        if (!TryFindChildBox(traf, "tfhd", out int tfhdS, out int tfhdL)) return;
        var tfhd = traf.Slice(tfhdS, tfhdL);
        if (tfhd.Length < 8) return;
        uint tfhdFlags = (uint)((tfhd[1] << 16) | (tfhd[2] << 8) | tfhd[3]);
        uint tfhdTrackId = BinaryPrimitives.ReadUInt32BigEndian(tfhd.Slice(4, 4));
        if (tfhdTrackId != videoTrackId) return;

        int o = 8;
        long baseDataOffset;
        bool baseIsMoof = (tfhdFlags & 0x020000) != 0;
        if ((tfhdFlags & 0x000001) != 0)
        {
            baseDataOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(tfhd.Slice(o, 8));
            o += 8;
        }
        else if (baseIsMoof)
        {
            baseDataOffset = moofStart;
        }
        else
        {
            // Spec: default base = first byte of moof when no override (close enough for our usage).
            baseDataOffset = moofStart;
        }
        if ((tfhdFlags & 0x000002) != 0) o += 4; // sample_description_index: ignored
        uint defDur = trex.SampleDuration;
        uint defSize = trex.SampleSize;
        uint defFlags = trex.SampleFlags;
        if ((tfhdFlags & 0x000008) != 0) { defDur = BinaryPrimitives.ReadUInt32BigEndian(tfhd.Slice(o, 4)); o += 4; }
        if ((tfhdFlags & 0x000010) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(tfhd.Slice(o, 4)); o += 4; }
        if ((tfhdFlags & 0x000020) != 0) { defFlags = BinaryPrimitives.ReadUInt32BigEndian(tfhd.Slice(o, 4)); o += 4; }

        // tfdt overrides cumulative decode time for this fragment.
        if (TryFindChildBox(traf, "tfdt", out int tfdtS, out int tfdtL) && tfdtL >= 8)
        {
            var b = traf.Slice(tfdtS, tfdtL);
            byte ver = b[0];
            if (ver == 1 && b.Length >= 4 + 8)
                cumulativeDt = (long)BinaryPrimitives.ReadUInt64BigEndian(b.Slice(4, 8));
            else if (b.Length >= 4 + 4)
                cumulativeDt = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4, 4));
        }

        // Iterate all trun atoms (typically one per traf, but the spec allows several).
        int tp = 0;
        while (tp + 8 <= traf.Length)
        {
            int sz = BinaryPrimitives.ReadInt32BigEndian(traf.Slice(tp, 4));
            string ty = AsFourcc(traf.Slice(tp + 4, 4));
            if (sz < 8 || tp + sz > traf.Length) break;
            if (ty == "trun")
            {
                ParseTrun(traf.Slice(tp + 8, sz - 8), baseDataOffset, defDur, defSize, defFlags,
                    lengthSize, tsInv, samples, ref cumulativeDt, editMediaTimeOffset);
            }
            tp += sz;
        }
    }

    private static void ParseTrun(ReadOnlySpan<byte> trun, long baseDataOffset,
        uint defDur, uint defSize, uint defFlags,
        int lengthSize, double tsInv,
        List<Mp4Sample> samples, ref long cumulativeDt, long editMediaTimeOffset)
    {
        if (trun.Length < 8) return;
        byte version = trun[0];
        uint flags = (uint)((trun[1] << 16) | (trun[2] << 8) | trun[3]);
        int sampleCount = BinaryPrimitives.ReadInt32BigEndian(trun.Slice(4, 4));
        int o = 8;

        int dataOffset = 0;
        if ((flags & 0x000001) != 0)
        {
            dataOffset = BinaryPrimitives.ReadInt32BigEndian(trun.Slice(o, 4));
            o += 4;
        }
        uint firstSampleFlags = defFlags;
        bool hasFirstSampleFlags = (flags & 0x000004) != 0;
        if (hasFirstSampleFlags)
        {
            firstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(trun.Slice(o, 4));
            o += 4;
        }
        bool hasDur = (flags & 0x000100) != 0;
        bool hasSize = (flags & 0x000200) != 0;
        bool hasFlags = (flags & 0x000400) != 0;
        bool hasCtts = (flags & 0x000800) != 0;

        long runOffset = baseDataOffset + dataOffset;
        for (int i = 0; i < sampleCount; i++)
        {
            uint dur = hasDur ? BinaryPrimitives.ReadUInt32BigEndian(trun.Slice(o, 4)) : defDur; if (hasDur) o += 4;
            uint size = hasSize ? BinaryPrimitives.ReadUInt32BigEndian(trun.Slice(o, 4)) : defSize; if (hasSize) o += 4;
            uint sflags = hasFlags ? BinaryPrimitives.ReadUInt32BigEndian(trun.Slice(o, 4))
                : (i == 0 ? firstSampleFlags : defFlags);
            if (hasFlags) o += 4;
            int ctts = 0;
            if (hasCtts)
            {
                // Version 1 -> signed; version 0 -> unsigned (cast to int, may saturate but rare in practice).
                if (version == 1) ctts = BinaryPrimitives.ReadInt32BigEndian(trun.Slice(o, 4));
                else ctts = (int)BinaryPrimitives.ReadUInt32BigEndian(trun.Slice(o, 4));
                o += 4;
            }

            // sample_is_non_sync_sample lives in bit 16 of the lower half (= 0x00010000).
            // Equivalently sample_depends_on==2 ("does not depend") marks a sync sample.
            bool isSync = (sflags & 0x00010000) == 0;
            int sampleDependsOn = (int)((sflags >> 24) & 0x3);
            if (sampleDependsOn == 2) isSync = true;

            double dtSec = (cumulativeDt - editMediaTimeOffset) * tsInv;
            double ctSec = (cumulativeDt + ctts - editMediaTimeOffset) * tsInv;
            samples.Add(new Mp4Sample(runOffset, (int)size, dtSec, ctSec, isSync, lengthSize));
            runOffset += size;
            cumulativeDt += dur;
        }
    }
}

/// <summary>
/// MP4 video track parsed into NAL units plus timing metadata: per-sample decode time,
/// composition time, sync-sample flag, plus container-level duration and dimensions.
/// Samples carry only metadata (file offset + size); call <see cref="ResolveNalUnits(int)"/>
/// to lazily read and parse a sample's NAL units from the source stream.
/// </summary>
public sealed class Mp4SampleStream
{
    private readonly Stream _source;

    public IReadOnlyList<NalUnit> AvcCConfigNalUnits { get; }
    public IReadOnlyList<Mp4Sample> Samples { get; }
    public uint Timescale { get; }
    public double DurationSeconds { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>NAL-length prefix size minus 1 (from avcC); resolved length is this+1 bytes.</summary>
    public byte LengthSizeMinusOne { get; }

    public Mp4SampleStream(
        IReadOnlyList<NalUnit> avcConfig, IReadOnlyList<Mp4Sample> samples,
        uint timescale, double durationSeconds, int width, int height,
        byte lengthSizeMinusOne, Stream source)
    {
        AvcCConfigNalUnits = avcConfig;
        Samples = samples;
        Timescale = timescale;
        DurationSeconds = durationSeconds;
        Width = width;
        Height = height;
        LengthSizeMinusOne = lengthSizeMinusOne;
        _source = source;
    }

    /// <summary>Resolve a sample's NAL units by reading the bytes from the source stream.</summary>
    public IReadOnlyList<NalUnit> ResolveNalUnits(int sampleIndex) =>
        Samples[sampleIndex].ResolveNalUnits(_source);
}

/// <summary>
/// One MP4 video sample (typically an access unit / coded picture). Holds only the
/// file offset, size, and timing — NAL units are parsed on demand by
/// <see cref="ResolveNalUnits(Stream)"/> to avoid loading the entire mdat into memory.
/// </summary>
public sealed class Mp4Sample
{
    /// <summary>Absolute byte offset of this sample's data within the source file.</summary>
    public long FileOffset { get; }
    /// <summary>Sample size in bytes.</summary>
    public int Size { get; }
    public double DecodeTimeSeconds { get; }
    public double CompositionTimeSeconds { get; }
    public bool IsSyncSample { get; }

    private readonly int _lengthSize;

    public Mp4Sample(long fileOffset, int size, double decodeTime, double compositionTime, bool isSync, int lengthSize)
    {
        FileOffset = fileOffset;
        Size = size;
        DecodeTimeSeconds = decodeTime;
        CompositionTimeSeconds = compositionTime;
        IsSyncSample = isSync;
        _lengthSize = lengthSize;
    }

    /// <summary>Read this sample's bytes from <paramref name="source"/> and parse AVCC length-prefixed NAL units.</summary>
    public IReadOnlyList<NalUnit> ResolveNalUnits(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek) throw new ArgumentException("source stream must be seekable", nameof(source));
        if (Size <= 0) return Array.Empty<NalUnit>();

        byte[] buffer = new byte[Size];
        source.Position = FileOffset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.Read(buffer, total, buffer.Length - total);
            if (n == 0) throw new InvalidDataException(
                $"MP4: truncated sample at offset {FileOffset} (expected {Size} bytes, got {total})");
            total += n;
        }

        var nals = new List<NalUnit>();
        ReadOnlySpan<byte> sample = buffer;
        int pos = 0;
        while (pos + _lengthSize <= sample.Length)
        {
            int nalLen = Mp4Reader.ReadBE(sample, pos, _lengthSize);
            pos += _lengthSize;
            if (nalLen < 1 || pos + nalLen > sample.Length)
                throw new InvalidDataException($"MP4: NAL length {nalLen} overflows sample at offset {FileOffset}");
            nals.Add(Mp4Reader.BuildNalUnit(sample.Slice(pos, nalLen)));
            pos += nalLen;
        }
        return nals;
    }
}
