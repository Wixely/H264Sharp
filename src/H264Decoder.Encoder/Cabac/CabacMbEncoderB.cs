using H264Decoder.Cabac;
using H264Decoder.Encoder.Mode;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Cabac;

/// <summary>Phase 5b: encode a B-slice macroblock (16x16 partition: B_L0_16x16 / B_L1_16x16 /
/// B_Bi_16x16) using CABAC entropy coding. Reuses the prediction + residual bundle that
/// <see cref="BMbEncoder"/> already produced; only the entropy stage switches from CAVLC to
/// CABAC. B_Direct / B_Skip / sub-MB partitions / intra-in-B are not yet supported.</summary>
internal static class CabacMbEncoderB
{
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>Emit a non-skip B-MB. mb_skip_flag is emitted as 0 by the caller, then this method
    /// emits mb_type, mvd_l0 / mvd_l1, CBP luma+chroma, mb_qp_delta (when CBP != 0), residual.
    /// Updates <paramref name="state"/> with all per-block bookkeeping (per-list MV/MVD, RefIdx,
    /// PredFlag, NZC, CBF) so subsequent neighbor lookups see the right values.</summary>
    public static void EncodeNonSkip(
        CabacEncoder cabac,
        BMbEncoder.BCandidate cand,
        MacroblockEncoderState state,
        int mbAddress, int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        ref int prevMbQpDeltaState)
    {
        // mb_skip_flag = 0 (B-slice ctxIdxOffset=24).
        CabacEncSliceB.EncodeMbSkipFlagB(cabac, isSkip: false, leftMb, topMb);

        // mb_type.
        int mbType = cand.Direction switch
        {
            BMbEncoder.Dir.L0 => 1,
            BMbEncoder.Dir.L1 => 2,
            BMbEncoder.Dir.Bi => 3,
            _ => throw new InvalidOperationException()
        };
        CabacEncSliceB.EncodeMbTypeB16x16(cabac, mbType, leftMb, topMb);

        // ---- Update state up-front (before MVD emission) so in-MB neighbor lookups work for
        // the residual phase. The mvd values are zeroed here and filled below as we emit. ----
        bool useL0 = cand.Direction != BMbEncoder.Dir.L1;
        bool useL1 = cand.Direction != BMbEncoder.Dir.L0;
        state.MbAddress = mbAddress;
        state.IsBInter = true;
        state.IsInter = true;
        state.IsInterP16x16 = false;
        state.IsIntra16x16 = false;
        state.IsIntra4x4 = false;
        state.IsSkipped = false;
        state.BPredDir = (byte)cand.Direction;
        state.RawMbType = mbType;
        state.QpY = qpY;
        for (int i = 0; i < 16; i++)
        {
            state.PredFlagL0Block[i] = useL0 ? (byte)1 : (byte)0;
            state.PredFlagL1Block[i] = useL1 ? (byte)1 : (byte)0;
            state.MvL0XBlock[i] = useL0 ? cand.MvL0X : 0;
            state.MvL0YBlock[i] = useL0 ? cand.MvL0Y : 0;
            state.MvL1XBlock[i] = useL1 ? cand.MvL1X : 0;
            state.MvL1YBlock[i] = useL1 ? cand.MvL1Y : 0;
            state.MvdL0XBlock[i] = 0;
            state.MvdL0YBlock[i] = 0;
            state.MvdL1XBlock[i] = 0;
            state.MvdL1YBlock[i] = 0;
        }
        for (int q = 0; q < 4; q++)
        {
            state.RefIdxL08x8[q] = useL0 ? 0 : -1;
            state.RefIdxL18x8[q] = useL1 ? 0 : -1;
        }

        // ---- mvd_l0 then mvd_l1 (per decoder iteration order). ----
        int mvdL0X = 0, mvdL0Y = 0, mvdL1X = 0, mvdL1Y = 0;
        if (useL0)
        {
            mvdL0X = cand.MvL0X - predL0X;
            mvdL0Y = cand.MvL0Y - predL0Y;
            int sumX = NeighborAbsMvdSum(leftMb, topMb, listX: 0, xComp: true);
            int sumY = NeighborAbsMvdSum(leftMb, topMb, listX: 0, xComp: false);
            CabacEncSliceP.EncodeMvd(cabac, mvdL0X, sumX, ctxBase: 40);
            CabacEncSliceP.EncodeMvd(cabac, mvdL0Y, sumY, ctxBase: 47);
        }
        if (useL1)
        {
            mvdL1X = cand.MvL1X - predL1X;
            mvdL1Y = cand.MvL1Y - predL1Y;
            int sumX = NeighborAbsMvdSum(leftMb, topMb, listX: 1, xComp: true);
            int sumY = NeighborAbsMvdSum(leftMb, topMb, listX: 1, xComp: false);
            CabacEncSliceP.EncodeMvd(cabac, mvdL1X, sumX, ctxBase: 40);
            CabacEncSliceP.EncodeMvd(cabac, mvdL1Y, sumY, ctxBase: 47);
        }
        // Populate per-block MVD arrays (used by neighbor lookups in subsequent MBs).
        for (int i = 0; i < 16; i++)
        {
            state.MvdL0XBlock[i] = useL0 ? mvdL0X : 0;
            state.MvdL0YBlock[i] = useL0 ? mvdL0Y : 0;
            state.MvdL1XBlock[i] = useL1 ? mvdL1X : 0;
            state.MvdL1YBlock[i] = useL1 ? mvdL1Y : 0;
        }
        state.MvL0X = useL0 ? cand.MvL0X : 0;
        state.MvL0Y = useL0 ? cand.MvL0Y : 0;
        state.RefIdxL0 = useL0 ? 0 : -1;
        state.RefIdxL1 = useL1 ? 0 : -1;

        // ---- CBP luma + chroma (shared with P-slice CABAC encoder). ----
        var bundle = cand.Bundle;
        CabacEncSliceP.EncodeCbpLumaInter(cabac, bundle.CbpLuma, leftMb, topMb);
        CabacEncSliceP.EncodeCbpChromaInter(cabac, bundle.CbpChroma, leftMb, topMb);

        state.CbpLuma = bundle.CbpLuma;
        state.CbpChroma = bundle.CbpChroma;

        // ---- mb_qp_delta + residual (only when any CBP bit is set). ----
        bool hasResidual = bundle.CbpLuma != 0 || bundle.CbpChroma != 0;
        if (hasResidual)
        {
            CabacEncSlice.EncodeMbQpDelta(cabac, 0, ref prevMbQpDeltaState);
            EncodeResidualInter(cabac, bundle, state, leftMb, topMb);
        }
        else
        {
            prevMbQpDeltaState = 0;
        }

        bundle.ReconY.CopyTo(state.ReconY, 0);
        bundle.ReconU.CopyTo(state.ReconU, 0);
        bundle.ReconV.CopyTo(state.ReconV, 0);
    }

