using H264Decoder.Cabac;
using H264Decoder.Encoder.Mode;

namespace H264Decoder.Encoder.Cabac;

/// <summary>
/// CABAC syntax-element encoders for B-slice macroblock layer (spec Table 9-37/9-39).
/// Phase 5b: supports mb_skip_flag and mb_type for 16x16 inter codes 1/2/3 (B_L0_16x16,
/// B_L1_16x16, B_Bi_16x16). B_Direct_16x16, sub-MB partitions (codes 4..21, 22 = B_8x8),
/// and intra-in-B (codes 23..48) are deferred to later phases. mvd / cbp / qp_delta /
/// residual all share contexts with the P-slice encoder (<see cref="CabacEncSliceP"/>).
/// </summary>
internal static class CabacEncSliceB
{
    /// <summary>Encode mb_skip_flag for a B-slice MB (ctxIdxOffset=24). ctxIdxInc = condA + condB
    /// where condTermFlag(N) = (N is available && N is NOT B_Skip).</summary>
    public static void EncodeMbSkipFlagB(CabacEncoder cabac, bool isSkip,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int condA = (leftMb != null && !IsBSkipNeighbor(leftMb)) ? 1 : 0;
        int condB = (topMb != null && !IsBSkipNeighbor(topMb)) ? 1 : 0;
        cabac.EncodeBin(24 + condA + condB, isSkip ? 1 : 0);
    }

    /// <summary>Encode B mb_type for codes 0..21 (Table 9-37, ctxIdxOffset=27). Bin strings per
    /// the decoder's <c>DecodeMbTypeB</c> tree:
    ///   0 (B_Direct_16x16) : "0"
    ///   1 (B_L0_16x16)     : "1 0 0"
    ///   2 (B_L1_16x16)     : "1 0 1"
    ///   3 (B_Bi_16x16)     : "1 1 0 0 0 0"
    ///   4..10              : "1 1 0 . . ." (3-bit suffix b3 b4 b5 = code-3)
    ///   11 (B_L1_L0_8x16)  : "1 1 1 1 1 0"
    ///   12..15 (L0_Bi/L1_Bi/Bi_L0/Bi_L1 + bottom-half ones at 16..19)
    ///   Actually codes 12..19 use prefix "1 1 1 0 ..." with b4 b5 b6 = (code-12)
    ///   20 (B_Bi_Bi_16x8)  : "1 1 1 1 0 0 0"
    ///   21 (B_Bi_Bi_8x16)  : "1 1 1 1 0 0 1"
    /// ctxIdxInc per binIdx:
    ///   binIdx 0 : condA + condB ; binIdx 1 : 3 ; binIdx 2 : bin1==0?5:4 ; binIdx 3+ : 5.
    /// </summary>
    public static void EncodeMbTypeB16x16(CabacEncoder cabac, int mbType,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        if (mbType < 0 || mbType > 21)
            throw new NotSupportedException($"CABAC encode: B mb_type {mbType} not supported (only 0..21)");

        int condA = NeighborMbTypeFlagB(leftMb);
        int condB = NeighborMbTypeFlagB(topMb);

        if (mbType == 0)
        {
            cabac.EncodeBin(27 + condA + condB, 0);
            return;
        }
        // bin0 = 1.
        cabac.EncodeBin(27 + condA + condB, 1);

        if (mbType == 1)
        {
            cabac.EncodeBin(30, 0);
            cabac.EncodeBin(32, 0);
            return;
        }
        if (mbType == 2)
        {
            cabac.EncodeBin(30, 0);
            cabac.EncodeBin(32, 1);
            return;
        }
        // bin1 = 1 from here on.
        cabac.EncodeBin(30, 1);

        if (mbType == 3)
        {
            // "1 1 0 0 0 0"
            cabac.EncodeBin(31, 0);
            cabac.EncodeBin(32, 0);
            cabac.EncodeBin(32, 0);
            cabac.EncodeBin(32, 0);
            return;
        }

        // Codes 4..10: prefix "1 1 0 b3 b4 b5" with idx = code - 3 (range 1..7).
        if (mbType >= 4 && mbType <= 10)
        {
            cabac.EncodeBin(31, 0); // bin2 (b1==1 → ctx 31, inc=4)
            int idx = mbType - 3; // 1..7
            cabac.EncodeBin(32, (idx >> 2) & 1); // bin3
            cabac.EncodeBin(32, (idx >> 1) & 1); // bin4
            cabac.EncodeBin(32, idx & 1);        // bin5
            return;
        }

        // From here bin2 = 1 (decoder branches into the "1 1 1 ..." subtree).
        cabac.EncodeBin(31, 1);

        if (mbType == 11)
        {
            // "1 1 1 1 1 0" — decoder reads b3=1, b4=1, b5=0.
            cabac.EncodeBin(32, 1);
            cabac.EncodeBin(32, 1);
            cabac.EncodeBin(32, 0);
            return;
        }

        // Codes 20, 21: prefix "1 1 1 1 0 0 b6" (b3=1, b4=0, b5=0, b6 = code-20).
        if (mbType == 20 || mbType == 21)
        {
            cabac.EncodeBin(32, 1);
            cabac.EncodeBin(32, 0);
            cabac.EncodeBin(32, 0);
            cabac.EncodeBin(32, mbType == 20 ? 0 : 1);
            return;
        }

        // Codes 12..19: prefix "1 1 1 0 b4 b5 b6" (b3=0, then 3-bit suffix = code-12).
        if (mbType >= 12 && mbType <= 19)
        {
            cabac.EncodeBin(32, 0); // b3
            int idx2 = mbType - 12; // 0..7
            cabac.EncodeBin(32, (idx2 >> 2) & 1); // b4
            cabac.EncodeBin(32, (idx2 >> 1) & 1); // b5
            cabac.EncodeBin(32, idx2 & 1);        // b6
            return;
        }

        throw new InvalidOperationException($"unhandled B mb_type {mbType}");
    }

