using H264Decoder.Bitstream;
using H264Decoder.Encoder.Bitstream;

namespace H264Decoder.Tests.Encoder;

public class AnnexBWriterTests
{
    [Fact]
    public void EmulationPreventionInserts_03_AfterTwoZeros()
    {
        byte[] rbsp = { 0x00, 0x00, 0x00, 0x01, 0x99 };
        byte[] ebsp = AnnexBWriter.InsertEmulationPreventionBytes(rbsp);
        // After two consecutive zeros, any subsequent byte ≤ 0x03 forces a 03 insertion; the
        // counter then resets. Input 00 00 00 01 99 -> 00 00 03 00 01 99.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x03, 0x00, 0x01, 0x99 }, ebsp);
    }

    [Fact]
    public void EmulationPreventionInserts_03_BeforeStartCodeBytes()
    {
        // 00 00 02 should round-trip cleanly via the decoder's stripper.
        byte[] rbsp = { 0xAA, 0x00, 0x00, 0x02, 0xFF };
        byte[] ebsp = AnnexBWriter.InsertEmulationPreventionBytes(rbsp);
        Assert.Equal(new byte[] { 0xAA, 0x00, 0x00, 0x03, 0x02, 0xFF }, ebsp);
    }

    [Fact]
    public void BuildNalUnit_RoundTrip_ThroughDecoder()
    {
        byte[] rbsp = { 0xAA, 0x00, 0x00, 0x02, 0x55 };
        byte[] nal = AnnexBWriter.BuildNalUnit(NalUnitType.Sps, nalRefIdc: 3, rbsp);
        // First byte is the NAL header.
        Assert.Equal(0x67, nal[0]); // 0x67 = 01100111 = 0 (forbidden) 11 (ref_idc=3) 00111 (type=7=Sps)
        // Re-frame as Annex-B and ask the decoder to split.
        var ms = new MemoryStream();
        AnnexBWriter.WriteAnnexB(ms, new[] { nal });
        var nals = AnnexBReader.SplitNalUnits(ms.ToArray());
        Assert.Single(nals);
        Assert.Equal(NalUnitType.Sps, nals[0].NalUnitType);
        Assert.Equal(rbsp, nals[0].Rbsp.ToArray());
    }
}
