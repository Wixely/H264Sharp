using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cavlc;
using H264Decoder.Encoder.Transform;
using H264Decoder.Transform;

namespace H264Decoder.Encoder.Mode;

/// <summary>
/// Phase 5a: B-slice macroblock encoder for 16x16 partition only (no sub-MB partitions, no
/// direct/skip). Picks per-MB between B_L0_16x16 (mb_type=1), B_L1_16x16 (mb_type=2), and
/// B_Bi_16x16 (mb_type=3). MV prediction uses spec §8.4.1.3.1 median over A/B/C neighbors,
/// per-list. Reference indices are implicit (num_ref_idx_active_lN_minus1=0).
/// </summary>
internal static class BMbEncoder
{
    /// <summary>Direction of a B-MB partition. Direct = B_Direct_16x16 (or B_Skip when zero residual).</summary>
    internal enum Dir : byte { L0 = 0, L1 = 1, Bi = 2, Direct = 3 }

    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static readonly int[] SpatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

    /// <summary>Holds the chosen B-inter mode plus its built bundle (prediction, residual,
    /// reconstruction) and the MVs used. <see cref="Bundle"/>.CbpLuma/CbpChroma reflect the actual
    /// chosen mode. For Direct mode, MvL{0,1}PerBlock hold the per-4x4 MVs (varying due to colZero
    /// override); for L0/L1/Bi the per-block arrays are uniform.</summary>
    internal sealed class BCandidate
    {
        public Dir Direction;
        public int MvL0X, MvL0Y;
        public int MvL1X, MvL1Y;
        public MacroblockEncoderInter.InterEncodeBundle Bundle = null!;
        public int TotalCost; // SAD + λ * mode-bits proxy

        /// <summary>Direct mode only: per-4x4 luma block MVs (raster index 0..15). Quarter-pel.</summary>
        public int[]? MvL0XPerBlock;
        public int[]? MvL0YPerBlock;
        public int[]? MvL1XPerBlock;
        public int[]? MvL1YPerBlock;
        /// <summary>Direct mode only: which of L0/L1 is used (matches spec's predFlag derivation).</summary>
        public bool DirectUseL0;
        public bool DirectUseL1;
        /// <summary>Direct mode only: refIdx for each list (0 in our scope; -1 means unused).</summary>
        public int DirectRefL0;
        public int DirectRefL1;
        /// <summary>True when the encoder picks B_Skip (CBP=0 AND mode-decision prefers skip's zero bits).</summary>
        public bool IsSkip;
    }

    /// <summary>Run ME against L0 and L1, build L0/L1/Bi candidates plus a Direct candidate (when
    /// <paramref name="colocatedMbStates"/> is supplied), pick the lowest cost. If the chosen
    /// candidate is Direct with CBP=0, IsSkip is set so the caller emits B_Skip instead.</summary>
    internal static BCandidate ChooseBestInterWithDirect(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refL0Y, byte[] refL0U, byte[] refL0V,
        byte[] refL1Y, byte[] refL1U, byte[] refL1V,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        int searchRangePel, int maxSadEvalsPerMb,
        bool enableSubpel, int lambda,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        MacroblockEncoderState?[]? colocatedMbStates,
        int mbsPerRow, int mbAddress)
    {
        var best = ChooseBestInter(
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            predL0X, predL0Y, predL1X, predL1Y,
            searchRangePel, maxSadEvalsPerMb, enableSubpel, lambda);

        // Direct candidate: spatial direct with colZero override, per-4x4 prediction.
        var direct = BuildDirectCandidate(
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            leftMb, topMb, topRightMb, topLeftMb,
            colocatedMbStates, mbsPerRow, mbAddress);

        // Cost B_Direct (no mvds, just mb_type=0 → 1 bit ue + CBP + residual).
        // Cost B_Skip (zero bits if eligible: CBP==0 + at least one direction usable).
        int directBits = 1 /*mb_type=0 ue*/;
        direct.TotalCost = direct.Bundle.Sad + lambda * directBits;

        // Choose between L0/L1/Bi/Direct by lowest cost.
        if (direct.TotalCost < best.TotalCost) best = direct;

        // After mode selection: if the chosen candidate is Direct AND its CBP is 0, prefer B_Skip
        // (zero bits) — the prediction is identical, the decoder reconstruction is identical.
        if (best.Direction == Dir.Direct &&
            best.Bundle.CbpLuma == 0 && best.Bundle.CbpChroma == 0)
        {
            best.IsSkip = true;
        }
        return best;
    }