    /// <summary>Encode the B-slice intra-branch mb_type for an Intra_16x16 MB. Emits the prefix
    /// bins for B mb_type code 23 (1 1 1 1 0 1) followed by the intra body at ctxIdxOffset=32 per
    /// spec Table 9-39 (binIdx 0=0, then +1, +2, +2, +3, +3). <paramref name="iSliceMbType"/> is
    /// the I-slice mb_type value (1..24 for Intra_16x16; I_NxN and I_PCM not yet supported).</summary>
    public static void EncodeMbTypeBIntra16x16(CabacEncoder cabac, int iSliceMbType,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        if (iSliceMbType < 1 || iSliceMbType > 24)
            throw new NotSupportedException(
                $"CABAC encode: B-slice intra I-slice mb_type {iSliceMbType} not supported (only 1..24 / Intra_16x16)");

        int condA = NeighborMbTypeFlagB(leftMb);
        int condB = NeighborMbTypeFlagB(topMb);
        // ---- B mb_type prefix for code 23 (intra branch entry): bins "1 1 1 1 0 1". ----
        cabac.EncodeBin(27 + condA + condB, 1); // bin0
        cabac.EncodeBin(30, 1);                 // bin1
        cabac.EncodeBin(31, 1);                 // bin2 (b1==1 → ctx 31)
        cabac.EncodeBin(32, 1);                 // bin3
        cabac.EncodeBin(32, 0);                 // bin4
        cabac.EncodeBin(32, 1);                 // bin5

        // ---- Intra body at ctxIdxOffset = 32 (Table 9-39). ----
        const int Off = 32;
        cabac.EncodeBin(Off, 1);                // bin0 = 1 (not I_NxN)
        cabac.EncodeTerminate(0);               // terminate = 0 (not I_PCM)

        int m0 = iSliceMbType - 1;
        int p = m0 % 4;
        int g = m0 / 4;
        cabac.EncodeBin(Off + 1, g >= 3 ? 1 : 0); // bin "+12"
        int cbpChroma = g % 3;
        cabac.EncodeBin(Off + 2, cbpChroma > 0 ? 1 : 0); // bin "+8/+4 outer"
        if (cbpChroma > 0)
        {
            cabac.EncodeBin(Off + 2, cbpChroma == 2 ? 1 : 0); // bin "+8 inner" (same ctx as outer per spec)
        }
        cabac.EncodeBin(Off + 3, (p >> 1) & 1); // bin "+2"
        cabac.EncodeBin(Off + 3, p & 1);        // bin "+1"
    }

    /// <summary>Mirror of decoder's <c>NeighborMbTypeFlagB</c> condTermFlag derivation.</summary>
    private static int NeighborMbTypeFlagB(MacroblockEncoderState? mb)
    {
        if (mb == null) return 0;
        if (IsBSkipNeighbor(mb)) return 0;
        // B_Direct_16x16 would be RawMbType==0 with IsBInter; Phase 5b doesn't emit it but
        // keep the check so the rule still holds when 5c adds B_Direct.
        if (mb.IsBInter && mb.RawMbType == 0) return 0;
        return 1;
    }

    private static bool IsBSkipNeighbor(MacroblockEncoderState mb)
    {
        // In Phase 5b we never emit B_Skip; IsSkipped only appears for P-slice neighbors in
        // mixed streams (which we don't generate). When Phase 5c adds B_Skip, the same
        // IsSkipped flag will be reused, so this lookup stays correct.
        return mb.IsSkipped;
    }
}
