using System.Buffers.Binary;

namespace H264Decoder.Bitstream;

/// <summary>
/// Minimal MP4 (ISOBMFF) reader. Walks the atom tree just enough to extract the
/// H.264 elementary stream from an MP4 video track:
///   - SPS + PPS from the avcC (AVCDecoderConfigurationRecord)
///   - All video samples from mdat, sliced per the stbl sample table
///
/// Streams the file: only the moov atom is loaded into memory, so arbitrary-size
/// inputs are supported as long as moov itself fits in a byte[] (~2 GiB).
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
    public static Mp4SampleStream ExtractH264WithTiming(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("MP4: stream must be seekable", nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("MP4: stream must be readable", nameof(stream));

        byte[]? moovBytes = ReadMoovAtom(stream);
        if (moovBytes is null) throw new InvalidDataException("MP4: no 'moov' box");
        return ParseMoov(moovBytes, stream);
    }

    /// <summary>
    /// Scans top-level boxes for 'moov' and returns its payload (post-header).
    /// mdat and other large boxes are skipped without buffering.
    /// </summary>
    private static byte[]? ReadMoovAtom(Stream stream)
    {
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
                // Extended 64-bit size follows the 8-byte header.
                if (ReadExact(stream, header[8..16]) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                payloadStart = pos + 16;
            }
            else if (sz32 == 0)
            {
                // Box extends to end of file.
                boxSize = fileLength - pos;
                payloadStart = pos + 8;
            }
            else
            {
                boxSize = sz32;
                payloadStart = pos + 8;
            }

            if (boxSize < 8 || pos + boxSize > fileLength) break;

            if (ty == "moov")
            {
                long payloadLen = pos + boxSize - payloadStart;
                if (payloadLen > int.MaxValue)
                    throw new InvalidDataException("MP4: moov atom too large to load (>2 GiB)");
                byte[] buf = new byte[(int)payloadLen];
                stream.Position = payloadStart;
                if (ReadExact(stream, buf) != buf.Length)
                    throw new InvalidDataException("MP4: truncated moov atom");
                return buf;
            }
            pos += boxSize;
        }
        return null;
    }

    private static Mp4SampleStream ParseMoov(byte[] moovBytes, Stream source)
    {
        ReadOnlySpan<byte> moov = moovBytes;

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

        uint[] sttsDeltas = ReadStts(stbl, sampleCount);
        int[] cttsOffsets = ReadCtts(stbl, sampleCount);
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
            double dtSec = cumulativeDt * tsInv;
            double ctSec = (cumulativeDt + cttsOffsets[i]) * tsInv;
            samples.Add(new Mp4Sample(offset, size, dtSec, ctSec, isSync[i], lengthSize));
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
