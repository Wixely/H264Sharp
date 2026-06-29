using H264Sharp.Decoder.Cabac;
using H264Sharp.Encoder.Mode;
using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Encoder.Cabac;

/// <summary>CABAC syntax-element encoders for P-slice macroblock layer. Mirrors the
/// decoder's <c>CabacSliceP</c> parse paths but as encode. Currently supports
/// P_Skip, P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, and P_8x8 (with sub_mb_type 0..3)
/// for single-reference (num_ref_idx_l0_active_minus1 == 0) encoding.</summary>
internal static class CabacEncSliceP
{
    /// <summary>Encode mb_skip_flag for a P-slice MB (ctxIdxOffset=11). ctxIdxInc = condA + condB
    /// where condTermFlag for neighbor N = (N is available && N is NOT P_Skip) ? 1 : 0.</summary>
    public static void EncodeMbSkipFlag(CabacEncoder cabac, bool isSkip,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int condA = (leftMb != null && !leftMb.IsSkipped) ? 1 : 0;
        int condB = (topMb != null && !topMb.IsSkipped) ? 1 : 0;
        cabac.EncodeBin(11 + condA + condB, isSkip ? 1 : 0);
    }

    /// <summary>Encode P-slice mb_type for an inter MB (Table 9-37, ctxIdxOffset=14).
    /// rawMbType in 0..3 emits the inter branch (bin0=0). Intra-in-P (rawMbType>=5) uses
    /// the intra-suffix path which is not yet supported on the encode side.</summary>
    public static void EncodeMbTypeP(CabacEncoder cabac, int rawMbType)
    {
        if (rawMbType < 0 || rawMbType > 3)
            throw new NotSupportedException($"CABAC encode: P mb_type {rawMbType} not supported");

        // bin0 (ctx 14, ctxIdxInc=0 fixed for P/SP): 0 = inter branch.
        cabac.EncodeBin(14, 0);

        // Tree per spec Table 9-37 / 9-39 inverse:
        //   "0 0 0" => 0 (P_L0_16x16)
        //   "0 1 1" => 1 (P_L0_L0_16x8)
        //   "0 1 0" => 2 (P_L0_L0_8x16)
        //   "0 0 1" => 3 (P_8x8)
        // bin1 uses ctx 15. bin2: ctx 16 when bin1==0; ctx 17 when bin1==1.
        int b1 = (rawMbType == 1 || rawMbType == 2) ? 1 : 0;
        cabac.EncodeBin(15, b1);
        int b2;
        int b2Ctx;
        if (b1 == 0)
        {
            // rawMbType 0 ⇒ b2=0; rawMbType 3 ⇒ b2=1.
            b2 = rawMbType == 3 ? 1 : 0;
            b2Ctx = 16;
        }
        else
        {
            // rawMbType 1 ⇒ b2=1; rawMbType 2 ⇒ b2=0.
            b2 = rawMbType == 1 ? 1 : 0;
            b2Ctx = 17;
        }
        cabac.EncodeBin(b2Ctx, b2);
    }

    /// <summary>Encode sub_mb_type for one P_8x8 sub-MB (Table 9-38, ctxIdxOffset=21).
    /// Codes 0..3 → "1" / "0 0" / "0 1 1" / "0 1 0" (ctx 21, 22, 23).</summary>
    public static void EncodeSubMbTypeP(CabacEncoder cabac, int subMbType)
    {
        switch (subMbType)
        {
            case 0: // PL0_8x8
                cabac.EncodeBin(21, 1);
                return;
            case 1: // PL0_8x4
                cabac.EncodeBin(21, 0);
                cabac.EncodeBin(22, 0);
                return;
            case 2: // PL0_4x8
                cabac.EncodeBin(21, 0);
                cabac.EncodeBin(22, 1);
                cabac.EncodeBin(23, 1);
                return;
            case 3: // PL0_4x4
                cabac.EncodeBin(21, 0);
                cabac.EncodeBin(22, 1);
                cabac.EncodeBin(23, 0);
                return;
            default:
                throw new NotSupportedException($"CABAC encode: sub_mb_type {subMbType} not supported");
        }
    }

    /// <summary>Encode ref_idx_l0 as unary with ctxIdxOffset=54 and condTermFlag derivation.
    /// bin0 ctxIdxInc = (leftRefIdxGt0 ? 1 : 0) + 2*(topRefIdxGt0 ? 1 : 0); subsequent bins use
    /// inc 4 / 5 per spec Table 9-39.</summary>
    public static void EncodeRefIdxL0(CabacEncoder cabac, int refIdx, int condA, int condB)
    {
        int ctxIdxInc0 = condA + 2 * condB;
        if (refIdx == 0)
        {
            cabac.EncodeBin(54 + ctxIdxInc0, 0);
            return;
        }
        cabac.EncodeBin(54 + ctxIdxInc0, 1);
        // Each subsequent 1-bin uses ctx 54+4 (binIdx=1) or 54+5 (binIdx>=2), then terminating 0.
        for (int k = 1; k < refIdx; k++)
        {
            int ctx = (k == 1) ? (54 + 4) : (54 + 5);
            cabac.EncodeBin(ctx, 1);
        }
        // Terminating 0.
        int termCtx = (refIdx == 1) ? (54 + 4) : (54 + 5);
        cabac.EncodeBin(termCtx, 0);
    }

