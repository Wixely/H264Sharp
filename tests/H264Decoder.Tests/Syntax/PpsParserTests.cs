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
        // Baseline PPS: High extension absent → default-derived fields.
        Assert.False(pps.Transform8x8ModeFlag);
        Assert.Equal(pps.ChromaQpIndexOffset, pps.SecondChromaQpIndexOffset);
    }

    [Fact]
    public void ParsesHighProfilePpsWithTransform8x8ModeFlag()
    {
        // x264 -profile:v high -x264-params 8x8dct=1 should yield transform_8x8_mode_flag=1.
        var sample = FfmpegFixture.Mandelbrot128x96High8x8Dct();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        NalUnit ppsNal = nals.First(n => n.NalUnitType == NalUnitType.Pps);

        PictureParameterSet pps = PictureParameterSet.Parse(ppsNal.Rbsp.Span);

        Assert.True(pps.Transform8x8ModeFlag);
    }
}
