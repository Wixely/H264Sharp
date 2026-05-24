using H264Decoder.Cabac;
using H264Decoder.Encoder.Mode;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Cabac;

/// <summary>CABAC syntax-element encoders for I-slice macroblock layer. Mirrors the
/// decoder's <c>CabacSliceI</c> parse paths but as encode. Currently supports
/// Intra_16x16 (the I-slice production path in this encoder); Intra_4x4 (I_NxN) is
/// also supported for the bin-level mb_type/pred-mode/CBP path so the combined
/// Intra_4x4 + CABAC pipeline works.</summary>
internal static class CabacEncSlice
{
    /// <summary>Encode I-slice mb_type (Table 9-37, ctxIdxOffset=3). Inverse of
    /// <c>CabacSliceI.DecodeMbTypeI</c>. Supports values 0 (I_NxN) and 1..24 (Intra_16x16).</summary>
    public static void EncodeMbTypeI(CabacEncoder cabac, int mbType,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int condA = (leftMb != null && IsNonINxNIntra(leftMb)) ? 1 : 0;
        int condB = (topMb != null && IsNonINxNIntra(topMb)) ? 1 : 0;
        if (mbType == 0)
        {
            // I_NxN: just bin0=0.
            cabac.EncodeBin(3 + condA + condB, 0);
            return;
        }
        // bin0 = 1 (not I_NxN).
        cabac.EncodeBin(3 + condA + condB, 1);
        // Terminate bin = 0 (not I_PCM).
        cabac.EncodeTerminate(0);

        // bins encoded:
        //   bin1: (mbType > 12 ? 1 : 0) using ctx 6.
        //   bin2: ((mbType-1)/4 mod 6) > 0? actually let's reconstruct directly from decoder logic.
        // Decoder maps:
        //   mbType = 1; if bin6=1, +=12; if bin7=1, if bin8=1, +=8 else +=4; if bin9=1, +=2; if bin10=1, +=1.
        // So inverse: bin6 = ((mbType-1) >= 12) ? 1 : 0; let m=mbType-1, p=m%4 (prediction mode), g=m/4 (group).
        //   Groups are: 0,1,2 = cbpLuma=0 (cbpChroma 0/1/2), 3,4,5 = cbpLuma=15 (cbpChroma 0/1/2).
        //   bin6 (ctx 6) = (g >= 3) ? 1 : 0.
        //   bin7 (ctx 7) = (chromaBlock of group != 0) — bin7 high if cbpChroma > 0.
        //     i.e. for g in {1,2,4,5} bin7=1, else 0.
        //   bin8 (ctx 8) = (cbpChroma == 2) ? 1 : 0 (only emitted when bin7 == 1).
        //   bin9 (ctx 9) = ((p >> 1) & 1) — pred mode high bit.
        //   bin10 (ctx 10) = (p & 1) — pred mode low bit.
        int m0 = mbType - 1;
        int p = m0 % 4;
        int g = m0 / 4;

        int b6 = g >= 3 ? 1 : 0;
        cabac.EncodeBin(6, b6);

        // cbpChroma: 0, 1, or 2. Map (g) → cbpChroma: g%3 (groups 0/3→0, 1/4→1, 2/5→2).
        int cbpChroma = g % 3;
        int b7 = cbpChroma > 0 ? 1 : 0;
        cabac.EncodeBin(7, b7);
        if (b7 == 1)
        {
            int b8 = cbpChroma == 2 ? 1 : 0;
            cabac.EncodeBin(8, b8);
        }
        // Pred mode high bit, then low bit.
        cabac.EncodeBin(9, (p >> 1) & 1);
        cabac.EncodeBin(10, p & 1);
    }

    /// <summary>Encode intra_chroma_pred_mode (TU max=3, ctxIdxOffset=64).</summary>
    public static void EncodeIntraChromaPredMode(CabacEncoder cabac, int chromaMode,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int condA = (leftMb != null && IsIntraNonPcm(leftMb)
                     && leftMb.ChromaPredMode != IntraChromaPredMode.Dc) ? 1 : 0;
        int condB = (topMb != null && IsIntraNonPcm(topMb)
                     && topMb.ChromaPredMode != IntraChromaPredMode.Dc) ? 1 : 0;
        if (chromaMode == 0)
        {
            cabac.EncodeBin(64 + condA + condB, 0);
            return;
        }
        cabac.EncodeBin(64 + condA + condB, 1);
        if (chromaMode == 1) { cabac.EncodeBin(67, 0); return; }
        cabac.EncodeBin(67, 1);
        cabac.EncodeBin(67, chromaMode == 3 ? 1 : 0);
    }

