using H264Sharp.Decoder;
using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Picture;
using H264Sharp.Tests.Fixtures;

namespace H264Sharp.Tests.Bitstream;

public sealed class AvccReaderTests
{
    [Fact]
    public void SplitNalUnits_BasicTwoNalRoundTrip()
    {
        // length(4)=2, NAL header=0x67 SPS, payload byte 0xAA -> two-byte NAL
        // length(4)=2, NAL header=0x68 PPS, payload byte 0xBB
        byte[] stream =
        [
            0x00, 0x00, 0x00, 0x02, 0x67, 0xAA,
            0x00, 0x00, 0x00, 0x02, 0x68, 0xBB,
        ];
        List<NalUnit> nals = AvccReader.SplitNalUnits(stream);
        Assert.Equal(2, nals.Count);
        Assert.Equal(NalUnitType.Sps, nals[0].NalUnitType);
        Assert.Equal(new byte[] { 0xAA }, nals[0].Rbsp.ToArray());
        Assert.Equal(NalUnitType.Pps, nals[1].NalUnitType);
        Assert.Equal(new byte[] { 0xBB }, nals[1].Rbsp.ToArray());
    }

    [Fact]
    public void SplitNalUnits_ThrowsOnLengthOverflow()
    {
        byte[] bad = [0x00, 0x00, 0x00, 0x10, 0x67]; // claims 16-byte NAL but only 1 byte follows
        Assert.Throws<InvalidDataException>(() => AvccReader.SplitNalUnits(bad));
    }

    [Fact]
    public void DecoderAcceptsAvccFraming_BitExactWithAnnexB()
    {
        // Take the existing Annex-B sample, repackage as AVCC, decode both, compare.
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] annexB = File.ReadAllBytes(sample.H264Path);
        byte[] avcc = AnnexBToAvcc(annexB);

        var dec1 = new H264FrameDecoder();
        DecodedPicture fromAnnexB = dec1.DecodeFirstIFrame(annexB);

        var dec2 = new H264FrameDecoder();
        DecodedPicture fromAvcc = dec2.DecodeFirstIFrame(avcc);

        Assert.Equal(fromAnnexB.Y, fromAvcc.Y);
        Assert.Equal(fromAnnexB.U, fromAvcc.U);
        Assert.Equal(fromAnnexB.V, fromAvcc.V);
    }

    /// <summary>Convert an Annex-B byte stream to AVCC format with 4-byte big-endian length prefixes.</summary>
    private static byte[] AnnexBToAvcc(byte[] annexB)
    {
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(annexB);
        // Reconstruct the (header + RBSP) bytes for each NAL.
        // RBSP needs emulation-prevention bytes re-inserted for AVCC payload to be a
        // valid NAL unit on the wire. But since AVCC is length-prefixed, *no* start
        // code collisions can occur, so the AVCC payload IS just the raw header+RBSP
        // without escape bytes (this is the entire point of AVCC).
        using var ms = new MemoryStream();
        foreach (var n in nals)
        {
            byte header = (byte)(((int)n.NalUnitType & 0x1F) | (n.NalRefIdc << 5));
            int len = 1 + n.Rbsp.Length;
            ms.WriteByte((byte)((len >> 24) & 0xFF));
            ms.WriteByte((byte)((len >> 16) & 0xFF));
            ms.WriteByte((byte)((len >> 8) & 0xFF));
            ms.WriteByte((byte)(len & 0xFF));
            ms.WriteByte(header);
            ms.Write(n.Rbsp.Span);
        }
        return ms.ToArray();
    }
}
