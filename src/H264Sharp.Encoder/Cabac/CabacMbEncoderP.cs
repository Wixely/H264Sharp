using H264Sharp.Decoder.Cabac;
using H264Sharp.Encoder.Mode;
using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Encoder.Cabac;

/// <summary>Encode P-slice macroblocks (P_Skip / P_L0_16x16 / P_L0_L0_16x8 / P_L0_L0_8x16 / P_8x8)
/// using CABAC entropy coding. Wraps the existing CAVLC partition pipeline by reusing
/// the inter prediction + residual bundle and only switching the entropy step.</summary>
internal static class CabacMbEncoderP
{
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static readonly int[] SpatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

    /// <summary>Emit an entire P-slice MB at the CABAC level. The MB has already been mode-decided
    /// and motion-compensated by the caller; <paramref name="cand"/> describes the chosen partition
    /// shape and per-partition MVs, and <paramref name="bundle"/> holds the residual and reconstruction.</summary>
    public static void EncodePSkip(
        CabacEncoder cabac,
        MacroblockEncoderState state,
        int mbAddress, int qpY,
        int mvX, int mvY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        // mb_skip_flag = 1.
        CabacEncSliceP.EncodeMbSkipFlag(cabac, isSkip: true, leftMb, topMb);
        // No further syntax for a skip MB.
        state.MbAddress = mbAddress;
        state.IsSkipped = true;
        state.IsInter = true;
        state.RawMbType = -1;
        state.QpY = qpY;
        state.MvL0X = mvX;
        state.MvL0Y = mvY;
        state.RefIdxL0 = 0;
        for (int i = 0; i < 16; i++) { state.MvL0XBlock[i] = mvX; state.MvL0YBlock[i] = mvY; }
        for (int q = 0; q < 4; q++) state.RefIdxL08x8[q] = 0;
    }

    /// <summary>Encode one non-skip P-slice MB through CABAC. Mirrors the CAVLC partition path but
    /// emits CABAC-style bins. Updates <paramref name="state"/> with all per-block bookkeeping
    /// (MV, MVD, CBF, NZC) so subsequent neighbor lookups work correctly.</summary>
    public static void EncodeNonSkip(
        CabacEncoder cabac,
        MacroblockEncoderPartition.PartitionCandidate cand,
        MacroblockEncoderInter.InterEncodeBundle bundle,
        MacroblockEncoderState state,
        int mbAddress, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        ref int prevMbQpDeltaState)
    {
        // mb_skip_flag = 0.
        CabacEncSliceP.EncodeMbSkipFlag(cabac, isSkip: false, leftMb, topMb);

        // mb_type tree (inter branch only; intra-in-P not yet supported on encode side).
        CabacEncSliceP.EncodeMbTypeP(cabac, cand.RawMbType);

        // Initialize state for neighbor predictor reads. IsInter MUST be set before WriteMvds
        // since the partition predictor checks cur.IsInter for in-MB blocks.
        state.MbAddress = mbAddress;
        state.IsInter = true;
        state.IsInterP16x16 = cand.RawMbType == 0;
        state.IsIntra16x16 = false;
        state.IsSkipped = false;
        state.RawMbType = cand.RawMbType;
        state.QpY = qpY;
        for (int i = 0; i < 16; i++) { state.MvL0XBlock[i] = 0; state.MvL0YBlock[i] = 0; state.MvdL0XBlock[i] = 0; state.MvdL0YBlock[i] = 0; }
        for (int q = 0; q < 4; q++) state.RefIdxL08x8[q] = 0;

        // sub_mb_type only for P_8x8.
        if (cand.RawMbType == 3)
        {
            for (int q = 0; q < 4; q++)
            {
                CabacEncSliceP.EncodeSubMbTypeP(cabac, cand.SubMbTypes[q]);
            }
        }

        // ref_idx_l0: only when num_ref_idx_l0_active_minus1 > 0; our encoder uses single ref
        // (maxRef=0), so nothing is emitted, mirroring the decoder's `if (maxRef == 0) return 0`.

        // Emit MVDs in the same iteration order the decoder reads them.
        EmitMvdsForCandidate(cabac, cand, state, leftMb, topMb, topRightMb, topLeftMb);

        // Set convenience scalar MV from partition 0 for legacy callers.
        if (cand.Partitions.Count > 0)
        {
            state.MvL0X = cand.Partitions[0].MvX;
            state.MvL0Y = cand.Partitions[0].MvY;
            state.RefIdxL0 = 0;
        }

        // CBP luma + CBP chroma — separate CABAC binarizations for inter MBs.
        CabacEncSliceP.EncodeCbpLumaInter(cabac, bundle.CbpLuma, leftMb, topMb);
        CabacEncSliceP.EncodeCbpChromaInter(cabac, bundle.CbpChroma, leftMb, topMb);

        state.CbpLuma = bundle.CbpLuma;
        state.CbpChroma = bundle.CbpChroma;

        // mb_qp_delta + residual (only when any CBP bit is set).
        bool hasResidual = bundle.CbpLuma != 0 || bundle.CbpChroma != 0;
        if (hasResidual)
        {
            // Our encoder doesn't dynamically adjust QP within a slice — mb_qp_delta is always 0.
            CabacEncSlice.EncodeMbQpDelta(cabac, 0, ref prevMbQpDeltaState);
            EncodeResidualInter(cabac, bundle, state, leftMb, topMb);
        }
        else
        {
            // No qp_delta read by decoder → prev-state resets.
            prevMbQpDeltaState = 0;
        }

        bundle.ReconY.CopyTo(state.ReconY, 0);
        bundle.ReconU.CopyTo(state.ReconU, 0);
        bundle.ReconV.CopyTo(state.ReconV, 0);
    }

