namespace H264Decoder.Cabac;

/// <summary>
/// CABAC residual_block_cabac (spec §7.3.5.3.2 + §9.3.3.1.1.9) for the 4x4 + chroma-DC
/// block categories used by Baseline/Main profiles (4:2:0, 8-bit). Decodes:
///   - coded_block_flag (returns false if all coefficients are zero)
///   - significant_coeff_flag / last_significant_coeff_flag along the scan
///   - coeff_abs_level_minus1 (UEGk binarization with k=0, uCoff=14) + coeff_sign_flag
///
/// Output coefficients are written into <paramref name="coeffs"/> at the scan position
/// (matching the CAVLC convention so the same reconstructor can consume both paths).
/// </summary>
internal static class CabacResidual
{
    /// <summary>ctxBlockCat values relevant to Baseline/Main 4:2:0.</summary>
    public const int CatIntra16x16Dc = 0;
    public const int CatIntra16x16Ac = 1;
    public const int CatLuma4x4 = 2;
    public const int CatChromaDc = 3;
    public const int CatChromaAc = 4;

    // Spec §9.3.3.1.1.9 ctxIdx offsets per ctxBlockCat (frame-coded, 4:2:0).
    private static readonly int[] CbfBase = { 0, 4, 8, 12, 16 };       // coded_block_flag base offsets
    private static readonly int[] SigBase = { 0, 15, 29, 44, 47 };     // significant_coeff_flag
    private static readonly int[] LastBase = { 0, 15, 29, 44, 47 };    // last_significant_coeff_flag
    private static readonly int[] AbsBase = { 0, 10, 20, 30, 39 };     // coeff_abs_level_minus1

    private const int CtxCbfStart = 85;     // ctxIdx range 85..104
    private const int CtxSigStart = 105;    // 105..165
    private const int CtxLastStart = 166;   // 166..226
    private const int CtxAbsStart = 227;    // 227..275

    /// <summary>
    /// Decode one residual block in CABAC. Returns true if at least one non-zero
    /// coefficient was present (i.e. coded_block_flag == 1).
    /// </summary>
    /// <param name="cabac">Arithmetic decoder.</param>
    /// <param name="coeffs">Output, length == maxNumCoeff; cleared on entry.</param>
    /// <param name="maxNumCoeff">16 (Intra16x16 DC / Luma 4x4), 15 (Intra16x16 AC / chroma AC), or 4 (chroma DC).</param>
    /// <param name="ctxBlockCat">One of the Cat* constants above.</param>
    /// <param name="condTermFlagA">Neighbor-A coded_block_flag (or fallback when unavailable).</param>
    /// <param name="condTermFlagB">Neighbor-B coded_block_flag (or fallback when unavailable).</param>
    public static bool ReadResidualBlock(
        CabacDecoder cabac,
        scoped Span<int> coeffs,
        int maxNumCoeff,
        int ctxBlockCat,
        int condTermFlagA,
        int condTermFlagB)
    {
        coeffs.Clear();

        // 1) coded_block_flag
        int cbfCtx = CtxCbfStart + CbfBase[ctxBlockCat] + condTermFlagA + 2 * condTermFlagB;
        int codedBlockFlag = cabac.DecodeBin(cbfCtx);
        if (codedBlockFlag == 0)
        {
            return false;
        }

        // 2) Walk the scan positions reading significant_coeff_flag / last_significant_coeff_flag.
        // significantCoeffFlag[i] indicates position i is non-zero. lastSignificantCoeffFlag[i]==1
        // means position i is the last non-zero in the block — everything after is zero.
        Span<bool> sigMap = stackalloc bool[16];
        int numCoeff = 0;
        int sigBase = CtxSigStart + SigBase[ctxBlockCat];
        int lastBase = CtxLastStart + LastBase[ctxBlockCat];

        for (int i = 0; i < maxNumCoeff - 1; i++)
        {
            int sig = cabac.DecodeBin(sigBase + i);
            if (sig == 1)
            {
                sigMap[i] = true;
                numCoeff++;
                int last = cabac.DecodeBin(lastBase + i);
                if (last == 1)
                {
                    // Positions i+1..maxNumCoeff-1 are zero by definition.
                    goto DecodeLevels;
                }
            }
        }
        // If we exit the loop without hitting last==1, the final position is implicitly significant.
        sigMap[maxNumCoeff - 1] = true;
        numCoeff++;

        DecodeLevels:

        // 3) Read coefficient absolute levels + signs in REVERSE scan order
        // (from the last non-zero position back to position 0).
        int numDecodAbsLevelEq1 = 0;
        int numDecodAbsLevelGt1 = 0;
        int absBase = CtxAbsStart + AbsBase[ctxBlockCat];

        for (int i = maxNumCoeff - 1; i >= 0; i--)
        {
            if (!sigMap[i]) continue;

            // binIdx 0: ctxIdxInc
            int ctxIdxInc0 = (numDecodAbsLevelGt1 != 0)
                ? 0
                : Math.Min(4, 1 + numDecodAbsLevelEq1);
            int b0 = cabac.DecodeBin(absBase + ctxIdxInc0);

            int absLevelMinus1;
            if (b0 == 0)
            {
                absLevelMinus1 = 0; // coeff_abs_level == 1
                numDecodAbsLevelEq1++;
            }
            else
            {
                // Truncated-unary prefix continuation. ctxIdxInc for binIdx>0 is constant per coeff.
                int ctxIdxIncK = 5 + Math.Min(4, numDecodAbsLevelGt1);
                int ctxIdxK = absBase + ctxIdxIncK;

                int prefixOnes = 1;          // bin0 was already a '1'
                while (prefixOnes < 14)
                {
                    int bk = cabac.DecodeBin(ctxIdxK);
                    if (bk == 0) break;
                    prefixOnes++;
                }

                if (prefixOnes < 14)
                {
                    absLevelMinus1 = prefixOnes;
                }
                else
                {
                    // EG0 suffix in bypass mode.
                    int egValue = ReadExpGolombBypass(cabac, k: 0);
                    absLevelMinus1 = 14 + egValue;
                }
                numDecodAbsLevelGt1++;
            }

            int sign = cabac.DecodeBypass();
            int level = absLevelMinus1 + 1;
            coeffs[i] = sign == 1 ? -level : level;
        }

        _ = numCoeff;
        return true;
    }

    /// <summary>EGk decoder in bypass mode (spec §9.3.3.2.3). Returns the non-negative value.</summary>
    private static int ReadExpGolombBypass(CabacDecoder cabac, int k)
    {
        // Read leading 1's terminated by a 0; each leading 1 doubles the base.
        int leadingOnes = 0;
        while (cabac.DecodeBypass() == 1)
        {
            leadingOnes++;
            if (leadingOnes > 31) throw new InvalidDataException("EGk runaway");
        }
        // value = ((1 << leadingOnes) - 1) << k + suffix
        // where suffix is (leadingOnes + k) bits read MSB-first in bypass.
        int suffixBits = leadingOnes + k;
        int suffix = 0;
        for (int i = 0; i < suffixBits; i++)
        {
            suffix = (suffix << 1) | cabac.DecodeBypass();
        }
        return (((1 << leadingOnes) - 1) << k) + suffix;
    }
}
