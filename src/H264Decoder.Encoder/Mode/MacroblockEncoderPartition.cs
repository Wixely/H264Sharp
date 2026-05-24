using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cavlc;

namespace H264Decoder.Encoder.Mode;

/// <summary>P-slice MB partition mode decision and syntax emit. Stage 3b: supports
/// raw mb_type 0..3 (P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, P_8x8 with sub_mb_type 0..3).
/// Reuses <see cref="MacroblockEncoderInter"/>'s shared transform/residual pipeline.</summary>
internal static class MacroblockEncoderPartition
{
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>One inter partition or sub-partition. Position/size in 4x4-block units within the MB.</summary>
    public readonly record struct Partition(int Bx, int By, int Bw, int Bh, int MvX, int MvY);

    /// <summary>Per-partition ME-result bundle for a candidate partition shape.</summary>
    public sealed class PartitionCandidate
    {
        public int RawMbType;              // 0=16x16, 1=16x8, 2=8x16, 3=P_8x8
        public int[] SubMbTypes = new int[4]; // for P_8x8: 0=8x8, 1=8x4, 2=4x8, 3=4x4
        public List<Partition> Partitions = new();
        public int TotalSad;
        public int EstimatedBits;
    }

    /// <summary>Run ME for each sub-rectangle and produce candidates for each partition shape
    /// (16x16, 16x8, 8x16, P_8x8 with sub-types). Returns the candidate with lowest cost.</summary>
    public static PartitionCandidate ChooseBestPartition(
        ReadOnlySpan<byte> srcLuma16x16,
        byte[] refY, int refW, int refH,
        int mbX, int mbY,
        int startMvX, int startMvY,
        int searchRangePel, int maxSadEvals, bool enableSubpel,
        bool enableSubMb,
        int lambda)
    {
        // Copy the 16x16 source into a managed buffer so local helpers can read it (Span-typed
        // parameters can't be captured by local functions in C#).
        byte[] src16 = new byte[256];
        for (int i = 0; i < 256; i++) src16[i] = srcLuma16x16[i];

        // Helper: ME for a sub-rectangle. Source is read from the 16x16 luma into a contiguous
        // (bw*4 * bh*4) byte[] starting at (bx*4, by*4) in the MB.
        MotionEstimator.MeResult MeFor(int bx, int by, int bw, int bh)
        {
            int wPx = bw * 4;
            int hPx = bh * 4;
            byte[] sub = new byte[wPx * hPx];
            for (int y = 0; y < hPx; y++)
                for (int x = 0; x < wPx; x++)
                    sub[y * wPx + x] = src16[(by * 4 + y) * 16 + (bx * 4 + x)];
            return MotionEstimator.SearchBlock(
                refY, refW, refH,
                sub,
                mbX * 16 + bx * 4, mbY * 16 + by * 4,
                startMvX, startMvY, searchRangePel, maxSadEvals,
                bWidth: wPx, bHeight: hPx,
                enableSubpel: enableSubpel);
        }

        // 16x16 candidate.
        var me16 = MeFor(0, 0, 4, 4);
        var c16 = new PartitionCandidate { RawMbType = 0, TotalSad = me16.Sad, EstimatedBits = 4 + BitsForMvd(me16.MvX - startMvX, me16.MvY - startMvY) };
        c16.Partitions.Add(new Partition(0, 0, 4, 4, me16.MvX, me16.MvY));
        PartitionCandidate best = c16;
        int bestCost = c16.TotalSad + lambda * c16.EstimatedBits;

        if (!enableSubMb) return best;

        // 16x8 candidate.
        var meTop = MeFor(0, 0, 4, 2);
        var meBot = MeFor(0, 2, 4, 2);
        var c16x8 = new PartitionCandidate { RawMbType = 1, TotalSad = meTop.Sad + meBot.Sad };
        c16x8.Partitions.Add(new Partition(0, 0, 4, 2, meTop.MvX, meTop.MvY));
        c16x8.Partitions.Add(new Partition(0, 2, 4, 2, meBot.MvX, meBot.MvY));
        c16x8.EstimatedBits = 4 + BitsForMvd(meTop.MvX - startMvX, meTop.MvY - startMvY) + BitsForMvd(meBot.MvX - startMvX, meBot.MvY - startMvY);
        int cost16x8 = c16x8.TotalSad + lambda * c16x8.EstimatedBits;
        if (cost16x8 < bestCost) { best = c16x8; bestCost = cost16x8; }

        // 8x16 candidate.
        var meLeft = MeFor(0, 0, 2, 4);
        var meRight = MeFor(2, 0, 2, 4);
        var c8x16 = new PartitionCandidate { RawMbType = 2, TotalSad = meLeft.Sad + meRight.Sad };
        c8x16.Partitions.Add(new Partition(0, 0, 2, 4, meLeft.MvX, meLeft.MvY));
        c8x16.Partitions.Add(new Partition(2, 0, 2, 4, meRight.MvX, meRight.MvY));
        c8x16.EstimatedBits = 4 + BitsForMvd(meLeft.MvX - startMvX, meLeft.MvY - startMvY) + BitsForMvd(meRight.MvX - startMvX, meRight.MvY - startMvY);
        int cost8x16 = c8x16.TotalSad + lambda * c8x16.EstimatedBits;
        if (cost8x16 < bestCost) { best = c8x16; bestCost = cost8x16; }

        // P_8x8 candidate: per 8x8 quadrant, try 8x8 first; if SAD high relative to overall,
        // try 8x4 / 4x8 / 4x4 sub-partitions and pick the lowest-SAD sub-type per quadrant.
        var cP8x8 = new PartitionCandidate { RawMbType = 3, EstimatedBits = 4 };
        int totalSadP8x8 = 0;
        for (int q = 0; q < 4; q++)
        {
            int qBx = (q & 1) * 2;
            int qBy = (q >> 1) * 2;
            // Try sub_mb_type 0 (8x8) first.
            var me8x8 = MeFor(qBx, qBy, 2, 2);
            int bestSubSad = me8x8.Sad;
            int bestSubType = 0;
            List<Partition> bestSubParts = new() { new Partition(qBx, qBy, 2, 2, me8x8.MvX, me8x8.MvY) };
            int bestSubBits = 3 + BitsForMvd(me8x8.MvX - startMvX, me8x8.MvY - startMvY);

            // Early-exit: if 8x8 SAD is very small, skip the smaller splits.
            int earlyExitThresh = 64 * 2 * 2; // ~SAD/sample of 1 -> very low.
            if (me8x8.Sad > earlyExitThresh)
            {
                // sub_mb_type 1 (8x4): two horizontal halves.
                var me8x4a = MeFor(qBx, qBy,     2, 1);
                var me8x4b = MeFor(qBx, qBy + 1, 2, 1);
                int sad8x4 = me8x4a.Sad + me8x4b.Sad;
                if (sad8x4 < bestSubSad)
                {
                    bestSubSad = sad8x4;
                    bestSubType = 1;
                    bestSubParts = new()
                    {
                        new Partition(qBx, qBy,     2, 1, me8x4a.MvX, me8x4a.MvY),
                        new Partition(qBx, qBy + 1, 2, 1, me8x4b.MvX, me8x4b.MvY),
                    };
                    bestSubBits = 3 + BitsForMvd(me8x4a.MvX - startMvX, me8x4a.MvY - startMvY) + BitsForMvd(me8x4b.MvX - startMvX, me8x4b.MvY - startMvY);
                }
                // sub_mb_type 2 (4x8): two vertical halves.
                var me4x8a = MeFor(qBx,     qBy, 1, 2);
                var me4x8b = MeFor(qBx + 1, qBy, 1, 2);
                int sad4x8 = me4x8a.Sad + me4x8b.Sad;
                if (sad4x8 < bestSubSad)
                {
                    bestSubSad = sad4x8;
                    bestSubType = 2;
                    bestSubParts = new()
                    {
                        new Partition(qBx,     qBy, 1, 2, me4x8a.MvX, me4x8a.MvY),
                        new Partition(qBx + 1, qBy, 1, 2, me4x8b.MvX, me4x8b.MvY),
                    };
                    bestSubBits = 3 + BitsForMvd(me4x8a.MvX - startMvX, me4x8a.MvY - startMvY) + BitsForMvd(me4x8b.MvX - startMvX, me4x8b.MvY - startMvY);
                }
                // sub_mb_type 3 (4x4): four sub-blocks.
                var m00 = MeFor(qBx,     qBy,     1, 1);
                var m10 = MeFor(qBx + 1, qBy,     1, 1);
                var m01 = MeFor(qBx,     qBy + 1, 1, 1);
                var m11 = MeFor(qBx + 1, qBy + 1, 1, 1);
                int sad4x4 = m00.Sad + m10.Sad + m01.Sad + m11.Sad;
                if (sad4x4 < bestSubSad)
                {
                    bestSubSad = sad4x4;
                    bestSubType = 3;
                    bestSubParts = new()
                    {
                        new Partition(qBx,     qBy,     1, 1, m00.MvX, m00.MvY),
                        new Partition(qBx + 1, qBy,     1, 1, m10.MvX, m10.MvY),
                        new Partition(qBx,     qBy + 1, 1, 1, m01.MvX, m01.MvY),
                        new Partition(qBx + 1, qBy + 1, 1, 1, m11.MvX, m11.MvY),
                    };
                    bestSubBits = 3
                        + BitsForMvd(m00.MvX - startMvX, m00.MvY - startMvY)
                        + BitsForMvd(m10.MvX - startMvX, m10.MvY - startMvY)
                        + BitsForMvd(m01.MvX - startMvX, m01.MvY - startMvY)
                        + BitsForMvd(m11.MvX - startMvX, m11.MvY - startMvY);
                }
            }
            cP8x8.SubMbTypes[q] = bestSubType;
            cP8x8.Partitions.AddRange(bestSubParts);
            totalSadP8x8 += bestSubSad;
            cP8x8.EstimatedBits += bestSubBits;
        }
        cP8x8.TotalSad = totalSadP8x8;
        int costP8x8 = cP8x8.TotalSad + lambda * cP8x8.EstimatedBits;
        if (costP8x8 < bestCost) { best = cP8x8; bestCost = costP8x8; }

        return best;
    }