    /// <summary>Emit mvd_l0_x / mvd_l0_y bins for each partition of <paramref name="cand"/> in the
    /// decoder's iteration order. Computes per-block absMvdSum from neighbor MVDs, predicts the MV,
    /// emits (mvdX, mvdY), and updates <paramref name="state"/>'s per-block MV/MVD arrays.</summary>
    private static void EmitMvdsForCandidate(
        CabacEncoder cabac,
        MacroblockEncoderPartition.PartitionCandidate cand,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        switch (cand.RawMbType)
        {
            case 0: // P_L0_16x16
                EmitOnePartMvd(cabac, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 4, bh: 4,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                break;
            case 1: // 16x8
                EmitOnePartMvd(cabac, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 4, bh: 2,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                EmitOnePartMvd(cabac, state, cand, partIdx: 1,
                    bx: 0, by: 2, bw: 4, bh: 2,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[1]);
                break;
            case 2: // 8x16
                EmitOnePartMvd(cabac, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 2, bh: 4,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                EmitOnePartMvd(cabac, state, cand, partIdx: 1,
                    bx: 2, by: 0, bw: 2, bh: 4,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[1]);
                break;
            case 3: // P_8x8
                int pIdx = 0;
                for (int q = 0; q < 4; q++)
                {
                    int qBx = (q & 1) * 2;
                    int qBy = (q >> 1) * 2;
                    int sub = cand.SubMbTypes[q];
                    foreach (var (spBx, spBy, spBw, spBh) in MacroblockEncoderPartition.SubPartLayout(sub))
                    {
                        EmitOnePartMvd(cabac, state, cand, partIdx: 0,
                            bx: qBx + spBx, by: qBy + spBy, bw: spBw, bh: spBh,
                            leftMb, topMb, topRightMb, topLeftMb,
                            cand.Partitions[pIdx],
                            forceStandardMedian: true);
                        pIdx++;
                    }
                }
                break;
            default:
                throw new NotSupportedException($"CABAC encode: rawMbType {cand.RawMbType} not supported");
        }
    }

    private static void EmitOnePartMvd(
        CabacEncoder cabac,
        MacroblockEncoderState state,
        MacroblockEncoderPartition.PartitionCandidate cand,
        int partIdx, int bx, int by, int bw, int bh,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        MacroblockEncoderPartition.Partition p, bool forceStandardMedian = false)
    {
        int rmt = forceStandardMedian ? 0 : cand.RawMbType;
        (int predX, int predY) = PartitionMvPredictor.Predict(
            state, rmt, partIdx, bx, by, bw, bh, curRefIdx: 0,
            leftMb, topMb, topRightMb, topLeftMb);
        int mvdX = p.MvX - predX;
        int mvdY = p.MvY - predY;

        // absMvdSum: per-block neighbor lookup of |mvdComp|.
        int sumX = NeighborAbsMvdSum(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, xComp: true);
        int sumY = NeighborAbsMvdSum(state, bx, by, leftMb, topMb, topRightMb, topLeftMb, xComp: false);
        CabacEncSliceP.EncodeMvd(cabac, mvdX, sumX, ctxBase: 40);
        CabacEncSliceP.EncodeMvd(cabac, mvdY, sumY, ctxBase: 47);

        // Update per-block MV + MVD arrays so subsequent partitions see them.
        for (int yy = by; yy < by + bh; yy++)
        {
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int idx = SpatialToRaster[yy * 4 + xx];
                state.MvL0XBlock[idx] = p.MvX;
                state.MvL0YBlock[idx] = p.MvY;
                state.MvdL0XBlock[idx] = mvdX;
                state.MvdL0YBlock[idx] = mvdY;
            }
        }
    }

    /// <summary>Compute absMvdSum for one MV component over the A (left) and B (top) neighbor blocks
    /// of the partition's top-left 4x4 block. Mirrors decoder's <c>NeighborAbsMvdSumX/Y</c>.</summary>
    private static int NeighborAbsMvdSum(
        MacroblockEncoderState cur, int bx, int by,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        bool xComp)
    {
        return AbsMvdAt(cur, bx - 1, by, leftMb, topMb, topRightMb, topLeftMb, xComp)
             + AbsMvdAt(cur, bx, by - 1, leftMb, topMb, topRightMb, topLeftMb, xComp);
    }

    /// <summary>Look up the |mvd| of one component at MB-relative block coords (<paramref name="bx"/>,
    /// <paramref name="by"/>). Returns 0 for unavailable / intra / skip neighbors.</summary>
    private static int AbsMvdAt(
        MacroblockEncoderState cur, int bx, int by,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        bool xComp)
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
        if (!mb.IsInter || mb.IsSkipped) return 0;
        int idx = SpatialToRaster[nbBy * 4 + nbBx];
        int v = xComp ? mb.MvdL0XBlock[idx] : mb.MvdL0YBlock[idx];
        return v < 0 ? -v : v;
    }

    /// <summary>Encode the residual blocks (16 luma 4x4 + chroma DC + chroma AC) for an inter MB.</summary>
    private static void EncodeResidualInter(
        CabacEncoder cabac,
        MacroblockEncoderInter.InterEncodeBundle bundle,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        // Luma 4x4 blocks (16 of them, gated by CbpLuma bit per 8x8 sub-block).
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

        // Chroma DC.
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

        // Chroma AC.
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

    /// <summary>Luma 4x4 AC CBF condTerm derivation for inter MBs (P_Skip/intra neighbor → 0).</summary>
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

    /// <summary>Chroma AC CBF condTerm derivation for inter MBs.</summary>
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
