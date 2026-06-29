using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Decoder.Cabac;

/// <summary>
/// CABAC parser for B-slice non-skip macroblocks (spec §7.3.5.1 + §9.3.3.1).
/// Mirrors CabacSliceP but handles per-direction (L0/L1) ref_idx and mvd plus
/// the B-specific mb_type / sub_mb_type binarization trees (Tables 9-37, 9-38).
/// </summary>
internal static class CabacSliceB
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
        bool transform8x8ModeFlag = false,
        Macroblock? colocatedMb = null,
        TemporalDirectContext? tdCtx = null,
        bool direct8x8InferenceFlag = true)
    {
        int mbTypeCode = DecodeMbTypeB(cabac, leftMb, topMb);

        // Intra branch: code >= 23 maps to I-slice mb_type code (code - 23).
        if (mbTypeCode >= 23)
        {
            int iMbType = CabacSliceI.DecodeIntraMbTypeAtOffset(cabac, ctxIdxOffset: 32);
            if (iMbType == 25)
            {
                return CabacSliceI.ParsePcmMb(cabac, mbAddress, qpYRunning, ref prevMbQpDeltaState);
            }
            return CabacSliceI.ParseIntraMbBody(cabac, iMbType, leftMb, topMb, mbAddress,
                                                ref qpYRunning, ref prevMbQpDeltaState,
                                                transform8x8ModeFlag);
        }

        // Inter B mb_type 0..22.
        var info = BMbType.Info(mbTypeCode);
        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = new IntraMbType(mbTypeCode, MbPartPredMode.PredL0, default, 0, 0),
            IsBInter = true,
        };

        // ---- mb_pred / sub_mb_pred ----
        ParseBInterMbPred(cabac, mb, info, sliceHeader, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, tdCtx, direct8x8InferenceFlag);

        // ---- coded_block_pattern (separate luma/chroma CABAC binarizations, same as P) ----
        int cbpLuma = CabacSliceP.DecodeCbpLuma(cabac, mb, leftMb, topMb);
        int cbpChroma = CabacSliceP.DecodeCbpChroma(cabac, leftMb, topMb);
        mb.CbpLuma = cbpLuma;
        mb.CbpChroma = cbpChroma;

        // transform_size_8x8_flag for B-inter (spec §7.3.5.1).
        // Eligible when (16x16 / 16x8 / 8x16 / B_Direct_16x16 partition shape) OR
        // (B_8x8 with noSubMbPartSizeLessThan8x8Flag==true). B_Direct_16x16 (mbTypeCode 0)
        // is explicitly included — OpenH264 IS_DIRECT branch, ref decode_slice.cpp:1194.
        if (transform8x8ModeFlag && cbpLuma > 0)
        {
            bool eligible = (mbTypeCode >= 0 && mbTypeCode <= 21)
                            || (mbTypeCode == 22 && mb.NoSubMbPartSizeLessThan8x8Flag);
            if (eligible)
            {
                int ctxA = (leftMb != null && leftMb.TransformSize8x8) ? 1 : 0;
                int ctxB = (topMb != null && topMb.TransformSize8x8) ? 1 : 0;
                int flag = cabac.DecodeBin(399 + ctxA + ctxB);
                mb.TransformSize8x8 = flag == 1;
            }
        }

        // ---- mb_qp_delta + residual ----
        if (cbpLuma != 0 || cbpChroma != 0)
        {
            int mbQpDelta = CabacCommon.DecodeMbQpDelta(cabac, ref prevMbQpDeltaState);
            qpYRunning = CabacCommon.Mod52(qpYRunning + mbQpDelta);
            mb.QpY = qpYRunning;
            CabacSliceP.ReadResidualInter(cabac, mb, leftMb, topMb);
        }
        else
        {
            mb.QpY = qpYRunning;
            prevMbQpDeltaState = 0;
        }
        return mb;
    }

    // ---------------------------------------------------------------------
    // B mb_type binarization (spec Table 9-37, ctxIdxOffset=27).
    // Bin string per mb_type:
    //   0  B_Direct_16x16        : 0
    //   1  B_L0_16x16            : 1 0 0
    //   2  B_L1_16x16            : 1 0 1
    //   3  B_Bi_16x16            : 1 1 0 0 0 0
    //   4  B_L0_L0_16x8          : 1 1 0 0 0 1
    //   5  B_L0_L0_8x16          : 1 1 0 0 1 0
    //   6  B_L1_L1_16x8          : 1 1 0 0 1 1
    //   7  B_L1_L1_8x16          : 1 1 0 1 0 0
    //   8  B_L0_L1_16x8          : 1 1 0 1 0 1
    //   9  B_L0_L1_8x16          : 1 1 0 1 1 0
    //   10 B_L1_L0_16x8          : 1 1 0 1 1 1
    //   11 B_L1_L0_8x16          : 1 1 1 1 1 0
    //   12 B_L0_Bi_16x8          : 1 1 1 0 0 0 0
    //   13 B_L0_Bi_8x16          : 1 1 1 0 0 0 1
    //   14 B_L1_Bi_16x8          : 1 1 1 0 0 1 0
    //   15 B_L1_Bi_8x16          : 1 1 1 0 0 1 1
    //   16 B_Bi_L0_16x8          : 1 1 1 0 1 0 0
    //   17 B_Bi_L0_8x16          : 1 1 1 0 1 0 1
    //   18 B_Bi_L1_16x8          : 1 1 1 0 1 1 0
    //   19 B_Bi_L1_8x16          : 1 1 1 0 1 1 1
    //   20 B_Bi_Bi_16x8          : 1 1 1 1 0 0 0
    //   21 B_Bi_Bi_8x16          : 1 1 1 1 0 0 1
    //   22 B_8x8                 : 1 1 1 1 1 1
    //   23..48 intra             : 1 1 1 1 0 1 + I-slice mb_type tree (offset 32)
    // ctxIdxInc per binIdx (Table 9-39, B slice):
    //   binIdx 0 : condA + condB (mbN avail && !B_Skip && mb_type(N) != B_Direct → 1 else 0)
    //   binIdx 1 : 3
    //   binIdx 2 : bin1==0 ? 4 : 5
    //   binIdx 3+: 5
    // ---------------------------------------------------------------------
    private static int DecodeMbTypeB(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        int condA = NeighborMbTypeFlagB(leftMb);
        int condB = NeighborMbTypeFlagB(topMb);
        int b0 = cabac.DecodeBin(27 + condA + condB);
        if (b0 == 0) return 0; // B_Direct_16x16

        int b1 = cabac.DecodeBin(30);
        // ctxIdxInc for binIdx 2 depends on b1 (spec §9.3.3.1.1 Table 9-39, B-slice mb_type):
        //   b1 == 0 -> ctxIdxInc = 5 (ctx 32); b1 == 1 -> ctxIdxInc = 4 (ctx 31).
        int b2 = cabac.DecodeBin(b1 == 0 ? 32 : 31);
        if (b1 == 0)
        {
            // Prefix "1 0 b2" -> B_L0_16x16 (1) or B_L1_16x16 (2).
            return b2 == 0 ? 1 : 2;
        }

        // Prefix "1 1 ..." — read further bins (all ctx 32).
        int b3 = cabac.DecodeBin(32);
        int b4 = cabac.DecodeBin(32);
        int b5 = cabac.DecodeBin(32);
        if (b2 == 0)
        {
            // Prefix "1 1 0 b3 b4 b5" -> codes 3..10 (8 codes).
            int idx = (b3 << 2) | (b4 << 1) | b5;
            return 3 + idx;
        }
        // b2 == 1, prefix "1 1 1 b3 b4 b5"
        if (b3 == 1 && b4 == 1 && b5 == 0)
        {
            return 11; // "1 1 1 1 1 0" -> B_L1_L0_8x16
        }
        if (b3 == 1 && b4 == 1 && b5 == 1)
        {
            // "1 1 1 1 1 1" -> B_8x8 (22)
            return 22;
        }
        if (b3 == 1 && b4 == 0 && b5 == 1)
        {
            // "1 1 1 1 0 1" -> intra branch.
            return 23;
        }
        if (b3 == 1 && b4 == 0 && b5 == 0)
        {
            // "1 1 1 1 0 0 b6" -> codes 20 (b6=0) or 21 (b6=1).
            int b6 = cabac.DecodeBin(32);
            return b6 == 0 ? 20 : 21;
        }
        // b3 == 0, prefix "1 1 1 0 b4 b5 b6" -> codes 12..19 (8 codes).
        int b6t = cabac.DecodeBin(32);
        int idx2 = (b4 << 2) | (b5 << 1) | b6t;
        return 12 + idx2;
    }

    private static int NeighborMbTypeFlagB(Macroblock? mb)
    {
        if (mb == null) return 0;
        if (mb.IsBSkip || mb.IsSkipped) return 0;
        // B_Direct_16x16 has RawMbType == 0 and IsBInter == true.
        if (mb.IsBInter && mb.Type.RawMbType == 0) return 0;
        return 1;
    }

    // ---------------------------------------------------------------------
    // B sub_mb_type binarization (spec Table 9-38, ctxIdxOffset=36).
    //   0  B_Direct_8x8 : 0
    //   1  B_L0_8x8     : 1 0 0
    //   2  B_L1_8x8     : 1 0 1
    //   3  B_Bi_8x8     : 1 1 0 0 0
    //   4  B_L0_8x4     : 1 1 0 0 1
    //   5  B_L0_4x8     : 1 1 0 1 0
    //   6  B_L1_8x4     : 1 1 0 1 1
    //   7  B_L1_4x8     : 1 1 1 0 0 0
    //   8  B_Bi_8x4     : 1 1 1 0 0 1
    //   9  B_Bi_4x8     : 1 1 1 0 1 0
    //   10 B_L0_4x4     : 1 1 1 0 1 1
    //   11 B_L1_4x4     : 1 1 1 1 0
    //   12 B_Bi_4x4     : 1 1 1 1 1
    // ctxIdxInc per binIdx: 0→0 (ctx 36), 1→1 (ctx 37),
    //   2→ bin1==1 ? 2 : 3 (ctx 38 or 39), 3+→3 (ctx 39).
    // ---------------------------------------------------------------------
    private static BSubMbType DecodeSubMbTypeB(CabacDecoder cabac)
    {
        int b0 = cabac.DecodeBin(36);
        if (b0 == 0) return BSubMbType.Direct_8x8;
        int b1 = cabac.DecodeBin(37);
        int b2 = cabac.DecodeBin(b1 == 1 ? 38 : 39);
        if (b1 == 0)
        {
            // Prefix "1 0 b2"
            return b2 == 0 ? BSubMbType.L0_8x8 : BSubMbType.L1_8x8;
        }
        // Prefix "1 1 b2 ..."
        int b3 = cabac.DecodeBin(39);
        if (b2 == 0)
        {
            // Prefix "1 1 0 b3 b4" -> codes 3..6.
            int b4 = cabac.DecodeBin(39);
            int idx = (b3 << 1) | b4;
            return idx switch
            {
                0 => BSubMbType.Bi_8x8,
                1 => BSubMbType.L0_8x4,
                2 => BSubMbType.L0_4x8,
                _ => BSubMbType.L1_8x4,
            };
        }
        // Prefix "1 1 1 b3 ..."
        if (b3 == 1)
        {
            // "1 1 1 1 b4" -> codes 11..12.
            int b4 = cabac.DecodeBin(39);
            return b4 == 0 ? BSubMbType.L1_4x4 : BSubMbType.Bi_4x4;
        }
        // Prefix "1 1 1 0 b4 b5" -> codes 7..10.
        int b4n = cabac.DecodeBin(39);
        int b5 = cabac.DecodeBin(39);
        int idx2 = (b4n << 1) | b5;
        return idx2 switch
        {
            0 => BSubMbType.L1_4x8,
            1 => BSubMbType.Bi_8x4,
            2 => BSubMbType.Bi_4x8,
            _ => BSubMbType.L0_4x4,
        };
    }

    // ---------------------------------------------------------------------
    // Read ref_idx unary (shared between L0 and L1; same ctxIdxOffset=54).
    // ---------------------------------------------------------------------
    private static int DecodeRefIdx(CabacDecoder cabac, int condA, int condB)
    {
        int ctxIdxInc0 = condA + 2 * condB;
        int b0 = cabac.DecodeBin(54 + ctxIdxInc0);
        if (b0 == 0) return 0;
        int n = 1;
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
    // mvd: same as P-slice. ctxBase=40 for X, 47 for Y. Shared between L0 and L1.
    // ---------------------------------------------------------------------
    private static int DecodeMvd(CabacDecoder cabac, int absMvdSum, int ctxBase)
    {
        int ctxIdxInc0 = absMvdSum < 3 ? 0 : (absMvdSum < 33 ? 1 : 2);
        int b0 = cabac.DecodeBin(ctxBase + ctxIdxInc0);
        if (b0 == 0) return 0;

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
    // mb_pred / sub_mb_pred for B-slice inter MBs.
    // ---------------------------------------------------------------------
    private static void ParseBInterMbPred(
        CabacDecoder cabac, Macroblock mb, BMbTypeInfo info, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null,
        TemporalDirectContext? tdCtx = null,
        bool direct8x8InferenceFlag = true)
    {
        int rawMb = info.RawMbType;
        if (rawMb == 0)
        {
            // B_Direct_16x16: no syntax; derive via direct mode.
            BDirectMode.ApplyDirect16x16(mb, sliceHeader, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, tdCtx);
            return;
        }

        if (rawMb == 22)
        {
            // B_8x8: 4 CABAC sub_mb_types, then per-direction ref/mvd.
            var subTypes = new BSubMbType[4];
            for (int i = 0; i < 4; i++)
            {
                subTypes[i] = DecodeSubMbTypeB(cabac);
            }
            // noSubMbPartSizeLessThan8x8Flag (spec §7.4.5.2): for B_8x8, AND over the 4 subs.
            // Each sub contributes (sub == B_Direct_8x8 ? direct_8x8_inference_flag : NumSubMbPart==1).
            bool noLessThan = true;
            for (int i = 0; i < 4; i++)
            {
                if (subTypes[i] == BSubMbType.Direct_8x8)
                {
                    if (!direct8x8InferenceFlag) { noLessThan = false; break; }
                }
                else if (BSubMbTypeOps.NumSubMbPart(subTypes[i]) > 1)
                {
                    noLessThan = false; break;
                }
            }
            mb.NoSubMbPartSizeLessThan8x8Flag = noLessThan;
            ParseB8x8RefAndMv(cabac, mb, subTypes, sliceHeader,
                leftMb, topMb, topRightMb, topLeftMb, colocatedMb, tdCtx);
            return;
        }

        // mb_type 1..21: 1 or 2 partitions with fixed directions.
        ParseB16Partitions(cabac, mb, info, sliceHeader, leftMb, topMb, topRightMb, topLeftMb);
    }

    private static void ParseB16Partitions(
        CabacDecoder cabac, Macroblock mb, BMbTypeInfo info, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        int numPart = info.NumMbPart;
        uint maxRefL0 = sliceHeader.NumRefIdxL0ActiveMinus1;
        uint maxRefL1 = sliceHeader.NumRefIdxL1ActiveMinus1;

        var partRects = new (int X, int Y, int W, int H)[numPart];
        if (numPart == 1)
        {
            partRects[0] = (0, 0, 16, 16);
        }
        else if (info.PartWidth == 16)
        {
            partRects[0] = (0, 0, 16, 8);
            partRects[1] = (0, 8, 16, 8);
        }
        else
        {
            partRects[0] = (0, 0, 8, 16);
            partRects[1] = (8, 0, 8, 16);
        }

        // Read ref_idx_l0 per partition (only if direction uses L0).
        // Replicate (and pre-fill PredFlagL0Block for the partition's 4x4 blocks)
        // after each decode so subsequent partitions' condTermFlag reads
        // (spec §9.3.3.1.1.6) see the just-set value when the spatial neighbor is
        // an earlier partition of the SAME MB. GetMvNeighborList uses PredFlag to
        // decide whether to read refIdx, so the flag must be set in-loop too.
        int[] refL0 = new int[numPart];
        int[] refL1 = new int[numPart];
        for (int p = 0; p < numPart; p++)
        {
            var dir = info.DirForPart(p);
            bool useL0 = dir == BPredDir.L0 || dir == BPredDir.Bi;
            refL0[p] = useL0 ? ReadRefIdxIfNeeded(cabac, maxRefL0,
                mb, partRects[p], leftMb, topMb, topRightMb, topLeftMb, listX: 0) : -1;
            ReplicateBRefAcross16(info, refL0, mb.RefIdxL08x8);
            if (useL0)
            {
                MacroblockParser.SetPredFlag(mb.PredFlagL0Block,
                    partRects[p].X / 4, partRects[p].Y / 4, partRects[p].W / 4, partRects[p].H / 4, 1);
            }
        }
        for (int p = 0; p < numPart; p++)
        {
            var dir = info.DirForPart(p);
            bool useL1 = dir == BPredDir.L1 || dir == BPredDir.Bi;
            refL1[p] = useL1 ? ReadRefIdxIfNeeded(cabac, maxRefL1,
                mb, partRects[p], leftMb, topMb, topRightMb, topLeftMb, listX: 1) : -1;
            ReplicateBRefAcross16(info, refL1, mb.RefIdxL18x8);
            if (useL1)
            {
                MacroblockParser.SetPredFlag(mb.PredFlagL1Block,
                    partRects[p].X / 4, partRects[p].Y / 4, partRects[p].W / 4, partRects[p].H / 4, 1);
            }
        }

        // Replicate per-quadrant (final, idempotent — values already in place from the loops).
        ReplicateBRefAcross16(info, refL0, mb.RefIdxL08x8);
        ReplicateBRefAcross16(info, refL1, mb.RefIdxL18x8);

        // mvds: L0 first then L1. Fill MvdL{0,1}{X,Y}Block per partition immediately
        // after decoding so subsequent partitions' neighbor-absMvdSum lookups
        // (spec §9.3.3.1.1.7) see the just-decoded value when the spatial neighbor
        // lies in an earlier partition of THIS MB.
        var mvdL0 = new (int X, int Y)[numPart];
        var mvdL1 = new (int X, int Y)[numPart];
        for (int p = 0; p < numPart; p++)
        {
            var dir = info.DirForPart(p);
            bool useL0 = dir == BPredDir.L0 || dir == BPredDir.Bi;
            if (useL0)
            {
                var rect = partRects[p];
                int bx = rect.X / 4, by = rect.Y / 4;
                int bw = rect.W / 4, bh = rect.H / 4;
                int sumX = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, x: true);
                int sumY = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, x: false);
                mvdL0[p].X = DecodeMvd(cabac, sumX, ctxBase: 40);
                mvdL0[p].Y = DecodeMvd(cabac, sumY, ctxBase: 47);
                MacroblockParser.FillBlockMvdsL0(mb, bx, by, bw, bh, mvdL0[p].X, mvdL0[p].Y);
            }
        }
        for (int p = 0; p < numPart; p++)
        {
            var dir = info.DirForPart(p);
            bool useL1 = dir == BPredDir.L1 || dir == BPredDir.Bi;
            if (useL1)
            {
                var rect = partRects[p];
                int bx = rect.X / 4, by = rect.Y / 4;
                int bw = rect.W / 4, bh = rect.H / 4;
                int sumX = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, x: true);
                int sumY = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, x: false);
                mvdL1[p].X = DecodeMvd(cabac, sumX, ctxBase: 40);
                mvdL1[p].Y = DecodeMvd(cabac, sumY, ctxBase: 47);
                MacroblockParser.FillBlockMvdsL1(mb, bx, by, bw, bh, mvdL1[p].X, mvdL1[p].Y);
            }
        }

        // Apply MV prediction and build BInterPartitions.
        for (int p = 0; p < numPart; p++)
        {
            var rect = partRects[p];
            var dir = info.DirForPart(p);
            int bx = rect.X / 4, by = rect.Y / 4, bw = rect.W / 4, bh = rect.H / 4;
            int mvL0X = 0, mvL0Y = 0, mvL1X = 0, mvL1Y = 0;

            if (dir == BPredDir.L0 || dir == BPredDir.Bi)
            {
                (int predX, int predY) = MacroblockParser.PredictMvForPartitionListB(
                    mb, info.RawMbType, p, bx, by, bw, bh, refL0[p], listX: 0,
                    leftMb, topMb, topRightMb, topLeftMb);
                mvL0X = predX + mvdL0[p].X;
                mvL0Y = predY + mvdL0[p].Y;
                MacroblockParser.FillBlockMvsL0(mb, bx, by, bw, bh, mvL0X, mvL0Y);
                MacroblockParser.FillBlockMvdsL0(mb, bx, by, bw, bh, mvdL0[p].X, mvdL0[p].Y);
                MacroblockParser.SetPredFlag(mb.PredFlagL0Block, bx, by, bw, bh, 1);
            }
            if (dir == BPredDir.L1 || dir == BPredDir.Bi)
            {
                (int predX, int predY) = MacroblockParser.PredictMvForPartitionListB(
                    mb, info.RawMbType, p, bx, by, bw, bh, refL1[p], listX: 1,
                    leftMb, topMb, topRightMb, topLeftMb);
                mvL1X = predX + mvdL1[p].X;
                mvL1Y = predY + mvdL1[p].Y;
                MacroblockParser.FillBlockMvsL1(mb, bx, by, bw, bh, mvL1X, mvL1Y);
                MacroblockParser.FillBlockMvdsL1(mb, bx, by, bw, bh, mvdL1[p].X, mvdL1[p].Y);
                MacroblockParser.SetPredFlag(mb.PredFlagL1Block, bx, by, bw, bh, 1);
            }

            mb.BInterPartitions.Add(new BMvPartition(
                rect.X, rect.Y, rect.W, rect.H, dir,
                refL0[p], mvL0X, mvL0Y,
                refL1[p], mvL1X, mvL1Y));
        }
    }

    private static void ParseB8x8RefAndMv(
        CabacDecoder cabac, Macroblock mb, BSubMbType[] subTypes, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null,
        TemporalDirectContext? tdCtx = null)
    {
        uint maxRefL0 = sliceHeader.NumRefIdxL0ActiveMinus1;
        uint maxRefL1 = sliceHeader.NumRefIdxL1ActiveMinus1;

        int[] refL0 = new int[4];
        int[] refL1 = new int[4];
        // Decode each ref_idx and immediately write into mb.RefIdxL{0,1}8x8[q] AND
        // mark the corresponding 4x4 blocks in PredFlagL{0,1}Block so later
        // quadrants' condTermFlag reads (spec §9.3.3.1.1.6) see the just-set value
        // when the spatial neighbor lies in an earlier quadrant of THIS MB.
        for (int q = 0; q < 4; q++)
        {
            var dir = BSubMbTypeOps.Dir(subTypes[q]);
            bool useL0 = dir == BPredDir.L0 || dir == BPredDir.Bi;
            refL0[q] = useL0 ? ReadRefIdxIfNeededQ(cabac, maxRefL0, mb, q,
                leftMb, topMb, topRightMb, topLeftMb, listX: 0) : -1;
            mb.RefIdxL08x8[q] = refL0[q] < 0 ? 0 : refL0[q];
            if (useL0)
            {
                int qx = (q & 1) * 2, qy = (q >> 1) * 2;
                MacroblockParser.SetPredFlag(mb.PredFlagL0Block, qx, qy, 2, 2, 1);
            }
        }
        for (int q = 0; q < 4; q++)
        {
            var dir = BSubMbTypeOps.Dir(subTypes[q]);
            bool useL1 = dir == BPredDir.L1 || dir == BPredDir.Bi;
            refL1[q] = useL1 ? ReadRefIdxIfNeededQ(cabac, maxRefL1, mb, q,
                leftMb, topMb, topRightMb, topLeftMb, listX: 1) : -1;
            mb.RefIdxL18x8[q] = refL1[q] < 0 ? 0 : refL1[q];
            if (useL1)
            {
                int qx = (q & 1) * 2, qy = (q >> 1) * 2;
                MacroblockParser.SetPredFlag(mb.PredFlagL1Block, qx, qy, 2, 2, 1);
            }
        }

        // mvd_l0 per sub-partition.
        for (int q = 0; q < 4; q++)
        {
            var sub = subTypes[q];
            var dir = BSubMbTypeOps.Dir(sub);
            if (dir != BPredDir.L0 && dir != BPredDir.Bi) continue;
            int n = BSubMbTypeOps.NumSubMbPart(sub);
            var (sw, sh) = BSubMbTypeOps.SubMbPartSize(sub);
            int qx = (q & 1) * 8, qy = (q >> 1) * 8;
            for (int sp = 0; sp < n; sp++)
            {
                int spx, spy;
                if (sw == 8 && sh == 8) { spx = 0; spy = 0; }
                else if (sw == 8 && sh == 4) { spx = 0; spy = sp * 4; }
                else if (sw == 4 && sh == 8) { spx = sp * 4; spy = 0; }
                else { spx = (sp & 1) * 4; spy = (sp >> 1) * 4; }
                int partX = qx + spx, partY = qy + spy;
                int bx = partX / 4, by = partY / 4, bw = sw / 4, bh = sh / 4;

                int sumX = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, x: true);
                int sumY = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 0, x: false);
                int mvdX = DecodeMvd(cabac, sumX, ctxBase: 40);
                int mvdY = DecodeMvd(cabac, sumY, ctxBase: 47);
                (int predX, int predY) = MacroblockParser.PredictMvForPartitionListB(mb, 0, 0,
                    bx, by, bw, bh, refL0[q], listX: 0,
                    leftMb, topMb, topRightMb, topLeftMb);
                int mvX = predX + mvdX, mvY = predY + mvdY;
                MacroblockParser.FillBlockMvsL0(mb, bx, by, bw, bh, mvX, mvY);
                MacroblockParser.FillBlockMvdsL0(mb, bx, by, bw, bh, mvdX, mvdY);
                MacroblockParser.SetPredFlag(mb.PredFlagL0Block, bx, by, bw, bh, 1);
            }
        }
        for (int q = 0; q < 4; q++)
        {
            var sub = subTypes[q];
            var dir = BSubMbTypeOps.Dir(sub);
            if (dir != BPredDir.L1 && dir != BPredDir.Bi) continue;
            int n = BSubMbTypeOps.NumSubMbPart(sub);
            var (sw, sh) = BSubMbTypeOps.SubMbPartSize(sub);
            int qx = (q & 1) * 8, qy = (q >> 1) * 8;
            for (int sp = 0; sp < n; sp++)
            {
                int spx, spy;
                if (sw == 8 && sh == 8) { spx = 0; spy = 0; }
                else if (sw == 8 && sh == 4) { spx = 0; spy = sp * 4; }
                else if (sw == 4 && sh == 8) { spx = sp * 4; spy = 0; }
                else { spx = (sp & 1) * 4; spy = (sp >> 1) * 4; }
                int partX = qx + spx, partY = qy + spy;
                int bx = partX / 4, by = partY / 4, bw = sw / 4, bh = sh / 4;

                int sumX = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, x: true);
                int sumY = NeighborAbsMvdSum(mb, bx, by, leftMb, topMb, topRightMb, topLeftMb, listX: 1, x: false);
                int mvdX = DecodeMvd(cabac, sumX, ctxBase: 40);
                int mvdY = DecodeMvd(cabac, sumY, ctxBase: 47);
                (int predX, int predY) = MacroblockParser.PredictMvForPartitionListB(mb, 0, 0,
                    bx, by, bw, bh, refL1[q], listX: 1,
                    leftMb, topMb, topRightMb, topLeftMb);
                int mvX = predX + mvdX, mvY = predY + mvdY;
                MacroblockParser.FillBlockMvsL1(mb, bx, by, bw, bh, mvX, mvY);
                MacroblockParser.FillBlockMvdsL1(mb, bx, by, bw, bh, mvdX, mvdY);
                MacroblockParser.SetPredFlag(mb.PredFlagL1Block, bx, by, bw, bh, 1);
            }
        }
        // Direct sub-blocks: derive MVs via direct mode per 8x8 quadrant.
        for (int q = 0; q < 4; q++)
        {
            if (BSubMbTypeOps.Dir(subTypes[q]) != BPredDir.Direct) continue;
            BDirectMode.ApplyDirect8x8(mb, q, sliceHeader, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, tdCtx);
        }

        // Build BInterPartitions.
        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 8, qy = (q >> 1) * 8;
            var sub = subTypes[q];
            var dir = BSubMbTypeOps.Dir(sub);
            int n = BSubMbTypeOps.NumSubMbPart(sub);
            var (sw, sh) = BSubMbTypeOps.SubMbPartSize(sub);
            for (int sp = 0; sp < n; sp++)
            {
                int spx, spy;
                if (sw == 8 && sh == 8) { spx = 0; spy = 0; }
                else if (sw == 8 && sh == 4) { spx = 0; spy = sp * 4; }
                else if (sw == 4 && sh == 8) { spx = sp * 4; spy = 0; }
                else { spx = (sp & 1) * 4; spy = (sp >> 1) * 4; }
                int bx = (qx + spx) / 4, by = (qy + spy) / 4;
                int idx = MacroblockParser.SpatialToRaster(bx, by);
                int mvL0X = mb.MvL0XBlock[idx], mvL0Y = mb.MvL0YBlock[idx];
                int mvL1X = mb.MvL1XBlock[idx], mvL1Y = mb.MvL1YBlock[idx];
                int rL0 = mb.PredFlagL0Block[idx] != 0 ? mb.RefIdxL08x8[q] : -1;
                int rL1 = mb.PredFlagL1Block[idx] != 0 ? mb.RefIdxL18x8[q] : -1;
                BPredDir effDir = dir;
                if (dir == BPredDir.Direct)
                {
                    if (mb.PredFlagL0Block[idx] != 0 && mb.PredFlagL1Block[idx] != 0) effDir = BPredDir.Bi;
                    else if (mb.PredFlagL0Block[idx] != 0) effDir = BPredDir.L0;
                    else if (mb.PredFlagL1Block[idx] != 0) effDir = BPredDir.L1;
                }
                mb.BInterPartitions.Add(new BMvPartition(qx + spx, qy + spy, sw, sh, effDir,
                    rL0, mvL0X, mvL0Y, rL1, mvL1X, mvL1Y));
            }
        }
    }

    private static void ReplicateBRefAcross16(BMbTypeInfo info, int[] partRef, int[] perQuadrant)
    {
        if (info.NumMbPart == 1)
        {
            for (int q = 0; q < 4; q++) perQuadrant[q] = partRef[0] < 0 ? 0 : partRef[0];
        }
        else if (info.PartWidth == 16)
        {
            perQuadrant[0] = perQuadrant[1] = partRef[0] < 0 ? 0 : partRef[0];
            perQuadrant[2] = perQuadrant[3] = partRef[1] < 0 ? 0 : partRef[1];
        }
        else
        {
            perQuadrant[0] = perQuadrant[2] = partRef[0] < 0 ? 0 : partRef[0];
            perQuadrant[1] = perQuadrant[3] = partRef[1] < 0 ? 0 : partRef[1];
        }
    }

    /// <summary>Decode ref_idx for one partition (16x16 / 16x8 / 8x16). ctxIdxInc for binIdx0
    /// uses neighbor predFlagL/refIdx at the partition's top-left 4x4 block.</summary>
    private static int ReadRefIdxIfNeeded(
        CabacDecoder cabac, uint maxRef,
        Macroblock cur, (int X, int Y, int W, int H) rect,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        int listX)
    {
        if (maxRef == 0) return 0;
        int bx = rect.X / 4, by = rect.Y / 4;
        var A = MacroblockParser.GetMvNeighborListPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        var B = MacroblockParser.GetMvNeighborListPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        // §9.3.3.1.1.6: refIdxZeroFlagN=1 if mbN is B_Skip or has direct prediction mode
        // (B_Direct_16x16, B_Direct_8x8) — those have no signaled refIdx, so condTerm=0.
        // §9.3.3.1.1.6: refIdxZeroFlagN=1 if mbN is B_Skip or has direct prediction mode
        // (B_Direct_16x16, B_Direct_8x8) — those have no signaled refIdx, so condTerm=0.
        int condA = (A.Avail && !A.IsDirect && A.RefIdx > 0) ? 1 : 0;
        int condB = (B.Avail && !B.IsDirect && B.RefIdx > 0) ? 1 : 0;
        return DecodeRefIdx(cabac, condA, condB);
    }

    private static int ReadRefIdxIfNeededQ(
        CabacDecoder cabac, uint maxRef, Macroblock cur, int q,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        int listX)
    {
        if (maxRef == 0) return 0;
        int bx = (q & 1) * 2, by = (q >> 1) * 2;
        var A = MacroblockParser.GetMvNeighborListPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        var B = MacroblockParser.GetMvNeighborListPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        // §9.3.3.1.1.6: refIdxZeroFlagN=1 if mbN is B_Skip or has direct prediction mode
        // (B_Direct_16x16, B_Direct_8x8) — those have no signaled refIdx, so condTerm=0.
        // §9.3.3.1.1.6: refIdxZeroFlagN=1 if mbN is B_Skip or has direct prediction mode
        // (B_Direct_16x16, B_Direct_8x8) — those have no signaled refIdx, so condTerm=0.
        int condA = (A.Avail && !A.IsDirect && A.RefIdx > 0) ? 1 : 0;
        int condB = (B.Avail && !B.IsDirect && B.RefIdx > 0) ? 1 : 0;
        return DecodeRefIdx(cabac, condA, condB);
    }

    // ---------------------------------------------------------------------
    // Per-direction absMvd neighbor sum for ctxIdxInc of bin0.
    // ---------------------------------------------------------------------
    private static int NeighborAbsMvdSum(
        Macroblock cur, int bx, int by,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        int listX, bool x)
    {
        return AbsMvdComp(cur, bx - 1, by, leftMb, topMb, topRightMb, topLeftMb, listX, x)
             + AbsMvdComp(cur, bx, by - 1, leftMb, topMb, topRightMb, topLeftMb, listX, x);
    }

    private static int AbsMvdComp(
        Macroblock cur, int bx, int by,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        int listX, bool x)
    {
        var nb = MacroblockParser.GetMvNeighborListPublic(bx, by, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        if (!nb.Avail || nb.RefIdx < 0) return 0;
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
        int v;
        if (listX == 0) v = x ? mb.MvdL0XBlock[idx] : mb.MvdL0YBlock[idx];
        else v = x ? mb.MvdL1XBlock[idx] : mb.MvdL1YBlock[idx];
        return Math.Abs(v);
    }
}
