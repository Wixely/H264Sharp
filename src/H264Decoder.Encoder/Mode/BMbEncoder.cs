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
    /// <summary>Direction of a B-MB partition.</summary>
    internal enum Dir : byte { L0 = 0, L1 = 1, Bi = 2 }

    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>Holds the chosen B-inter mode plus its built bundle (prediction, residual,
    /// reconstruction) and the MVs used. <see cref="Bundle"/>.CbpLuma/CbpChroma reflect the actual
    /// chosen mode.</summary>
    internal sealed class BCandidate
    {
        public Dir Direction;
        public int MvL0X, MvL0Y;
        public int MvL1X, MvL1Y;
        public MacroblockEncoderInter.InterEncodeBundle Bundle = null!;
        public int TotalCost; // SAD + λ * mode-bits proxy
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

    /// <summary>Emit B-slice macroblock_layer() syntax for a chosen 16x16 inter candidate.</summary>
    internal static void EmitBMb16x16(
        BitWriter w,
        BCandidate cand,
        int qpY,
        int predL0X, int predL0Y, int predL1X, int predL1Y,
        MacroblockEncoderState state,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int mbType = cand.Direction switch
        {
            Dir.L0 => 1,
            Dir.L1 => 2,
            Dir.Bi => 3,
            _ => throw new InvalidOperationException()
        };
        ExpGolombWriter.WriteUe(w, (uint)mbType);
        // No ref_idx fields (num_ref_idx_active_lN_minus1 = 0).
        // mvd_l0 if L0 or Bi, then mvd_l1 if L1 or Bi.
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

        // ---- Update state for neighbor lookups ----
        state.IsBInter = true;
        state.IsInter = true; // shared "this is an inter MB" flag
        state.IsInterP16x16 = false;
        state.IsIntra16x16 = false;
        state.IsIntra4x4 = false;
        state.BPredDir = (byte)cand.Direction;
        state.RawMbType = mbType;
        state.CbpLuma = cand.Bundle.CbpLuma;
        state.CbpChroma = cand.Bundle.CbpChroma;
        state.QpY = qpY;

        bool useL0 = cand.Direction != Dir.L1;
        bool useL1 = cand.Direction != Dir.L0;
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
