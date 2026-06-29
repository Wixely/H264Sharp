using H264Sharp.Decoder.Cabac;

namespace H264Sharp.Encoder.Cabac;

/// <summary>CABAC residual block encoder. Inverse of <c>CabacResidual.ReadResidualBlock</c>.
/// Mirrors decoder's bin sequence for 4x4 + chroma-DC block categories used by
/// Baseline/Main 4:2:0 8-bit.</summary>
internal static class CabacEncResidual
{
    // Mirror decoder constants.
    public const int CatIntra16x16Dc = 0;
    public const int CatIntra16x16Ac = 1;
    public const int CatLuma4x4 = 2;
    public const int CatChromaDc = 3;
    public const int CatChromaAc = 4;

    private static readonly int[] CbfBase = { 0, 4, 8, 12, 16 };
    private static readonly int[] SigBase = { 0, 15, 29, 44, 47 };
    private static readonly int[] LastBase = { 0, 15, 29, 44, 47 };
    private static readonly int[] AbsBase = { 0, 10, 20, 30, 39 };

    private const int CtxCbfStart = 85;
    private const int CtxSigStart = 105;
    private const int CtxLastStart = 166;
    private const int CtxAbsStart = 227;

    /// <summary>Encode one residual block. <paramref name="coeffs"/> is in scan order
    /// (length == <paramref name="maxNumCoeff"/>). The coded_block_flag bin is emitted
    /// based on whether any coefficient is non-zero.</summary>
    /// <returns>True if any non-zero coefficient was emitted (coded_block_flag=1).</returns>
    public static bool EncodeResidualBlock(
        CabacEncoder cabac,
        ReadOnlySpan<int> coeffs,
        int maxNumCoeff,
        int ctxBlockCat,
        int condTermFlagA,
        int condTermFlagB)
    {
        // Find any non-zero and the last non-zero position.
        int lastNz = -1;
        for (int i = 0; i < maxNumCoeff; i++) if (coeffs[i] != 0) { lastNz = i; }
        bool hasAny = lastNz >= 0;

        int cbfCtx = CtxCbfStart + CbfBase[ctxBlockCat] + condTermFlagA + 2 * condTermFlagB;
        cabac.EncodeBin(cbfCtx, hasAny ? 1 : 0);
        if (!hasAny) return false;

        // significant_coeff_flag / last_significant_coeff_flag along scan positions 0..maxNumCoeff-2.
        int sigBase = CtxSigStart + SigBase[ctxBlockCat];
        int lastBase = CtxLastStart + LastBase[ctxBlockCat];
        for (int i = 0; i < maxNumCoeff - 1; i++)
        {
            int isSig = coeffs[i] != 0 ? 1 : 0;
            cabac.EncodeBin(sigBase + i, isSig);
            if (isSig == 1)
            {
                int isLast = (i == lastNz) ? 1 : 0;
                cabac.EncodeBin(lastBase + i, isLast);
                if (isLast == 1) break;
            }
        }
        // If lastNz == maxNumCoeff-1, no last-bin emitted (last position is implicit).

        // Reverse-scan: emit abs_level (UEGk k=0, uCoff=14) + sign for each non-zero.
        int numDecodAbsLevelEq1 = 0;
        int numDecodAbsLevelGt1 = 0;
        int absBase = CtxAbsStart + AbsBase[ctxBlockCat];
        for (int i = lastNz; i >= 0; i--)
        {
            int c = coeffs[i];
            if (c == 0) continue;
            int absVal = c < 0 ? -c : c;
            int absLevelMinus1 = absVal - 1;

            int ctxIdxInc0 = (numDecodAbsLevelGt1 != 0)
                ? 0
                : Math.Min(4, 1 + numDecodAbsLevelEq1);
            int b0 = absLevelMinus1 == 0 ? 0 : 1;
            cabac.EncodeBin(absBase + ctxIdxInc0, b0);

            if (b0 == 0)
            {
                numDecodAbsLevelEq1++;
            }
            else
            {
                int cap = (ctxBlockCat == CatChromaDc) ? 3 : 4;
                int ctxIdxIncK = 5 + Math.Min(cap, numDecodAbsLevelGt1);
                int ctxIdxK = absBase + ctxIdxIncK;

                // Truncated unary up to 13 additional 1-bits or escape into EGk.
                int extra = absLevelMinus1; // total prefix bins including the implicit '1' bin0
                if (extra < 14)
                {
                    // Emit (extra-1) additional '1' bins (bin0 already coded), then a '0'.
                    for (int k = 1; k < extra; k++)
                    {
                        cabac.EncodeBin(ctxIdxK, 1);
                    }
                    cabac.EncodeBin(ctxIdxK, 0);
                }
                else
                {
                    // Emit 13 more '1' bins (total 14 including bin0), then EG0 suffix in bypass.
                    for (int k = 1; k < 14; k++) cabac.EncodeBin(ctxIdxK, 1);
                    WriteExpGolombBypass(cabac, absLevelMinus1 - 14, k: 0);
                }
                numDecodAbsLevelGt1++;
            }

            int sign = c < 0 ? 1 : 0;
            cabac.EncodeBypass(sign);
        }
        return true;
    }

    /// <summary>Encode an EGk value in bypass mode (spec §9.3.3.2.3). Emit leading 1-bits
    /// terminated by 0, then a (leadingOnes + k)-bit suffix.</summary>
    private static void WriteExpGolombBypass(CabacEncoder cabac, int value, int k)
    {
        // Find leadingOnes such that value >= ((1 << leadingOnes) - 1) << k and
        // value < ((1 << (leadingOnes+1)) - 1) << k.
        int leadingOnes = 0;
        long thresh = (1L << k) - (1L << k); // 0
        long nextThresh = ((1L << 1) - 1) << k; // (1<<1 -1)*(1<<k) = 1<<k
        while (value >= nextThresh + thresh)
        {
            thresh = ((1L << (leadingOnes + 1)) - 1) << k;
            leadingOnes++;
            nextThresh = ((1L << (leadingOnes + 1)) - 1) << k;
        }
        // Above loop is awkward — use the inverse of the decoder's read:
        //   value = ((1<<leadingOnes)-1)<<k + suffix, where suffix in [0, (1<<(leadingOnes+k)) - 1].
        // Find smallest leadingOnes such that ((1<<leadingOnes)-1)<<k <= value.
        // But for k=0: ((1<<L)-1) <= value → L = floor(log2(value+1)).
        // For general k, value/k determines it. Let's compute directly.
        leadingOnes = 0;
        long baseVal = 0;
        while (true)
        {
            long nextBase = ((1L << (leadingOnes + 1)) - 1) << k;
            if (nextBase > value) break;
            baseVal = nextBase;
            leadingOnes++;
        }
        int suffix = (int)(value - baseVal);
        int suffixBits = leadingOnes + k;
        for (int i = 0; i < leadingOnes; i++) cabac.EncodeBypass(1);
        cabac.EncodeBypass(0);
        for (int i = suffixBits - 1; i >= 0; i--) cabac.EncodeBypass((suffix >> i) & 1);
    }
}
