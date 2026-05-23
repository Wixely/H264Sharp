using H264Decoder.Bitstream;
using H264Decoder.Cli;
using H264Decoder.Picture;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.Cli;

public class CliCommandsTests
{
    [Theory]
    [InlineData("all", new[] { 0, 1 })]
    [InlineData("0", new[] { 0 })]
    [InlineData("1", new[] { 1 })]
    [InlineData("0-1", new[] { 0, 1 })]
    [InlineData("1-0", new[] { 0, 1 })] // swapped range; auto-corrected
    [InlineData("0,1", new[] { 0, 1 })]
    [InlineData("0,0,1,1", new[] { 0, 1 })] // dedupes
    public void TryParseFrameSpec_AcceptsValidSpecs(string spec, int[] expected)
    {
        Assert.True(Commands.TryParseFrameSpec(spec, totalFrames: 2, out var indices, out _));
        Assert.Equal(expected, indices.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("2")]    // out of range when totalFrames=2
    [InlineData("0-5")]   // upper bound out of range
    [InlineData("1-abc")]
    public void TryParseFrameSpec_RejectsInvalid(string spec)
    {
        Assert.False(Commands.TryParseFrameSpec(spec, totalFrames: 2, out _, out _));
    }

    [Fact]
    public void ExtractFrames_WritesAllFrames()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outDir = Path.Combine(Path.GetTempPath(), $"frames_{Guid.NewGuid():N}");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ExtractFrames(sample.H264Path, outDir, "all", stderr);
            Assert.Equal(0, rc);
            var pngs = Directory.GetFiles(outDir, "frame_*.png");
            Assert.Equal(2, pngs.Length);
            // PNG magic check on each
            foreach (string png in pngs)
            {
                byte[] bytes = File.ReadAllBytes(png);
                Assert.True(bytes.Length > 8);
                Assert.Equal(0x89, bytes[0]);
                Assert.Equal((byte)'P', bytes[1]);
            }
            // Files should be sortable lexically — names use 5-digit zero-padding.
            Assert.Contains(pngs, p => Path.GetFileName(p) == "frame_00000.png");
            Assert.Contains(pngs, p => Path.GetFileName(p) == "frame_00001.png");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractFrames_RangeAndSingle()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outDir = Path.Combine(Path.GetTempPath(), $"frames_range_{Guid.NewGuid():N}");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ExtractFrames(sample.H264Path, outDir, "0-1", stderr);
            Assert.Equal(0, rc);
            Assert.Equal(2, Directory.GetFiles(outDir, "frame_*.png").Length);
            // Single-frame extraction overwrites cleanly into the same dir.
            rc = Commands.ExtractFrames(sample.H264Path, outDir, "1", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(Path.Combine(outDir, "frame_00001.png")));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractFrames_OnAnnexB_FailsWithMessage()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outDir = Path.Combine(Path.GetTempPath(), $"frames_annexb_{Guid.NewGuid():N}");
        var stderr = new StringWriter();
        int rc = Commands.ExtractFrames(sample.H264Path, outDir, "0", stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("MP4", stderr.ToString());
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }

    [Fact]
    public void Mp4Reader_BPyramid_AppliesEditListSoFirstCompositionTimeIsZero()
    {
        // ffmpeg-produced B-pyramid clips carry an edts/elst entry with media_time > 0
        // to shift the displayed timeline back to 0. Without elst handling, the first
        // sample's CompositionTimeSeconds is > 0; with it, it must be ~0.
        var sample = FfmpegFixture.BPyramidMp4();
        using var fs = File.OpenRead(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(fs);

        Assert.True(stream.Samples.Count >= 4);
        // Find sample with smallest composition time — must equal 0 after elst correction.
        double minCt = double.MaxValue;
        foreach (var s in stream.Samples)
            if (s.CompositionTimeSeconds < minCt) minCt = s.CompositionTimeSeconds;
        Assert.InRange(minCt, -1e-6, 1e-6);
    }

    [Fact]
    public void DecodedPicture_DecodeOrderIndex_IsAssignedInOrder()
    {
        // After DecodeAllFrames returns, the POC-sorted output list must carry the original
        // decode-order index on each picture, letting callers map sample-table index → frame.
        var sample = FfmpegFixture.BPyramidMp4();
        using var fs = File.OpenRead(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(fs);
        var nals = new List<NalUnit>(stream.AvcCConfigNalUnits);
        for (int i = 0; i < stream.Samples.Count; i++) nals.AddRange(stream.ResolveNalUnits(i));

        var decoder = new H264FrameDecoder();
        List<DecodedPicture> frames = decoder.DecodeAllFrames(nals);
        // Every decode-order index from 0..N-1 appears exactly once.
        var seen = new HashSet<int>();
        foreach (var f in frames) Assert.True(seen.Add(f.DecodeOrderIndex));
        for (int i = 0; i < frames.Count; i++) Assert.Contains(i, seen);

        // POC-sort: indices are NOT monotonically increasing for B-pyramid content
        // (proves decode order ≠ display order — i.e. the bug fix actually matters).
        bool anyOutOfOrder = false;
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].DecodeOrderIndex < frames[i - 1].DecodeOrderIndex) { anyOutOfOrder = true; break; }
        Assert.True(anyOutOfOrder, "expected B-pyramid clip to have decode/display order mismatch");
    }

    [Fact]
    public void ThumbnailAt_BPyramidMp4_AtZero_PicksDisplayOrderFirstFrame()
    {
        // With the edit-list and decode-order-index fixes, --at 0 must pick the frame
        // with the smallest POC (display-order first frame), which is the IDR.
        var sample = FfmpegFixture.BPyramidMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"bpy_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            // stderr reports sample N; with elst correction sample 0 has composition ≈ 0.
            Assert.Contains("sample 0", stderr.ToString());
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Fact]
    public void DecodeOrderIndex_LookupPicksTheCorrectFrame_OnBPyramid()
    {
        // Direct demonstration of the Commands.cs bug: on a B-pyramid clip the
        // POC-sorted output list's index N differs from the bitstream's Nth decoded picture.
        // Using DecodeOrderIndex to look up the IDR (decode order 0) must return the
        // picture with the smallest POC; the old frames[target-idr] subscript would not.
        var sample = FfmpegFixture.BPyramidMp4();
        using var fs = File.OpenRead(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(fs);
        var nals = new List<NalUnit>(stream.AvcCConfigNalUnits);
        for (int i = 0; i < stream.Samples.Count; i++) nals.AddRange(stream.ResolveNalUnits(i));

        var decoder = new H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(nals);

        // The IDR is decode-order 0 and must also be POC-smallest (display-first).
        var idrPic = frames.First(f => f.DecodeOrderIndex == 0);
        int minPoc = int.MaxValue;
        foreach (var f in frames) if (f.PicOrderCnt < minPoc) minPoc = f.PicOrderCnt;
        Assert.Equal(minPoc, idrPic.PicOrderCnt);
        // frames[0] (smallest POC) IS the IDR. Therefore looking up by subscript with
        // decode index 0 happens to be correct here — but a non-IDR frame proves divergence:
        var lastDecoded = frames.First(f => f.DecodeOrderIndex == frames.Count - 1);
        Assert.NotEqual(lastDecoded, frames[^1]); // last-decoded != last-by-POC for IBBP.
    }

    [Fact]
    public void Info_FromMp4_PrintsDurationResolutionFramesProfile()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int rc = Commands.Info(sample.H264Path, stdout, stderr);

        Assert.Equal(0, rc);
        string s = stdout.ToString();
        Assert.Contains("duration:", s);
        Assert.Contains("resolution: 128x96", s);
        Assert.Contains("frames: 2", s);
        Assert.Contains("profile:", s);
    }

    [Fact]
    public void Info_FromAnnexB_PrintsResolutionAndFrameCount()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int rc = Commands.Info(sample.H264Path, stdout, stderr);

        Assert.Equal(0, rc);
        string s = stdout.ToString();
        Assert.Contains("resolution: 16x16", s);
        Assert.Contains("frames: 1", s);
    }