    /// <summary>Rough bit estimate for an MVD pair: 1 bit per leading magnitude bit (Exp-Golomb-ish).</summary>
    private static int BitsForMvd(int mvdX, int mvdY)
    {
        return EgBits(mvdX) + EgBits(mvdY);
    }

    /// <summary>Rough Exp-Golomb-coded bit length for a signed value.</summary>
    private static int EgBits(int v)
    {
        // se(v): code_num = 2|v|-1 for v>0, code_num = -2v for v<0; ue length = 2*log2(code_num+1)+1.
        uint codeNum = v == 0 ? 0u : v > 0 ? (uint)(2 * v - 1) : (uint)(-2 * v);
        int n = 0; uint t = codeNum + 1;
        while (t > 1) { t >>= 1; n++; }
        return 2 * n + 1;
    }

    /// <summary>Build a per-block prediction buffer for a multi-partition candidate.
    /// Fills out a 16x16 luma prediction and the 8x8 chroma predictions, matching what
    /// the decoder will reconstruct given the chosen partition shape + MVs.</summary>
    public static void BuildPrediction(
        PartitionCandidate cand,
        byte[] refY, int refW, int refH,
        byte[] refU, byte[] refV, int refCw, int refCh,
        int mbX, int mbY,
        Span<byte> predY, Span<byte> predU, Span<byte> predV)
    {
        Span<byte> tmp = stackalloc byte[256];
        foreach (var p in cand.Partitions)
        {
            int pxLuma = p.Bx * 4;
            int pyLuma = p.By * 4;
            int wLuma = p.Bw * 4;
            int hLuma = p.Bh * 4;
            // Luma MC for this partition into a temp buffer of size (wLuma * hLuma).
            Span<byte> dstL = tmp[..(wLuma * hLuma)];
            MotionEstimator.LumaPredictBlock(refY, refW, refH,
                mbX * 16 + pxLuma, mbY * 16 + pyLuma, p.MvX, p.MvY, wLuma, hLuma, dstL);
            for (int y = 0; y < hLuma; y++)
                for (int x = 0; x < wLuma; x++)
                    predY[(pyLuma + y) * 16 + (pxLuma + x)] = dstL[y * wLuma + x];

            // Chroma: partition size in 8x8 luma maps to half-size chroma. For partitions whose
            // luma size is < 8 (e.g. 4x4 sub-parts), chroma reuses the chroma partitioning rule
            // (each chroma 4x4 sub-block has its own MV per spec §8.4.1.4).
            int pxC = p.Bx * 2;
            int pyC = p.By * 2;
            int wC = p.Bw * 2;
            int hC = p.Bh * 2;
            Span<byte> dstU = tmp[..(wC * hC)];
            MotionEstimator.ChromaPredictBlock(refU, refCw, refCh,
                mbX * 8 + pxC, mbY * 8 + pyC, p.MvX, p.MvY, wC, hC, dstU);
            for (int y = 0; y < hC; y++)
                for (int x = 0; x < wC; x++)
                    predU[(pyC + y) * 8 + (pxC + x)] = dstU[y * wC + x];
            Span<byte> dstV = tmp[..(wC * hC)];
            MotionEstimator.ChromaPredictBlock(refV, refCw, refCh,
                mbX * 8 + pxC, mbY * 8 + pyC, p.MvX, p.MvY, wC, hC, dstV);
            for (int y = 0; y < hC; y++)
                for (int x = 0; x < wC; x++)
                    predV[(pyC + y) * 8 + (pxC + x)] = dstV[y * wC + x];
        }
    }

