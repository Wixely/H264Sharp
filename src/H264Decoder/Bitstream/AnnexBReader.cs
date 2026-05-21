namespace H264Decoder.Bitstream;

public static class AnnexBReader
{
    public static List<NalUnit> SplitNalUnits(ReadOnlySpan<byte> stream)
    {
        var results = new List<NalUnit>();

        int i = 0;
        int n = stream.Length;
        int nalStart = -1;

        while (i < n)
        {
            int scLen = StartCodeLengthAt(stream, i);
            if (scLen == 0)
            {
                i++;
                continue;
            }

            if (nalStart >= 0)
            {
                EmitNalUnit(stream.Slice(nalStart, i - nalStart), results);
            }

            i += scLen;
            nalStart = i;
        }

        if (nalStart >= 0 && nalStart < n)
        {
            EmitNalUnit(stream[nalStart..], results);
        }

        return results;
    }

    private static int StartCodeLengthAt(ReadOnlySpan<byte> s, int i)
    {
        int n = s.Length;
        if (i + 3 <= n && s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1)
        {
            return 3;
        }
        if (i + 4 <= n && s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 0 && s[i + 3] == 1)
        {
            return 4;
        }
        return 0;
    }

    private static void EmitNalUnit(ReadOnlySpan<byte> nalWithTrailingZeros, List<NalUnit> sink)
    {
        ReadOnlySpan<byte> nal = TrimTrailingZeroBytes(nalWithTrailingZeros);
        if (nal.IsEmpty)
        {
            return;
        }

        byte header = nal[0];
        byte forbiddenZeroBit = (byte)((header >> 7) & 0x01);
        if (forbiddenZeroBit != 0)
        {
            throw new InvalidDataException("forbidden_zero_bit must be 0 in NAL unit header");
        }

        byte nalRefIdc = (byte)((header >> 5) & 0x03);
        var nalUnitType = (NalUnitType)(header & 0x1F);

        ReadOnlySpan<byte> ebsp = nal[1..];
        byte[] rbsp = StripEmulationPreventionBytes(ebsp);

        sink.Add(new NalUnit(nalRefIdc, nalUnitType, rbsp));
    }

    private static ReadOnlySpan<byte> TrimTrailingZeroBytes(ReadOnlySpan<byte> s)
    {
        int end = s.Length;
        while (end > 0 && s[end - 1] == 0)
        {
            end--;
        }
        return s[..end];
    }

    internal static byte[] StripEmulationPreventionBytes(ReadOnlySpan<byte> ebsp)
    {
        int n = ebsp.Length;
        byte[] tmp = new byte[n];
        int outLen = 0;
        int zeros = 0;

        for (int i = 0; i < n; i++)
        {
            byte b = ebsp[i];
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue;
            }
            tmp[outLen++] = b;
            zeros = (b == 0) ? zeros + 1 : 0;
        }

        if (outLen == tmp.Length)
        {
            return tmp;
        }

        byte[] result = new byte[outLen];
        Array.Copy(tmp, result, outLen);
        return result;
    }
}
