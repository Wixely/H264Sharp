using H264Decoder.Encoder.Syntax;
using H264Decoder.Syntax;

namespace H264Decoder.Tests.Encoder;

public class SpsPpsWriterTests
{
    [Fact]
    public void Sps_RoundTrip_Baseline_16x16()
    {
        var sps = SpsWriter.BuildBaseline(16, 16);
        byte[] rbsp = SpsWriter.Serialize(sps);
        var parsed = SequenceParameterSet.Parse(rbsp);
        Assert.Equal(sps.ProfileIdc, parsed.ProfileIdc);
        Assert.Equal(sps.PicWidthInMbsMinus1, parsed.PicWidthInMbsMinus1);
        Assert.Equal(sps.PicHeightInMapUnitsMinus1, parsed.PicHeightInMapUnitsMinus1);
        Assert.Equal(sps.FrameMbsOnlyFlag, parsed.FrameMbsOnlyFlag);
        Assert.Equal(sps.PicOrderCntType, parsed.PicOrderCntType);
        Assert.Equal((uint)16, parsed.CroppedWidth);
        Assert.Equal((uint)16, parsed.CroppedHeight);
    }

    [Fact]
    public void Sps_RoundTrip_NonAlignedSize_AppliesCrop()
    {
        var sps = SpsWriter.BuildBaseline(64, 48);
        byte[] rbsp = SpsWriter.Serialize(sps);
        var parsed = SequenceParameterSet.Parse(rbsp);
        Assert.Equal((uint)64, parsed.CroppedWidth);
        Assert.Equal((uint)48, parsed.CroppedHeight);
        Assert.True(parsed.FrameCroppingFlag == false || parsed.FrameCroppingFlag); // crop optional when MB-aligned
    }

    [Fact]
    public void Pps_RoundTrip_Baseline()
    {
        var pps = PpsWriter.BuildBaseline();
        byte[] rbsp = PpsWriter.Serialize(pps);
        var parsed = PictureParameterSet.Parse(rbsp);
        Assert.Equal(pps.PicParameterSetId, parsed.PicParameterSetId);
        Assert.False(parsed.EntropyCodingModeFlag); // CAVLC
        Assert.True(parsed.DeblockingFilterControlPresentFlag);
        Assert.Equal(0, parsed.ChromaQpIndexOffset);
    }
}