    /// <summary>Emit the macroblock_layer() syntax + residual for a chosen partition candidate.
    /// Writes mb_type, sub_mb_types (if P_8x8), ref_idx_l0 (none — single ref), mvd_l0,
    /// CBP, qp_delta, residual. Updates <paramref name="state"/> with per-block MV/ref/NZC.</summary>
    public static void EmitPartitionMb(
        BitWriter w,
        PartitionCandidate cand,
        MacroblockEncoderInter.InterEncodeBundle bundle,
        int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        MacroblockEncoderState state)
    {
        ExpGolombWriter.WriteUe(w, (uint)cand.RawMbType);
        if (cand.RawMbType == 3) // P_8x8
        {
            for (int q = 0; q < 4; q++) ExpGolombWriter.WriteUe(w, (uint)cand.SubMbTypes[q]);
        }

        // ref_idx_l0 — single reference (max=0), so nothing is signaled per spec
        // (num_ref_idx_active_minus1=0 means te(v) becomes 1 bit, but our SPS has
        // MaxNumRefFrames=1 → NumRefIdxL0ActiveMinus1=0 → no ref_idx_l0 written).

        // Pre-initialize per-block state arrays to zero (defaults). RefIdx is single-ref (0) for all
        // quadrants. WriteMvds will then fill per-block MVs partition-by-partition.
        for (int i = 0; i < 16; i++) { state.MvL0XBlock[i] = 0; state.MvL0YBlock[i] = 0; }
        for (int q = 0; q < 4; q++) state.RefIdxL08x8[q] = 0;
        // Set IsInter BEFORE WriteMvds — the partition MV predictor reads cur.IsInter for blocks
        // within the same MB; if false, neighbor MVs get refIdx=-1 and the median falls back wrong.
        state.IsInter = true;
        state.RawMbType = cand.RawMbType;

        // mvd_l0 per partition (in MB partition raster, then sub-partition raster).
        WriteMvds(w, cand, state, leftMb, topMb, topRightMb, topLeftMb);

        // Set convenience scalar MV from partition 0 for legacy callers.
        if (cand.Partitions.Count > 0)
        {
            state.MvL0X = cand.Partitions[0].MvX;
            state.MvL0Y = cand.Partitions[0].MvY;
            state.RefIdxL0 = 0;
        }

        // CBP + qp_delta + residual.
        int cbp = bundle.CbpLuma | (bundle.CbpChroma << 4);
        int code = MacroblockEncoderInter.CbpToCodeNumInter(cbp);
        if (code < 0) throw new InvalidOperationException($"unmappable inter CBP {cbp}");
        ExpGolombWriter.WriteUe(w, (uint)code);
        bool hasResidual = bundle.CbpLuma != 0 || bundle.CbpChroma != 0;
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta = 0
        }

