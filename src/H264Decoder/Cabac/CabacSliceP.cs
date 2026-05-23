using H264Decoder.Syntax;

namespace H264Decoder.Cabac;

/// <summary>
/// CABAC syntax for one non-skip P-slice macroblock (spec §7.3.5.1 + §9.3.3.1).
/// Implements the inter mb_types (0..3): P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, P_8x8.
/// The intra branch (5..30 → I_NxN/Intra16x16) of P-slice mb_type is currently NotSupported.
/// </summary>
internal static class CabacSliceP
{
    public static Macroblock ParseMb(
        CabacDecoder cabac,
        SliceHeader sliceHeader,
        Macroblock? leftMb,
        Macroblock? topMb,
        Macroblock? topRightMb,
        Macroblock? topLeftMb,
        int mbAddress,
        ref int qpYRunning,
        ref int prevMbQpDeltaState,
        bool transform8x8ModeFlag = false)
    {
        int _diagStart = cabac.CurrentBitPos;
        int mbTypeCode = DecodeMbTypeP(cabac, leftMb, topMb);
        if (mbTypeCode >= 5)
        {
            // Intra branch — decode the I-slice mb_type suffix with ctxIdxOffset=17,
            // then dispatch to the shared intra-MB body parser.
            int iMbType = CabacSliceI.DecodeIntraMbTypeAtOffset(cabac, ctxIdxOffset: 17);
            if (iMbType == 25)
            {
                return CabacSliceI.ParsePcmMb(cabac, mbAddress, qpYRunning, ref prevMbQpDeltaState);
            }
            return CabacSliceI.ParseIntraMbBody(cabac, iMbType, leftMb, topMb, mbAddress,
                                                ref qpYRunning, ref prevMbQpDeltaState,
                                                transform8x8ModeFlag);
        }
        if (mbTypeCode == 4)
        {
            // P_8x8ref0 is not signalled in CABAC; only 0..3 are inter values per Table 9-37.
            throw new NotSupportedException("CABAC: unexpected P mb_type code 4");
        }

        var type = IntraMbType.FromPSliceCodeword((uint)mbTypeCode);
        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = type,
        };

        // ---- mb_pred / sub_mb_pred: read sub_mb_types (P_8x8), ref_idx, mvds ----
        ParseInterMbPred(cabac, mb, sliceHeader, leftMb, topMb, topRightMb, topLeftMb);

        // ---- coded_block_pattern (separate from mb_type for inter) ----
        int cbpLuma = DecodeCbpLuma(cabac, mb, leftMb, topMb);
        int cbpChroma = DecodeCbpChroma(cabac, leftMb, topMb);
        mb.CbpLuma = cbpLuma;
        mb.CbpChroma = cbpChroma;

        // transform_size_8x8_flag (inter MB: only when all sub-mbs are 8x8 AND CBP-luma > 0).
        if (transform8x8ModeFlag && cbpLuma > 0)
        {
            int rawMbType = type.RawMbType;
            bool eligible = rawMbType <= 2 || (rawMbType == 3 && AllSubMbsAre8x8(mb));
            if (eligible)
            {
                int ctxA = (leftMb != null && leftMb.TransformSize8x8) ? 1 : 0;
                int ctxB = (topMb != null && topMb.TransformSize8x8) ? 1 : 0;
                int flag = cabac.DecodeBin(399 + ctxA + ctxB);
                mb.TransformSize8x8 = flag == 1;
            }
        }

