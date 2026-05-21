using H264Decoder.Bitstream;
using H264Decoder.Syntax;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.Syntax;

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
}