    /// <summary>Encode coded_block_pattern luma (4 bins, one per 8x8 quadrant, ctx 73+inc).
    /// Intra MB neighbor derivation: P_Skip neighbor cbpLuma=0; unavailable neighbor → condTermFlag=0.</summary>
    public static void EncodeCbpLumaIntra(CabacEncoder cabac, int cbpLuma,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        for (int i = 0; i < 4; i++)
        {
            int cx = i & 1, cy = i >> 1;
            int condA;
            if (cx > 0) { int nb = cy * 2 + (cx - 1); condA = ((cbpLuma >> nb) & 1) == 0 ? 1 : 0; }
            else if (leftMb == null) condA = 0;
            else { int extCbp = leftMb.IsSkipped ? 0 : leftMb.CbpLuma; int extBit = (extCbp >> (cy * 2 + 1)) & 1; condA = extBit == 0 ? 1 : 0; }
            int condB;
            if (cy > 0) { int nb = (cy - 1) * 2 + cx; condB = ((cbpLuma >> nb) & 1) == 0 ? 1 : 0; }
            else if (topMb == null) condB = 0;
            else { int extCbp = topMb.IsSkipped ? 0 : topMb.CbpLuma; int extBit = (extCbp >> (2 + cx)) & 1; condB = extBit == 0 ? 1 : 0; }
            int bit = (cbpLuma >> i) & 1;
            cabac.EncodeBin(73 + condA + 2 * condB, bit);
        }
    }

    /// <summary>Encode coded_block_pattern chroma (TU cMax=2). bin0 ctx 77+inc, bin1 ctx 81+inc.</summary>
    public static void EncodeCbpChromaIntra(CabacEncoder cabac, int cbpChroma,
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

    /// <summary>Encode mb_qp_delta (signed) per spec §9.3.3.1.1.5. ctxIdx 60..63.</summary>
    public static void EncodeMbQpDelta(CabacEncoder cabac, int mbQpDelta, ref int prevMbQpDeltaState)
    {
        // Map signed value to unsigned (binIdx 0 ↦ |delta|==0; remaining unary on |delta|>0).
        // Per spec §9.3.2.7: codeNum = (mbQpDelta>0) ? 2*mbQpDelta - 1 : -2*mbQpDelta.
        int codeNum = (mbQpDelta > 0) ? (2 * mbQpDelta - 1) : (-2 * mbQpDelta);
        int bin0Ctx = 60 + (prevMbQpDeltaState != 0 ? 1 : 0);
        if (codeNum == 0)
        {
            cabac.EncodeBin(bin0Ctx, 0);
            prevMbQpDeltaState = 0;
            return;
        }
        cabac.EncodeBin(bin0Ctx, 1);
        // Subsequent bins: ctx 62 for binIdx=1, ctx 63 for binIdx>=2.
        int remaining = codeNum - 1;
        for (int k = 0; k < remaining; k++)
        {
            int ctx = (k == 0) ? 62 : 63;
            cabac.EncodeBin(ctx, 1);
        }
        // Terminating 0 at ctx for current binIdx.
        int termCtx = (remaining == 0) ? 62 : 63;
        cabac.EncodeBin(termCtx, 0);
        prevMbQpDeltaState = mbQpDelta;
    }

    /// <summary>Encode end_of_slice_flag (terminate bin).</summary>
    public static void EncodeEndOfSliceFlag(CabacEncoder cabac, bool endOfSlice)
    {
        cabac.EncodeTerminate(endOfSlice ? 1 : 0);
    }

    /// <summary>Encode prev_intra4x4_pred_mode_flag (single bin, ctx 68). Inverse of the
    /// decoder's <c>cabac.DecodeBin(68)</c> at <c>CabacSliceI.ParseIntraMbBody</c> Intra_4x4 branch.</summary>
    public static void EncodePrevIntra4x4PredModeFlag(CabacEncoder cabac, bool useNeighborPrediction)
    {
        cabac.EncodeBin(68, useNeighborPrediction ? 1 : 0);
    }

    /// <summary>Encode rem_intra4x4_pred_mode (3 bins, ctx 69, LSB first). The decoder reads
    /// r0,r1,r2 and assembles <c>(r2&lt;&lt;2) | (r1&lt;&lt;1) | r0</c>; we emit in the same order.</summary>
    public static void EncodeRemIntra4x4PredMode(CabacEncoder cabac, int rem)
    {
        cabac.EncodeBin(69, rem & 1);
        cabac.EncodeBin(69, (rem >> 1) & 1);
        cabac.EncodeBin(69, (rem >> 2) & 1);
    }

    // -----------------------------------------------------------------------------------
    // Helpers — replicate decoder logic for "is intra non-PCM" / "is non-INxN intra".
    // -----------------------------------------------------------------------------------

    private static bool IsNonINxNIntra(MacroblockEncoderState s)
    {
        // Non-I_NxN intra: Intra_16x16 or I_PCM. We don't emit I_PCM from this encoder.
        // IsIntra16x16 covers Intra_16x16; we treat any non-IsIntra4x4 + non-IsInter as Intra_16x16.
        return s.IsIntra16x16;
    }

    private static bool IsIntraNonPcm(MacroblockEncoderState s)
    {
        return s.IsIntra16x16 || s.IsIntra4x4;
    }
}