        // ---- mb_qp_delta + residual (only if any CBP bit is set) ----
        if (cbpLuma != 0 || cbpChroma != 0)
        {
            int mbQpDelta = CabacCommon.DecodeMbQpDelta(cabac, ref prevMbQpDeltaState);
            qpYRunning = CabacCommon.Mod52(qpYRunning + mbQpDelta);
            mb.QpY = qpYRunning;
            ReadResidualInter(cabac, mb, leftMb, topMb);
        }
        else
        {
            mb.QpY = qpYRunning;
            // No qp_delta read → reset the prev-state.
            prevMbQpDeltaState = 0;
        }
        mb.ParseStartBit = _diagStart;
        mb.ParseEndBit = cabac.CurrentBitPos;
        return mb;
    }

    private static bool AllSubMbsAre8x8(Macroblock mb)
    {
        if (mb.InterPartitions.Count != 4) return false;
        foreach (var p in mb.InterPartitions)
        {
            if (p.Width != 8 || p.Height != 8) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // mb_type for P-slice (Table 9-37, ctxIdxOffset=14)
    //   bin0 (ctx 14): 0=inter, 1=intra
    //   inter path: bin1 (ctx 15), bin2 (ctx 16)
    //     "0 0 0" = 0 (P_L0_16x16)
    //     "0 1 1" = 1 (P_L0_L0_16x8)
    //     "0 1 0" = 2 (P_L0_L0_8x16)
    //     "0 0 1" = 3 (P_8x8)
    //   intra path: read terminate (I_PCM check), then suffix using ctxIdxOffset 17.
    // ---------------------------------------------------------------------
    private static int DecodeMbTypeP(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        // Per spec Table 9-39: P/SP-slice mb_type binIdx 0 uses ctxIdxInc=0 (fixed).
        // (Unlike I-slice, no neighbor-derived condA/condB applies here.)
        int b0 = cabac.DecodeBin(14);
        if (b0 == 1)
        {
            // Intra branch — caller throws NotSupported.
            return 5;
        }

        int b1 = cabac.DecodeBin(15);
        // Spec Table 9-39: bin2 uses ctxIdxInc=2 when bin1==0 (ctx 16) and ctxIdxInc=3 when bin1==1 (ctx 17).
        int b2 = cabac.DecodeBin(b1 == 0 ? 16 : 17);
        if (b1 == 0)
        {
            // "0 0 0" => 0, "0 0 1" => 3
            return b2 == 0 ? 0 : 3;
        }
        else
        {
            // "0 1 1" => 1, "0 1 0" => 2
            return b2 == 1 ? 1 : 2;
        }
    }

    private static bool IsP_L0_16x16(Macroblock mb)
        => mb.Type.PredMode == MbPartPredMode.PredL0 && mb.Type.RawMbType == 0;

    // ---------------------------------------------------------------------
    // sub_mb_type (Table 9-38, ctxIdxOffset=21).
    //   "1" => PL0_8x8
    //   "0 0" => PL0_8x4
    //   "0 1 1" => PL0_4x8
    //   "0 1 0" => PL0_4x4
    // (Reading the spec table: bin0=1 ⇒ 8x8; bin0=0,bin1=0 ⇒ 8x4; bin0=0,bin1=1,bin2=1 ⇒ 4x8; bin0=0,bin1=1,bin2=0 ⇒ 4x4.)
    // ctxIdxInc per binIdx: 0→0, 1→1, 2→2. So ctx 21, 22, 23.
    // ---------------------------------------------------------------------
    private static SubMbType DecodeSubMbTypeP(CabacDecoder cabac)
    {
        int b0 = cabac.DecodeBin(21);
        if (b0 == 1) return SubMbType.PL0_8x8;
        int b1 = cabac.DecodeBin(22);
        if (b1 == 0) return SubMbType.PL0_8x4;
        int b2 = cabac.DecodeBin(23);
        return b2 == 1 ? SubMbType.PL0_4x8 : SubMbType.PL0_4x4;
    }

    // ---------------------------------------------------------------------
    // ref_idx_l0 unary, ctxIdxOffset=54 (ctx 54..59).
    // ---------------------------------------------------------------------
    private static int DecodeRefIdxL0(CabacDecoder cabac, int condA, int condB)
    {
        int ctxIdxInc0 = condA + 2 * condB;
        int b0 = cabac.DecodeBin(54 + ctxIdxInc0);
        if (b0 == 0) return 0;
        int n = 1;
        // binIdx>=1: ctx 4 for bin1, ctx 5 for bin2+.
        int b1 = cabac.DecodeBin(54 + 4);
        while (b1 == 1)
        {
            n++;
            if (n > 32) throw new InvalidDataException("ref_idx unary runaway");
            b1 = cabac.DecodeBin(54 + 5);
        }
        return n;
    }

    // ---------------------------------------------------------------------
    // mvd_l0 (signed UEG3): prefix TU(cMax=9), then EG3 suffix in bypass, then sign.
    // ctxIdxOffset = 40 for X, 47 for Y.
    // Bin0 ctxIdxInc derived from neighbor absMvdComp sum.
    // ---------------------------------------------------------------------
    private static int DecodeMvd(CabacDecoder cabac, int absMvdSum, int ctxBase)
    {
        // Bin0 ctxIdxInc: <3→0, 3..32→1, >=33→2
        int ctxIdxInc0 = absMvdSum < 3 ? 0 : (absMvdSum < 33 ? 1 : 2);
        int b0 = cabac.DecodeBin(ctxBase + ctxIdxInc0);
        if (b0 == 0) return 0;

        // Continue TU prefix bins: binIdx 1→3, 2→4, 3→5, 4+→6.
        int absPrefix = 1;
        while (absPrefix < 9)
        {
            int incK = absPrefix == 1 ? 3 : (absPrefix == 2 ? 4 : (absPrefix == 3 ? 5 : 6));
            int bk = cabac.DecodeBin(ctxBase + incK);
            if (bk == 0) break;
            absPrefix++;
        }

        int absVal = absPrefix;
        if (absPrefix >= 9)
        {
            // EG3 suffix in bypass mode (spec §9.1.2.3 — UEGk binarization).
            int suffix = ReadEGkBypass(cabac, k: 3);
            absVal = 9 + suffix;
        }

        int sign = cabac.DecodeBypass();
        return sign == 1 ? -absVal : absVal;
    }

    private static int ReadEGkBypass(CabacDecoder cabac, int k)
    {
        int leadingOnes = 0;
        while (cabac.DecodeBypass() == 1)
        {
            leadingOnes++;
            if (leadingOnes > 31) throw new InvalidDataException("EGk runaway");
        }
        int suffixBits = leadingOnes + k;
        int suffix = 0;
        for (int i = 0; i < suffixBits; i++)
        {
            suffix = (suffix << 1) | cabac.DecodeBypass();
        }
        return (((1 << leadingOnes) - 1) << k) + suffix;
    }

    // ---------------------------------------------------------------------
    // mb_pred / sub_mb_pred for P-slice inter MBs (rawMbType 0..3).
    // Mirrors MacroblockParser.ParseInterMbPred but reads via CABAC.
    // ---------------------------------------------------------------------
    private static void ParseInterMbPred(
        CabacDecoder cabac, Macroblock mb, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        int rawMbType = mb.Type.RawMbType;
        bool isP8x8 = rawMbType == 3;
        uint maxRef = sliceHeader.NumRefIdxL0ActiveMinus1;

        SubMbType[]? subMbTypes = null;
        if (isP8x8)
        {
            subMbTypes = new SubMbType[4];
            for (int i = 0; i < 4; i++)
            {
                subMbTypes[i] = DecodeSubMbTypeP(cabac);
            }
        }

        // Read ref_idx_l0
        int[] refIdxPerQuadrant = new int[4];
        if (rawMbType <= 2)
        {
            int numMbPart = IntraMbType.NumMbPart(rawMbType);
            int[] partRefIdx = new int[numMbPart];
            for (int p = 0; p < numMbPart; p++)
            {
                partRefIdx[p] = ReadRefIdxIfNeeded(cabac, maxRef, mb, p, rawMbType, isSubPart: false, subPartIdx: -1,
                                                   leftMb, topMb, topRightMb, topLeftMb);
                // Replicate the just-decoded ref_idx across the partition's quadrants
                // so a subsequent partition's spatial neighbor lookup inside this MB
                // sees the correct value (spec §9.3.3.1.1.6).
                MacroblockParser.ReplicateRefIdxAcross16x16PartitionsPublic(rawMbType, partRefIdx, refIdxPerQuadrant);
                for (int qq = 0; qq < 4; qq++) mb.RefIdxL08x8[qq] = refIdxPerQuadrant[qq];
            }
            MacroblockParser.ReplicateRefIdxAcross16x16PartitionsPublic(rawMbType, partRefIdx, refIdxPerQuadrant);
        }
        else // P_8x8
        {
            for (int q = 0; q < 4; q++)
            {
                refIdxPerQuadrant[q] = ReadRefIdxIfNeeded(cabac, maxRef, mb, q, rawMbType, isSubPart: true, subPartIdx: q,
                                                          leftMb, topMb, topRightMb, topLeftMb);
                // Write the just-decoded ref_idx into the MB's per-quadrant slot before
                // the next iteration: later quadrants' condTermFlag may read this slot
                // when their spatial neighbor is an earlier quadrant of the SAME MB
                // (spec §9.3.3.1.1.6: condTermFlagN looks at the partition the neighbor
                // 4x4 block belongs to, which is in-MB for P_8x8 quadrants 1/2/3).
                mb.RefIdxL08x8[q] = refIdxPerQuadrant[q];
            }
        }
        for (int q = 0; q < 4; q++) mb.RefIdxL08x8[q] = refIdxPerQuadrant[q];

        // Read mvds and apply MV prediction per partition.
        if (rawMbType <= 2)
        {
            ParseInterMvds_NoSubMb(cabac, mb, rawMbType, refIdxPerQuadrant,
                                   leftMb, topMb, topRightMb, topLeftMb);
        }
        else
        {
            ParseInterMvds_P8x8(cabac, mb, subMbTypes!, refIdxPerQuadrant,
                                leftMb, topMb, topRightMb, topLeftMb);
        }

        if (mb.InterPartitions.Count > 0)
        {
            var p0 = mb.InterPartitions[0];
            mb.RefIdxL0 = p0.RefIdxL0;
            mb.MvL0X = p0.MvL0X;
            mb.MvL0Y = p0.MvL0Y;
        }
    }

    /// <summary>
    /// Read ref_idx_l0 for one partition/quadrant if num_ref_idx_l0_active_minus1>0.
    /// CTX inc for binIdx 0: condA + 2*condB where condTermFlagN = (refIdxL0(N)>0?1:0)
    /// for inter neighbor blocks at the partition's top-left 4x4 position,
    /// and 0 for unavailable/intra/skip neighbors.
    /// </summary>
    private static int ReadRefIdxIfNeeded(
        CabacDecoder cabac, uint maxRef, Macroblock cur, int partOrQuadIdx,
        int rawMbType, bool isSubPart, int subPartIdx,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        if (maxRef == 0) return 0;

        // Find the top-left 4x4 block (in MB-relative coords) of this partition.
        (int bx, int by) = PartitionTopLeftBlock(rawMbType, isSubPart, partOrQuadIdx);

        var A = MacroblockParser.GetMvNeighborPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb);
        var B = MacroblockParser.GetMvNeighborPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        int condA = (A.Avail && A.RefIdx > 0) ? 1 : 0;
        int condB = (B.Avail && B.RefIdx > 0) ? 1 : 0;

        return DecodeRefIdxL0(cabac, condA, condB);
    }

    private static (int bx, int by) PartitionTopLeftBlock(int rawMbType, bool isSubPart, int idx)
    {
        if (!isSubPart)
        {
            // Top-left 4x4 of partition `idx` in mb_type 0/1/2.
            return rawMbType switch
            {
                0 => (0, 0),                          // 16x16 single
                1 => idx == 0 ? (0, 0) : (0, 2),      // 16x8: top/bottom
                2 => idx == 0 ? (0, 0) : (2, 0),      // 8x16: left/right
                _ => (0, 0),
            };
        }
        // P_8x8 quadrant idx (0=TL, 1=TR, 2=BL, 3=BR): each 8x8 occupies 2x2 4x4 blocks.
        int qx = (idx & 1) * 2;
        int qy = (idx >> 1) * 2;
        return (qx, qy);
    }

    private static void ParseInterMvds_NoSubMb(
        CabacDecoder cabac, Macroblock mb, int rawMbType, int[] refIdxPerQuadrant,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        var partRects = rawMbType switch
        {
            0 => new[] { (X: 0, Y: 0, W: 16, H: 16) },
            1 => new[] { (X: 0, Y: 0, W: 16, H: 8), (X: 0, Y: 8, W: 16, H: 8) },
            2 => new[] { (X: 0, Y: 0, W: 8, H: 16), (X: 8, Y: 0, W: 8, H: 16) },
            _ => throw new ArgumentOutOfRangeException(nameof(rawMbType)),
        };

        for (int p = 0; p < partRects.Length; p++)
        {
            int bx0 = partRects[p].X / 4;
            int by0 = partRects[p].Y / 4;
            int bw = partRects[p].W / 4;
            int bh = partRects[p].H / 4;

            // MVD X/Y for this partition's top-left 4x4 block. Neighbor absMvdSum
            // computed from per-block (already-filled-in-current-MB or external) MVs.
            int sumX = NeighborAbsMvdSumX(mb, bx0, by0, leftMb, topMb, topRightMb, topLeftMb);
            int sumY = NeighborAbsMvdSumY(mb, bx0, by0, leftMb, topMb, topRightMb, topLeftMb);
            int mvdX = DecodeMvd(cabac, sumX, ctxBase: 40);
            int mvdY = DecodeMvd(cabac, sumY, ctxBase: 47);

            int curRefIdx = refIdxPerQuadrant[QuadrantOf(bx0, by0)];

            (int predX, int predY) = MacroblockParser.PredictMvForPartition(
                mb, rawMbType, p, bx0, by0, bw, bh, curRefIdx,
                leftMb, topMb, topRightMb, topLeftMb);

            int mvX = predX + mvdX;
            int mvY = predY + mvdY;

            mb.InterPartitions.Add(new MvPartition(partRects[p].X, partRects[p].Y, partRects[p].W, partRects[p].H,
                                                    curRefIdx, mvX, mvY));
            MacroblockParser.FillBlockMvs(mb, bx0, by0, bw, bh, mvX, mvY);
            MacroblockParser.FillBlockMvds(mb, bx0, by0, bw, bh, mvdX, mvdY);
        }
    }

    private static void ParseInterMvds_P8x8(
        CabacDecoder cabac, Macroblock mb, SubMbType[] subMbTypes, int[] refIdxPerQuadrant,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 8;
            int qy = (q >> 1) * 8;
            var (subW, subH) = SubMbTypeOps.SubMbPartSize(subMbTypes[q]);
            int numSubParts = SubMbTypeOps.NumSubMbPart(subMbTypes[q]);

            for (int sp = 0; sp < numSubParts; sp++)
            {
                int spx, spy;
                if (subW == 8 && subH == 8) { spx = 0; spy = 0; }
                else if (subW == 8 && subH == 4) { spx = 0; spy = sp * 4; }
                else if (subW == 4 && subH == 8) { spx = sp * 4; spy = 0; }
                else { spx = (sp & 1) * 4; spy = (sp >> 1) * 4; }

                int partX = qx + spx;
                int partY = qy + spy;
                int bx0 = partX / 4;
                int by0 = partY / 4;
                int bw = subW / 4;
                int bh = subH / 4;

                int sumX = NeighborAbsMvdSumX(mb, bx0, by0, leftMb, topMb, topRightMb, topLeftMb);
                int sumY = NeighborAbsMvdSumY(mb, bx0, by0, leftMb, topMb, topRightMb, topLeftMb);
                int mvdX = DecodeMvd(cabac, sumX, ctxBase: 40);
                int mvdY = DecodeMvd(cabac, sumY, ctxBase: 47);

                int curRefIdx = refIdxPerQuadrant[q];

                (int predX, int predY) = MacroblockParser.PredictMvForPartition(
                    mb, 0 /*standard median for sub-8x8*/, 0,
                    bx0, by0, bw, bh, curRefIdx,
                    leftMb, topMb, topRightMb, topLeftMb);

                int mvX = predX + mvdX;
                int mvY = predY + mvdY;

                mb.InterPartitions.Add(new MvPartition(partX, partY, subW, subH, curRefIdx, mvX, mvY));
                MacroblockParser.FillBlockMvs(mb, bx0, by0, bw, bh, mvX, mvY);
                MacroblockParser.FillBlockMvds(mb, bx0, by0, bw, bh, mvdX, mvdY);
                // Critical: per-block MV gets written so subsequent partitions' neighbor
                // sums see the just-decoded MV. (FillBlockMvs handles this.)
            }
        }
    }

    private static int QuadrantOf(int bx, int by) => MacroblockParser.QuadrantOf(bx, by);

    // -----------------------------------------------------------------
    // mvd-component absMvdSum from spatial neighbors (A=left, B=top) of
    // the partition's top-left 4x4 block. For per-component, sum |mvd|
    // where mvd is the *motion-vector difference*. We approximate with
    // |MV - predMV| being unavailable here — H.264 spec actually requires
    // the **absMvdComp** stored per 4x4 block. For our minimal implementation
    // (single-MB / consistent neighbors) we use the *MV* values, since
    // x264 P-MBs with similar MVs produce similar mvds. Strictly this
    // requires storing per-block mvd; we'll need that for full correctness.
    // -----------------------------------------------------------------
    private static int NeighborAbsMvdSumX(Macroblock cur, int bx, int by,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        var A = MacroblockParser.GetMvNeighborPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb);
        var B = MacroblockParser.GetMvNeighborPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        return AbsMvdComp(A, cur, bx - 1, by, leftMb, topMb, topRightMb, topLeftMb, x: true)
             + AbsMvdComp(B, cur, bx, by - 1, leftMb, topMb, topRightMb, topLeftMb, x: true);
    }

    private static int NeighborAbsMvdSumY(Macroblock cur, int bx, int by,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        var A = MacroblockParser.GetMvNeighborPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb);
        var B = MacroblockParser.GetMvNeighborPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        return AbsMvdComp(A, cur, bx - 1, by, leftMb, topMb, topRightMb, topLeftMb, x: false)
             + AbsMvdComp(B, cur, bx, by - 1, leftMb, topMb, topRightMb, topLeftMb, x: false);
    }

    private static int AbsMvdComp(
        MacroblockParser.MvNeighbor nb, Macroblock cur, int bx, int by,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        bool x)
    {
        if (!nb.Avail || nb.RefIdx < 0) return 0; // unavailable/intra
        // Resolve the actual block-storing MB to pull the stored mvd.
        Macroblock? mb;
        int nbBx, nbBy;
        if (bx >= 0 && by >= 0 && bx <= 3 && by <= 3) { mb = cur; nbBx = bx; nbBy = by; }
        else if (bx < 0 && by >= 0 && by <= 3) { mb = leftMb; nbBx = 3; nbBy = by; }
        else if (by < 0 && bx >= 0 && bx <= 3) { mb = topMb; nbBx = bx; nbBy = 3; }
        else if (bx < 0 && by < 0) { mb = topLeftMb; nbBx = 3; nbBy = 3; }
        else if (bx > 3 && by < 0) { mb = topRightMb; nbBx = 0; nbBy = 3; }
        else { return 0; }
        if (mb is null) return 0;
        int idx = MacroblockParser.SpatialToRaster(nbBx, nbBy);
        int v = x ? mb.MvdL0XBlock[idx] : mb.MvdL0YBlock[idx];
        return Math.Abs(v);
    }

    // ---------------------------------------------------------------------
    // coded_block_pattern (separate luma/chroma binarizations).
    // Luma: 4 bins, one per 8x8 luma sub-block (ctx 73..76).
    // ---------------------------------------------------------------------
    internal static int DecodeCbpLuma(CabacDecoder cabac, Macroblock cur,
        Macroblock? leftMb, Macroblock? topMb)
    {
        // luma 8x8 sub-block layout:
        //   0 1
        //   2 3
        int cbp = 0;
        for (int i = 0; i < 4; i++)
        {
            int condA = LumaCbpNeighbor8x8(i, isLeft: true, cur, cbp, leftMb, topMb);
            int condB = LumaCbpNeighbor8x8(i, isLeft: false, cur, cbp, leftMb, topMb);
            // condTermFlagN = (neighbor's cbp bit == 0) ? 1 : 0
            int ctxIdxInc = condA + 2 * condB;
            int bit = cabac.DecodeBin(73 + ctxIdxInc);
            cbp |= bit << i;
        }
        return cbp;
    }

    /// <summary>
    /// For luma 8x8 block index i (0..3), return condTermFlag for neighbor.
    /// Per H.264 §9.3.3.1.1.4 / FFmpeg behavior: unavailable neighbor in inter-current
    /// path uses cbp=0x0F (all bits set → condTermFlag=0); P_Skip neighbor has CbpLuma=0
    /// (all bits clear → condTermFlag=1). Otherwise condTermFlag = (neighbor bit == 0) ? 1 : 0.
    /// </summary>
    private static int LumaCbpNeighbor8x8(int i, bool isLeft, Macroblock cur, int cbpSoFar,
        Macroblock? leftMb, Macroblock? topMb)
    {
        // Compute current block's coord in 8x8 grid: 0=(0,0),1=(1,0),2=(0,1),3=(1,1).
        int cx = i & 1, cy = i >> 1;

        if (isLeft)
        {
            int nx = cx - 1, ny = cy;
            if (nx >= 0)
            {
                int nbIdx = ny * 2 + nx;
                int bit = (cbpSoFar >> nbIdx) & 1;
                return bit == 0 ? 1 : 0;
            }
            // External left: unavailable → treat as fully coded (bit=1, condTerm=0); P_Skip → cbp=0.
            if (leftMb == null) return 0;
            int extCbp = leftMb.IsSkipped ? 0 : leftMb.CbpLuma;
            int extBit = (extCbp >> (cy * 2 + 1)) & 1;
            return extBit == 0 ? 1 : 0;
        }
        else
        {
            int nx = cx, ny = cy - 1;
            if (ny >= 0)
            {
                int nbIdx = ny * 2 + nx;
                int bit = (cbpSoFar >> nbIdx) & 1;
                return bit == 0 ? 1 : 0;
            }
            if (topMb == null) return 0;
            int extCbp = topMb.IsSkipped ? 0 : topMb.CbpLuma;
            int extBit = (extCbp >> (1 * 2 + cx)) & 1;
            return extBit == 0 ? 1 : 0;
        }
    }

    /// <summary>
    /// CBP chroma binarization (TU, cMax=2). bin0 ctx 77+inc, bin1 ctx 81+inc.
    /// </summary>
    internal static int DecodeCbpChroma(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        // bin0: condTermFlag = (mbN avail && !skip && cbpChroma(N) != 0) ? 1 : 0
        int condA0 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma != 0) ? 1 : 0;
        int condB0 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma != 0) ? 1 : 0;
        int b0 = cabac.DecodeBin(77 + condA0 + 2 * condB0);
        if (b0 == 0) return 0;

        // bin1: condTermFlag = (mbN avail && !skip && cbpChroma(N) == 2) ? 1 : 0
        int condA1 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma == 2) ? 1 : 0;
        int condB1 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma == 2) ? 1 : 0;
        int b1 = cabac.DecodeBin(81 + condA1 + 2 * condB1);
        return b1 == 1 ? 2 : 1;
    }

    // ---------------------------------------------------------------------
    // Inter residual: 16 Luma 4x4 blocks (CbpLuma-gated, ctxBlockCat=2)
    //                 + chroma DC (Cat=3) + chroma AC (Cat=4).
    // For inter MBs with unavailable neighbors, condTermFlag defaults to 0.
    // ---------------------------------------------------------------------
    internal static void ReadResidualInter(
        CabacDecoder cabac, Macroblock mb, Macroblock? leftMb, Macroblock? topMb)
    {
        Span<int> coeffs = stackalloc int[16];

        if (mb.TransformSize8x8)
        {
            // 4 luma 8x8 blocks. CBP-luma bit i8 gates 8x8 block i8 directly (no CBF read).
            // Per spec: the 8x8 block's CBP bit propagates to the contained 4 4x4 blocks'
            // LumaAcCbf (for downstream deblocking/neighbor derivation).
            Span<int> coeffs8 = stackalloc int[64];
            for (int i8 = 0; i8 < 4; i8++)
            {
                bool coded = (mb.CbpLuma & (1 << i8)) != 0;
                int bx0 = (i8 & 1) * 2, by0 = (i8 >> 1) * 2;
                for (int sy = 0; sy < 2; sy++)
                    for (int sx = 0; sx < 2; sx++)
                    {
                        int idx = MacroblockParser.SpatialToRaster(bx0 + sx, by0 + sy);
                        mb.LumaAcCbf[idx] = coded;
                        if (coded) mb.NonZeroCountLuma[idx] = 1;
                    }
                if (!coded) continue;
                CabacResidual.ReadResidualBlock8x8(cabac, coeffs8);
                int total = 0;
                for (int j = 0; j < 64; j++)
                {
                    mb.Luma8x8[i8, j] = coeffs8[j];
                    if (coeffs8[j] != 0) total++;
                }
                mb.NonZeroCountLuma8x8[i8] = total;
            }
        }
        else
        {
            // ---- Luma 4x4 blocks (CbpLuma bit per 8x8 sub-block) ----
            for (int i = 0; i < 16; i++)
            {
                bool blockCoded = (mb.CbpLuma & (1 << (i >> 2))) != 0;
                if (!blockCoded)
                {
                    mb.LumaAcCbf[i] = false;
                    continue;
                }
                (int cA, int cB) = LumaAcNeighborCbfInter(i, mb, leftMb, topMb);
                bool acCbf = CabacResidual.ReadResidualBlock(
                    cabac, coeffs, maxNumCoeff: 16, ctxBlockCat: CabacResidual.CatLuma4x4,
                    condTermFlagA: cA, condTermFlagB: cB);
                mb.LumaAcCbf[i] = acCbf;
                if (acCbf)
                {
                    mb.NonZeroCountLuma[i] = 1;
                    for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
                }
            }
        }

        // ---- Chroma DC ----
        if ((mb.CbpChroma & 3) != 0)
        {
            Span<int> dcCoeffs = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                int caC = (leftMb == null || leftMb.IsSkipped) ? 0 : (leftMb.ChromaDcCbf[c] ? 1 : 0);
                int cbC = (topMb == null || topMb.IsSkipped) ? 0 : (topMb.ChromaDcCbf[c] ? 1 : 0);
                bool cbf = CabacResidual.ReadResidualBlock(
                    cabac, dcCoeffs, maxNumCoeff: 4, ctxBlockCat: CabacResidual.CatChromaDc,
                    condTermFlagA: caC, condTermFlagB: cbC);
                mb.ChromaDcCbf[c] = cbf;
                if (cbf)
                {
                    for (int j = 0; j < 4; j++) mb.ChromaDc[c, j] = dcCoeffs[j];
                }
            }
        }

        // ---- Chroma AC ----
        if ((mb.CbpChroma & 2) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    (int cA, int cB) = ChromaAcNeighborCbfInter(c, i, mb, leftMb, topMb);
                    bool acCbf = CabacResidual.ReadResidualBlock(
                        cabac, coeffs, maxNumCoeff: 15, ctxBlockCat: CabacResidual.CatChromaAc,
                        condTermFlagA: cA, condTermFlagB: cB);
                    mb.ChromaAcCbf[c, i] = acCbf;
                    if (acCbf)
                    {
                        mb.NonZeroCountChromaAc[c, i] = 1;
                        for (int j = 0; j < 16; j++) mb.ChromaAc[c, i, j] = coeffs[j];
                    }
                }
            }
        }
    }

    private static (int A, int B) LumaAcNeighborCbfInter(int blockIdx, Macroblock cur,
        Macroblock? leftMb, Macroblock? topMb)
    {
        (int x, int y) = MacroblockParser.LumaBlockPos[blockIdx];

        int condA;
        if (x > 0) condA = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x - 1, y)] ? 1 : 0;
        else if (leftMb == null || leftMb.IsSkipped) condA = 0; // inter: default 0
        else condA = leftMb.LumaAcCbf[MacroblockParser.SpatialToRaster(3, y)] ? 1 : 0;

        int condB;
        if (y > 0) condB = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x, y - 1)] ? 1 : 0;
        else if (topMb == null || topMb.IsSkipped) condB = 0;
        else condB = topMb.LumaAcCbf[MacroblockParser.SpatialToRaster(x, 3)] ? 1 : 0;

        return (condA, condB);
    }

    private static (int A, int B) ChromaAcNeighborCbfInter(int comp, int blockIdx, Macroblock cur,
        Macroblock? leftMb, Macroblock? topMb)
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