    /// <summary>Run ME against L0 and L1, build L0/L1/Bi candidates, pick the lowest cost.
    /// Costs use SAD + λ-weighted bits proxy (mvd magnitude + mb_type bits).</summary>
    internal static BCandidate ChooseBestInter(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refL0Y, byte[] refL0U, byte[] refL0V,
        byte[] refL1Y, byte[] refL1U, byte[] refL1V,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        int searchRangePel, int maxSadEvalsPerMb,
        bool enableSubpel, int lambda)
    {
        Span<byte> srcLuma = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                srcLuma[y * 16 + x] = srcY[y * srcStrideY + x];

        // ---- ME L0 ----
        var meL0 = MotionEstimator.SearchBlock(
            refL0Y, refW, refH, srcLuma,
            blockX: mbX * 16, blockY: mbY * 16,
            startMvX: predL0X, startMvY: predL0Y,
            searchRangePel, maxSadEvalsPerMb,
            bWidth: 16, bHeight: 16, enableSubpel: enableSubpel);

        // ---- ME L1 ----
        var meL1 = MotionEstimator.SearchBlock(
            refL1Y, refW, refH, srcLuma,
            blockX: mbX * 16, blockY: mbY * 16,
            startMvX: predL1X, startMvY: predL1Y,
            searchRangePel, maxSadEvalsPerMb,
            bWidth: 16, bHeight: 16, enableSubpel: enableSubpel);

        var candL0 = BuildSingleListCandidate(srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refW, refH, refCw, refCh,
            mbX, mbY, qpY, meL0.MvX, meL0.MvY, isL1: false);
        candL0.Direction = Dir.L0;
        candL0.MvL0X = meL0.MvX; candL0.MvL0Y = meL0.MvY;

        var candL1 = BuildSingleListCandidate(srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL1Y, refL1U, refL1V, refW, refH, refCw, refCh,
            mbX, mbY, qpY, meL1.MvX, meL1.MvY, isL1: true);
        candL1.Direction = Dir.L1;
        candL1.MvL1X = meL1.MvX; candL1.MvL1Y = meL1.MvY;

        // ---- Bipred: average L0 prediction + L1 prediction (non-iterative). ----
        var candBi = BuildBipredCandidate(srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            meL0.MvX, meL0.MvY, meL1.MvX, meL1.MvY);
        candBi.Direction = Dir.Bi;
        candBi.MvL0X = meL0.MvX; candBi.MvL0Y = meL0.MvY;
        candBi.MvL1X = meL1.MvX; candBi.MvL1Y = meL1.MvY;

        // ---- Cost: SAD + λ * bits proxy. ----
        int mvdL0X = meL0.MvX - predL0X, mvdL0Y = meL0.MvY - predL0Y;
        int mvdL1X = meL1.MvX - predL1X, mvdL1Y = meL1.MvY - predL1Y;
        int bitsL0 = 1 /*mb_type=1 (ue)*/ + EgBits(mvdL0X) + EgBits(mvdL0Y);
        int bitsL1 = 2 /*mb_type=2 (ue 010)*/ + EgBits(mvdL1X) + EgBits(mvdL1Y);
        int bitsBi = 3 /*mb_type=3 (ue 011)*/ + EgBits(mvdL0X) + EgBits(mvdL0Y) + EgBits(mvdL1X) + EgBits(mvdL1Y);

        candL0.TotalCost = candL0.Bundle.Sad + lambda * bitsL0;
        candL1.TotalCost = candL1.Bundle.Sad + lambda * bitsL1;
        candBi.TotalCost = candBi.Bundle.Sad + lambda * bitsBi;

        BCandidate best = candL0;
        if (candL1.TotalCost < best.TotalCost) best = candL1;
        if (candBi.TotalCost < best.TotalCost) best = candBi;
        return best;
    }

    /// <summary>Build a single-list (L0 or L1) prediction + residual + reconstruction bundle.</summary>
    private static BCandidate BuildSingleListCandidate(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refY, byte[] refU, byte[] refV,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int mvX, int mvY,
        bool isL1)
    {
        var bundle = MacroblockEncoderInter.BuildInterCandidate(
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refY, refU, refV, refW, refH, refCw, refCh,
            mbX, mbY, qpY, mvX, mvY);
        return new BCandidate { Bundle = bundle };
    }

