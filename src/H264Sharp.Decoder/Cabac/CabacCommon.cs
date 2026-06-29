namespace H264Sharp.Decoder.Cabac;

/// <summary>Helpers shared between I-slice and P-slice CABAC MB parsers.</summary>
internal static class CabacCommon
{
    /// <summary>mb_qp_delta (ctxIdxOffset=60; signed unary).</summary>
    public static int DecodeMbQpDelta(CabacDecoder cabac, ref int prevNonZeroState)
    {
        int b = cabac.DecodeBin(60 + prevNonZeroState);
        if (b == 0)
        {
            prevNonZeroState = 0;
            return 0;
        }
        int n = 1;
        int next = cabac.DecodeBin(62);
        while (next == 1)
        {
            n++;
            if (n > 60) throw new InvalidDataException("mb_qp_delta unary runaway");
            next = cabac.DecodeBin(63);
        }
        prevNonZeroState = 1;
        // Signed mapping: 0→0, 1→1, 2→-1, 3→2, 4→-2 ...
        return (n & 1) == 1 ? (n + 1) / 2 : -(n / 2);
    }

    public static int Mod52(int v)
    {
        int r = v % 52;
        return r < 0 ? r + 52 : r;
    }
}