    /// <summary>Compute neighbor absMvdSum over A (left, block (-1,0)) and B (top, block (0,-1))
    /// for a 16x16 partition starting at block (0,0). For listX=0 reads MvdL0Block; for listX=1
    /// reads MvdL1Block. Returns 0 for neighbors that don't use this list (RefIdx=-1) or aren't
    /// inter MBs.</summary>
    private static int NeighborAbsMvdSum(
        MacroblockEncoderState? leftMb,
        MacroblockEncoderState? topMb,
        int listX, bool xComp)
    {
        // 16x16 partition: block (0,0) → A = left MB block (3,0); B = top MB block (0,3).
        int a = AbsMvdAt(leftMb, blockBxInNeighbor: 3, blockByInNeighbor: 0, quadIdx: 1, listX, xComp);
        int b = AbsMvdAt(topMb, blockBxInNeighbor: 0, blockByInNeighbor: 3, quadIdx: 2, listX, xComp);
        return a + b;
    }

    private static int AbsMvdAt(
        MacroblockEncoderState? mb, int blockBxInNeighbor, int blockByInNeighbor, int quadIdx,
        int listX, bool xComp)
    {
        if (mb is null) return 0;
        // Intra / skip / non-inter neighbors contribute 0.
        if (!mb.IsInter && !mb.IsBInter) return 0;
        if (mb.IsSkipped) return 0;
        int refIdx = listX == 0 ? mb.RefIdxL08x8[quadIdx] : mb.RefIdxL18x8[quadIdx];
        if (refIdx < 0) return 0;
        int idx = MacroblockParser.SpatialToRaster(blockBxInNeighbor, blockByInNeighbor);
        int v;
        if (listX == 0) v = xComp ? mb.MvdL0XBlock[idx] : mb.MvdL0YBlock[idx];
        else v = xComp ? mb.MvdL1XBlock[idx] : mb.MvdL1YBlock[idx];
        return v < 0 ? -v : v;
    }

