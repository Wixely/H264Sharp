using H264Sharp.Encoder.Bitstream;
using H264Sharp.Encoder.Cavlc;
using H264Sharp.Encoder.Transform;
using H264Sharp.Decoder.Transform;

namespace H264Sharp.Encoder.Mode;

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

    /// <summary>Partition shape for a B-MB. 16x16 covers L0/L1/Bi/Direct/Skip; 16x8/8x16 split the
    /// MB into two equal halves with independent direction per half; P8x8 splits into four 8x8
    /// quadrants each with its own sub_mb_type (Phase 5e supports sub_mb_type 0..3).</summary>
    internal enum Shape : byte { Sq16x16 = 0, P16x8 = 1, P8x16 = 2, P8x8 = 3 }

    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static readonly int[] SpatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

    /// <summary>Holds the chosen B-inter mode plus its built bundle (prediction, residual,
    /// reconstruction) and the MVs used. <see cref="Bundle"/>.CbpLuma/CbpChroma reflect the actual
    /// chosen mode. For Direct mode, MvL{0,1}PerBlock hold the per-4x4 MVs (varying due to colZero
    /// override); for L0/L1/Bi the per-block arrays are uniform.</summary>
    internal sealed class BCandidate
    {
        public Shape Shape = Shape.Sq16x16;
        public Dir Direction; // For Sq16x16: applies to whole MB. For 16x8/8x16: partition 0's direction.
        public int MvL0X, MvL0Y; // Partition 0's L0 MV (quarter-pel). Valid only when partition 0 uses L0/Bi.
        public int MvL1X, MvL1Y;
        public MacroblockEncoderInter.InterEncodeBundle Bundle = null!;
        public int TotalCost; // SAD + λ * mode-bits proxy

        /// <summary>For 16x8/8x16: partition 1's direction.</summary>
        public Dir Part1Direction;
        /// <summary>For 16x8/8x16: partition 1's MVs (only the lists matching Part1Direction are meaningful).</summary>
        public int Part1MvL0X, Part1MvL0Y;
        public int Part1MvL1X, Part1MvL1Y;

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

        // ---- P8x8 fields (Phase 5e). ----
        /// <summary>For Shape.P8x8: per-quadrant sub_mb_type (0..12 per Table 7-17).</summary>
        public int[]? SubMbTypes;
        // Per-4x4-block MVs for P8x8 live in MvL{0,1}{X,Y}PerBlock (shared with Direct mode).
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
        int mbsPerRow, int mbAddress,
        bool enableP8x8SubPartitions = true)
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

        // Partitioned candidates (16x8 and 8x16). For each shape, BuildPartitionCandidate runs
        // ME L0+L1 on each partition and picks the per-partition direction with lowest cost.
        var p16x8 = BuildPartitionCandidate(
            Shape.P16x8,
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            predL0X, predL0Y, predL1X, predL1Y,
            searchRangePel, maxSadEvalsPerMb, enableSubpel, lambda);
        // Cost: mb_type bits (~3-6 bits depending on direction combo) + per-partition mvds.
        p16x8.TotalCost = p16x8.Bundle.Sad + lambda * EstimatedPartitionBits(p16x8, predL0X, predL0Y, predL1X, predL1Y);

        var p8x16 = BuildPartitionCandidate(
            Shape.P8x16,
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            predL0X, predL0Y, predL1X, predL1Y,
            searchRangePel, maxSadEvalsPerMb, enableSubpel, lambda);
        p8x16.TotalCost = p8x16.Bundle.Sad + lambda * EstimatedPartitionBits(p8x16, predL0X, predL0Y, predL1X, predL1Y);

        if (p16x8.TotalCost < best.TotalCost) best = p16x8;
        if (p8x16.TotalCost < best.TotalCost) best = p8x16;

        // P8x8: four 8x8 quadrants with independent direction per quadrant. Best when motion is
        // genuinely different in all 4 corners.
        var p8x8 = BuildP8x8Candidate(
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            refL0Y, refL0U, refL0V, refL1Y, refL1U, refL1V,
            refW, refH, refCw, refCh,
            mbX, mbY, qpY,
            predL0X, predL0Y, predL1X, predL1Y,
            searchRangePel, maxSadEvalsPerMb, enableSubpel, lambda,
            enableSubPartitions: enableP8x8SubPartitions);
        p8x8.TotalCost = p8x8.Bundle.Sad + lambda * EstimatedP8x8Bits(p8x8, predL0X, predL0Y, predL1X, predL1Y);
        if (p8x8.TotalCost < best.TotalCost) best = p8x8;

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

    /// <summary>Build a partitioned (16x8 or 8x16) B-MB candidate. For each partition: runs ME
    /// against L0 and L1, evaluates L0/L1/Bi predictions, picks the lowest-SAD direction per
    /// partition. Assembles the per-MB prediction, residual, and reconstruction.</summary>
    private static BCandidate BuildPartitionCandidate(
        Shape shape,
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
        // Partition rectangles in luma pixels (within MB).
        // 16x8: top half (0,0,16,8), bottom half (0,8,16,8).
        // 8x16: left half (0,0,8,16), right half (8,0,8,16).
        (int x, int y, int w, int h)[] parts = shape == Shape.P16x8
            ? new[] { (0, 0, 16, 8), (0, 8, 16, 8) }
            : new[] { (0, 0, 8, 16), (8, 0, 8, 16) };

        var bundle = new MacroblockEncoderInter.InterEncodeBundle();
        Span<byte> predY = bundle.PredY;
        Span<byte> predU = bundle.PredU;
        Span<byte> predV = bundle.PredV;

        Dir[] chosenDir = new Dir[2];
        int[] mvL0X = new int[2], mvL0Y = new int[2];
        int[] mvL1X = new int[2], mvL1Y = new int[2];

        Span<byte> srcPart = stackalloc byte[256];
        Span<byte> predL0Buf = stackalloc byte[256];
        Span<byte> predL1Buf = stackalloc byte[256];

        for (int p = 0; p < 2; p++)
        {
            var (px, py, pw, ph) = parts[p];
            int pix = pw * ph;

            // Read source partition into a contiguous buffer (luma).
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                    srcPart[y * pw + x] = srcY[(py + y) * srcStrideY + (px + x)];

            // ME against L0.
            var meL0 = MotionEstimator.SearchBlock(
                refL0Y, refW, refH, srcPart.Slice(0, pix),
                mbX * 16 + px, mbY * 16 + py,
                predL0X, predL0Y, searchRangePel, maxSadEvalsPerMb,
                bWidth: pw, bHeight: ph, enableSubpel: enableSubpel);
            // ME against L1.
            var meL1 = MotionEstimator.SearchBlock(
                refL1Y, refW, refH, srcPart.Slice(0, pix),
                mbX * 16 + px, mbY * 16 + py,
                predL1X, predL1Y, searchRangePel, maxSadEvalsPerMb,
                bWidth: pw, bHeight: ph, enableSubpel: enableSubpel);

            // Build L0-only luma prediction.
            MotionEstimator.LumaPredictBlock(refL0Y, refW, refH,
                mbX * 16 + px, mbY * 16 + py, meL0.MvX, meL0.MvY, pw, ph, predL0Buf.Slice(0, pix));
            // Build L1-only luma prediction.
            MotionEstimator.LumaPredictBlock(refL1Y, refW, refH,
                mbX * 16 + px, mbY * 16 + py, meL1.MvX, meL1.MvY, pw, ph, predL1Buf.Slice(0, pix));

            int sadL0 = meL0.Sad;
            int sadL1 = meL1.Sad;
            int sadBi = 0;
            for (int i = 0; i < pix; i++)
            {
                int avg = (predL0Buf[i] + predL1Buf[i] + 1) >> 1;
                sadBi += Math.Abs(srcPart[i] - avg);
            }

            // Pick lowest cost direction (SAD + λ × mvd-bits proxy per partition).
            int mvdL0X = meL0.MvX - predL0X, mvdL0Y = meL0.MvY - predL0Y;
            int mvdL1X = meL1.MvX - predL1X, mvdL1Y = meL1.MvY - predL1Y;
            int costL0 = sadL0 + lambda * (EgBits(mvdL0X) + EgBits(mvdL0Y));
            int costL1 = sadL1 + lambda * (EgBits(mvdL1X) + EgBits(mvdL1Y));
            int costBi = sadBi + lambda * (EgBits(mvdL0X) + EgBits(mvdL0Y) + EgBits(mvdL1X) + EgBits(mvdL1Y));

            Dir partDir;
            if (costL0 <= costL1 && costL0 <= costBi) partDir = Dir.L0;
            else if (costL1 <= costBi) partDir = Dir.L1;
            else partDir = Dir.Bi;

            chosenDir[p] = partDir;
            mvL0X[p] = meL0.MvX; mvL0Y[p] = meL0.MvY;
            mvL1X[p] = meL1.MvX; mvL1Y[p] = meL1.MvY;

            // Write the chosen partition prediction into the bundle's PredY at the partition rect.
            ReadOnlySpan<byte> chosenPred = partDir == Dir.L0 ? predL0Buf.Slice(0, pix)
                                          : partDir == Dir.L1 ? predL1Buf.Slice(0, pix)
                                          : null;
            if (partDir == Dir.Bi)
            {
                for (int i = 0; i < pix; i++)
                {
                    int avg = (predL0Buf[i] + predL1Buf[i] + 1) >> 1;
                    int outY = py + i / pw;
                    int outX = px + i % pw;
                    predY[outY * 16 + outX] = (byte)avg;
                }
            }
            else
            {
                for (int i = 0; i < pix; i++)
                {
                    int outY = py + i / pw;
                    int outX = px + i % pw;
                    predY[outY * 16 + outX] = chosenPred[i];
                }
            }
        }

        // Chroma prediction per partition (4:2:0 → halve coords/dims).
        Span<byte> chBuf = stackalloc byte[64];
        Span<byte> chL1Buf = stackalloc byte[64];
        for (int p = 0; p < 2; p++)
        {
            var (px, py, pw, ph) = parts[p];
            int cpx = px / 2, cpy = py / 2, cpw = pw / 2, cph = ph / 2;
            int cpix = cpw * cph;
            int mvLpX = chosenDir[p] == Dir.L1 ? mvL1X[p] : mvL0X[p];
            int mvLpY = chosenDir[p] == Dir.L1 ? mvL1Y[p] : mvL0Y[p];

            for (int comp = 0; comp < 2; comp++)
            {
                byte[] refL0 = comp == 0 ? refL0U : refL0V;
                byte[] refL1 = comp == 0 ? refL1U : refL1V;
                Span<byte> outPred = comp == 0 ? predU : predV;

                if (chosenDir[p] == Dir.L0 || chosenDir[p] == Dir.Bi)
                {
                    MotionEstimator.ChromaPredictBlock(refL0, refCw, refCh,
                        mbX * 8 + cpx, mbY * 8 + cpy, mvL0X[p], mvL0Y[p], cpw, cph, chBuf.Slice(0, cpix));
                }
                if (chosenDir[p] == Dir.L0)
                {
                    for (int i = 0; i < cpix; i++)
                    {
                        int outY = cpy + i / cpw;
                        int outX = cpx + i % cpw;
                        outPred[outY * 8 + outX] = chBuf[i];
                    }
                }
                else if (chosenDir[p] == Dir.L1)
                {
                    MotionEstimator.ChromaPredictBlock(refL1, refCw, refCh,
                        mbX * 8 + cpx, mbY * 8 + cpy, mvL1X[p], mvL1Y[p], cpw, cph, chBuf.Slice(0, cpix));
                    for (int i = 0; i < cpix; i++)
                    {
                        int outY = cpy + i / cpw;
                        int outX = cpx + i % cpw;
                        outPred[outY * 8 + outX] = chBuf[i];
                    }
                }
                else // Bi
                {
                    // L0 already in chBuf from above. Add L1 and average.
                    MotionEstimator.ChromaPredictBlock(refL1, refCw, refCh,
                        mbX * 8 + cpx, mbY * 8 + cpy, mvL1X[p], mvL1Y[p], cpw, cph, chL1Buf.Slice(0, cpix));
                    for (int i = 0; i < cpix; i++)
                    {
                        int outY = cpy + i / cpw;
                        int outX = cpx + i % cpw;
                        outPred[outY * 8 + outX] = (byte)((chBuf[i] + chL1Buf[i] + 1) >> 1);
                    }
                }
            }
        }

        // Residual + reconstruction using shared helpers.
        MacroblockEncoderInter.BuildInterCandidateFromPrediction(bundle, srcY, srcStrideY, qpY);
        int qPc = MacroblockEncoderInter.ChromaQpFromLuma(qpY);
        MacroblockEncoderInter.EncodeChromaFromPrediction(srcU, srcV, srcStrideC, qPc, bundle);

        return new BCandidate
        {
            Shape = shape,
            Direction = chosenDir[0],
            Part1Direction = chosenDir[1],
            MvL0X = mvL0X[0], MvL0Y = mvL0Y[0],
            MvL1X = mvL1X[0], MvL1Y = mvL1Y[0],
            Part1MvL0X = mvL0X[1], Part1MvL0Y = mvL0Y[1],
            Part1MvL1X = mvL1X[1], Part1MvL1Y = mvL1Y[1],
            Bundle = bundle,
        };
    }

    /// <summary>Sub-partition layout for one B_8x8 sub_mb_type, expressed as (px, py, pw, ph)
    /// rectangles within the 8x8 quadrant. Indexed by sub_mb_type 0..12.</summary>
    private static readonly (int Px, int Py, int Pw, int Ph)[][] SubMbPartLayouts =
    {
        new[] { (0, 0, 8, 8) },                                              // 0: Direct_8x8
        new[] { (0, 0, 8, 8) },                                              // 1: L0_8x8
        new[] { (0, 0, 8, 8) },                                              // 2: L1_8x8
        new[] { (0, 0, 8, 8) },                                              // 3: Bi_8x8
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) },                                // 4: L0_8x4
        new[] { (0, 0, 4, 8), (4, 0, 4, 8) },                                // 5: L0_4x8
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) },                                // 6: L1_8x4
        new[] { (0, 0, 4, 8), (4, 0, 4, 8) },                                // 7: L1_4x8
        new[] { (0, 0, 8, 4), (0, 4, 8, 4) },                                // 8: Bi_8x4
        new[] { (0, 0, 4, 8), (4, 0, 4, 8) },                                // 9: Bi_4x8
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },    // 10: L0_4x4
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },    // 11: L1_4x4
        new[] { (0, 0, 4, 4), (4, 0, 4, 4), (0, 4, 4, 4), (4, 4, 4, 4) },    // 12: Bi_4x4
    };

    /// <summary>Direction for each sub_mb_type (L0/L1/Bi/Direct).</summary>
    private static readonly Dir[] SubMbTypeDir =
    {
        Dir.Direct, // 0
        Dir.L0, Dir.L1, Dir.Bi,             // 1,2,3
        Dir.L0, Dir.L0, Dir.L1, Dir.L1,     // 4,5,6,7
        Dir.Bi, Dir.Bi,                     // 8,9
        Dir.L0, Dir.L1, Dir.Bi,             // 10,11,12
    };

    /// <summary>Per-quadrant winning candidate for B_8x8 mode decision.</summary>
    private struct QuadCandidate
    {
        public int SubMbType;
        public int Cost;
        public int[] MvL0X; public int[] MvL0Y; // per-sub-partition
        public int[] MvL1X; public int[] MvL1Y;
    }

    /// <summary>Build a B_8x8 candidate. Per quadrant, evaluates sub_mb_types 1..3 (8x8 partition
    /// with L0/L1/Bi direction) plus, when <paramref name="enableSubPartitions"/> is true, the
    /// sub-8x8 partition variants (sub_mb_types 4..12 — 8x4, 4x8, 4x4). Picks the lowest-cost
    /// per quadrant and combines into a full-MB prediction. Direct_8x8 (sub_mb_type 0) is not
    /// yet evaluated as a candidate (no per-quadrant spatial direct on encoder side yet).</summary>
    private static BCandidate BuildP8x8Candidate(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refL0Y, byte[] refL0U, byte[] refL0V,
        byte[] refL1Y, byte[] refL1U, byte[] refL1V,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        int searchRangePel, int maxSadEvalsPerMb,
        bool enableSubpel, int lambda,
        bool enableSubPartitions)
    {
        var bundle = new MacroblockEncoderInter.InterEncodeBundle();
        Span<byte> predY = bundle.PredY;
        Span<byte> predU = bundle.PredU;
        Span<byte> predV = bundle.PredV;

        int[] subMbType = new int[4];
        int[] mvL0XPerBlock = new int[16];
        int[] mvL0YPerBlock = new int[16];
        int[] mvL1XPerBlock = new int[16];
        int[] mvL1YPerBlock = new int[16];

        // Per-quadrant winning sub_mb_type + per-sub-partition MVs.
        var winners = new QuadCandidate[4];

        // Stack buffers hoisted out of inner loops (avoids CA2014 stack-overflow-in-loop warnings).
        Span<byte> srcSub = stackalloc byte[64];
        Span<byte> predL0Buf = stackalloc byte[64];
        Span<byte> predL1Buf = stackalloc byte[64];
        Span<byte> tmpL0 = stackalloc byte[64];
        Span<byte> tmpL1 = stackalloc byte[64];
        Span<byte> tmpL0c = stackalloc byte[64];
        Span<byte> tmpL1c = stackalloc byte[64];

        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 8, qy = (q >> 1) * 8;
            int bestCost = int.MaxValue;
            QuadCandidate bestQ = default;

            // Evaluate sub_mb_types 1..3 always; 4..12 only when sub-partition mode is enabled.
            int subMax = enableSubPartitions ? 12 : 3;
            for (int sub = 1; sub <= subMax; sub++)
            {
                var layout = SubMbPartLayouts[sub];
                Dir dir = SubMbTypeDir[sub];
                int n = layout.Length;
                int[] subMvL0X = new int[n], subMvL0Y = new int[n];
                int[] subMvL1X = new int[n], subMvL1Y = new int[n];
                int totalSad = 0;
                int totalMvdBits = 0;

                for (int sp = 0; sp < n; sp++)
                {
                    var (spx, spy, spw, sph) = layout[sp];
                    int absX = mbX * 16 + qx + spx;
                    int absY = mbY * 16 + qy + spy;
                    int subPix = spw * sph;
                    for (int yy = 0; yy < sph; yy++)
                        for (int xx = 0; xx < spw; xx++)
                            srcSub[yy * spw + xx] = srcY[(qy + spy + yy) * srcStrideY + (qx + spx + xx)];

                    if (dir == Dir.L0 || dir == Dir.Bi)
                    {
                        var meL0 = MotionEstimator.SearchBlock(refL0Y, refW, refH, srcSub.Slice(0, subPix),
                            absX, absY, predL0X, predL0Y, searchRangePel, maxSadEvalsPerMb,
                            bWidth: spw, bHeight: sph, enableSubpel: enableSubpel);
                        subMvL0X[sp] = meL0.MvX; subMvL0Y[sp] = meL0.MvY;
                        totalMvdBits += EgBits(meL0.MvX - predL0X) + EgBits(meL0.MvY - predL0Y);
                        if (dir == Dir.L0) totalSad += meL0.Sad;
                    }
                    if (dir == Dir.L1 || dir == Dir.Bi)
                    {
                        var meL1 = MotionEstimator.SearchBlock(refL1Y, refW, refH, srcSub.Slice(0, subPix),
                            absX, absY, predL1X, predL1Y, searchRangePel, maxSadEvalsPerMb,
                            bWidth: spw, bHeight: sph, enableSubpel: enableSubpel);
                        subMvL1X[sp] = meL1.MvX; subMvL1Y[sp] = meL1.MvY;
                        totalMvdBits += EgBits(meL1.MvX - predL1X) + EgBits(meL1.MvY - predL1Y);
                        if (dir == Dir.L1) totalSad += meL1.Sad;
                    }
                    if (dir == Dir.Bi)
                    {
                        MotionEstimator.LumaPredictBlock(refL0Y, refW, refH,
                            absX, absY, subMvL0X[sp], subMvL0Y[sp], spw, sph, predL0Buf.Slice(0, subPix));
                        MotionEstimator.LumaPredictBlock(refL1Y, refW, refH,
                            absX, absY, subMvL1X[sp], subMvL1Y[sp], spw, sph, predL1Buf.Slice(0, subPix));
                        int biSad = 0;
                        for (int i = 0; i < subPix; i++)
                            biSad += Math.Abs(srcSub[i] - ((predL0Buf[i] + predL1Buf[i] + 1) >> 1));
                        totalSad += biSad;
                    }
                }

                // Cost: sub_mb_type bits (rough) + per-sub-partition mvd bits.
                int subTypeBits = sub <= 3 ? 3 : sub <= 6 ? 5 : sub <= 10 ? 7 : 5;
                int cost = totalSad + lambda * (subTypeBits + totalMvdBits);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestQ = new QuadCandidate
                    {
                        SubMbType = sub,
                        Cost = cost,
                        MvL0X = subMvL0X, MvL0Y = subMvL0Y,
                        MvL1X = subMvL1X, MvL1Y = subMvL1Y,
                    };
                }
            }

            winners[q] = bestQ;
            subMbType[q] = bestQ.SubMbType;

            // Fill per-4x4-block MVs based on the winning sub_mb_type's layout.
            var winLayout = SubMbPartLayouts[bestQ.SubMbType];
            Dir winDir = SubMbTypeDir[bestQ.SubMbType];
            bool useL0 = winDir == Dir.L0 || winDir == Dir.Bi;
            bool useL1 = winDir == Dir.L1 || winDir == Dir.Bi;
            for (int sp = 0; sp < winLayout.Length; sp++)
            {
                var (spx, spy, spw, sph) = winLayout[sp];
                int bx0 = (qx + spx) / 4, by0 = (qy + spy) / 4;
                int bxN = spw / 4, byN = sph / 4;
                for (int by = by0; by < by0 + byN; by++)
                    for (int bx = bx0; bx < bx0 + bxN; bx++)
                    {
                        int idx = SpatialToRaster[by * 4 + bx];
                        if (useL0) { mvL0XPerBlock[idx] = bestQ.MvL0X![sp]; mvL0YPerBlock[idx] = bestQ.MvL0Y![sp]; }
                        if (useL1) { mvL1XPerBlock[idx] = bestQ.MvL1X![sp]; mvL1YPerBlock[idx] = bestQ.MvL1Y![sp]; }
                    }
            }

            // Build the quadrant's luma + chroma prediction using winning sub_mb_type.
            for (int sp = 0; sp < winLayout.Length; sp++)
            {
                var (spx, spy, spw, sph) = winLayout[sp];
                int absX = mbX * 16 + qx + spx;
                int absY = mbY * 16 + qy + spy;
                int subPix = spw * sph;
                if (useL0) MotionEstimator.LumaPredictBlock(refL0Y, refW, refH,
                    absX, absY, bestQ.MvL0X![sp], bestQ.MvL0Y![sp], spw, sph, tmpL0.Slice(0, subPix));
                if (useL1) MotionEstimator.LumaPredictBlock(refL1Y, refW, refH,
                    absX, absY, bestQ.MvL1X![sp], bestQ.MvL1Y![sp], spw, sph, tmpL1.Slice(0, subPix));
                for (int yy = 0; yy < sph; yy++)
                    for (int xx = 0; xx < spw; xx++)
                    {
                        int outY = qy + spy + yy;
                        int outX = qx + spx + xx;
                        int srcIdx = yy * spw + xx;
                        int v;
                        if (useL0 && useL1) v = (tmpL0[srcIdx] + tmpL1[srcIdx] + 1) >> 1;
                        else if (useL0) v = tmpL0[srcIdx];
                        else v = tmpL1[srcIdx];
                        predY[outY * 16 + outX] = (byte)v;
                    }

                // Chroma: half-resolution sub-partition.
                int cspx = spx / 2, cspy = spy / 2, cspw = spw / 2, csph = sph / 2;
                int cabsX = mbX * 8 + qx / 2 + cspx;
                int cabsY = mbY * 8 + qy / 2 + cspy;
                int cpix = cspw * csph;
                for (int comp = 0; comp < 2; comp++)
                {
                    byte[] refL0 = comp == 0 ? refL0U : refL0V;
                    byte[] refL1 = comp == 0 ? refL1U : refL1V;
                    Span<byte> outChromaPred = comp == 0 ? predU : predV;
                    if (useL0) MotionEstimator.ChromaPredictBlock(refL0, refCw, refCh,
                        cabsX, cabsY, bestQ.MvL0X![sp], bestQ.MvL0Y![sp], cspw, csph, tmpL0c.Slice(0, cpix));
                    if (useL1) MotionEstimator.ChromaPredictBlock(refL1, refCw, refCh,
                        cabsX, cabsY, bestQ.MvL1X![sp], bestQ.MvL1Y![sp], cspw, csph, tmpL1c.Slice(0, cpix));
                    for (int yy = 0; yy < csph; yy++)
                        for (int xx = 0; xx < cspw; xx++)
                        {
                            int outY = qy / 2 + cspy + yy;
                            int outX = qx / 2 + cspx + xx;
                            int srcIdx = yy * cspw + xx;
                            int v;
                            if (useL0 && useL1) v = (tmpL0c[srcIdx] + tmpL1c[srcIdx] + 1) >> 1;
                            else if (useL0) v = tmpL0c[srcIdx];
                            else v = tmpL1c[srcIdx];
                            outChromaPred[outY * 8 + outX] = (byte)v;
                        }
                }
            }
        }

        // Residual + reconstruction using shared helpers.
        MacroblockEncoderInter.BuildInterCandidateFromPrediction(bundle, srcY, srcStrideY, qpY);
        int qPc = MacroblockEncoderInter.ChromaQpFromLuma(qpY);
        MacroblockEncoderInter.EncodeChromaFromPrediction(srcU, srcV, srcStrideC, qPc, bundle);

        return new BCandidate
        {
            Shape = Shape.P8x8,
            Bundle = bundle,
            SubMbTypes = subMbType,
            MvL0XPerBlock = mvL0XPerBlock, MvL0YPerBlock = mvL0YPerBlock,
            MvL1XPerBlock = mvL1XPerBlock, MvL1YPerBlock = mvL1YPerBlock,
        };
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

    /// <summary>For partitioned B-MB (16x8 / 8x16 / 8x8): populate per-block MV/refIdx/predFlag
    /// arrays from the per-partition values stored on the candidate.</summary>
    private static void PopulatePartitionedState(BCandidate cand, MacroblockEncoderState state)
    {
        if (cand.Shape == Shape.P8x8)
        {
            PopulateP8x8State(cand, state);
            return;
        }
        // Block index ranges per partition.
        // 16x8: part0 = rows 0..1 (raster 0..7), part1 = rows 2..3 (raster 8..15).
        // 8x16: part0 = cols 0..1, part1 = cols 2..3.
        for (int p = 0; p < 2; p++)
        {
            Dir d = p == 0 ? cand.Direction : cand.Part1Direction;
            bool useL0 = d == Dir.L0 || d == Dir.Bi;
            bool useL1 = d == Dir.L1 || d == Dir.Bi;
            int mvL0X = p == 0 ? cand.MvL0X : cand.Part1MvL0X;
            int mvL0Y = p == 0 ? cand.MvL0Y : cand.Part1MvL0Y;
            int mvL1X = p == 0 ? cand.MvL1X : cand.Part1MvL1X;
            int mvL1Y = p == 0 ? cand.MvL1Y : cand.Part1MvL1Y;

            for (int by = 0; by < 4; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    bool inPart = cand.Shape == Shape.P16x8
                        ? (p == 0 ? by < 2 : by >= 2)
                        : (p == 0 ? bx < 2 : bx >= 2);
                    if (!inPart) continue;
                    int idx = SpatialToRaster[by * 4 + bx];
                    state.PredFlagL0Block[idx] = useL0 ? (byte)1 : (byte)0;
                    state.PredFlagL1Block[idx] = useL1 ? (byte)1 : (byte)0;
                    state.MvL0XBlock[idx] = useL0 ? mvL0X : 0;
                    state.MvL0YBlock[idx] = useL0 ? mvL0Y : 0;
                    state.MvL1XBlock[idx] = useL1 ? mvL1X : 0;
                    state.MvL1YBlock[idx] = useL1 ? mvL1Y : 0;
                    state.MvdL0XBlock[idx] = 0; // CABAC layer will fill these post-mvd-emit.
                    state.MvdL0YBlock[idx] = 0;
                    state.MvdL1XBlock[idx] = 0;
                    state.MvdL1YBlock[idx] = 0;
                }
            // Per-8x8-quadrant refIdx.
            int[] partQuads = QuadrantsOfPartition(cand.Shape, p);
            foreach (int q in partQuads)
            {
                state.RefIdxL08x8[q] = useL0 ? 0 : -1;
                state.RefIdxL18x8[q] = useL1 ? 0 : -1;
            }
        }
        state.MvL0X = cand.MvL0X;
        state.MvL0Y = cand.MvL0Y;
        state.RefIdxL0 = (cand.Direction == Dir.L0 || cand.Direction == Dir.Bi) ? 0 : -1;
        state.RefIdxL1 = (cand.Direction == Dir.L1 || cand.Direction == Dir.Bi) ? 0 : -1;
    }

    /// <summary>For P8x8: set per-quadrant refIdx; zero all per-block arrays (MV / MVD / PredFlag).
    /// The CAVLC and CABAC emit paths fill these progressively per sub-partition so neighbor
    /// reads in MV prediction match the decoder. (CABAC additionally bulk-sets PredFlag per
    /// quadrant before its MV emit phase to mirror the decoder's refIdx → PredFlag pattern.)</summary>
    private static void PopulateP8x8State(BCandidate cand, MacroblockEncoderState state)
    {
        for (int i = 0; i < 16; i++)
        {
            state.PredFlagL0Block[i] = 0;
            state.PredFlagL1Block[i] = 0;
            state.MvL0XBlock[i] = 0;
            state.MvL0YBlock[i] = 0;
            state.MvL1XBlock[i] = 0;
            state.MvL1YBlock[i] = 0;
            state.MvdL0XBlock[i] = 0;
            state.MvdL0YBlock[i] = 0;
            state.MvdL1XBlock[i] = 0;
            state.MvdL1YBlock[i] = 0;
        }
        for (int q = 0; q < 4; q++)
        {
            int sub = cand.SubMbTypes![q];
            Dir dir = SubMbTypeDir[sub];
            bool useL0 = dir == Dir.L0 || dir == Dir.Bi;
            bool useL1 = dir == Dir.L1 || dir == Dir.Bi;
            state.RefIdxL08x8[q] = useL0 ? 0 : -1;
            state.RefIdxL18x8[q] = useL1 ? 0 : -1;
        }
        // Convenience scalar MV: quadrant 0's first 4x4 block.
        int sub0 = cand.SubMbTypes![0];
        Dir dir0 = SubMbTypeDir[sub0];
        bool useL00 = dir0 == Dir.L0 || dir0 == Dir.Bi;
        state.MvL0X = useL00 ? cand.MvL0XPerBlock![0] : 0;
        state.MvL0Y = useL00 ? cand.MvL0YPerBlock![0] : 0;
        state.RefIdxL0 = useL00 ? 0 : -1;
        state.RefIdxL1 = (dir0 == Dir.L1 || dir0 == Dir.Bi) ? 0 : -1;
    }

    /// <summary>Companion to <see cref="FillBlockMvds"/>: writes the final per-block MV after a
    /// sub-partition's mvd has been emitted so subsequent sub-partitions' MV predictors see this
    /// quadrant's MVs (matching the decoder, which fills these progressively too).</summary>
    private static void FillBlockMvs(MacroblockEncoderState state, int bx, int by, int bw, int bh,
        int listX, int mvX, int mvY)
    {
        for (int yy = by; yy < by + bh; yy++)
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int idx = SpatialToRaster[yy * 4 + xx];
                if (listX == 0) { state.MvL0XBlock[idx] = mvX; state.MvL0YBlock[idx] = mvY; }
                else { state.MvL1XBlock[idx] = mvX; state.MvL1YBlock[idx] = mvY; }
            }
    }

    /// <summary>Set PredFlag bytes for a sub-partition's 4x4 blocks.</summary>
    private static void FillBlockPredFlags(MacroblockEncoderState state, int bx, int by, int bw, int bh, int listX)
    {
        for (int yy = by; yy < by + bh; yy++)
            for (int xx = bx; xx < bx + bw; xx++)
            {
                int idx = SpatialToRaster[yy * 4 + xx];
                if (listX == 0) state.PredFlagL0Block[idx] = 1;
                else state.PredFlagL1Block[idx] = 1;
            }
    }

    /// <summary>Set PredFlag bytes for an 8x8 quadrant (CABAC: bulk-set per quadrant in the
    /// refIdx phase of its decoder, before MV prediction reads neighbor PredFlag).</summary>
    internal static void SetQuadrantPredFlag(MacroblockEncoderState state, int q, int listX)
    {
        int qBx = (q & 1) * 2, qBy = (q >> 1) * 2;
        for (int by = qBy; by < qBy + 2; by++)
            for (int bx = qBx; bx < qBx + 2; bx++)
            {
                int idx = SpatialToRaster[by * 4 + bx];
                if (listX == 0) state.PredFlagL0Block[idx] = 1;
                else state.PredFlagL1Block[idx] = 1;
            }
    }

    /// <summary>Expose <see cref="FillBlockMvs"/> and <see cref="FillBlockPredFlags"/> to CabacMbEncoderB.</summary>
    internal static void FillBlockMvsPublic(MacroblockEncoderState state, int bx, int by, int bw, int bh,
        int listX, int mvX, int mvY) => FillBlockMvs(state, bx, by, bw, bh, listX, mvX, mvY);

    /// <summary>Which 8x8 quadrants (raster: 0=TL, 1=TR, 2=BL, 3=BR) belong to partition p of a
    /// given shape. 16x8 part0 = TL+TR (0,1); part1 = BL+BR (2,3). 8x16 part0 = TL+BL (0,2);
    /// part1 = TR+BR (1,3).</summary>
    private static int[] QuadrantsOfPartition(Shape shape, int p)
    {
        if (shape == Shape.P16x8) return p == 0 ? new[] { 0, 1 } : new[] { 2, 3 };
        return p == 0 ? new[] { 0, 2 } : new[] { 1, 3 };
    }

    /// <summary>Map (shape, dir0, dir1) → B mb_type codeword (Table 7-14). Phase 5d-min: only
    /// 16x16 (codes 0..3) and 16x8/8x16 (codes 4..21) supported.</summary>
    internal static int MbTypeOf(BCandidate c)
    {
        if (c.Shape == Shape.Sq16x16)
        {
            return c.Direction switch
            {
                Dir.Direct => 0,
                Dir.L0 => 1,
                Dir.L1 => 2,
                Dir.Bi => 3,
                _ => 0,
            };
        }
        if (c.Shape == Shape.P8x8) return 22;
        // 16x8 = codes 4/6/8/10/12/14/16/18/20 (even). 8x16 = codes 5/7/9/11/13/15/17/19/21 (odd).
        // Direction pair (part0, part1) → 9 combos × 2 shapes = 18 codes.
        // Lookup: row = pair-index (0..8), col = 0 (16x8) or 1 (8x16). 4 + row*2 + col.
        int pairIdx = PairIndex(c.Direction, c.Part1Direction);
        int col = c.Shape == Shape.P16x8 ? 0 : 1;
        return 4 + pairIdx * 2 + col;
    }

    /// <summary>Map (dir0, dir1) → index 0..8 used to compute the partitioned mb_type code.
    /// Ordering matches Table 7-14: (L0,L0), (L1,L1), (L0,L1), (L1,L0), (L0,Bi), (L1,Bi),
    /// (Bi,L0), (Bi,L1), (Bi,Bi).</summary>
    internal static int PairIndex(Dir d0, Dir d1)
    {
        return (d0, d1) switch
        {
            (Dir.L0, Dir.L0) => 0,
            (Dir.L1, Dir.L1) => 1,
            (Dir.L0, Dir.L1) => 2,
            (Dir.L1, Dir.L0) => 3,
            (Dir.L0, Dir.Bi) => 4,
            (Dir.L1, Dir.Bi) => 5,
            (Dir.Bi, Dir.L0) => 6,
            (Dir.Bi, Dir.L1) => 7,
            (Dir.Bi, Dir.Bi) => 8,
            _ => throw new InvalidOperationException($"unsupported partition direction pair ({d0}, {d1})"),
        };
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
        state.RawMbType = MbTypeOf(cand);
        state.CbpLuma = cand.Bundle.CbpLuma;
        state.CbpChroma = cand.Bundle.CbpChroma;
        state.QpY = qpY;

        // For partitioned shapes, populate per-block arrays per partition.
        if (cand.Shape != Shape.Sq16x16)
        {
            PopulatePartitionedState(cand, state);
            return;
        }

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

    /// <summary>Emit B-slice macroblock_layer() syntax (CAVLC) for any chosen candidate. Dispatches
    /// to the 16x16 path or the partitioned path based on <paramref name="cand"/>.Shape. Caller
    /// must have emitted any mb_skip_run prefix already; B_Skip candidates go through mb_skip_run
    /// accumulation instead.</summary>
    internal static void EmitBMb(
        BitWriter w,
        BCandidate cand,
        int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        if (cand.IsSkip)
            throw new InvalidOperationException("B_Skip should be handled via mb_skip_run, not EmitBMb");
        if (cand.Shape == Shape.Sq16x16)
        {
            EmitBMb16x16(w, cand, qpY, predL0X, predL0Y, predL1X, predL1Y, state, leftMb, topMb);
            return;
        }
        if (cand.Shape == Shape.P8x8)
        {
            EmitBMbP8x8(w, cand, qpY, state, leftMb, topMb, topRightMb, topLeftMb);
            return;
        }
        EmitBMbPartitioned(w, cand, qpY, state, leftMb, topMb, topRightMb, topLeftMb);
    }

    /// <summary>CAVLC emit for a B_8x8 macroblock (mb_type=22). Emits: mb_type, 4× sub_mb_type,
    /// per-quadrant per-sub-partition per-list mvds (spec iteration: all L0 over quadrants/sub-
    /// partitions then all L1), CBP + qp_delta + residual.</summary>
    private static void EmitBMbP8x8(
        BitWriter w,
        BCandidate cand,
        int qpY,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        ExpGolombWriter.WriteUe(w, 22); // mb_type = 22 (B_8x8)

        // 4 sub_mb_types in raster order.
        for (int q = 0; q < 4; q++)
        {
            ExpGolombWriter.WriteUe(w, (uint)cand.SubMbTypes![q]);
        }

        // Populate per-block state (MVs / refIdx / predFlags) so the per-sub-partition MV
        // predictor sees consistent neighbor values for in-MB blocks.
        PopulateBMbState(cand, state, state.MbAddress, qpY, 0, 0, 0, 0);

        // ---- mvd_l0 per quadrant, per sub-partition ----
        for (int q = 0; q < 4; q++)
        {
            int sub = cand.SubMbTypes![q];
            Dir dir = SubMbTypeDir[sub];
            if (dir != Dir.L0 && dir != Dir.Bi) continue;
            EmitQuadrantMvdsCavlc(w, cand, state, q, sub, listX: 0,
                leftMb, topMb, topRightMb, topLeftMb);
        }
        // ---- mvd_l1 per quadrant, per sub-partition ----
        for (int q = 0; q < 4; q++)
        {
            int sub = cand.SubMbTypes![q];
            Dir dir = SubMbTypeDir[sub];
            if (dir != Dir.L1 && dir != Dir.Bi) continue;
            EmitQuadrantMvdsCavlc(w, cand, state, q, sub, listX: 1,
                leftMb, topMb, topRightMb, topLeftMb);
        }

        // CBP + residual.
        int cbp = cand.Bundle.CbpLuma | (cand.Bundle.CbpChroma << 4);
        int code = MacroblockEncoderInter.CbpToCodeNumInter(cbp);
        if (code < 0) throw new InvalidOperationException($"unmappable inter CBP {cbp}");
        ExpGolombWriter.WriteUe(w, (uint)code);

        bool hasResidual = cand.Bundle.CbpLuma != 0 || cand.Bundle.CbpChroma != 0;
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta
        }
        EmitInterResidualCavlc(w, cand, state, leftMb, topMb);
    }

    /// <summary>Emit mvds for one direction of one quadrant of a B_8x8 MB. Iterates the sub-
    /// partitions (1/2/4 depending on sub_mb_type), reading the MV at each sub-partition's
    /// top-left 4x4 block from <paramref name="cand"/>'s per-block array.</summary>
    private static void EmitQuadrantMvdsCavlc(
        BitWriter w, BCandidate cand, MacroblockEncoderState state,
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
            (int predX, int predY) = PredictPartitionMvBList(
                state, rawMbType: 0, partIdx: 0,
                bx: bx0, by: by0, bw: bw, bh: bh,
                curRefIdx: 0, listX: listX,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = listX == 0 ? cand.MvL0XPerBlock![idx0] : cand.MvL1XPerBlock![idx0];
            int mvY = listX == 0 ? cand.MvL0YPerBlock![idx0] : cand.MvL1YPerBlock![idx0];
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            ExpGolombWriter.WriteSe(w, mvdX);
            ExpGolombWriter.WriteSe(w, mvdY);
            FillBlockMvds(state, bx0, by0, bw, bh, listX, mvdX, mvdY);
            FillBlockMvs(state, bx0, by0, bw, bh, listX, mvX, mvY);
            FillBlockPredFlags(state, bx0, by0, bw, bh, listX);
        }
    }

    /// <summary>CAVLC emit for a 16x8 or 8x16 partitioned B-MB. mb_type code 4..21 (Table 7-14).
    /// Per partition: ref_idx omitted (num_ref_active=1); then iter-by-list mvd emission per
    /// spec §7.3.5.1 (all mvd_l0 then all mvd_l1).</summary>
    private static void EmitBMbPartitioned(
        BitWriter w,
        BCandidate cand,
        int qpY,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        int mbType = MbTypeOf(cand);
        ExpGolombWriter.WriteUe(w, (uint)mbType);

        // Populate state first so partition-1's MVD predictor can read partition 0's MVs (in-MB
        // neighbor). PopulatePartitionedState only writes MVs/refIdx, mvd arrays stay zero.
        PopulateBMbState(cand, state, state.MbAddress, qpY, 0, 0, 0, 0);

        // Partition rectangles in 4x4-block units.
        (int Bx, int By, int Bw, int Bh)[] partsB = cand.Shape == Shape.P16x8
            ? new[] { (0, 0, 4, 2), (0, 2, 4, 2) }
            : new[] { (0, 0, 2, 4), (2, 0, 2, 4) };

        // ---- mvd_l0 per partition (only when direction includes L0). ----
        for (int p = 0; p < 2; p++)
        {
            Dir d = p == 0 ? cand.Direction : cand.Part1Direction;
            if (d != Dir.L0 && d != Dir.Bi) continue;
            var (bx, by, bw, bh) = partsB[p];
            (int predX, int predY) = PredictPartitionMvBList(
                state, MbTypeOf(cand), p, bx, by, bw, bh, curRefIdx: 0, listX: 0,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = p == 0 ? cand.MvL0X : cand.Part1MvL0X;
            int mvY = p == 0 ? cand.MvL0Y : cand.Part1MvL0Y;
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            ExpGolombWriter.WriteSe(w, mvdX);
            ExpGolombWriter.WriteSe(w, mvdY);
            FillBlockMvds(state, bx, by, bw, bh, listX: 0, mvdX, mvdY);
        }
        // ---- mvd_l1 per partition. ----
        for (int p = 0; p < 2; p++)
        {
            Dir d = p == 0 ? cand.Direction : cand.Part1Direction;
            if (d != Dir.L1 && d != Dir.Bi) continue;
            var (bx, by, bw, bh) = partsB[p];
            (int predX, int predY) = PredictPartitionMvBList(
                state, MbTypeOf(cand), p, bx, by, bw, bh, curRefIdx: 0, listX: 1,
                leftMb, topMb, topRightMb, topLeftMb);
            int mvX = p == 0 ? cand.MvL1X : cand.Part1MvL1X;
            int mvY = p == 0 ? cand.MvL1Y : cand.Part1MvL1Y;
            int mvdX = mvX - predX;
            int mvdY = mvY - predY;
            ExpGolombWriter.WriteSe(w, mvdX);
            ExpGolombWriter.WriteSe(w, mvdY);
            FillBlockMvds(state, bx, by, bw, bh, listX: 1, mvdX, mvdY);
        }

        // CBP + residual.
        int cbp = cand.Bundle.CbpLuma | (cand.Bundle.CbpChroma << 4);
        int code = MacroblockEncoderInter.CbpToCodeNumInter(cbp);
        if (code < 0) throw new InvalidOperationException($"unmappable inter CBP {cbp}");
        ExpGolombWriter.WriteUe(w, (uint)code);

        bool hasResidual = cand.Bundle.CbpLuma != 0 || cand.Bundle.CbpChroma != 0;
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta
        }

        // Reuse the CAVLC residual emit body from EmitBMb16x16 by inlining it.
        EmitInterResidualCavlc(w, cand, state, leftMb, topMb);
    }

    /// <summary>Public re-export for cross-file use (CabacMbEncoderB).</summary>
    internal static (int X, int Y) PredictPartitionMvBListPublic(
        MacroblockEncoderState cur, int rawMbType, int partIdx,
        int bx, int by, int bw, int bh, int curRefIdx, int listX,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
        => PredictPartitionMvBList(cur, rawMbType, partIdx, bx, by, bw, bh, curRefIdx, listX,
            leftMb, topMb, topRightMb, topLeftMb);

    /// <summary>Shape-aware MV predictor for a B-slice partition, list listX. Mirrors the
    /// decoder's <c>PredictMvForPartitionListB</c> (spec §8.4.1.3.1) so encoder-side MVD =
    /// MV - prediction round-trips correctly.</summary>
    private static (int X, int Y) PredictPartitionMvBList(
        MacroblockEncoderState cur, int rawMbType, int partIdx,
        int bx, int by, int bw, int bh, int curRefIdx, int listX,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        var A = GetNeighborMvB(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        var B = GetNeighborMvB(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        int cBx = bx + bw, cBy = by - 1;
        var C = GetNeighborMvB(cBx, cBy, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        if (!C.Avail)
            C = GetNeighborMvB(bx - 1, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);

        bool shape16x8 = false, shape8x16 = false;
        if (rawMbType >= 4 && rawMbType <= 21)
        {
            // Even codes (4,6,8,...,20) are 16x8; odd codes (5,7,9,...,21) are 8x16.
            shape16x8 = (rawMbType & 1) == 0;
            shape8x16 = (rawMbType & 1) == 1;
        }
        if (shape16x8)
        {
            if (partIdx == 0 && B.Avail && B.RefIdx == curRefIdx) return (B.MvX, B.MvY);
            if (partIdx == 1 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
        }
        else if (shape8x16)
        {
            if (partIdx == 0 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
            if (partIdx == 1 && C.Avail && C.RefIdx == curRefIdx) return (C.MvX, C.MvY);
        }
        if (!B.Avail && !C.Avail && A.Avail) return (A.MvX, A.MvY);

        int aX = A.Avail ? A.MvX : 0, aY = A.Avail ? A.MvY : 0, aR = A.Avail ? A.RefIdx : -1;
        int bX = B.Avail ? B.MvX : 0, bY = B.Avail ? B.MvY : 0, bR = B.Avail ? B.RefIdx : -1;
        int cX = C.Avail ? C.MvX : 0, cY = C.Avail ? C.MvY : 0, cR = C.Avail ? C.RefIdx : -1;
        int matchCount = (aR == curRefIdx ? 1 : 0) + (bR == curRefIdx ? 1 : 0) + (cR == curRefIdx ? 1 : 0);
        if (matchCount == 1)
        {
            if (aR == curRefIdx) return (aX, aY);
            if (bR == curRefIdx) return (bX, bY);
            return (cX, cY);
        }
        return (Median3(aX, bX, cX), Median3(aY, bY, cY));
    }

    private readonly struct NeighborMvInfo
    {
        public readonly bool Avail;
        public readonly int MvX, MvY, RefIdx;
        public NeighborMvInfo(bool a, int x, int y, int r) { Avail = a; MvX = x; MvY = y; RefIdx = r; }
    }

    private static NeighborMvInfo GetNeighborMvB(
        int bx, int by, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        int listX)
    {
        MacroblockEncoderState? mb;
        int nbBx, nbBy;
        if (bx >= 0 && by >= 0 && bx <= 3 && by <= 3) { mb = cur; nbBx = bx; nbBy = by; }
        else if (bx < 0 && by >= 0 && by <= 3) { mb = leftMb; nbBx = 3; nbBy = by; }
        else if (by < 0 && bx >= 0 && bx <= 3) { mb = topMb; nbBx = bx; nbBy = 3; }
        else if (bx < 0 && by < 0) { mb = topLeftMb; nbBx = 3; nbBy = 3; }
        else if (bx > 3 && by < 0) { mb = topRightMb; nbBx = 0; nbBy = 3; }
        else return new NeighborMvInfo(false, 0, 0, -1);

        if (mb is null) return new NeighborMvInfo(false, 0, 0, -1);
        if (!mb.IsInter && !mb.IsBInter) return new NeighborMvInfo(true, 0, 0, -1);
        int idx = SpatialToRaster[nbBy * 4 + nbBx];
        int q = (nbBy >> 1) * 2 + (nbBx >> 1);
        if (listX == 0)
        {
            if (mb.IsBInter && mb.PredFlagL0Block[idx] == 0) return new NeighborMvInfo(true, 0, 0, -1);
            return new NeighborMvInfo(true, mb.MvL0XBlock[idx], mb.MvL0YBlock[idx], mb.RefIdxL08x8[q]);
        }
        else
        {
            if (!mb.IsBInter) return new NeighborMvInfo(true, 0, 0, -1);
            if (mb.PredFlagL1Block[idx] == 0) return new NeighborMvInfo(true, 0, 0, -1);
            return new NeighborMvInfo(true, mb.MvL1XBlock[idx], mb.MvL1YBlock[idx], mb.RefIdxL18x8[q]);
        }
    }

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

    /// <summary>Shared CAVLC residual emit body (16 luma 4x4 + chroma DC + chroma AC) for any
    /// inter B-MB candidate.</summary>
    private static void EmitInterResidualCavlc(
        BitWriter w, BCandidate cand, MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
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

    /// <summary>Coarse bit-cost estimator for a P8x8 B-MB. mb_type=22 ue costs 9 bits ("000001011")
    /// plus 4 sub_mb_types ue (~3 bits each) + per-quadrant mvds.</summary>
    private static int EstimatedP8x8Bits(BCandidate c, int predL0X, int predL0Y, int predL1X, int predL1Y)
    {
        int bits = 9 /*mb_type=22*/ + 4 * 3 /*sub_mb_types*/;
        for (int q = 0; q < 4; q++)
        {
            int sub = c.SubMbTypes![q];
            Dir dir = SubMbTypeDir[sub];
            bool useL0 = dir == Dir.L0 || dir == Dir.Bi;
            bool useL1 = dir == Dir.L1 || dir == Dir.Bi;
            // Approximate: read MV at the quadrant's top-left 4x4 block. Sub-partitions add a few
            // extra mvds; this is a coarse estimate for mode-decision ranking only.
            int qBx = (q & 1) * 2, qBy = (q >> 1) * 2;
            int idx = SpatialToRaster[qBy * 4 + qBx];
            int numSub = SubMbPartLayouts[sub].Length;
            if (useL0)
                bits += numSub * (EgBits(c.MvL0XPerBlock![idx] - predL0X) + EgBits(c.MvL0YPerBlock![idx] - predL0Y));
            if (useL1)
                bits += numSub * (EgBits(c.MvL1XPerBlock![idx] - predL1X) + EgBits(c.MvL1YPerBlock![idx] - predL1Y));
        }
        return bits;
    }

    /// <summary>Coarse bit-cost estimator for a partitioned B-MB candidate. Uses a single MB-level
    /// MV predictor (predLN) for both partitions instead of running the per-partition shape-aware
    /// predictor — this is for ranking only, so the approximation is acceptable.</summary>
    private static int EstimatedPartitionBits(BCandidate c, int predL0X, int predL0Y, int predL1X, int predL1Y)
    {
        int bits = 5; // mb_type 4..21 ue costs roughly 5-9 bits.
        if (c.Direction == Dir.L0 || c.Direction == Dir.Bi)
            bits += EgBits(c.MvL0X - predL0X) + EgBits(c.MvL0Y - predL0Y);
        if (c.Direction == Dir.L1 || c.Direction == Dir.Bi)
            bits += EgBits(c.MvL1X - predL1X) + EgBits(c.MvL1Y - predL1Y);
        if (c.Part1Direction == Dir.L0 || c.Part1Direction == Dir.Bi)
            bits += EgBits(c.Part1MvL0X - predL0X) + EgBits(c.Part1MvL0Y - predL0Y);
        if (c.Part1Direction == Dir.L1 || c.Part1Direction == Dir.Bi)
            bits += EgBits(c.Part1MvL1X - predL1X) + EgBits(c.Part1MvL1Y - predL1Y);
        return bits;
    }

    /// <summary>Cheap exp-Golomb bit-length estimator for SAD-cost mode decision.</summary>
    private static int EgBits(int v)
    {
        uint codeNum = (uint)((v <= 0) ? (-2 * v) : (2 * v - 1));
        int n = 0; uint x = codeNum + 1; while (x > 1) { x >>= 1; n++; }
        return 2 * n + 1;
    }
}
