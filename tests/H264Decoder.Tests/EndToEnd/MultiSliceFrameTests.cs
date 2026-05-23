using H264Decoder.Bitstream;
using H264Decoder.Picture;
using H264Decoder.Syntax;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.EndToEnd;

/// <summary>Regression tests for multi-slice frame support. Apple's VideoToolbox encoder
/// (iPhone screen recordings) splits each coded picture into multiple slices using
/// <c>first_mb_in_slice &gt; 0</c> for continuation slices (spec §7.4.1.2 — access unit
/// boundaries). The decoder must recognize first_mb_in_slice == 0 as the start of a new
/// access unit and treat the next slice as a continuation of the same DecodedPicture, not
/// allocate a new picture and overwrite the partial MB array.</summary>
public sealed class MultiSliceFrameTests
{
    [Fact]
    public void MultiSliceFixture_EmitsContinuationSlice()
    {
        // Sanity-check that the ffmpeg fixture really did encode multi-slice frames —
        // i.e. somewhere in the bitstream we find first_mb_in_slice > 0. Without this,
        // the regression test below would silently degrade into a single-slice test.
        var sample = FfmpegFixture.TwoFramesMultiSlice128x96Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var nals = AnnexBReader.SplitNalUnits(stream);
        SequenceParameterSet? sps = null;
        PictureParameterSet? pps = null;
        int continuationSliceCount = 0;
        foreach (var nal in nals)
        {
            if (nal.NalUnitType == NalUnitType.Sps) sps = SequenceParameterSet.Parse(nal.Rbsp.Span);
            else if (nal.NalUnitType == NalUnitType.Pps) pps = PictureParameterSet.Parse(nal.Rbsp.Span);
            else if ((nal.NalUnitType == NalUnitType.SliceIdr || nal.NalUnitType == NalUnitType.SliceNonIdr)
                     && sps is not null && pps is not null)
            {
                var hdr = SliceHeader.Parse(nal.Rbsp.Span, nal, sps, pps);
                if (hdr.FirstMbInSlice > 0) continuationSliceCount++;
            }
        }
        Assert.True(continuationSliceCount > 0,
            "fixture did not produce any continuation slices (first_mb_in_slice > 0)");
    }

    [Fact]
    public void TwoFramesMultiSlice128x96Cavlc_ByteExactWithinTolerance()
    {
        // Each of the 2 frames is split into 2 slices. Before the access-unit boundary
        // fix, slice 1's MBs landed in a fresh DecodedPicture with uninitialized planes
        // where slice 0's MBs should have been — producing scanlines of garbage that
        // matched the iPhone-thumbnail artifact. With the fix all 4 slices contribute
        // to the correct 2 pictures and the YUV is byte-exact (modulo deblock-disabled
        // 0..2 tolerance for inter-MB rounding).
        var sample = FfmpegFixture.TwoFramesMultiSlice128x96Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        Assert.Equal(2, frames.Count);

        byte[] reference = File.ReadAllBytes(sample.YuvPath);
        int yLen = sample.Width * sample.Height;
        int cLen = yLen / 4;
        int frameBytes = yLen + 2 * cLen;

        int worstMaxY = 0;
        for (int f = 0; f < 2; f++)
        {
            var pic = frames[f];
            Assert.Equal(sample.Width, pic.Width);
            Assert.Equal(sample.Height, pic.Height);
            int offset = f * frameBytes;
            int maxY = 0;
            for (int i = 0; i < yLen; i++) maxY = Math.Max(maxY, Math.Abs(pic.Y[i] - reference[offset + i]));
            worstMaxY = Math.Max(worstMaxY, maxY);
        }
        Assert.True(worstMaxY <= 2, $"luma max abs error across multi-slice frames = {worstMaxY}");
    }

    [Fact]
    public void TwoFramesMultiSlice128x96Cavlc_DecodeOrderIndexIsPerPicture()
    {
        // Two coded pictures, regardless of how many slices each one contains, should
        // produce exactly two DecodedPicture outputs with DecodeOrderIndex 0 and 1.
        // (Before the fix each slice produced a separate output, giving 4 entries.)
        var sample = FfmpegFixture.TwoFramesMultiSlice128x96Cavlc();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);

        Assert.Equal(2, frames.Count);
        var indices = frames.Select(f => f.DecodeOrderIndex).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1 }, indices);
    }
}