    /// <summary>Build a bipred candidate: average L0 + L1 luma + chroma predictions, then
    /// forward residual/transform/quant/reconstruct.</summary>
    private static BCandidate BuildBipredCandidate(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refL0Y, byte[] refL0U, byte[] refL0V,
        byte[] refL1Y, byte[] refL1U, byte[] refL1V,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int mvL0X, int mvL0Y, int mvL1X, int mvL1Y)
    {
        var bundle = new MacroblockEncoderInter.InterEncodeBundle();

        Span<byte> predL0Y_buf = stackalloc byte[256];
        Span<byte> predL1Y_buf = stackalloc byte[256];
        MotionEstimator.LumaPredictBlock(refL0Y, refW, refH,
            mbX * 16, mbY * 16, mvL0X, mvL0Y, 16, 16, predL0Y_buf);
        MotionEstimator.LumaPredictBlock(refL1Y, refW, refH,
            mbX * 16, mbY * 16, mvL1X, mvL1Y, 16, 16, predL1Y_buf);
        Span<byte> predY = bundle.PredY;
        for (int i = 0; i < 256; i++) predY[i] = (byte)((predL0Y_buf[i] + predL1Y_buf[i] + 1) >> 1);

        Span<byte> predL0U_buf = stackalloc byte[64];
        Span<byte> predL1U_buf = stackalloc byte[64];
        Span<byte> predL0V_buf = stackalloc byte[64];
        Span<byte> predL1V_buf = stackalloc byte[64];
        MotionEstimator.ChromaPredictBlock(refL0U, refCw, refCh, mbX * 8, mbY * 8, mvL0X, mvL0Y, 8, 8, predL0U_buf);
        MotionEstimator.ChromaPredictBlock(refL1U, refCw, refCh, mbX * 8, mbY * 8, mvL1X, mvL1Y, 8, 8, predL1U_buf);
        MotionEstimator.ChromaPredictBlock(refL0V, refCw, refCh, mbX * 8, mbY * 8, mvL0X, mvL0Y, 8, 8, predL0V_buf);
        MotionEstimator.ChromaPredictBlock(refL1V, refCw, refCh, mbX * 8, mbY * 8, mvL1X, mvL1Y, 8, 8, predL1V_buf);
        Span<byte> predU = bundle.PredU;
        Span<byte> predV = bundle.PredV;
        for (int i = 0; i < 64; i++) predU[i] = (byte)((predL0U_buf[i] + predL1U_buf[i] + 1) >> 1);
        for (int i = 0; i < 64; i++) predV[i] = (byte)((predL0V_buf[i] + predL1V_buf[i] + 1) >> 1);

        MacroblockEncoderInter.BuildInterCandidateFromPrediction(bundle, srcY, srcStrideY, qpY);
        int qPc = MacroblockEncoderInter.ChromaQpFromLuma(qpY);
        MacroblockEncoderInter.EncodeChromaFromPrediction(srcU, srcV, srcStrideC, qPc, bundle);

        return new BCandidate { Bundle = bundle };
    }

