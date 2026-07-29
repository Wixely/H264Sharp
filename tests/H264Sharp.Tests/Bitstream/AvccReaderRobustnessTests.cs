using H264Sharp.Decoder.Bitstream;

namespace H264Sharp.Tests.Bitstream;

/// <summary>AVCC framing edge cases that need no ffmpeg (so they run in CI): emulation-prevention
/// stripping of length-prefixed NAL payloads, and integer-overflow-safe length validation.</summary>
public sealed class AvccReaderRobustnessTests
{
    [Fact]
    public void SplitNalUnits_StripsEmulationPreventionBytes()
    {
        // AVCC payloads are EBSP (ISO/IEC 14496-15): a length prefix replaces the start code but
        // the NAL still carries emulation-prevention bytes. Payload here is header 0x65 (IDR slice)
        // then RBSP 00 00 00 04, EBSP-escaped to 00 00 03 00 04 (an 03 inserted after the two 00s).
        // len = 1 (header) + 5 (escaped body) = 6.
        byte[] stream =
        [
            0x00, 0x00, 0x00, 0x06,
            0x65, 0x00, 0x00, 0x03, 0x00, 0x04,
        ];

        List<NalUnit> nals = AvccReader.SplitNalUnits(stream);

        Assert.Single(nals);
        Assert.Equal(NalUnitType.SliceIdr, nals[0].NalUnitType);
        // The 0x03 must be stripped, restoring the original RBSP.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x04 }, nals[0].Rbsp.ToArray());
    }

    [Fact]
    public void SplitNalUnits_HugeLengthField_ThrowsInvalidData()
    {
        // A 4-byte length of 0x7FFFFFFF would overflow `pos + len`; the reader must reject it
        // cleanly rather than crash with OutOfMemory / ArgumentOutOfRange.
        byte[] stream = [0x7F, 0xFF, 0xFF, 0xFF, 0x65, 0x00];
        Assert.Throws<InvalidDataException>(() => AvccReader.SplitNalUnits(stream));
    }

    [Fact]
    public void SplitNalUnits_ZeroLength_ThrowsInvalidData()
    {
        byte[] stream = [0x00, 0x00, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => AvccReader.SplitNalUnits(stream));
    }
}
