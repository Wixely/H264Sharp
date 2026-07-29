namespace H264Sharp.Decoder.Bitstream;

/// <summary>
/// Reads AVCC-framed NAL units: each NAL is prefixed by an unsigned big-endian
/// length field. Length size is 1, 2, or 4 bytes (4 is the MP4 default and
/// what `lengthSizeMinusOne` typically encodes as 3). The length prefix replaces
/// the Annex-B start code only — the NAL payload is still EBSP and carries
/// emulation-prevention bytes (ISO/IEC 14496-15 §5.3.4.2).
/// </summary>
public static class AvccReader
{
    public static List<NalUnit> SplitNalUnits(ReadOnlySpan<byte> stream, int lengthSize = 4)
    {
        if (lengthSize != 1 && lengthSize != 2 && lengthSize != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize), "AVCC length size must be 1, 2, or 4");
        }

        var results = new List<NalUnit>();
        int pos = 0;
        while (pos + lengthSize <= stream.Length)
        {
            int len = ReadLen(stream, pos, lengthSize);
            pos += lengthSize;
            // Subtraction form: `pos + len` can overflow int for hostile 4-byte lengths.
            if (len < 1 || len > stream.Length - pos)
            {
                throw new InvalidDataException($"AVCC: NAL length {len} at offset {pos} exceeds stream");
            }

            byte header = stream[pos];
            if ((header & 0x80) != 0)
            {
                throw new InvalidDataException("AVCC: forbidden_zero_bit set in NAL header");
            }
            byte nalRefIdc = (byte)((header >> 5) & 0x03);
            var nalUnitType = (NalUnitType)(header & 0x1F);

            byte[] rbsp = AnnexBReader.StripEmulationPreventionBytes(stream.Slice(pos + 1, len - 1));
            results.Add(new NalUnit(nalRefIdc, nalUnitType, rbsp));

            pos += len;
        }
        if (pos != stream.Length)
        {
            throw new InvalidDataException($"AVCC: trailing {stream.Length - pos} bytes after last NAL");
        }
        return results;
    }

    private static int ReadLen(ReadOnlySpan<byte> s, int pos, int n)
    {
        int v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | s[pos + i];
        return v;
    }
}