    /// <summary>Encode residual blocks (16 luma 4x4 + chroma DC + chroma AC) for the B-MB.</summary>
    private static void EncodeResidualInter(
        CabacEncoder cabac,
        MacroblockEncoderInter.InterEncodeBundle bundle,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        Span<int> lumaScan = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int q = i >> 2;
            bool coded = (bundle.CbpLuma & (1 << q)) != 0;
            if (!coded)
            {
                state.LumaAcCbf[i] = false;
                state.NonZeroCountLuma[i] = 0;
                continue;
            }
            for (int s = 0; s < 16; s++) lumaScan[s] = bundle.Luma4x4[i * 16 + ZigZag4x4[s]];
            (int cA, int cB) = LumaAcNeighborCbfInter(i, state, leftMb, topMb);
            bool cbf = CabacEncResidual.EncodeResidualBlock(
                cabac, lumaScan, maxNumCoeff: 16, ctxBlockCat: CabacEncResidual.CatLuma4x4,
                condTermFlagA: cA, condTermFlagB: cB);
            state.LumaAcCbf[i] = cbf;
            state.NonZeroCountLuma[i] = cbf ? 1 : 0;
        }

        if ((bundle.CbpChroma & 3) != 0)
        {
            Span<int> dc = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dc[k] = bundle.ChromaDc[c, k];
                int caC = (leftMb == null || leftMb.IsSkipped) ? 0 : (leftMb.ChromaDcCbf[c] ? 1 : 0);
                int cbC = (topMb == null || topMb.IsSkipped) ? 0 : (topMb.ChromaDcCbf[c] ? 1 : 0);
                bool cbf = CabacEncResidual.EncodeResidualBlock(
                    cabac, dc, maxNumCoeff: 4, ctxBlockCat: CabacEncResidual.CatChromaDc,
                    condTermFlagA: caC, condTermFlagB: cbC);
                state.ChromaDcCbf[c] = cbf;
            }
        }

        if ((bundle.CbpChroma & 2) != 0)
        {
            Span<int> ac = stackalloc int[15];
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int k = 0; k < 15; k++) ac[k] = bundle.ChromaAc[c, i, k];
                    (int cA, int cB) = ChromaAcNeighborCbfInter(c, i, state, leftMb, topMb);
                    bool cbf = CabacEncResidual.EncodeResidualBlock(
                        cabac, ac, maxNumCoeff: 15, ctxBlockCat: CabacEncResidual.CatChromaAc,
                        condTermFlagA: cA, condTermFlagB: cB);
                    state.ChromaAcCbf[c, i] = cbf;
                    state.NonZeroCountChromaAc[c, i] = cbf ? 1 : 0;
                }
            }
        }
    }

    private static (int A, int B) LumaAcNeighborCbfInter(int blockIdx, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        (int x, int y) = MacroblockParser.LumaBlockPos[blockIdx];
        int condA;
        if (x > 0) condA = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x - 1, y)] ? 1 : 0;
        else if (leftMb == null || leftMb.IsSkipped) condA = 0;
        else condA = leftMb.LumaAcCbf[MacroblockParser.SpatialToRaster(3, y)] ? 1 : 0;
        int condB;
        if (y > 0) condB = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x, y - 1)] ? 1 : 0;
        else if (topMb == null || topMb.IsSkipped) condB = 0;
        else condB = topMb.LumaAcCbf[MacroblockParser.SpatialToRaster(x, 3)] ? 1 : 0;
        return (condA, condB);
    }

    private static (int A, int B) ChromaAcNeighborCbfInter(int comp, int blockIdx, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int x = blockIdx & 1;
        int y = (blockIdx >> 1) & 1;
        int condA;
        if (x > 0) condA = cur.ChromaAcCbf[comp, blockIdx - 1] ? 1 : 0;
        else if (leftMb == null || leftMb.IsSkipped) condA = 0;
        else condA = leftMb.ChromaAcCbf[comp, blockIdx + 1] ? 1 : 0;
        int condB;
        if (y > 0) condB = cur.ChromaAcCbf[comp, blockIdx - 2] ? 1 : 0;
        else if (topMb == null || topMb.IsSkipped) condB = 0;
        else condB = topMb.ChromaAcCbf[comp, blockIdx + 2] ? 1 : 0;
        return (condA, condB);
    }
}
