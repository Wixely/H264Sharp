using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Tests.Fixtures;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Syntax;

[Trait("Category", "Ffmpeg")]
public sealed class SliceHeaderTests
{
    [Fact]
    public void ParsesIdrSliceHeaderFromFfmpegSample()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        SequenceParameterSet sps = SequenceParameterSet.Parse(
            nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        PictureParameterSet pps = PictureParameterSet.Parse(
            nals.First(n => n.NalUnitType == NalUnitType.Pps).Rbsp.Span);
        NalUnit idr = nals.First(n => n.NalUnitType == NalUnitType.SliceIdr);

        SliceHeader hdr = SliceHeader.Parse(idr.Rbsp.Span, idr, sps, pps);

        Assert.Equal(0u, hdr.FirstMbInSlice);
        Assert.Equal(SliceType.I, hdr.SliceType);
        Assert.True(hdr.IdrPicFlag);
        Assert.Equal(0u, hdr.FrameNum);
        int qp = hdr.SliceQpY(pps);
        Assert.InRange(qp, 0, 51);
    }

    [Fact]
    public void ParsesBSliceHeaderFromFfmpegSample()
    {
        var sample = FfmpegFixture.ThreeFramesBFrames16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        SequenceParameterSet sps = SequenceParameterSet.Parse(
            nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        PictureParameterSet pps = PictureParameterSet.Parse(
            nals.First(n => n.NalUnitType == NalUnitType.Pps).Rbsp.Span);

        // Parse every slice header. Expect at least one B-slice.
        var sliceNals = nals.Where(n =>
            n.NalUnitType == NalUnitType.SliceIdr || n.NalUnitType == NalUnitType.SliceNonIdr).ToList();
        var headers = sliceNals.Select(n => SliceHeader.Parse(n.Rbsp.Span, n, sps, pps)).ToList();

        Assert.Contains(headers, h => h.SliceType == SliceType.B);
        Assert.Contains(headers, h => h.IdrPicFlag);
    }

    [Fact]
    public void BFrameStreamDecodesAllSkipBSlice()
    {
        // Red-only B-frame fixture: every B-MB is B_Skip (matching reference).
        // Stage 2: B_Skip + B_Direct spatial via direct mode now supported (CABAC mb_skip_flag
        // handles routing; non-skip B-MBs would still throw via CabacSliceB stub).
        var sample = FfmpegFixture.ThreeFramesBFrames16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);

        var decoder = new H264Sharp.Decoder.H264FrameDecoder();
        var frames = decoder.DecodeAllFrames(stream);
        Assert.Equal(3, frames.Count);
    }
}
