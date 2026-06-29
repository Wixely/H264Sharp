namespace H264Sharp.Encoder.Bitstream;

/// <summary>Exp-Golomb encoding (spec §9.1). Inverse of ExpGolomb reader in decoder.</summary>
public static class ExpGolombWriter
{
    /// <summary>Unsigned exp-Golomb codeNum: ue(v).</summary>
    public static void WriteUe(BitWriter w, uint codeNum)
    {
        // codeWord = (codeNum + 1) in binary, padded with (prefixZeros) leading zeros.
        uint v = codeNum + 1u;
        int leadingZeros = 0;
        uint tmp = v;
        while (tmp > 1u) { tmp >>= 1; leadingZeros++; }
        for (int i = 0; i < leadingZeros; i++) w.WriteBit(0);
        w.WriteBits(v, leadingZeros + 1);
    }

    /// <summary>Signed exp-Golomb: se(v). Maps positive → odd codeNum, non-positive → even.</summary>
    public static void WriteSe(BitWriter w, int value)
    {
        uint codeNum;
        if (value > 0) codeNum = (uint)(2 * value - 1);
        else codeNum = (uint)(-2 * value);
        WriteUe(w, codeNum);
    }

    /// <summary>Truncated te(v) with upper bound x. For x==1, te(v) is one bit (0→1, 1→0).</summary>
    public static void WriteTe(BitWriter w, uint value, uint x)
    {
        if (x == 1)
        {
            w.WriteBit(value == 0 ? 1u : 0u);
            return;
        }
        WriteUe(w, value);
    }
}