    /// <summary>Build a B_Direct_16x16 candidate. Derives per-4x4 MVs via spatial direct mode
    /// (§8.4.1.2.2) including the colZero override, then builds per-4x4 luma + per-2x2 chroma
    /// predictions, then forward residual + reconstruction. The candidate's per-block arrays
    /// are populated so the syntax emitter can replay them into state for neighbor lookups.</summary>
    private static BCandidate BuildDirectCandidate(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refL0Y, byte[] refL0U, byte[] refL0V,
        byte[] refL1Y, byte[] refL1U, byte[] refL1V,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        MacroblockEncoderState?[]? colocatedMbStates,
        int mbsPerRow, int mbAddress)
    {
        // ---- Step 1: spatial-direct base derivation (one (refL0, mvL0) and one (refL1, mvL1)
        // for the whole MB before per-4x4 colZero override). ----
        int refL0 = MinPositiveRefAcrossNeighbors(leftMb, topMb, topRightMb, topLeftMb, listX: 0);
        int refL1 = MinPositiveRefAcrossNeighbors(leftMb, topMb, topRightMb, topLeftMb, listX: 1);
        bool noRefs = refL0 < 0 && refL1 < 0;

        int mvL0X = 0, mvL0Y = 0, mvL1X = 0, mvL1Y = 0;
        if (refL0 >= 0 && !noRefs)
        {
            (mvL0X, mvL0Y) = SpatialDirectPredictMv(
                leftMb, topMb, topRightMb, topLeftMb, listX: 0, refL0);
        }
        if (refL1 >= 0 && !noRefs)
        {
            (mvL1X, mvL1Y) = SpatialDirectPredictMv(
                leftMb, topMb, topRightMb, topLeftMb, listX: 1, refL1);
        }
        bool useL0 = refL0 >= 0 || noRefs;
        bool useL1 = refL1 >= 0 || noRefs;
        if (noRefs) { refL0 = 0; refL1 = 0; }

        // ---- Step 2: per-4x4 colZero override using colocated MB. ----
        int[] mvL0XBlk = new int[16];
        int[] mvL0YBlk = new int[16];
        int[] mvL1XBlk = new int[16];
        int[] mvL1YBlk = new int[16];
        for (int i = 0; i < 16; i++)
        {
            mvL0XBlk[i] = mvL0X; mvL0YBlk[i] = mvL0Y;
            mvL1XBlk[i] = mvL1X; mvL1YBlk[i] = mvL1Y;
        }

        var colocated = colocatedMbStates is not null && mbAddress < colocatedMbStates.Length
            ? colocatedMbStates[mbAddress] : null;
        bool colIsInter = colocated is not null && (colocated.IsInter || colocated.IsBInter);
        if (colIsInter && !noRefs)
        {
            // For each 4x4 raster index, look at colocated block's L0 (or L1 if no L0).
            for (int by = 0; by < 4; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    int idx = SpatialToRaster[by * 4 + bx];
                    int q = QuadrantOf(bx, by);
                    int colRefIdx, colMvX, colMvY;
                    if (colocated!.IsBInter)
                    {
                        bool colHasL0 = colocated.PredFlagL0Block[idx] != 0;
                        if (colHasL0)
                        {
                            colRefIdx = colocated.RefIdxL08x8[q];
                            colMvX = colocated.MvL0XBlock[idx];
                            colMvY = colocated.MvL0YBlock[idx];
                        }
                        else
                        {
                            colRefIdx = colocated.RefIdxL18x8[q];
                            colMvX = colocated.MvL1XBlock[idx];
                            colMvY = colocated.MvL1YBlock[idx];
                        }
                    }
                    else
                    {
                        // P-slice colocated (including P_Skip).
                        colRefIdx = colocated.RefIdxL08x8[q];
                        colMvX = colocated.MvL0XBlock[idx];
                        colMvY = colocated.MvL0YBlock[idx];
                    }
                    bool colSmall = colRefIdx == 0
                        && Math.Abs(colMvX) <= 1 && Math.Abs(colMvY) <= 1;
                    if (!colSmall) continue;
                    if (refL0 == 0 && useL0) { mvL0XBlk[idx] = 0; mvL0YBlk[idx] = 0; }
                    if (refL1 == 0 && useL1) { mvL1XBlk[idx] = 0; mvL1YBlk[idx] = 0; }
                }
        }

        // ---- Step 3: build per-4x4 luma + per-2x2 chroma prediction. ----
        var bundle = new MacroblockEncoderInter.InterEncodeBundle();
        Span<byte> predY = bundle.PredY;
        Span<byte> predU = bundle.PredU;
        Span<byte> predV = bundle.PredV;

        Span<byte> tmpL0 = stackalloc byte[16];
        Span<byte> tmpL1 = stackalloc byte[16];
        Span<byte> tmpL0c = stackalloc byte[4];
        Span<byte> tmpL1c = stackalloc byte[4];

        for (int by = 0; by < 4; by++)
            for (int bx = 0; bx < 4; bx++)
            {
                int idx = SpatialToRaster[by * 4 + bx];
                int px = mbX * 16 + bx * 4;
                int py = mbY * 16 + by * 4;

                if (useL0) MotionEstimator.LumaPredictBlock(refL0Y, refW, refH,
                    px, py, mvL0XBlk[idx], mvL0YBlk[idx], 4, 4, tmpL0);
                if (useL1) MotionEstimator.LumaPredictBlock(refL1Y, refW, refH,
                    px, py, mvL1XBlk[idx], mvL1YBlk[idx], 4, 4, tmpL1);

                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int outIdx = (by * 4 + yy) * 16 + (bx * 4 + xx);
                        int v;
                        if (useL0 && useL1)
                            v = (tmpL0[yy * 4 + xx] + tmpL1[yy * 4 + xx] + 1) >> 1;
                        else if (useL0)
                            v = tmpL0[yy * 4 + xx];
                        else
                            v = tmpL1[yy * 4 + xx];
                        predY[outIdx] = (byte)v;
                    }

                // Chroma: each luma 4x4 maps to a chroma 2x2 at half-resolution.
                int cpx = mbX * 8 + bx * 2;
                int cpy = mbY * 8 + by * 2;
                if (useL0)
                {
                    MotionEstimator.ChromaPredictBlock(refL0U, refCw, refCh,
                        cpx, cpy, mvL0XBlk[idx], mvL0YBlk[idx], 2, 2, tmpL0c);
                }
                if (useL1)
                {
                    MotionEstimator.ChromaPredictBlock(refL1U, refCw, refCh,
                        cpx, cpy, mvL1XBlk[idx], mvL1YBlk[idx], 2, 2, tmpL1c);
                }
                for (int yy = 0; yy < 2; yy++)
                    for (int xx = 0; xx < 2; xx++)
                    {
                        int outIdx = (by * 2 + yy) * 8 + (bx * 2 + xx);
                        int v;
                        if (useL0 && useL1) v = (tmpL0c[yy * 2 + xx] + tmpL1c[yy * 2 + xx] + 1) >> 1;
                        else if (useL0) v = tmpL0c[yy * 2 + xx];
                        else v = tmpL1c[yy * 2 + xx];
                        predU[outIdx] = (byte)v;
                    }
                if (useL0)
                {
                    MotionEstimator.ChromaPredictBlock(refL0V, refCw, refCh,
                        cpx, cpy, mvL0XBlk[idx], mvL0YBlk[idx], 2, 2, tmpL0c);
                }
                if (useL1)
                {
                    MotionEstimator.ChromaPredictBlock(refL1V, refCw, refCh,
                        cpx, cpy, mvL1XBlk[idx], mvL1YBlk[idx], 2, 2, tmpL1c);
                }
                for (int yy = 0; yy < 2; yy++)
                    for (int xx = 0; xx < 2; xx++)
                    {
                        int outIdx = (by * 2 + yy) * 8 + (bx * 2 + xx);
                        int v;
                        if (useL0 && useL1) v = (tmpL0c[yy * 2 + xx] + tmpL1c[yy * 2 + xx] + 1) >> 1;
                        else if (useL0) v = tmpL0c[yy * 2 + xx];
                        else v = tmpL1c[yy * 2 + xx];
                        predV[outIdx] = (byte)v;
                    }
            }

