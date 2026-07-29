namespace H264Sharp.Decoder.Bitstream;

/// <summary>
/// Exp-Golomb coded syntax elements (spec §9.1).
/// </summary>
public static class ExpGolomb
{
    /// <summary>Unsigned exp-Golomb codeNum: ue(v).</summary>
    public static uint ReadUe(ref BitReader r)
    {
        int leadingZeros = 0;
        while (r.ReadBit() == 0)
        {
            leadingZeros++;
            // Spec §9.1: max representable codeNum is 2^32-2 (31 leading zeros). 32 zeros
            // would need (1<<32)-1+suffix, which C#'s shift masking silently aliases to
            // small values — reject as malformed instead.
            if (leadingZeros > 31)
            {
                throw new InvalidDataException("ue(v) overflow: more than 31 leading zero bits");
            }
        }
        if (leadingZeros == 0)
        {
            return 0;
        }
        uint suffix = r.ReadBits(leadingZeros);
        return (1u << leadingZeros) - 1u + suffix;
    }

    /// <summary>Signed exp-Golomb: se(v).</summary>
    public static int ReadSe(ref BitReader r)
    {
        uint codeNum = ReadUe(ref r);
        // (-1)^(codeNum+1) * ceil(codeNum / 2)
        // codeNum odd  -> positive (codeNum+1)/2
        // codeNum even -> negative -(codeNum/2)
        if ((codeNum & 1) == 1)
        {
            return (int)((codeNum + 1u) >> 1);
        }
        return -(int)(codeNum >> 1);
    }

    /// <summary>Truncated exp-Golomb te(v) with upper bound x (inclusive).</summary>
    public static uint ReadTe(ref BitReader r, uint x)
    {
        if (x == 1)
        {
            // single bit, inverted: read 1 -> 0, read 0 -> 1
            return 1u - r.ReadBit();
        }
        return ReadUe(ref r);
    }
}