    /// <summary>Encode one mvd component (signed UEG3 binarization, spec §9.3.2.7 / Table 9-39).
    /// ctxBase = 40 for X component, 47 for Y. absMvdSum is the sum of neighbor blocks' |mvd| for
    /// the same component.</summary>
    public static void EncodeMvd(CabacEncoder cabac, int mvdValue, int absMvdSum, int ctxBase)
    {
        int absVal = mvdValue < 0 ? -mvdValue : mvdValue;
        int ctxIdxInc0 = absMvdSum < 3 ? 0 : (absMvdSum < 33 ? 1 : 2);
        if (absVal == 0)
        {
            cabac.EncodeBin(ctxBase + ctxIdxInc0, 0);
            return;
        }
        // bin0 = 1.
        cabac.EncodeBin(ctxBase + ctxIdxInc0, 1);

        // TU prefix continues with binIdx 1..8 (cMax=9 means up to 9 total prefix bins including bin0).
        // The decoder increments absPrefix from 1 up to 9 reading one bin per increment, then either
        // stops on a 0 (no escape — absVal == absPrefix) or proceeds to the EG3 suffix when absPrefix
        // reaches 9 (escape — absVal == 9 + EG3-suffix).
        //   Loop bins use inc = 3 / 4 / 5 / 6 / 6 / 6 / 6 / 6 for binIdx 1..8.
        int prefixCap = 9;
        int absPrefix = Math.Min(absVal, prefixCap);
        // We already emitted bin0=1 (representing absPrefix>=1). Continue from binIdx=1.
        for (int k = 1; k < absPrefix; k++)
        {
            int incK = k == 1 ? 3 : k == 2 ? 4 : k == 3 ? 5 : 6;
            cabac.EncodeBin(ctxBase + incK, 1);
        }
        if (absVal < prefixCap)
        {
            // Terminating 0 at binIdx=absPrefix.
            int incTerm = absPrefix == 1 ? 3 : absPrefix == 2 ? 4 : absPrefix == 3 ? 5 : 6;
            cabac.EncodeBin(ctxBase + incTerm, 0);
        }
        else
        {
            // absVal >= 9: bin0 + 8 ones = 9 prefix bins already emitted. Append EG3 suffix in bypass.
            WriteEGkBypass(cabac, absVal - 9, k: 3);
        }
        // Sign bypass.
        cabac.EncodeBypass(mvdValue < 0 ? 1 : 0);
    }

    /// <summary>Encode coded_block_pattern luma for an inter MB (4 bins, one per 8x8 quadrant, ctx 73+inc).
    /// Inter neighbor rule per spec §9.3.3.1.1.4: P_Skip neighbor cbpLuma=0; unavailable neighbor
    /// uses cbp=0x0F (all bits set → condTerm=0).</summary>
    public static void EncodeCbpLumaInter(CabacEncoder cabac, int cbpLuma,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        for (int i = 0; i < 4; i++)
        {
            int cx = i & 1, cy = i >> 1;
            int condA;
            if (cx > 0)
            {
                int nb = cy * 2 + (cx - 1);
                condA = ((cbpLuma >> nb) & 1) == 0 ? 1 : 0;
            }
            else if (leftMb == null)
            {
                // Unavailable → treat as fully coded (bit=1 → condTerm=0).
                condA = 0;
            }
            else
            {
                int extCbp = leftMb.IsSkipped ? 0 : leftMb.CbpLuma;
                int extBit = (extCbp >> (cy * 2 + 1)) & 1;
                condA = extBit == 0 ? 1 : 0;
            }
            int condB;
            if (cy > 0)
            {
                int nb = (cy - 1) * 2 + cx;
                condB = ((cbpLuma >> nb) & 1) == 0 ? 1 : 0;
            }
            else if (topMb == null)
            {
                condB = 0;
            }
            else
            {
                int extCbp = topMb.IsSkipped ? 0 : topMb.CbpLuma;
                int extBit = (extCbp >> (2 + cx)) & 1;
                condB = extBit == 0 ? 1 : 0;
            }
            int bit = (cbpLuma >> i) & 1;
            cabac.EncodeBin(73 + condA + 2 * condB, bit);
        }
    }

    /// <summary>Encode coded_block_pattern chroma for an inter MB (TU cMax=2). Same neighbor
    /// derivation as intra except skip neighbors treated as cbpChroma=0.</summary>
    public static void EncodeCbpChromaInter(CabacEncoder cabac, int cbpChroma,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int condA0 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma != 0) ? 1 : 0;
        int condB0 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma != 0) ? 1 : 0;
        cabac.EncodeBin(77 + condA0 + 2 * condB0, cbpChroma > 0 ? 1 : 0);
        if (cbpChroma == 0) return;
        int condA1 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma == 2) ? 1 : 0;
        int condB1 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma == 2) ? 1 : 0;
        cabac.EncodeBin(81 + condA1 + 2 * condB1, cbpChroma == 2 ? 1 : 0);
    }

    /// <summary>Inverse of decoder's <c>ReadEGkBypass</c>: encode a non-negative value in EGk bypass mode.</summary>
    private static void WriteEGkBypass(CabacEncoder cabac, int value, int k)
    {
        // value = ((1<<leadingOnes)-1)<<k + suffix, suffix in [0, (1<<(leadingOnes+k)) - 1].
        // Find smallest leadingOnes such that the next base exceeds value.
        int leadingOnes = 0;
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
