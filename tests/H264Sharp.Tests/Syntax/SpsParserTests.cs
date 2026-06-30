using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Tests.Fixtures;

namespace H264Sharp.Tests.Syntax;

[Trait("Category", "Ffmpeg")]
public sealed class SpsParserTests
{
    [Fact]
    public void ParsesSpsFromFfmpegBaselineClip()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        NalUnit spsNal = nals.First(n => n.NalUnitType == NalUnitType.Sps);

        SequenceParameterSet sps = SequenceParameterSet.Parse(spsNal.Rbsp.Span);

        Assert.Equal(66, sps.ProfileIdc);
        Assert.True(sps.FrameMbsOnlyFlag);
        Assert.Equal(1u, sps.PicWidthInMbs);
        Assert.Equal(1u, sps.PicHeightInMbs);
        Assert.Equal(16u, sps.PicWidthInSamplesL);
        Assert.Equal(16u, sps.PicHeightInSamplesL);
        Assert.Equal((uint)sample.Width, sps.CroppedWidth);
        Assert.Equal((uint)sample.Height, sps.CroppedHeight);
    }

    [Fact]
    public void VuiParsedWhenPresent()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        NalUnit spsNal = nals.First(n => n.NalUnitType == NalUnitType.Sps);

        SequenceParameterSet sps = SequenceParameterSet.Parse(spsNal.Rbsp.Span);

        Assert.True(sps.VuiParametersPresentFlag);
        Assert.NotNull(sps.Vui);
        // libx264 typically signals video_signal_type + colour_description for SD-sized inputs.
        // matrix_coefficients should be a recognised constant (BT.601 or BT.709 family).
        if (sps.Vui!.ColourDescriptionPresentFlag)
        {
            Assert.InRange(sps.Vui.MatrixCoefficients, (byte)0, (byte)11);
        }
    }

    [Fact]
    public void ThrowsOnUnsupportedProfile()
    {
        // profile_idc=50 isn't a real H.264 profile and isn't in our allow-list
        // (Baseline=66, Main=77, Extended=88, High=100 and friends).
        byte[] rbsp = [50, 0, 0, 0, 0];
        Assert.Throws<NotSupportedException>(() => SequenceParameterSet.Parse(rbsp));
    }
}
