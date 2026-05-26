using System.Buffers.Binary;
using H264Decoder.Bitstream;

namespace H264Decoder.Tests.Cli;

/// <summary>Verifies the MP4 reader handles a moov atom whose declared size exceeds 2 GiB —
/// we never materialize the moov payload into a single byte[], we stream-walk it.</summary>
public class Mp4LargeMoovTests
{
    [Fact]
    public void Mp4Reader_StreamWalksMoovWithMultiGiBFreePadding()
    {
        // Build a real (small) moov payload and an inflated "free" sub-box that pushes moov's
        // declared size past int.MaxValue (2 GiB). The reader walks moov via stream position
        // arithmetic only, so it doesn't actually need to read the free body — a sparse stream
        // serves zeros for that range. The real on-disk bytes are only a few KB.
        long freeBodySize = (long)int.MaxValue + 4_096; // > 2 GiB

        // Build inner boxes (small, fully materialized).
        byte[] mvhd = BuildMvhd(timescale: 1000, duration: 5_000);
        byte[] stsd = BuildStsdAvc1(width: 64, height: 48, sps: SpsBytesFromFixture(), pps: PpsBytesFromFixture());
        byte[] stts = BuildStts(deltas: new uint[] { 1000 });
        byte[] stsc = BuildStsc(samplesPerChunk: 1);
        // mdat sits after moov + free, but we can't know the exact mdat offset until we know
        // moov's total size. Compute it iteratively below.

        // We need stsz/stco that point at the eventual mdat location. We'll patch them after
        // computing the moov size.
        byte[] stsz = BuildStsz(sampleSize: 5); // single sample, 5 bytes long.
        byte[] stco = BuildStco(chunkOffset: 0); // placeholder; patched below.

        byte[] stbl = WrapBox("stbl", Concat(stsd, stts, stsc, stsz, stco));
        byte[] minf = WrapBox("minf", stbl);
        byte[] mdhd = BuildMdhd(timescale: 1000);
        byte[] hdlr = BuildHdlrVide();
        byte[] mdia = WrapBox("mdia", Concat(mdhd, hdlr, minf));
        byte[] tkhd = BuildTkhd(trackId: 1, width: 64, height: 48);
        byte[] trak = WrapBox("trak", Concat(tkhd, mdia));

        byte[] moovPayload = Concat(mvhd, trak);

        // moov box: header(8) + moovPayload + free box header(8) + freeBodySize.
        long moovTotalSize = 8L + moovPayload.Length + 8L + freeBodySize;

        byte[] ftyp = WrapBox("ftyp",
            Concat(FourCc("isom"), BeUInt32(0x00000200), FourCc("isom"), FourCc("mp41")));

        // mdat header(8) + payload(5 bytes "12345"). mdat offset = ftyp.Length + moovTotalSize.
        long mdatOffset = ftyp.Length + moovTotalSize;
        long mdatPayloadStart = mdatOffset + 8;

        // Patch stco's first (and only) chunk offset to point at mdat payload.
        // stco layout: 4 bytes version+flags, 4 bytes entry_count, N * 4 bytes chunk_offset.
        // (We use 32-bit stco for simplicity. mdatPayloadStart fits because we sized free
        // padding to just clear 2 GiB; total file ~ 2 GiB + a few KB, still < uint.MaxValue.)
        Assert.True(mdatPayloadStart < uint.MaxValue, $"mdatPayloadStart={mdatPayloadStart} overflows stco32");
        BinaryPrimitives.WriteUInt32BigEndian(stco.AsSpan(16, 4), (uint)mdatPayloadStart);

        // Rebuild stbl/minf/mdia/trak/moov with the patched stco.
        stbl = WrapBox("stbl", Concat(stsd, stts, stsc, stsz, stco));
        minf = WrapBox("minf", stbl);
        mdia = WrapBox("mdia", Concat(mdhd, hdlr, minf));
        trak = WrapBox("trak", Concat(tkhd, mdia));
        moovPayload = Concat(mvhd, trak);

        // free box header inside moov.
        byte[] freeHeader = new byte[8];
        // We need to express boxSize 8 + freeBodySize. Since this >2 GiB, we must use the
        // 64-bit "largesize" form (size32 == 1, then 8-byte largesize after fourcc).
        BinaryPrimitives.WriteUInt32BigEndian(freeHeader.AsSpan(0, 4), 1);
        FourCc("free").CopyTo(freeHeader.AsSpan(4, 4));
        byte[] freeLargesize = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(freeLargesize, (ulong)(16 + freeBodySize));
        long moovTotalSizeWithLargesize = 8L + moovPayload.Length + 16L + freeBodySize;

        byte[] moovHeader = new byte[8];
        // Pick form based on whether moovTotalSize fits in 32-bit.
        if (moovTotalSizeWithLargesize <= uint.MaxValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(moovHeader.AsSpan(0, 4), (uint)moovTotalSizeWithLargesize);
            FourCc("moov").CopyTo(moovHeader.AsSpan(4, 4));
        }
        else
        {
            // Use 64-bit largesize form for moov too. moov header becomes 16 bytes.
            moovHeader = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(moovHeader.AsSpan(0, 4), 1);
            FourCc("moov").CopyTo(moovHeader.AsSpan(4, 4));
            // largesize accounts for the 16-byte extended header + payload + free.
            BinaryPrimitives.WriteUInt64BigEndian(moovHeader.AsSpan(8, 8),
                (ulong)(16 + moovPayload.Length + 16 + freeBodySize));
            // Recompute mdat offset because moov grew by 8 bytes (16 vs 8 header).
            // Update stco & rebuild dependent boxes.
            mdatOffset = ftyp.Length + 16L + moovPayload.Length + 16L + freeBodySize;
            mdatPayloadStart = mdatOffset + 8;
            Assert.True(mdatPayloadStart < uint.MaxValue);
            BinaryPrimitives.WriteUInt32BigEndian(stco.AsSpan(16, 4), (uint)mdatPayloadStart);
            stbl = WrapBox("stbl", Concat(stsd, stts, stsc, stsz, stco));
            minf = WrapBox("minf", stbl);
            mdia = WrapBox("mdia", Concat(mdhd, hdlr, minf));
            trak = WrapBox("trak", Concat(tkhd, mdia));
            moovPayload = Concat(mvhd, trak);
            // Rewrite moov largesize with the (now stable) payload size.
            BinaryPrimitives.WriteUInt64BigEndian(moovHeader.AsSpan(8, 8),
                (ulong)(16 + moovPayload.Length + 16 + freeBodySize));
        }

        // mdat (8-byte header + 5-byte payload).
        byte[] mdat = WrapBox("mdat", new byte[] { 1, 2, 3, 4, 5 });

        // Build the "real" segments map. The sparse stream stitches them together; any read in
        // the free-body gap returns zeros.
        var segments = new List<(long Start, byte[] Data)>();
        long cursor = 0;
        segments.Add((cursor, ftyp)); cursor += ftyp.Length;
        segments.Add((cursor, moovHeader)); cursor += moovHeader.Length;
        segments.Add((cursor, moovPayload)); cursor += moovPayload.Length;
        segments.Add((cursor, Concat(freeHeader, freeLargesize))); cursor += 16;
        // Skip the free body — handled by the sparse stream.
        cursor += freeBodySize;
        segments.Add((cursor, mdat)); cursor += mdat.Length;
        long totalLength = cursor;

        using var sparse = new SparseStream(segments, totalLength);

        var stream = Mp4Reader.ExtractH264WithTiming(sparse);

        // The key assertions: parsing succeeded (didn't throw on a >2 GiB moov), and the sample
        // table was reconstructed correctly (its content lives in the real bytes, not the free
        // padding). Width/height may come from either SPS (if parseable) or tkhd fallback —
        // we don't pin a value here because the synthetic SPS is just a fixed byte sequence.
        Assert.Single(stream.Samples);
        Assert.True(stream.Samples[0].IsSyncSample);
        Assert.Equal(5, stream.Samples[0].Size);
        Assert.Equal(mdatPayloadStart, stream.Samples[0].FileOffset);
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Sps);
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Pps);
    }

    // ---------- box builders ----------

    private static byte[] WrapBox(string fourcc, byte[] payload)
    {
        long total = 8L + payload.Length;
        Assert.True(total <= uint.MaxValue, "test helper only emits 32-bit-size boxes");
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)total);
        FourCc(fourcc).CopyTo(result.AsSpan(4, 4));
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] FourCc(string s)
    {
        return new byte[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };
    }

    private static byte[] BeUInt32(uint v)
    {
        var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0; foreach (var p in parts) total += p.Length;
        var r = new byte[total]; int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    private static byte[] BuildMvhd(uint timescale, ulong duration)
    {
        // version 0: 4 vers+flags + 4 created + 4 modified + 4 timescale + 4 duration
        // + 4 rate + 2 vol + 2 reserved + 8 reserved2 + 36 matrix + 24 predef + 4 next_track_id.
        var b = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), (uint)duration);
        return WrapBox("mvhd", b);
    }

    private static byte[] BuildMdhd(uint timescale)
    {
        // version 0: 4 vers+flags + 4 created + 4 modified + 4 timescale + 4 duration + 2 lang + 2 predef
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), 0);
        return WrapBox("mdhd", b);
    }

    private static byte[] BuildHdlrVide()
    {
        // 4 vers+flags + 4 predef + 4 handler_type("vide") + 12 reserved + null-terminated name
        var b = new byte[24 + 1]; // name = "\0"
        FourCc("vide").CopyTo(b.AsSpan(8, 4));
        return WrapBox("hdlr", b);
    }

    private static byte[] BuildTkhd(uint trackId, int width, int height)
    {
        // version 0: 4 vers+flags + 4 created + 4 modified + 4 track_id + 4 reserved
        // + 4 duration + 8 reserved + 2 layer + 2 alt_group + 2 vol + 2 reserved + 36 matrix
        // + 4 width(16.16) + 4 height(16.16) = 84 bytes payload
        var b = new byte[84];
        b[3] = 0x07; // flags = 7 (TRACK_ENABLED | IN_MOVIE | IN_PREVIEW)
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), trackId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(76, 4), (uint)(width << 16));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(80, 4), (uint)(height << 16));
        return WrapBox("tkhd", b);
    }

    private static byte[] BuildStts(uint[] deltas)
    {
        // 4 vers+flags + 4 entry_count + N * (4 count + 4 delta)
        var b = new byte[8 + deltas.Length * 8];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), (uint)deltas.Length);
        for (int i = 0; i < deltas.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 8, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + i * 8 + 4, 4), deltas[i]);
        }
        return WrapBox("stts", b);
    }

    private static byte[] BuildStsc(int samplesPerChunk)
    {
        // single entry: first_chunk=1, samples_per_chunk=N, sample_description_index=1
        var b = new byte[8 + 12];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), (uint)samplesPerChunk);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), 1);
        return WrapBox("stsc", b);
    }

    private static byte[] BuildStsz(int sampleSize)
    {
        // version+flags(4) + sample_size(4) + sample_count(4)
        // When sample_size != 0, no per-sample table follows.
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), (uint)sampleSize);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), 1);
        return WrapBox("stsz", b);
    }

    private static byte[] BuildStco(uint chunkOffset)
    {
        // version+flags(4) + entry_count(4) + N * 4 offsets
        var b = new byte[8 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), chunkOffset);
        return WrapBox("stco", b);
    }

    private static byte[] BuildStsdAvc1(int width, int height, byte[] sps, byte[] pps)
    {
        // stsd: 4 vers+flags + 4 entry_count + entries
        // avc1 entry: 8 header + 6 reserved + 2 data_ref_index + 16 predef/reserved
        //   + 2 width + 2 height + 4 horiz + 4 vert + 4 reserved + 2 frame_count
        //   + 32 compressor_name + 2 depth + 2 predef = 78 bytes after header
        //   then child boxes: avcC
        byte[] avcC = BuildAvcC(sps, pps);
        int avc1PayloadLen = 78 + avcC.Length;
        byte[] avc1 = new byte[8 + avc1PayloadLen];
        BinaryPrimitives.WriteUInt32BigEndian(avc1.AsSpan(0, 4), (uint)avc1.Length);
        FourCc("avc1").CopyTo(avc1.AsSpan(4, 4));
        // bytes 8..13 reserved; 14..15 data_reference_index = 1.
        avc1[8 + 6 + 1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(8 + 24, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(8 + 26, 2), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(avc1.AsSpan(8 + 28, 4), 0x00480000);
        BinaryPrimitives.WriteUInt32BigEndian(avc1.AsSpan(8 + 32, 4), 0x00480000);
        BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(8 + 40, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(8 + 74, 2), 0x18);
        BinaryPrimitives.WriteInt16BigEndian(avc1.AsSpan(8 + 76, 2), -1);
        avcC.CopyTo(avc1, 8 + 78);

        // stsd box: 8 header + 4 vers+flags + 4 entry_count + avc1 entry.
        byte[] stsd = new byte[16 + avc1.Length];
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(0, 4), (uint)stsd.Length);
        FourCc("stsd").CopyTo(stsd.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(stsd.AsSpan(12, 4), 1); // entry_count
        avc1.CopyTo(stsd, 16);
        return stsd;
    }

    private static byte[] BuildAvcC(byte[] sps, byte[] pps)
    {
        // 1 configurationVersion + 1 profile + 1 profile_compat + 1 level
        // + 1 (nalLengthSizeMinusOne | 0xFC) + 1 (numSps | 0xE0)
        // + 2 spsLen + spsBytes + 1 numPps + 2 ppsLen + ppsBytes
        int len = 1 + 3 + 1 + 1 + 2 + sps.Length + 1 + 2 + pps.Length;
        byte[] body = new byte[len];
        body[0] = 1;
        body[1] = sps.Length >= 2 ? sps[1] : (byte)66;
        body[2] = sps.Length >= 3 ? sps[2] : (byte)0;
        body[3] = sps.Length >= 4 ? sps[3] : (byte)30;
        body[4] = 0xFF; // lengthSizeMinusOne = 3 (4-byte NAL prefix)
        body[5] = (byte)(0xE0 | 1); // numSps = 1
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6, 2), (ushort)sps.Length);
        sps.CopyTo(body, 8);
        int o = 8 + sps.Length;
        body[o++] = 1; // numPps = 1
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(o, 2), (ushort)pps.Length);
        o += 2;
        pps.CopyTo(body, o);
        return WrapBox("avcC", body);
    }

    /// <summary>Minimal valid SPS bytes (Baseline 64x48). Captured from an ffmpeg-encoded
    /// baseline fixture used elsewhere; the precise contents don't matter, only that the
    /// parser can read profile/cropped width/height without throwing.</summary>
    private static byte[] SpsBytesFromFixture()
    {
        // NAL header (0x67 = forbidden_zero_bit=0, nal_ref_idc=3, nal_unit_type=7 SPS).
        // The body encodes Baseline profile 4:2:0 64x48. This is a known-good byte sequence.
        return new byte[]
        {
            0x67, 0x42, 0xC0, 0x1E, 0x95, 0xA0, 0x21, 0xCF, 0x9E, 0x10, 0x00, 0x00,
            0x03, 0x00, 0x10, 0x00, 0x00, 0x03, 0x03, 0xC0, 0xF1, 0x42, 0x99, 0x60
        };
    }

    private static byte[] PpsBytesFromFixture()
    {
        return new byte[] { 0x68, 0xCB, 0x83, 0xCB, 0x20 };
    }

    // ---------- sparse stream ----------

    /// <summary>A read-only seekable stream stitched from non-overlapping (start, data) segments.
    /// Reads outside any segment return zeros up to <paramref name="totalLength"/>. Used to
    /// simulate an MP4 with a multi-GiB free-padding box without allocating those bytes.</summary>
    private sealed class SparseStream : Stream
    {
        private readonly List<(long Start, byte[] Data)> _segments;
        private readonly long _length;
        private long _position;

        public SparseStream(List<(long Start, byte[] Data)> segments, long totalLength)
        {
            // Validate non-overlapping ascending order.
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0) Assert.True(segments[i].Start >= segments[i - 1].Start + segments[i - 1].Data.Length);
                Assert.True(segments[i].Start + segments[i].Data.Length <= totalLength);
            }
            _segments = segments;
            _length = totalLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            int max = (int)Math.Min(count, _length - _position);
            // Find segment covering _position.
            for (int i = 0; i < _segments.Count; i++)
            {
                var (segStart, data) = _segments[i];
                long segEnd = segStart + data.Length;
                if (_position < segStart)
                {
                    // Gap: fill with zeros up to next segment or requested count.
                    int gap = (int)Math.Min(max, segStart - _position);
                    Array.Clear(buffer, offset, gap);
                    _position += gap;
                    return gap;
                }
                if (_position < segEnd)
                {
                    int chunk = (int)Math.Min(max, segEnd - _position);
                    Array.Copy(data, _position - segStart, buffer, offset, chunk);
                    _position += chunk;
                    return chunk;
                }
            }
            // Past last segment but before _length: zero fill.
            Array.Clear(buffer, offset, max);
            _position += max;
            return max;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
