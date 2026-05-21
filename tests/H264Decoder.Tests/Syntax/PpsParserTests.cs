using H264Decoder.Bitstream;
using H264Decoder.Syntax;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.Syntax;

public sealed class PpsParserTests
{
    [Fact]
    public void ParsesPpsFromFfmpegBaselineClip()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        NalUnit ppsNal = nals.First(n => n.NalUnitType == NalUnitType.Pps);

        PictureParameterSet pps = PictureParameterSet.Parse(ppsNal.Rbsp.Span);

        Assert.False(pps.EntropyCodingModeFlag); // CAVLC
        Assert.Equal(0u, pps.NumSliceGroupsMinus1);
        // pic_init_qp = 26 + pic_init_qp_minus26; reasonable libx264 default range
        int sliceQpBase = 26 + pps.PicInitQpMinus26;
        Assert.InRange(sliceQpBase, 0, 51);
    }

    [Fact]
    public void ThrowsOnCabacFlag()
    {
        // first ue(v)=pps_id=0 ("1"), second ue(v)=sps_id=0 ("1"), then entropy_coding=1
        // bits: 1 1 1 ... -> first byte 1110_0000 = 0xE0
        byte[] rbsp = [0xE0];
        Assert.Throws<NotSupportedException>(() => PictureParameterSet.Parse(rbsp));
    }
}