        state.IsInter = true;
        state.IsInterP16x16 = cand.RawMbType == 0;
        state.IsIntra16x16 = false;
        state.RawMbType = cand.RawMbType;
        state.CbpLuma = bundle.CbpLuma;
        state.CbpChroma = bundle.CbpChroma;
        state.QpY = qpY;

        // Write residual blocks (same path as 16x16 case).
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

    private static void WriteMvds(
        BitWriter w, PartitionCandidate cand, MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        // We must emit partition MVDs in the exact iteration order the decoder reads them:
        //   rawMbType 0..2: numMbPart partitions in raster order.
        //   rawMbType 3 (P_8x8): per-quadrant raster, then sub-partition raster.
        // Each MVD uses the predicted MV under the spec's PredictMvForPartition rules, which
        // depends on previously-decoded blocks within this MB.
        // Strategy: walk in the decoder's iteration order; for each partition, compute the
        // predicted MV using PartitionMvPredictor against the current state (which has per-block
        // MVs filled-in for partitions already processed); write mvd = mv - pred; then update
        // the per-block MV arrays before moving on.
        switch (cand.RawMbType)
        {
            case 0: // P_L0_16x16
                EmitPartitionMvd(w, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 4, bh: 4,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                break;
            case 1: // 16x8
                EmitPartitionMvd(w, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 4, bh: 2,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                EmitPartitionMvd(w, state, cand, partIdx: 1,
                    bx: 0, by: 2, bw: 4, bh: 2,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[1]);
                break;
            case 2: // 8x16
                EmitPartitionMvd(w, state, cand, partIdx: 0,
                    bx: 0, by: 0, bw: 2, bh: 4,
                    leftMb, topMb, topRightMb, topLeftMb,
                    cand.Partitions[0]);
                EmitPartitionMvd(w, state, cand, partIdx: 1,
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
                    foreach (var (spBx, spBy, spBw, spBh) in SubPartLayout(sub))
                    {
                        // For P_8x8 sub-partitions, the decoder uses "standard median" prediction
                        // (rawMbType sentinel 0 in PartitionMvPredictor.Predict).
                        EmitPartitionMvd(w, state, cand, partIdx: 0,
                            bx: qBx + spBx, by: qBy + spBy, bw: spBw, bh: spBh,
                            leftMb, topMb, topRightMb, topLeftMb,
                            cand.Partitions[pIdx],
                            forceStandardMedian: true);
                        pIdx++;
                    }
                }
                break;
        }
    }

    /// <summary>Enumerate sub-partition layouts within an 8x8 quadrant for a given sub_mb_type code.</summary>
    public static IEnumerable<(int Bx, int By, int Bw, int Bh)> SubPartLayout(int subMbType)
    {
        switch (subMbType)
        {
            case 0: // 8x8
                yield return (0, 0, 2, 2);
                break;
            case 1: // 8x4 (2 horizontal halves)
                yield return (0, 0, 2, 1);
                yield return (0, 1, 2, 1);
                break;
            case 2: // 4x8 (2 vertical halves)
                yield return (0, 0, 1, 2);
                yield return (1, 0, 1, 2);
                break;
            case 3: // 4x4
                yield return (0, 0, 1, 1);
                yield return (1, 0, 1, 1);
                yield return (0, 1, 1, 1);
                yield return (1, 1, 1, 1);
                break;
        }
    }

    private static void EmitPartitionMvd(
        BitWriter w, MacroblockEncoderState state, PartitionCandidate cand,
        int partIdx, int bx, int by, int bw, int bh,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb,
        Partition p, bool forceStandardMedian = false)
    {
        int rmt = forceStandardMedian ? 0 : cand.RawMbType;
        (int predX, int predY) = PartitionMvPredictor.Predict(
            state, rmt, partIdx, bx, by, bw, bh, curRefIdx: 0,
            leftMb, topMb, topRightMb, topLeftMb);
        int mvdX = p.MvX - predX;
        int mvdY = p.MvY - predY;
        ExpGolombWriter.WriteSe(w, mvdX);
        ExpGolombWriter.WriteSe(w, mvdY);
        // After writing this partition's MVD, fill the state's per-block MV arrays so the next
        // partition's predictor sees the updated MVs (mirrors decoder iteration order).
        FillBlockMvs(state, bx, by, bw, bh, p.MvX, p.MvY);
    }

    private static readonly int[] _spatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

    private static void FillBlockMvs(MacroblockEncoderState state, int bx0, int by0, int bw, int bh, int mvX, int mvY)
    {
        for (int by = by0; by < by0 + bh; by++)
            for (int bx = bx0; bx < bx0 + bw; bx++)
            {
                int idx = _spatialToRaster[by * 4 + bx];
                state.MvL0XBlock[idx] = mvX;
                state.MvL0YBlock[idx] = mvY;
            }
    }

}
