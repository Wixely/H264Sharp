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
                // Spec §9.3.3.1.1.7: cap is Min(4 - (ctxBlockCat==3 ? 1 : 0), numDecodAbsLevelGt1).
                int cap = (ctxBlockCat == CatChromaDc) ? 3 : 4;
                int ctxIdxIncK = 5 + Math.Min(cap, numDecodAbsLevelGt1);
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

    // Spec Table 9-43, ctxBlockCat=5 frame-coded: position-to-ctxIdxInc maps (63 entries each).
    private static readonly byte[] SigMap5Frame = new byte[]
    {
         0,  1,  2,  3,  4,  5,  5,  4,  4,  3,
         3,  4,  4,  4,  5,  5,  4,  4,  4,  4,
         3,  3,  6,  7,  7,  7,  8,  9, 10,  9,
         8,  7,  7,  6, 11, 12, 13, 11,  6,  7,
         8,  9, 14, 10,  9,  8,  6, 11, 12, 13,
        11,  6,  9, 14, 10,  9, 11, 12, 13, 11,
        14, 10, 12,
    };

    // Spec Table 9-43 ctxBlockCat=5 (last_significant_coeff_flag, 8x8 frame-coded).
    // 63 entries for scan positions 0..62 (position 63 is implicit-last and not coded).
    // Uses 9 ctxs (0..8). Matches openh264 g_kuiIdx2CtxLastSignificantCoeffFlag8x8.
    private static readonly byte[] LastMap5Frame = new byte[]
    {
        0, 1, 1, 1, 1, 1, 1, 1,   // pos 0..7
        1, 1, 1, 1, 1, 1, 1, 1,   // pos 8..15
        2, 2, 2, 2, 2, 2, 2, 2,   // pos 16..23
        2, 2, 2, 2, 2, 2, 2, 2,   // pos 24..31
        3, 3, 3, 3, 3, 3, 3, 3,   // pos 32..39
        4, 4, 4, 4, 4, 4, 4, 4,   // pos 40..47
        5, 5, 5, 5, 6, 6, 6, 6,   // pos 48..55
        7, 7, 7, 7, 8, 8, 8,      // pos 56..62
    };

    // Spec Table 9-42 ctxIdx bases for ctxBlockCat=5 (Luma8x8, frame-coded).
    private const int CtxSig5Start = 402;     // significant_coeff_flag base
    private const int CtxLast5Start = 417;    // last_significant_coeff_flag base
    private const int CtxAbs5Start = 426;     // coeff_abs_level_minus1 base

    /// <summary>
    /// Decode one 8x8 luma residual block in CABAC (ctxBlockCat=5). The block has no
    /// coded_block_flag in the bitstream — the caller's CBP bit signals presence. The
    /// 63 scan positions use spec Table 9-43 position-dependent ctxIdxInc maps.
    /// Coefficients are written at scan position (matching the CAVLC convention).
    /// </summary>
    public static void ReadResidualBlock8x8(CabacDecoder cabac, scoped Span<int> coeffs)
    {
        coeffs.Clear();

        // 1) significant_coeff_flag / last_significant_coeff_flag along 63 scan positions.
        Span<bool> sigMap = stackalloc bool[64];
        for (int i = 0; i < 63; i++)
        {
            int sig = cabac.DecodeBin(CtxSig5Start + SigMap5Frame[i]);
            if (sig == 1)
            {
                sigMap[i] = true;
                int last = cabac.DecodeBin(CtxLast5Start + LastMap5Frame[i]);
                if (last == 1)
                {
                    goto DecodeLevels;
                }
            }
        }
        // Position 63 is implicitly significant if we never saw last==1.
        sigMap[63] = true;

        DecodeLevels:

        // 2) Reverse-scan absolute level + sign decode (same UEGk(k=0,uCoff=14) flow as cat 0..4).
        int numDecodAbsLevelEq1 = 0;
        int numDecodAbsLevelGt1 = 0;
        for (int i = 63; i >= 0; i--)
        {
            if (!sigMap[i]) continue;

            int ctxIdxInc0 = (numDecodAbsLevelGt1 != 0)
                ? 0
                : Math.Min(4, 1 + numDecodAbsLevelEq1);
            int b0 = cabac.DecodeBin(CtxAbs5Start + ctxIdxInc0);

            int absLevelMinus1;
            if (b0 == 0)
            {
                absLevelMinus1 = 0;
                numDecodAbsLevelEq1++;
            }
            else
            {
                int ctxIdxIncK = 5 + Math.Min(4, numDecodAbsLevelGt1);
                int ctxIdxK = CtxAbs5Start + ctxIdxIncK;

                int prefixOnes = 1;
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
                    int egValue = ReadExpGolombBypass(cabac, k: 0);
                    absLevelMinus1 = 14 + egValue;
                }
                numDecodAbsLevelGt1++;
            }

            int sign = cabac.DecodeBypass();
            int level = absLevelMinus1 + 1;
            coeffs[i] = sign == 1 ? -level : level;
        }
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
