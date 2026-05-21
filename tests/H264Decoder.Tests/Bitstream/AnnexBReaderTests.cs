using H264Decoder.Bitstream;

namespace H264Decoder.Tests.Bitstream;

public sealed class AnnexBReaderTests
{
    [Fact]
    public void SplitsTwoNalUnitsOnThreeByteStartCodes()
    {
        // header bytes:
        //   0x67 = 0 11 00111 -> nal_ref_idc=3, nal_unit_type=7 (SPS)
        //   0x68 = 0 11 01000 -> nal_ref_idc=3, nal_unit_type=8 (PPS)
        byte[] stream =
        [
            0x00, 0x00, 0x01, 0x67, 0xAA, 0xBB,
            0x00, 0x00, 0x01, 0x68, 0xCC,
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        Assert.Equal(2, nals.Count);

        Assert.Equal(NalUnitType.Sps, nals[0].NalUnitType);
        Assert.Equal(3, nals[0].NalRefIdc);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, nals[0].Rbsp.ToArray());

        Assert.Equal(NalUnitType.Pps, nals[1].NalUnitType);
        Assert.Equal(3, nals[1].NalRefIdc);
        Assert.Equal(new byte[] { 0xCC }, nals[1].Rbsp.ToArray());
    }

    [Fact]
    public void HandlesFourByteStartCodes()
    {
        byte[] stream =
        [
            0x00, 0x00, 0x00, 0x01, 0x65, 0x11, 0x22, // IDR
            0x00, 0x00, 0x00, 0x01, 0x41, 0x33,       // non-IDR
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        Assert.Equal(2, nals.Count);
        Assert.Equal(NalUnitType.SliceIdr, nals[0].NalUnitType);
        Assert.Equal(new byte[] { 0x11, 0x22 }, nals[0].Rbsp.ToArray());
        Assert.Equal(NalUnitType.SliceNonIdr, nals[1].NalUnitType);
    }

    [Fact]
    public void StripsEmulationPreventionBytes()
    {
        byte[] stream =
        [
            0x00, 0x00, 0x01, 0x67,
            0x00, 0x00, 0x03, 0x01,   // 00 00 03 01 -> 00 00 01
            0x00, 0x00, 0x03, 0x00,   // 00 00 03 00 -> 00 00 00
            0xFF,
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        Assert.Single(nals);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF }, nals[0].Rbsp.ToArray());
    }

    [Fact]
    public void DoesNotStripNonEmulationZeroSequences()
    {
        // 00 00 03 followed by byte > 0x03 is not an emulation prevention sequence
        byte[] stream =
        [
            0x00, 0x00, 0x01, 0x67,
            0x00, 0x00, 0x04,
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        Assert.Single(nals);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04 }, nals[0].Rbsp.ToArray());
    }

    [Fact]
    public void IgnoresLeadingBytesBeforeFirstStartCode()
    {
        byte[] stream =
        [
            0xDE, 0xAD,
            0x00, 0x00, 0x01, 0x67, 0xAA,
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        Assert.Single(nals);
        Assert.Equal(NalUnitType.Sps, nals[0].NalUnitType);
        Assert.Equal(new byte[] { 0xAA }, nals[0].Rbsp.ToArray());
    }

    [Fact]
    public void TrimsTrailingZeroFillBetweenNalUnits()
    {
        // Some muxers pad NALs with trailing zeros before the next start code.
        // The trailing zeros are not part of the NAL.
        byte[] stream =
        [
            0x00, 0x00, 0x01, 0x67, 0xAA, 0x00, 0x00,
            0x00, 0x00, 0x01, 0x68, 0xBB,
        ];

        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);
        Assert.Equal(2, nals.Count);
        Assert.Equal(new byte[] { 0xAA }, nals[0].Rbsp.ToArray());
        Assert.Equal(new byte[] { 0xBB }, nals[1].Rbsp.ToArray());
    }

    [Fact]
    public void ThrowsOnForbiddenZeroBitSet()
    {
        byte[] stream = [0x00, 0x00, 0x01, 0x80];
        Assert.Throws<InvalidDataException>(() => AnnexBReader.SplitNalUnits(stream));
    }

    [Fact]
    public void EmptyStreamReturnsNoNals()
    {
        Assert.Empty(AnnexBReader.SplitNalUnits([]));
    }
}