    [Fact]
    public void ThumbnailAt_FromMp4_WritesPngOfCorrectSize()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            byte[] png = File.ReadAllBytes(outPng);
            // PNG magic: 89 50 4E 47 0D 0A 1A 0A
            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("0.5")]
    [InlineData("1.0")]
    public void ThumbnailAtPercent_FromMp4_WritesPng(string pct)
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, pct, stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            byte[] png = File.ReadAllBytes(outPng);
            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("2")]
    public void ThumbnailAtPercent_RejectsBadInput(string pct)
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_bad_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, pct, stderr);
        Assert.NotEqual(0, rc);
        Assert.False(File.Exists(outPng));
    }

    [Fact]
    public void ThumbnailAtPercent_OnAnnexB_FailsWithMessage()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_annexb_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, "0.5", stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("MP4", stderr.ToString());
        Assert.False(File.Exists(outPng));
    }

    [Fact]
    public void ThumbnailAt_OnAnnexB_FailsWithMessage()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_fail_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0.5", stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("MP4", stderr.ToString());
        Assert.False(File.Exists(outPng));
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("5", 5.0)]
    [InlineData("12.345", 12.345)]
    [InlineData("1:23.5", 83.5)]
    [InlineData("0:00.001", 0.001)]
    public void TryParseTimestamp_AcceptsSecondsAndMmSs(string input, double expected)
    {
        Assert.True(Commands.TryParseTimestamp(input, out double v));
        Assert.Equal(expected, v, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("-1")]
    public void TryParseTimestamp_RejectsBadInput(string input)
    {
        Assert.False(Commands.TryParseTimestamp(input, out _));
    }

    [Fact]
    public void Run_NoArgs_PrintsUsageAndReturnsNonZero()
    {
        var stderr = new StringWriter();
        int rc = Commands.Run(Array.Empty<string>(), new StringWriter(), stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("Usage", stderr.ToString());
    }

    [Fact]
    public void Mp4Reader_ExtractWithTiming_ReturnsSamplesAndDimensions()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        byte[] bytes = File.ReadAllBytes(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(bytes);

        Assert.Equal(2, stream.Samples.Count);
        Assert.Equal(128, stream.Width);
        Assert.Equal(96, stream.Height);
        Assert.True(stream.Timescale > 0);
        // First sample must be a sync sample (IDR).
        Assert.True(stream.Samples[0].IsSyncSample);
        // Composition times monotonically non-decreasing for this no-B-frame fixture.
        Assert.True(stream.Samples[1].CompositionTimeSeconds >= stream.Samples[0].CompositionTimeSeconds);
        // avcC carries at least one SPS and one PPS.
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Sps);
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Pps);
    }

    [Fact]
    public void DecodeFirstIFrameToFile_PreservesLegacyBehavior()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outYuv = Path.Combine(Path.GetTempPath(), $"frame_{Guid.NewGuid():N}.yuv");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.DecodeFirstIFrameToFile(sample.H264Path, outYuv, stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outYuv));
            // YUV 4:2:0: W*H*1.5 bytes
            long expected = 16 * 16 * 3 / 2;
            Assert.Equal(expected, new FileInfo(outYuv).Length);
        }
        finally
        {
            if (File.Exists(outYuv)) File.Delete(outYuv);
        }
    }

    [Fact]
    public void Mp4Reader_Stream_MatchesSpanOverload_OnSmallFile()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        byte[] bytes = File.ReadAllBytes(sample.H264Path);

        var spanStream = Mp4Reader.ExtractH264WithTiming(bytes);
        using var fs = File.OpenRead(sample.H264Path);
        var fsStream = Mp4Reader.ExtractH264WithTiming(fs);

        Assert.Equal(spanStream.Samples.Count, fsStream.Samples.Count);
        Assert.Equal(spanStream.Width, fsStream.Width);
        Assert.Equal(spanStream.Height, fsStream.Height);
        Assert.Equal(spanStream.Timescale, fsStream.Timescale);
        Assert.Equal(spanStream.DurationSeconds, fsStream.DurationSeconds);
        for (int i = 0; i < spanStream.Samples.Count; i++)
        {
            Assert.Equal(spanStream.Samples[i].FileOffset, fsStream.Samples[i].FileOffset);
            Assert.Equal(spanStream.Samples[i].Size, fsStream.Samples[i].Size);
            Assert.Equal(spanStream.Samples[i].IsSyncSample, fsStream.Samples[i].IsSyncSample);
            // NAL resolution through both paths produces equivalent NAL types + payloads.
            var spanNals = spanStream.ResolveNalUnits(i);
            var fsNals = fsStream.ResolveNalUnits(i);
            Assert.Equal(spanNals.Count, fsNals.Count);
            for (int j = 0; j < spanNals.Count; j++)
            {
                Assert.Equal(spanNals[j].NalUnitType, fsNals[j].NalUnitType);
                Assert.True(spanNals[j].Rbsp.Span.SequenceEqual(fsNals[j].Rbsp.Span));
            }
        }
    }

    [Fact]
    public void Commands_ThumbnailAt_WorksThroughStream()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            Assert.True(new FileInfo(outPng).Length > 0);
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Fact]
    public void Mp4Reader_Stream_HandlesLargeOffsets_Co64()
    {
        // Build a synthetic MP4 with a single video sample whose chunk offset is > 2^31.
        // We use a sparse stream wrapper so we never actually allocate 4 GiB of data.
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        byte[] real = File.ReadAllBytes(sample.H264Path);

        // Parse the real file via the span path to recover the existing samples.
        var refStream = Mp4Reader.ExtractH264WithTiming(real);
        Assert.True(refStream.Samples.Count >= 1);

        // Rebuild moov with stco replaced by co64 holding sample 0's chunk offset shifted
        // by +4 GiB. The sparse stream maps reads at the shifted offset back to the real bytes.
        long shift = 1L << 33;
        byte[] mutated = RewriteStcoToCo64WithShift(real, shift, out long origChunkOffset, out int rewriteDelta, out int rewritePos);
        // If moov rewrite happened before the chunk data, the data shifted by rewriteDelta in the mutated buffer.
        long dataPosInMutated = rewritePos < origChunkOffset ? origChunkOffset + rewriteDelta : origChunkOffset;

        using var sparse = new SparseShiftedStream(mutated, dataPosInMutated, origChunkOffset + shift, refStream.Samples[0].Size);
        var streamed = Mp4Reader.ExtractH264WithTiming(sparse);

        Assert.True(streamed.Samples[0].FileOffset >= shift, $"expected offset > 2^31, got {streamed.Samples[0].FileOffset}");
        var nals = streamed.ResolveNalUnits(0);
        Assert.NotEmpty(nals);
        // Compare to span-based reference: same NAL types in same order.
        var refNals = refStream.ResolveNalUnits(0);
        Assert.Equal(refNals.Count, nals.Count);
        for (int i = 0; i < refNals.Count; i++)
            Assert.Equal(refNals[i].NalUnitType, nals[i].NalUnitType);
    }

    // Rebuilds an MP4 byte array, replacing the stco box in the first video trak with a co64
    // whose only entry equals (firstChunkOffset + shift). All other bytes are preserved.
    // The returned array still references the original mdat region; reads at the shifted
    // offset will be served by the sparse stream wrapper.
    private static byte[] RewriteStcoToCo64WithShift(byte[] mp4, long shift, out long origChunkOffset, out int delta, out int rewritePos)
    {
        // Locate stco within the file (linear scan is fine for the small fixture).
        // We rewrite in-place: stco header is "size(4) 'stco' version_flags(4) count(4) entries..."
        // co64 entries are 8 bytes each vs stco's 4 — we must produce a new buffer if sizes differ.
        int stcoStart = FindBoxStart(mp4, "stco");
        if (stcoStart < 0) throw new InvalidOperationException("test fixture has no stco");
        int stcoSize = (int)((uint)mp4[stcoStart] << 24 | (uint)mp4[stcoStart + 1] << 16 | (uint)mp4[stcoStart + 2] << 8 | mp4[stcoStart + 3]);
        int count = (int)((uint)mp4[stcoStart + 12] << 24 | (uint)mp4[stcoStart + 13] << 16 | (uint)mp4[stcoStart + 14] << 8 | mp4[stcoStart + 15]);

        // Read first chunk offset (we shift all of them).
        origChunkOffset = (uint)mp4[stcoStart + 16] << 24 | (uint)mp4[stcoStart + 17] << 16 | (uint)mp4[stcoStart + 18] << 8 | mp4[stcoStart + 19];

        int newStcoSize = 16 + count * 8;
        delta = newStcoSize - stcoSize;
        rewritePos = stcoStart;
        byte[] outBuf = new byte[mp4.Length + delta];
        Buffer.BlockCopy(mp4, 0, outBuf, 0, stcoStart);

        // Write new co64 box.
        WriteU32BE(outBuf, stcoStart, (uint)newStcoSize);
        outBuf[stcoStart + 4] = (byte)'c'; outBuf[stcoStart + 5] = (byte)'o';
        outBuf[stcoStart + 6] = (byte)'6'; outBuf[stcoStart + 7] = (byte)'4';
        // version + flags (zero), copied from stco.
        Buffer.BlockCopy(mp4, stcoStart + 8, outBuf, stcoStart + 8, 4);
        WriteU32BE(outBuf, stcoStart + 12, (uint)count);
        for (int i = 0; i < count; i++)
        {
            long off = (uint)mp4[stcoStart + 16 + i * 4] << 24 | (uint)mp4[stcoStart + 17 + i * 4] << 16
                | (uint)mp4[stcoStart + 18 + i * 4] << 8 | mp4[stcoStart + 19 + i * 4];
            WriteU64BE(outBuf, stcoStart + 16 + i * 8, (ulong)(off + shift));
        }

        // Tail.
        Buffer.BlockCopy(mp4, stcoStart + stcoSize, outBuf, stcoStart + newStcoSize, mp4.Length - stcoStart - stcoSize);

        // Patch ancestor box sizes (stbl, minf, mdia, trak, moov) — find each enclosing box and add delta.
        PatchAncestorSizes(outBuf, stcoStart, delta);

        return outBuf;
    }

    private static void PatchAncestorSizes(byte[] buf, int childPos, int delta)
    {
        // Walk the file top-down to find boxes that contain childPos; grow their sizes.
        // Boxes containing childPos: moov > trak > mdia > minf > stbl.
        PatchContainingBoxes(buf, 0, buf.Length, childPos, delta);
    }

    private static void PatchContainingBoxes(byte[] buf, int start, int end, int childPos, int delta)
    {
        int p = start;
        while (p + 8 <= end)
        {
            int sz = (int)((uint)buf[p] << 24 | (uint)buf[p + 1] << 16 | (uint)buf[p + 2] << 8 | buf[p + 3]);
            if (sz < 8 || p + sz > end + delta) break;
            int boxEndOrig = p + sz; // pre-patch end in this region
            if (p < childPos && childPos < boxEndOrig)
            {
                // This box contains the child; grow it and recurse into payload.
                WriteU32BE(buf, p, (uint)(sz + delta));
                PatchContainingBoxes(buf, p + 8, boxEndOrig, childPos, delta);
                return;
            }
            p += sz;
        }
    }

    private static int FindBoxStart(byte[] buf, string fourcc)
    {
        for (int i = 0; i + 8 <= buf.Length; i++)
        {
            if (buf[i + 4] == fourcc[0] && buf[i + 5] == fourcc[1] && buf[i + 6] == fourcc[2] && buf[i + 7] == fourcc[3])
                return i;
        }
        return -1;
    }

    private static void WriteU32BE(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v >> 24); buf[pos + 1] = (byte)(v >> 16);
        buf[pos + 2] = (byte)(v >> 8); buf[pos + 3] = (byte)v;
    }

    private static void WriteU64BE(byte[] buf, int pos, ulong v)
    {
        for (int i = 0; i < 8; i++) buf[pos + i] = (byte)(v >> (56 - i * 8));
    }

    // A seekable stream that exposes a "virtual" length larger than 2 GiB by mapping reads
    // at [origOffset+shift .. +size) back to [origOffset .. +size) in the underlying buffer.
    // Reads in [0, mutated.Length) come from the mutated buffer directly.
    private sealed class SparseShiftedStream : Stream
    {
        private readonly byte[] _data;
        private readonly long _dataPosInMutated;
        private readonly long _shiftedStart;
        private readonly int _sampleSize;
        private long _position;

        public SparseShiftedStream(byte[] data, long dataPosInMutated, long shiftedStart, int sampleSize)
        {
            _data = data;
            _dataPosInMutated = dataPosInMutated;
            _shiftedStart = shiftedStart;
            _sampleSize = sampleSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _shiftedStart + _sampleSize;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Region A: low addresses — straight from mutated buffer (ftyp/moov/etc., possibly mdat too).
            if (_position < _data.Length)
            {
                int avail = (int)Math.Min(count, _data.Length - _position);
                Buffer.BlockCopy(_data, (int)_position, buffer, offset, avail);
                _position += avail;
                return avail;
            }
            // Region B: shifted sample window — map reads back to the data buffer.
            if (_position >= _shiftedStart && _position < _shiftedStart + _sampleSize)
            {
                int avail = (int)Math.Min(count, _shiftedStart + _sampleSize - _position);
                long srcPos = _dataPosInMutated + (_position - _shiftedStart);
                Buffer.BlockCopy(_data, (int)srcPos, buffer, offset, avail);
                _position += avail;
                return avail;
            }
            return 0;
        }
    }
}
