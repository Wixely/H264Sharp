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

    /// <summary>Emit B_Skip via CABAC: mb_skip_flag = 1, no further syntax. Updates state so
    /// later neighbor lookups (mb_skip_flag condTerm, mvd absMvdSum) see this MB as a skip.</summary>
    public static void EncodeBSkip(
        CabacEncoder cabac,
        BMbEncoder.BCandidate cand,
        MacroblockEncoderState state,
        int mbAddress, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        CabacEncSliceB.EncodeMbSkipFlagB(cabac, isSkip: true, leftMb, topMb);
        BMbEncoder.PopulateBMbState(cand, state, mbAddress, qpY, 0, 0, 0, 0);
        // For Skip we don't emit CBP/residual; CBF arrays stay zero.
        for (int i = 0; i < 16; i++) { state.LumaAcCbf[i] = false; state.NonZeroCountLuma[i] = 0; }
        state.LumaDcCbf = false;
        for (int c = 0; c < 2; c++)
        {
            state.ChromaDcCbf[c] = false;
            for (int i = 0; i < 4; i++) { state.ChromaAcCbf[c, i] = false; state.NonZeroCountChromaAc[c, i] = 0; }
        }
        cand.Bundle.ReconY.CopyTo(state.ReconY, 0);
        cand.Bundle.ReconU.CopyTo(state.ReconU, 0);
        cand.Bundle.ReconV.CopyTo(state.ReconV, 0);
    }

    /// <summary>Emit a non-skip B-MB. mb_skip_flag is emitted as 0 by the caller, then this method
    /// emits mb_type, mvd_l0 / mvd_l1 (only for L0/L1/Bi — Direct has none), CBP luma+chroma,
    /// mb_qp_delta (when CBP != 0), residual. Updates <paramref name="state"/> with all per-block
    /// bookkeeping (per-list MV/MVD, RefIdx, PredFlag, NZC, CBF).</summary>
    public static void EncodeNonSkip(
        CabacEncoder cabac,
        BMbEncoder.BCandidate cand,
        MacroblockEncoderState state,
        int mbAddress, int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        ref int prevMbQpDeltaState)
    {
        // mb_skip_flag = 0 (B-slice ctxIdxOffset=24).
        CabacEncSliceB.EncodeMbSkipFlagB(cabac, isSkip: false, leftMb, topMb);

        // mb_type.
        int mbType = BMbEncoder.MbTypeOf(cand);
        CabacEncSliceB.EncodeMbTypeB16x16(cabac, mbType, leftMb, topMb);

        // ---- Populate state for in-MB neighbor lookups during residual phase. ----
        BMbEncoder.PopulateBMbState(cand, state, mbAddress, qpY, 0, 0, 0, 0);

        // ---- sub_mb_types for B_8x8 (emit AFTER mb_type, BEFORE mvds) ----
        if (cand.Shape == BMbEncoder.Shape.P8x8)
        {
            for (int q = 0; q < 4; q++)
            {
                CabacEncSliceB.EncodeSubMbTypeB(cabac, cand.SubMbTypes![q]);
            }
        }

        // ---- MVD emission ----
        if (cand.Shape == BMbEncoder.Shape.Sq16x16 && cand.Direction != BMbEncoder.Dir.Direct)
        {
            EmitMvdsSq16x16(cabac, cand, state, predL0X, predL0Y, predL1X, predL1Y, leftMb, topMb);
        }
        else if (cand.Shape == BMbEncoder.Shape.P8x8)
        {
            EmitMvdsP8x8(cabac, cand, state, leftMb, topMb, topRightMb, topLeftMb);
        }
        else if (cand.Shape != BMbEncoder.Shape.Sq16x16)
        {
            EmitMvdsPartitioned(cabac, cand, state, leftMb, topMb, topRightMb, topLeftMb);
        }
        // Direct (Sq16x16 Direct) and Skip emit no MVDs.

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

    /// <summary>MVD emission for a Sq16x16 inter (L0/L1/Bi) candidate. Single 16x16 partition with
    /// neighbor blocks at (-1,0) and (0,-1).</summary>
    private static void EmitMvdsSq16x16(
        CabacEncoder cabac, BMbEncoder.BCandidate cand, MacroblockEncoderState state,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        bool useL0 = cand.Direction != BMbEncoder.Dir.L1;
        bool useL1 = cand.Direction != BMbEncoder.Dir.L0;
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
        // Re-populate per-block mvds (the PopulateBMbState call left them as zero).
        for (int i = 0; i < 16; i++)
        {
            state.MvdL0XBlock[i] = useL0 ? mvdL0X : 0;
            state.MvdL0YBlock[i] = useL0 ? mvdL0Y : 0;
            state.MvdL1XBlock[i] = useL1 ? mvdL1X : 0;
            state.MvdL1YBlock[i] = useL1 ? mvdL1Y : 0;
        }
    }

    /// <summary>MVD emission for a partitioned (16x8 or 8x16) B-MB candidate. Iterates L0 over all
    /// partitions, then L1 over all partitions (per decoder iteration order). Uses the same
    /// shape-aware per-partition predictor as the CAVLC path so the decoder reconstructs identical
    /// MVs. absMvdSum is computed from the partition's top-left neighbor blocks (in-MB for
    /// partition 1 of 16x8 / 8x16 reads partition 0's stored MVDs).</summary>
    private static void EmitMvdsPartitioned(
        CabacEncoder cabac, BMbEncoder.BCandidate cand, MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        (int Bx, int By, int Bw, int Bh)[] partsB = cand.Shape == BMbEncoder.Shape.P16x8
            ? new[] { (0, 0, 4, 2), (0, 2, 4, 2) }
            : new[] { (0, 0, 2, 4), (2, 0, 2, 4) };

        int mbType = BMbEncoder.MbTypeOf(cand);

        // L0 over partitions.
        for (int p = 0; p < 2; p++)
        {
            BMbEncoder.Dir d = p == 0 ? cand.Direction : cand.Part1Direction;
            if (d != BMbEncoder.Dir.L0 && d != BMbEncoder.Dir.Bi) continue;
            var (bx, by, bw, bh) = partsB[p];
            (int predX, int predY) = BMbEncoder.PredictPartitionMvBListPublic(
                state, mbType, p, bx, by, bw, bh, curRefIdx: 0, listX: 0,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = p == 0 ? cand.MvL0X : cand.Part1MvL0X;
            int mvY = p == 0 ? cand.MvL0Y : cand.Part1MvL0Y;
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            int sumX = NeighborAbsMvdSumAt(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, xComp: true);
            int sumY = NeighborAbsMvdSumAt(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, xComp: false);
            CabacEncSliceP.EncodeMvd(cabac, mvdX, sumX, ctxBase: 40);
            CabacEncSliceP.EncodeMvd(cabac, mvdY, sumY, ctxBase: 47);
            FillBlockMvds(state, bx, by, bw, bh, listX: 0, mvdX, mvdY);
        }
        // L1 over partitions.
        for (int p = 0; p < 2; p++)
        {
            BMbEncoder.Dir d = p == 0 ? cand.Direction : cand.Part1Direction;
            if (d != BMbEncoder.Dir.L1 && d != BMbEncoder.Dir.Bi) continue;
            var (bx, by, bw, bh) = partsB[p];
            (int predX, int predY) = BMbEncoder.PredictPartitionMvBListPublic(
                state, mbType, p, bx, by, bw, bh, curRefIdx: 0, listX: 1,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = p == 0 ? cand.MvL1X : cand.Part1MvL1X;
            int mvY = p == 0 ? cand.MvL1Y : cand.Part1MvL1Y;
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            int sumX = NeighborAbsMvdSumAt(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, xComp: true);
            int sumY = NeighborAbsMvdSumAt(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, xComp: false);
            CabacEncSliceP.EncodeMvd(cabac, mvdX, sumX, ctxBase: 40);
            CabacEncSliceP.EncodeMvd(cabac, mvdY, sumY, ctxBase: 47);
            FillBlockMvds(state, bx, by, bw, bh, listX: 1, mvdX, mvdY);
        }
    }

    /// <summary>CABAC mvd emission for a B_8x8 MB. Iterates L0 over quadrants/sub-partitions
    /// then L1, with each sub-partition emitting one mvd pair. Uses the standard-median MV
    /// predictor (rawMbType=0 sentinel per spec §8.4.1.3.2).</summary>
    private static void EmitMvdsP8x8(
        CabacEncoder cabac, BMbEncoder.BCandidate cand, MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        for (int q = 0; q < 4; q++)
        {
            int sub = cand.SubMbTypes![q];
            if (!UsesL0(sub)) continue;
            EmitQuadrantMvdsCabac(cabac, cand, state, q, sub, listX: 0, leftMb, topMb, topRightMb, topLeftMb);
        }
        for (int q = 0; q < 4; q++)
        {
            int sub = cand.SubMbTypes![q];
            if (!UsesL1(sub)) continue;
            EmitQuadrantMvdsCabac(cabac, cand, state, q, sub, listX: 1, leftMb, topMb, topRightMb, topLeftMb);
        }
    }

    private static bool UsesL0(int sub) =>
        sub == 1 || sub == 3 || sub == 4 || sub == 5 || sub == 8 || sub == 9 || sub == 10 || sub == 12;
    private static bool UsesL1(int sub) =>
        sub == 2 || sub == 3 || sub == 6 || sub == 7 || sub == 8 || sub == 9 || sub == 11 || sub == 12;

    private static readonly (int Px, int Py, int Pw, int Ph)[][] SubMbPartLayouts =
    {
        new[] { (0, 0, 8, 8) }, new[] { (0, 0, 8, 8) }, new[] { (0, 0, 8, 8) }, new[] { (0, 0, 8, 8) },
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) }, new[] { (0, 0, 4, 8), (4, 0, 4, 8) },
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) }, new[] { (0, 0, 4, 8), (4, 0, 4, 8) },
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) }, new[] { (0, 0, 4, 8), (4, 0, 4, 8) },
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },
    };

    private static void EmitQuadrantMvdsCabac(
        CabacEncoder cabac, BMbEncoder.BCandidate cand, MacroblockEncoderState state,
        int q, int sub, int listX,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        int qx = (q & 1) * 8, qy = (q >> 1) * 8;
        var layout = SubMbPartLayouts[sub];
        foreach (var (spx, spy, spw, sph) in layout)
        {
            int bx0 = (qx + spx) / 4;
            int by0 = (qy + spy) / 4;
            int bw = spw / 4, bh = sph / 4;
            int idx0 = SpatialToRaster[by0 * 4 + bx0];
            (int predX, int predY) = BMbEncoder.PredictPartitionMvBListPublic(
                state, rawMbType: 0, partIdx: 0,
                bx: bx0, by: by0, bw: bw, bh: bh,
                curRefIdx: 0, listX: listX,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = listX == 0 ? cand.MvL0XPerBlock![idx0] : cand.MvL1XPerBlock![idx0];
            int mvY = listX == 0 ? cand.MvL0YPerBlock![idx0] : cand.MvL1YPerBlock![idx0];
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            int sumX = NeighborAbsMvdSumAt(state, bx0, by0, leftMb, topMb, topRightMb, topLeftMb, listX, xComp: true);
            int sumY = NeighborAbsMvdSumAt(state, bx0, by0, leftMb, topMb, topRightMb, topLeftMb, listX, xComp: false);
            CabacEncSliceP.EncodeMvd(cabac, mvdX, sumX, ctxBase: 40);
            CabacEncSliceP.EncodeMvd(cabac, mvdY, sumY, ctxBase: 47);
            FillBlockMvds(state, bx0, by0, bw, bh, listX, mvdX, mvdY);
        }
    }

    private static readonly int[] SpatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

    private static void FillBlockMvds(MacroblockEncoderState state, int bx, int by, int bw, int bh,
        int listX, int mvdX, int mvdY)
    {
        for (int yy = by; yy < by + bh; yy++)
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int idx = SpatialToRaster[yy * 4 + xx];
                if (listX == 0) { state.MvdL0XBlock[idx] = mvdX; state.MvdL0YBlock[idx] = mvdY; }
                else { state.MvdL1XBlock[idx] = mvdX; state.MvdL1YBlock[idx] = mvdY; }
            }
    }

    /// <summary>absMvdSum computed at the partition's top-left block position. For partition 0 of
    /// 16x8/8x16 the neighbors lie outside the MB (leftMb / topMb). For partition 1 the in-MB
    /// neighbor block reads from <paramref name="cur"/>'s MVD arrays — partition 0's emit must run
    /// first so those slots are populated.</summary>
    private static int NeighborAbsMvdSumAt(
        MacroblockEncoderState cur, int bx, int by,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        int listX, bool xComp)
    {
        return AbsMvdAtPos(cur, bx - 1, by, leftMb, topMb, topRightMb, topLeftMb, listX, xComp)
             + AbsMvdAtPos(cur, bx, by - 1, leftMb, topMb, topRightMb, topLeftMb, listX, xComp);
    }

    private static int AbsMvdAtPos(
        MacroblockEncoderState cur, int bx, int by,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        int listX, bool xComp)
    {
        MacroblockEncoderState? mb;
        int nbBx, nbBy;
        if (bx >= 0 && by >= 0 && bx <= 3 && by <= 3) { mb = cur; nbBx = bx; nbBy = by; }
        else if (bx < 0 && by >= 0 && by <= 3) { mb = leftMb; nbBx = 3; nbBy = by; }
        else if (by < 0 && bx >= 0 && bx <= 3) { mb = topMb; nbBx = bx; nbBy = 3; }
        else if (bx < 0 && by < 0) { mb = topLeftMb; nbBx = 3; nbBy = 3; }
        else if (bx > 3 && by < 0) { mb = topRightMb; nbBx = 0; nbBy = 3; }
        else return 0;
        if (mb is null) return 0;
        if (!mb.IsInter && !mb.IsBInter) return 0;
        if (mb.IsSkipped) return 0;
        int idx = SpatialToRaster[nbBy * 4 + nbBx];
        int q = (nbBy >> 1) * 2 + (nbBx >> 1);
        int refIdx = listX == 0 ? mb.RefIdxL08x8[q] : mb.RefIdxL18x8[q];
        if (refIdx < 0) return 0;
        int v = listX == 0
            ? (xComp ? mb.MvdL0XBlock[idx] : mb.MvdL0YBlock[idx])
            : (xComp ? mb.MvdL1XBlock[idx] : mb.MvdL1YBlock[idx]);
        return v < 0 ? -v : v;
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
