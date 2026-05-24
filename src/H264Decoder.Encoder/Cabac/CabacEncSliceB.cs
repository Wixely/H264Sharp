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

    /// <summary>Encode B mb_type for 16x16 codes 0, 1, 2, or 3 (spec Table 9-37, ctxIdxOffset=27).
    /// Bin strings:
    ///   0 (B_Direct_16x16) : "0"
    ///   1 (B_L0_16x16) : "1 0 0"
    ///   2 (B_L1_16x16) : "1 0 1"
    ///   3 (B_Bi_16x16) : "1 1 0 0 0 0"
    /// ctxIdxInc per binIdx:
    ///   binIdx 0 : condA + condB (mbN avail &amp;&amp; !B_Skip &amp;&amp; not B_Direct → 1 else 0)
    ///   binIdx 1 : 3 (ctx 30)
    ///   binIdx 2 : bin1==0 ? 5 (ctx 32) : 4 (ctx 31)
    ///   binIdx 3+: 5 (ctx 32)
    /// </summary>
    public static void EncodeMbTypeB16x16(CabacEncoder cabac, int mbType,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        if (mbType < 0 || mbType > 3)
            throw new NotSupportedException($"CABAC encode: B mb_type {mbType} not supported in Phase 5b/5c (only 0/1/2/3)");

        int condA = NeighborMbTypeFlagB(leftMb);
        int condB = NeighborMbTypeFlagB(topMb);

        if (mbType == 0)
        {
            // B_Direct_16x16: bin0 = 0; no further bins.
            cabac.EncodeBin(27 + condA + condB, 0);
            return;
        }

        // bin0 = 1 (not B_Direct_16x16).
        cabac.EncodeBin(27 + condA + condB, 1);

        if (mbType == 1)
        {
            // "1 0 0"
            cabac.EncodeBin(30, 0); // bin1
            cabac.EncodeBin(32, 0); // bin2 (b1==0 → ctx 32, inc=5)
            return;
        }
        if (mbType == 2)
        {
            // "1 0 1"
            cabac.EncodeBin(30, 0);
            cabac.EncodeBin(32, 1);
            return;
        }
        // mbType == 3: "1 1 0 0 0 0"
        cabac.EncodeBin(30, 1);       // bin1
        cabac.EncodeBin(31, 0);       // bin2 (b1==1 → ctx 31, inc=4)
        cabac.EncodeBin(32, 0);       // bin3 (inc=5)
        cabac.EncodeBin(32, 0);       // bin4
        cabac.EncodeBin(32, 0);       // bin5
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