        // ---- Step 4: residual + reconstruction (shared helpers). ----
        MacroblockEncoderInter.BuildInterCandidateFromPrediction(bundle, srcY, srcStrideY, qpY);
        int qPc = MacroblockEncoderInter.ChromaQpFromLuma(qpY);
        MacroblockEncoderInter.EncodeChromaFromPrediction(srcU, srcV, srcStrideC, qPc, bundle);

        return new BCandidate
        {
            Direction = Dir.Direct,
            Bundle = bundle,
            MvL0XPerBlock = mvL0XBlk,
            MvL0YPerBlock = mvL0YBlk,
            MvL1XPerBlock = mvL1XBlk,
            MvL1YPerBlock = mvL1YBlk,
            DirectUseL0 = useL0,
            DirectUseL1 = useL1,
            DirectRefL0 = useL0 ? refL0 : -1,
            DirectRefL1 = useL1 ? refL1 : -1,
        };
    }

    /// <summary>Spatial-direct minimum-refIdx derivation across A/B/C neighbors at the MB's
    /// top-left block (spec §8.4.1.2.1). Returns -1 if no neighbor has a valid ref for the list.</summary>
    private static int MinPositiveRefAcrossNeighbors(
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        int listX)
    {
        var A = NeighborMv(leftMb, 5, 1, listX);
        var B = NeighborMv(topMb, 10, 2, listX);
        var C = NeighborMv(topRightMb, 10, 2, listX);
        if (!C.Avail) C = NeighborMv(topLeftMb, 15, 3, listX);
        int min = int.MaxValue;
        if (A.Avail && A.RefIdx >= 0) min = Math.Min(min, A.RefIdx);
        if (B.Avail && B.RefIdx >= 0) min = Math.Min(min, B.RefIdx);
        if (C.Avail && C.RefIdx >= 0) min = Math.Min(min, C.RefIdx);
        return min == int.MaxValue ? -1 : min;
    }

    /// <summary>Spatial direct MV predictor: median of A/B/C neighbors that match the chosen
    /// refIdx. Mirrors decoder's <c>PredictMvForPartitionListB</c> for 16x16 partition.</summary>
    private static (int X, int Y) SpatialDirectPredictMv(
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        int listX, int curRefIdx)
    {
        var A = NeighborMv(leftMb, 5, 1, listX);
        var B = NeighborMv(topMb, 10, 2, listX);
        var C = NeighborMv(topRightMb, 10, 2, listX);
        if (!C.Avail) C = NeighborMv(topLeftMb, 15, 3, listX);
        if (!B.Avail && !C.Avail && A.Avail) return (A.X, A.Y);

        int matchCount = (A.Avail && A.RefIdx == curRefIdx ? 1 : 0)
                       + (B.Avail && B.RefIdx == curRefIdx ? 1 : 0)
                       + (C.Avail && C.RefIdx == curRefIdx ? 1 : 0);
        if (matchCount == 1)
        {
            if (A.Avail && A.RefIdx == curRefIdx) return (A.X, A.Y);
            if (B.Avail && B.RefIdx == curRefIdx) return (B.X, B.Y);
            return (C.X, C.Y);
        }
        int aX = A.Avail ? A.X : 0, aY = A.Avail ? A.Y : 0;
        int bX = B.Avail ? B.X : 0, bY = B.Avail ? B.Y : 0;
        int cX = C.Avail ? C.X : 0, cY = C.Avail ? C.Y : 0;
        return (Median3(aX, bX, cX), Median3(aY, bY, cY));
    }

    private static int QuadrantOf(int bx, int by) => (by / 2) * 2 + (bx / 2);

    /// <summary>Predict a B-slice 16x16 partition MV for list <paramref name="listX"/>
    /// (0=L0, 1=L1) using the encoder-side analog of spec §8.4.1.3.1. With single 16x16 MB
    /// partition and num_ref_active=1, the predictor is the median of A/B/C (or A alone when
    /// B,C unavailable). Neighbors that don't use this list contribute MV=0 + refIdx=-1.</summary>
    internal static (int X, int Y) PredictBSliceMv(
        MacroblockEncoderState? leftMb,
        MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb,
        MacroblockEncoderState? topLeftMb,
        int listX)
    {
        // Block-position references for the current MB's block (0,0):
        //   A = left MB block (3,0) → raster 5; quadrant 1 (TR).
        //   B = top MB block (0,3)  → raster 10; quadrant 2 (BL).
        //   C = top-right MB block (0,3) → raster 10; quadrant 2.
        //   D = top-left MB block (3,3) → raster 15; quadrant 3.
        (int X, int Y, int RefIdx, bool Avail) A = NeighborMv(leftMb, 5, 1, listX);
        (int X, int Y, int RefIdx, bool Avail) B = NeighborMv(topMb, 10, 2, listX);
        (int X, int Y, int RefIdx, bool Avail) C = NeighborMv(topRightMb, 10, 2, listX);
        if (!C.Avail) C = NeighborMv(topLeftMb, 15, 3, listX);

        if (!B.Avail && !C.Avail && A.Avail) return (A.X, A.Y);

        int curRefIdx = 0; // we always emit refIdx=0
        int matchCount = (A.Avail && A.RefIdx == curRefIdx ? 1 : 0)
                       + (B.Avail && B.RefIdx == curRefIdx ? 1 : 0)
                       + (C.Avail && C.RefIdx == curRefIdx ? 1 : 0);
        if (matchCount == 1)
        {
            if (A.Avail && A.RefIdx == curRefIdx) return (A.X, A.Y);
            if (B.Avail && B.RefIdx == curRefIdx) return (B.X, B.Y);
            return (C.X, C.Y);
        }
        int aX = A.Avail ? A.X : 0, aY = A.Avail ? A.Y : 0;
        int bX = B.Avail ? B.X : 0, bY = B.Avail ? B.Y : 0;
        int cX = C.Avail ? C.X : 0, cY = C.Avail ? C.Y : 0;
        return (Median3(aX, bX, cX), Median3(aY, bY, cY));
    }

    private static (int X, int Y, int RefIdx, bool Avail) NeighborMv(
        MacroblockEncoderState? nb, int rasterIdx, int quadIdx, int listX)
    {
        if (nb is null) return (0, 0, -1, false);
        // Treat intra neighbor as unavailable for inter MV prediction.
        if (!nb.IsInter && !nb.IsBInter) return (0, 0, -1, false);
        if (listX == 0)
        {
            if (nb.IsBInter && nb.PredFlagL0Block[rasterIdx] == 0) return (0, 0, -1, true);
            return (nb.MvL0XBlock[rasterIdx], nb.MvL0YBlock[rasterIdx], nb.RefIdxL08x8[quadIdx], true);
        }
        else
        {
            if (nb.IsBInter && nb.PredFlagL1Block[rasterIdx] == 0) return (0, 0, -1, true);
            if (!nb.IsBInter) return (0, 0, -1, true); // P-MB doesn't use L1.
            return (nb.MvL1XBlock[rasterIdx], nb.MvL1YBlock[rasterIdx], nb.RefIdxL18x8[quadIdx], true);
        }
    }

    private static int Median3(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }

    /// <summary>Populate the per-MB state for a B-MB after mode decision. Idempotent regardless
    /// of entropy mode (CAVLC vs CABAC) — both call this before emitting syntax so neighbor
    /// lookups in subsequent MBs see the correct MVs / refIdx / predFlags / CBP.</summary>
    internal static void PopulateBMbState(
        BCandidate cand, MacroblockEncoderState state, int mbAddress, int qpY,
        int mvdL0X, int mvdL0Y, int mvdL1X, int mvdL1Y)
    {
        state.MbAddress = mbAddress;
        state.IsBInter = true;
        state.IsInter = true;
        state.IsInterP16x16 = false;
        state.IsIntra16x16 = false;
        state.IsIntra4x4 = false;
        state.IsSkipped = cand.IsSkip;
        state.BPredDir = (byte)cand.Direction;
        state.RawMbType = cand.Direction switch
        {
            Dir.Direct => 0,
            Dir.L0 => 1,
            Dir.L1 => 2,
            Dir.Bi => 3,
            _ => 0,
        };
        state.CbpLuma = cand.Bundle.CbpLuma;
        state.CbpChroma = cand.Bundle.CbpChroma;
        state.QpY = qpY;

        bool useL0, useL1;
        if (cand.Direction == Dir.Direct)
        {
            useL0 = cand.DirectUseL0;
            useL1 = cand.DirectUseL1;
            for (int i = 0; i < 16; i++)
            {
                state.PredFlagL0Block[i] = useL0 ? (byte)1 : (byte)0;
                state.PredFlagL1Block[i] = useL1 ? (byte)1 : (byte)0;
                state.MvL0XBlock[i] = useL0 ? cand.MvL0XPerBlock![i] : 0;
                state.MvL0YBlock[i] = useL0 ? cand.MvL0YPerBlock![i] : 0;
                state.MvL1XBlock[i] = useL1 ? cand.MvL1XPerBlock![i] : 0;
                state.MvL1YBlock[i] = useL1 ? cand.MvL1YPerBlock![i] : 0;
                // For direct mode, mvd is implicitly 0 (no mvd_lN syntax emitted; the decoder
                // skips reading neighbor mvd contributions for any block that has refIdx -1).
                state.MvdL0XBlock[i] = 0;
                state.MvdL0YBlock[i] = 0;
                state.MvdL1XBlock[i] = 0;
                state.MvdL1YBlock[i] = 0;
            }
            int r0 = useL0 ? (cand.DirectRefL0 < 0 ? 0 : cand.DirectRefL0) : -1;
            int r1 = useL1 ? (cand.DirectRefL1 < 0 ? 0 : cand.DirectRefL1) : -1;
            for (int q = 0; q < 4; q++)
            {
                state.RefIdxL08x8[q] = r0;
                state.RefIdxL18x8[q] = r1;
            }
            state.MvL0X = useL0 ? cand.MvL0XPerBlock![0] : 0;
            state.MvL0Y = useL0 ? cand.MvL0YPerBlock![0] : 0;
            state.RefIdxL0 = r0;
            state.RefIdxL1 = r1;
        }
        else
        {
            useL0 = cand.Direction != Dir.L1;
            useL1 = cand.Direction != Dir.L0;
            for (int i = 0; i < 16; i++)
            {
                state.PredFlagL0Block[i] = useL0 ? (byte)1 : (byte)0;
                state.PredFlagL1Block[i] = useL1 ? (byte)1 : (byte)0;
                state.MvL0XBlock[i] = useL0 ? cand.MvL0X : 0;
                state.MvL0YBlock[i] = useL0 ? cand.MvL0Y : 0;
                state.MvL1XBlock[i] = useL1 ? cand.MvL1X : 0;
                state.MvL1YBlock[i] = useL1 ? cand.MvL1Y : 0;
                state.MvdL0XBlock[i] = useL0 ? mvdL0X : 0;
                state.MvdL0YBlock[i] = useL0 ? mvdL0Y : 0;
                state.MvdL1XBlock[i] = useL1 ? mvdL1X : 0;
                state.MvdL1YBlock[i] = useL1 ? mvdL1Y : 0;
            }
            for (int q = 0; q < 4; q++)
            {
                state.RefIdxL08x8[q] = useL0 ? 0 : -1;
                state.RefIdxL18x8[q] = useL1 ? 0 : -1;
            }
            state.MvL0X = useL0 ? cand.MvL0X : 0;
            state.MvL0Y = useL0 ? cand.MvL0Y : 0;
            state.RefIdxL0 = useL0 ? 0 : -1;
            state.RefIdxL1 = useL1 ? 0 : -1;
        }
    }

    /// <summary>Emit B-slice macroblock_layer() syntax (CAVLC) for a chosen 16x16 inter candidate.
    /// Caller must have emitted any mb_skip_run prefix already. For B_Skip candidates the caller
    /// should accumulate this MB into mb_skip_run instead of calling EmitBMb16x16.</summary>
    internal static void EmitBMb16x16(
        BitWriter w,
        BCandidate cand,
        int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        if (cand.IsSkip)
            throw new InvalidOperationException("B_Skip should be handled via mb_skip_run, not EmitBMb16x16");

        int mbType = cand.Direction switch
        {
            Dir.Direct => 0,
            Dir.L0 => 1,
            Dir.L1 => 2,
            Dir.Bi => 3,
            _ => throw new InvalidOperationException()
        };
        ExpGolombWriter.WriteUe(w, (uint)mbType);

        // mvds: only for L0/L1/Bi (Direct has no mvds — MVs are derived).
        int mvdL0X = 0, mvdL0Y = 0, mvdL1X = 0, mvdL1Y = 0;
        if (cand.Direction == Dir.L0 || cand.Direction == Dir.Bi)
        {
            mvdL0X = cand.MvL0X - predL0X;
            mvdL0Y = cand.MvL0Y - predL0Y;
            ExpGolombWriter.WriteSe(w, mvdL0X);
            ExpGolombWriter.WriteSe(w, mvdL0Y);
        }
        if (cand.Direction == Dir.L1 || cand.Direction == Dir.Bi)
        {
            mvdL1X = cand.MvL1X - predL1X;
            mvdL1Y = cand.MvL1Y - predL1Y;
            ExpGolombWriter.WriteSe(w, mvdL1X);
            ExpGolombWriter.WriteSe(w, mvdL1Y);
        }

        int cbp = cand.Bundle.CbpLuma | (cand.Bundle.CbpChroma << 4);
        int code = MacroblockEncoderInter.CbpToCodeNumInter(cbp);
        if (code < 0) throw new InvalidOperationException($"unmappable inter CBP {cbp}");
        ExpGolombWriter.WriteUe(w, (uint)code);

        bool hasResidual = cand.Bundle.CbpLuma != 0 || cand.Bundle.CbpChroma != 0;
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta = 0
        }

        // Update state (shared with CABAC path via PopulateBMbState).
        PopulateBMbState(cand, state, state.MbAddress, qpY, mvdL0X, mvdL0Y, mvdL1X, mvdL1Y);

        // ---- Residual (CAVLC) ----
        var bundle = cand.Bundle;
        Span<int> coeffsScan = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int q = i >> 2;
            bool coded = (bundle.CbpLuma & (1 << q)) != 0;
            if (!coded)
            {
                state.NonZeroCountLuma[i] = 0;
                continue;
            }
            for (int s = 0; s < 16; s++) coeffsScan[s] = bundle.Luma4x4[i * 16 + ZigZag4x4[s]];
            int nC = MacroblockEncoderInter.NcLumaBlock(state, leftMb, topMb, i);
            CavlcEncoder.EncodeResidualBlock(w, coeffsScan, maxNumCoeff: 16, nC, chromaDc: false);
            int nz = 0; for (int k = 0; k < 16; k++) if (coeffsScan[k] != 0) nz++;
            state.NonZeroCountLuma[i] = nz;
        }
        if ((bundle.CbpChroma & 3) != 0)
        {
            Span<int> dc = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dc[k] = bundle.ChromaDc[c, k];
                CavlcEncoder.EncodeResidualBlock(w, dc, maxNumCoeff: 4, nC: 0, chromaDc: true);
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
                    int nC = MacroblockEncoderInter.NcChromaBlock(state, leftMb, topMb, c, i);
                    CavlcEncoder.EncodeResidualBlock(w, ac, maxNumCoeff: 15, nC, chromaDc: false);
                    int nz = 0; for (int k = 0; k < 15; k++) if (ac[k] != 0) nz++;
                    state.NonZeroCountChromaAc[c, i] = nz;
                }
            }
        }

        bundle.ReconY.CopyTo(state.ReconY, 0);
        bundle.ReconU.CopyTo(state.ReconU, 0);
        bundle.ReconV.CopyTo(state.ReconV, 0);
    }

    /// <summary>Cheap exp-Golomb bit-length estimator for SAD-cost mode decision.</summary>
    private static int EgBits(int v)
    {
        uint codeNum = (uint)((v <= 0) ? (-2 * v) : (2 * v - 1));
        int n = 0; uint x = codeNum + 1; while (x > 1) { x >>= 1; n++; }
        return 2 * n + 1;
    }
}
