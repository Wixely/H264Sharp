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
        if (!TryFindTopBox(mp4, "moov", out int moovStart, out int moovLen))
            throw new InvalidDataException("MP4: no 'moov' box");
        var moov = mp4.Slice(moovStart, moovLen);

        // Find the (first) video track and read its avcC and sample table.
        if (!TryFindVideoTrak(moov, out int trakStart, out int trakLen))
            throw new InvalidDataException("MP4: no video track");
        var trak = moov.Slice(trakStart, trakLen);

        if (!TryFindNestedBox(trak, "mdia", "minf", "stbl", out int stblStart, out int stblLen))
            throw new InvalidDataException("MP4: no stbl");
        var stbl = trak.Slice(stblStart, stblLen);

        var (sps, pps, lengthSize) = ReadAvcConfigFromStbl(stbl);
        if (sps.Count == 0 || pps.Count == 0)
            throw new InvalidDataException("MP4: avcC missing SPS or PPS");

        var sampleOffsets = BuildSampleOffsetTable(stbl);

        var results = new List<NalUnit>(sps.Count + pps.Count + sampleOffsets.Count);
        results.AddRange(sps);
        results.AddRange(pps);
        foreach (var (offset, size) in sampleOffsets)
        {
            if (offset < 0 || (long)offset + size > mp4.Length)
                throw new InvalidDataException($"MP4: sample at {offset}+{size} exceeds file size {mp4.Length}");
            var sample = mp4.Slice(offset, size);
            int pos = 0;
            while (pos + lengthSize <= sample.Length)
            {
                int nalLen = ReadBE(sample, pos, lengthSize);
                pos += lengthSize;
                if (nalLen < 1 || pos + nalLen > sample.Length)
                    throw new InvalidDataException($"MP4: NAL length {nalLen} overflows sample at offset {offset}");
                results.Add(BuildNalUnit(sample.Slice(pos, nalLen)));
                pos += nalLen;
            }
        }
        return results;
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
