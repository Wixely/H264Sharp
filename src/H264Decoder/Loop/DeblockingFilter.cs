using H264Decoder.Picture;
using H264Decoder.Syntax;

namespace H264Decoder.Loop;

/// <summary>
/// H.264 in-loop deblocking filter (spec §8.7). Supports I/P/B slices: per
/// 4x4-block-edge boundary strength derivation per §8.7.2.1 (intra/inter mix,
/// residual coefs, MV/refIdx diff). Filter formulas follow §8.7.2.2 (bS in
/// {1,2,3}) and §8.7.2.3 (bS == 4).
/// </summary>
public static class DeblockingFilter
{
    // Table 8-16: alpha (indexed by qP+offset, clamped to [0,51])
    private static readonly byte[] _alphaTable =
    [
          0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
          4,  4,  5,  6,  7,  8,  9, 10, 12, 13, 15, 17, 20, 22, 25, 28,
         32, 36, 40, 45, 50, 56, 63, 71, 80, 90,101,113,127,144,162,182,
        203,226,255,255,
    ];
    private static readonly byte[] _betaTable =
    [
          0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
          2,  2,  2,  3,  3,  3,  3,  4,  4,  4,  6,  6,  7,  7,  8,  8,
          9,  9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
         17, 17, 18, 18,
    ];
    // Table 8-17: tc0 indexed by [qP+offset clamped to [0,51]] [bS-1]
    private static readonly sbyte[,] _tc0Table =
    {
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 },
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 },
        { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 0 }, { 0, 0, 1 },
        { 0, 0, 1 }, { 0, 0, 1 }, { 0, 0, 1 }, { 0, 1, 1 }, { 0, 1, 1 }, { 1, 1, 1 },
        { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 2 }, { 1, 1, 2 }, { 1, 1, 2 },
        { 1, 1, 2 }, { 1, 2, 3 }, { 1, 2, 3 }, { 2, 2, 3 }, { 2, 2, 4 }, { 2, 3, 4 },
        { 2, 3, 4 }, { 3, 3, 5 }, { 3, 4, 6 }, { 3, 4, 6 }, { 4, 5, 7 }, { 4, 5, 8 },
        { 4, 6, 9 }, { 5, 7,10 }, { 6, 8,11 }, { 6, 8,13 }, { 7,10,14 }, { 8,11,16 },
        { 9,12,18 }, {10,13,20 }, {11,15,23 }, {13,17,25 },
    };

    public static void Apply(
        DecodedPicture pic,
        Macroblock[] mbs,
        int mbsPerRow,
        int chromaQpIndexOffset,
        int sliceAlphaC0OffsetDiv2,
        int sliceBetaOffsetDiv2,
        bool filterMbEdges)
    {
        int totalMbs = mbs.Length;
        for (int addr = 0; addr < totalMbs; addr++)
        {
            var mb = mbs[addr];
            if (mb is null) continue;
            // I_PCM MBs are not filtered (spec §8.7.5: samples bypass the in-loop filter).
            if (mb.IsPcm) continue;
            int mbX = addr % mbsPerRow;
            int mbY = addr / mbsPerRow;

            Macroblock? leftMb = mbX > 0 ? mbs[addr - 1] : null;
            Macroblock? topMb = mbY > 0 ? mbs[addr - mbsPerRow] : null;
            int qPLeft = leftMb is not null ? leftMb.QpY : mb.QpY;
            int qPTop = topMb is not null ? topMb.QpY : mb.QpY;
            bool leftIsPcm = leftMb is not null && leftMb.IsPcm;
            bool topIsPcm  = topMb is not null && topMb.IsPcm;

            // Luma vertical edges (left edges of MB cols 0, 4, 8, 12).
            // x==0 is the MB boundary; only filter if there is a left neighbor.
            // When transform_size_8x8_flag is set, the internal 4x4 edges at x=4 and x=12
            // are NOT filtered (only the 8x8-block boundary at x=8) per spec §8.7.
            for (int x = 0; x < 16; x += 4)
            {
                bool isMbEdge = x == 0;
                if (isMbEdge && (mbX == 0 || !filterMbEdges || leftIsPcm)) continue;
                if (!isMbEdge && mb.TransformSize8x8 && (x == 4 || x == 12)) continue;
                int qPp = isMbEdge ? qPLeft : mb.QpY;
                int qPq = mb.QpY;
                int qPavg = (qPp + qPq + 1) >> 1;
                FilterLumaVerticalEdge(pic, mb, isMbEdge ? leftMb : mb, mb, x, mbY * 16, qPavg, isMbEdge,
                    sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2, mbX * 16);
            }
            // Luma horizontal edges (top edges of MB rows 0, 4, 8, 12).
            for (int y = 0; y < 16; y += 4)
            {
                bool isMbEdge = y == 0;
                if (isMbEdge && (mbY == 0 || !filterMbEdges || topIsPcm)) continue;
                if (!isMbEdge && mb.TransformSize8x8 && (y == 4 || y == 12)) continue;
                int qPp = isMbEdge ? qPTop : mb.QpY;
                int qPq = mb.QpY;
                int qPavg = (qPp + qPq + 1) >> 1;
                FilterLumaHorizontalEdge(pic, mb, isMbEdge ? topMb : mb, mb, y, mbX * 16, qPavg, isMbEdge,
                    sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2, mbY * 16);
            }
            // Chroma: 2 vertical edges per component (left edge + middle), 2 horizontal.
            int qPc = MacroblockReconstructor.ChromaQp(mb.QpY, chromaQpIndexOffset);
            int qPcLeft = MacroblockReconstructor.ChromaQp(qPLeft, chromaQpIndexOffset);
            int qPcTop = MacroblockReconstructor.ChromaQp(qPTop, chromaQpIndexOffset);

            for (int comp = 0; comp < 2; comp++)
            {
                byte[] plane = comp == 0 ? pic.U : pic.V;
                int stride = pic.ChromaBufferWidth;
                for (int x = 0; x < 8; x += 4)
                {
                    bool isMbEdge = x == 0;
                    if (isMbEdge && (mbX == 0 || !filterMbEdges || leftIsPcm)) continue;
                    int qPavg = ((isMbEdge ? qPcLeft : qPc) + qPc + 1) >> 1;
                    FilterChromaVerticalEdge(plane, stride, mb, isMbEdge ? leftMb : mb, mb, x, mbY * 8, qPavg, isMbEdge,
                        sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2, mbX * 8);
                }
                for (int y = 0; y < 8; y += 4)
                {
                    bool isMbEdge = y == 0;
                    if (isMbEdge && (mbY == 0 || !filterMbEdges || topIsPcm)) continue;
                    int qPavg = ((isMbEdge ? qPcTop : qPc) + qPc + 1) >> 1;
                    FilterChromaHorizontalEdge(plane, stride, mb, isMbEdge ? topMb : mb, mb, y, mbX * 8, qPavg, isMbEdge,
                        sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2, mbY * 8);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Per-edge filter dispatchers
    // ------------------------------------------------------------------

    private static (int alpha, int beta, int indexA) GetAlphaBeta(int qP, int alphaOffset, int betaOffset)
    {
        int idxA = Clip(qP + alphaOffset, 0, 51);
        int idxB = Clip(qP + betaOffset, 0, 51);
        return (_alphaTable[idxA], _betaTable[idxB], idxA);
    }

    // Luma vertical edge at MB-relative x within mbQ; pSide is the MB providing the p-side blocks
    // (left neighbor when x==0; else mbQ itself).
    private static void FilterLumaVerticalEdge(DecodedPicture pic, Macroblock mbQ, Macroblock? pSide, Macroblock qSide,
        int x, int mbY0, int qP, bool isMbEdge, int aOff, int bOff, int mbX0Pixels)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        // Four 4-row segments along this vertical edge; each segment maps to one pair of
        // 4x4 blocks (p on the left, q on the right). bS is computed once per segment.
        for (int seg = 0; seg < 4; seg++)
        {
            int qBlkX = x / 4;            // 0..3 within mbQ
            int qBlkY = seg;              // segment index = row in 4x4 grid
            int pBlkX = isMbEdge ? 3 : (qBlkX - 1);
            int pBlkY = qBlkY;
            int bS = DeriveBsLuma(pSide, qSide, pBlkX, pBlkY, qBlkX, qBlkY, isMbEdge, vertical: true);
            if (bS == 0) continue;
            for (int row = 0; row < 4; row++)
            {
                int y = mbY0 + seg * 4 + row;
                int b = y * pic.BufferWidth + mbX0Pixels + x;
                FilterEdge1D(pic.Y, p3: b - 4, p2: b - 3, p1: b - 2, p0: b - 1,
                                    q0: b, q1: b + 1, q2: b + 2, q3: b + 3,
                                    alpha, beta, indexA, bS, isChroma: false);
            }
        }
    }

    private static void FilterLumaHorizontalEdge(DecodedPicture pic, Macroblock mbQ, Macroblock? pSide, Macroblock qSide,
        int y, int mbX0, int qP, bool isMbEdge, int aOff, int bOff, int mbY0Pixels)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        int s = pic.BufferWidth;
        for (int seg = 0; seg < 4; seg++)
        {
            int qBlkY = y / 4;
            int qBlkX = seg;
            int pBlkY = isMbEdge ? 3 : (qBlkY - 1);
            int pBlkX = qBlkX;
            int bS = DeriveBsLuma(pSide, qSide, pBlkX, pBlkY, qBlkX, qBlkY, isMbEdge, vertical: false);
            if (bS == 0) continue;
            for (int col = 0; col < 4; col++)
            {
                int xx = mbX0 + seg * 4 + col;
                int b = (mbY0Pixels + y) * s + xx;
                FilterEdge1D(pic.Y, p3: b - 4 * s, p2: b - 3 * s, p1: b - 2 * s, p0: b - s,
                                    q0: b, q1: b + s, q2: b + 2 * s, q3: b + 3 * s,
                                    alpha, beta, indexA, bS, isChroma: false);
            }
        }
    }

    // Chroma edges: 8x8 plane, 2x2 grid of 4x4 chroma blocks. bS derives from the *corresponding*
    // luma block pair: chroma edge at (xC, yC) corresponds to luma edge at (2xC, 2yC). Per spec
    // §8.7.2.2 the bS used at chroma row 2k is the bS of the luma edge at luma row 2k — so within
    // a single chroma 4-row segment the top two and bottom two rows draw bS from two *different*
    // luma 4x4 block rows. With sub-MB partitions (e.g. B_8x8) those can disagree, so iterate per
    // 2-row "half-segment" rather than once per full 4-row chroma segment.
    private static void FilterChromaVerticalEdge(byte[] plane, int stride, Macroblock mbQ, Macroblock? pSide, Macroblock qSide,
        int xC, int mbY0C, int qP, bool isMbEdge, int aOff, int bOff, int mbX0C)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        int qBlkX = (xC / 4) * 2;          // luma x-block index (0 or 2)
        int pBlkX = isMbEdge ? 2 : (qBlkX - 2);
        // 4 half-segments of 2 chroma rows each, mapping to the 4 luma 4x4 block rows.
        for (int halfSeg = 0; halfSeg < 4; halfSeg++)
        {
            int qBlkY = halfSeg;
            int pBlkY = qBlkY;
            int bS = DeriveBsLuma(pSide, qSide,
                                  isMbEdge ? 3 : pBlkX + 1, pBlkY,
                                  qBlkX, qBlkY, isMbEdge, vertical: true);
            if (bS == 0) continue;
            for (int row = 0; row < 2; row++)
            {
                int y = mbY0C + halfSeg * 2 + row;
                int b = y * stride + mbX0C + xC;
                FilterEdge1D(plane, p3: b - 4, p2: b - 3, p1: b - 2, p0: b - 1,
                                    q0: b, q1: b + 1, q2: b + 2, q3: b + 3,
                                    alpha, beta, indexA, bS, isChroma: true);
            }
        }
    }

    private static void FilterChromaHorizontalEdge(byte[] plane, int stride, Macroblock mbQ, Macroblock? pSide, Macroblock qSide,
        int yC, int mbX0C, int qP, bool isMbEdge, int aOff, int bOff, int mbY0C)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        int qBlkY = (yC / 4) * 2;
        int pBlkY = isMbEdge ? 2 : (qBlkY - 2);
        for (int halfSeg = 0; halfSeg < 4; halfSeg++)
        {
            int qBlkX = halfSeg;
            int pBlkX = qBlkX;
            int bS = DeriveBsLuma(pSide, qSide,
                                  pBlkX, isMbEdge ? 3 : pBlkY + 1,
                                  qBlkX, qBlkY, isMbEdge, vertical: false);
            if (bS == 0) continue;
            for (int col = 0; col < 2; col++)
            {
                int x = mbX0C + halfSeg * 2 + col;
                int b = (mbY0C + yC) * stride + x;
                FilterEdge1D(plane, p3: b - 4 * stride, p2: b - 3 * stride, p1: b - 2 * stride, p0: b - stride,
                                    q0: b, q1: b + stride, q2: b + 2 * stride, q3: b + 3 * stride,
                                    alpha, beta, indexA, bS, isChroma: true);
            }
        }
    }

    // ------------------------------------------------------------------
    // Boundary strength derivation (spec §8.7.2.1)
    // ------------------------------------------------------------------

    private static bool IsIntra(Macroblock mb)
    {
        var pm = mb.Type.PredMode;
        return pm == MbPartPredMode.Intra4x4 || pm == MbPartPredMode.Intra16x16 || pm == MbPartPredMode.IPcm;
    }

    // 4x4 block (bx,by) → z-scan index used by NonZeroCountLuma, MvL0XBlock, etc.
    private static int BlockIdx(int bx, int by) => MacroblockParser.SpatialToRaster(bx, by);

    /// <summary>Returns true if the 4x4 luma block at (bx,by) has non-zero residual coefficients.
    /// For an 8x8-transform MB the per-8x8 nzc is shared across the 4 sub 4x4 indices.</summary>
    private static bool HasNonZeroResidual(Macroblock mb, int bx, int by)
    {
        if (mb.IsPcm) return true;
        if (mb.TransformSize8x8)
        {
            int blk8 = (by >> 1) * 2 + (bx >> 1);
            return mb.NonZeroCountLuma8x8[blk8] > 0;
        }
        // Intra16x16: AC blocks track nzc; the shared DC block contributes if LumaDcCbf and DC nonzero.
        // Using NonZeroCountLuma directly handles both Intra16x16 AC and Intra4x4 / inter 4x4.
        return mb.NonZeroCountLuma[BlockIdx(bx, by)] > 0;
    }

    private static int DeriveBsLuma(Macroblock? pMb, Macroblock qMb, int pBlkX, int pBlkY, int qBlkX, int qBlkY, bool isMbEdge, bool vertical)
    {
        // I-slice safety: if p is null treat as q (shouldn't happen since edges with no p are skipped).
        if (pMb is null) return 0;
        bool pIntra = IsIntra(pMb);
        bool qIntra = IsIntra(qMb);
        if (isMbEdge && (pIntra || qIntra)) return 4;
        if (pIntra || qIntra) return 3;
        // Non-zero residual on either side → bS=2.
        if (HasNonZeroResidual(pMb, pBlkX, pBlkY) || HasNonZeroResidual(qMb, qBlkX, qBlkY)) return 2;
        // Inter-inter: compare MV / refIdx per direction.
        if (InterDiffers(pMb, qMb, pBlkX, pBlkY, qBlkX, qBlkY)) return 1;
        return 0;
    }

    /// <summary>Spec §8.7.2.1 inter-inter comparison: different ref pics OR |MV diff|>=4 in
    /// any used direction. We approximate "different ref pic" by comparing refIdx within the
    /// same list — valid since both blocks are in the same slice (same L0/L1 ordering).
    /// For B-MBs both L0 and L1 are checked; for P-slice MBs only L0 is active.</summary>
    private static bool InterDiffers(Macroblock pMb, Macroblock qMb, int pBlkX, int pBlkY, int qBlkX, int qBlkY)
    {
        int pIdx = BlockIdx(pBlkX, pBlkY);
        int qIdx = BlockIdx(qBlkX, qBlkY);
        int pQuad = (pBlkY >> 1) * 2 + (pBlkX >> 1);
        int qQuad = (qBlkY >> 1) * 2 + (qBlkX >> 1);
        // Determine L0/L1 active per side. P-slice inter MBs have predFlagL0=1, predFlagL1=0
        // (the parser doesn't set PredFlagL0Block for P-slice MBs).
        bool pL0 = pMb.IsBInter ? pMb.PredFlagL0Block[pIdx] != 0 : true;
        bool pL1 = pMb.IsBInter ? pMb.PredFlagL1Block[pIdx] != 0 : false;
        bool qL0 = qMb.IsBInter ? qMb.PredFlagL0Block[qIdx] != 0 : true;
        bool qL1 = qMb.IsBInter ? qMb.PredFlagL1Block[qIdx] != 0 : false;
        // If the sets of active lists differ → bS=1 (e.g. one side uses L0 only, other uses L1 or Bi).
        if (pL0 != qL0 || pL1 != qL1) return true;
        // Compare L0.
        if (pL0)
        {
            int pRef = pMb.RefIdxL08x8[pQuad];
            int qRef = qMb.RefIdxL08x8[qQuad];
            if (pRef != qRef) return true;
            if (Math.Abs(pMb.MvL0XBlock[pIdx] - qMb.MvL0XBlock[qIdx]) >= 4) return true;
            if (Math.Abs(pMb.MvL0YBlock[pIdx] - qMb.MvL0YBlock[qIdx]) >= 4) return true;
        }
        // Compare L1.
        if (pL1)
        {
            int pRef = pMb.RefIdxL18x8[pQuad];
            int qRef = qMb.RefIdxL18x8[qQuad];
            if (pRef != qRef) return true;
            if (Math.Abs(pMb.MvL1XBlock[pIdx] - qMb.MvL1XBlock[qIdx]) >= 4) return true;
            if (Math.Abs(pMb.MvL1YBlock[pIdx] - qMb.MvL1YBlock[qIdx]) >= 4) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 1-D filter on 8 samples around an edge.
    // ------------------------------------------------------------------
    private static void FilterEdge1D(
        byte[] buf,
        int p3, int p2, int p1, int p0,
        int q0, int q1, int q2, int q3,
        int alpha, int beta, int indexA, int bS, bool isChroma)
    {
        int P0 = buf[p0], P1 = buf[p1], P2 = buf[p2], P3 = buf[p3];
        int Q0 = buf[q0], Q1 = buf[q1], Q2 = buf[q2], Q3 = buf[q3];

        if (Math.Abs(P0 - Q0) >= alpha) return;
        if (Math.Abs(P1 - P0) >= beta) return;
        if (Math.Abs(Q1 - Q0) >= beta) return;

        bool aP = Math.Abs(P2 - P0) < beta;
        bool aQ = Math.Abs(Q2 - Q0) < beta;

        if (bS < 4)
        {
            int tc0 = _tc0Table[indexA, bS - 1];
            int tc = tc0 + (isChroma ? 1 : ((aP ? 1 : 0) + (aQ ? 1 : 0)));

            int delta = Clip(((Q0 - P0) * 4 + (P1 - Q1) + 4) >> 3, -tc, tc);
            buf[p0] = ClipByte(P0 + delta);
            buf[q0] = ClipByte(Q0 - delta);

            if (!isChroma)
            {
                if (aP)
                {
                    int d = Clip((P2 + ((P0 + Q0 + 1) >> 1) - (P1 << 1)) >> 1, -tc0, tc0);
                    buf[p1] = ClipByte(P1 + d);
                }
                if (aQ)
                {
                    int d = Clip((Q2 + ((P0 + Q0 + 1) >> 1) - (Q1 << 1)) >> 1, -tc0, tc0);
                    buf[q1] = ClipByte(Q1 + d);
                }
            }
        }
        else
        {
            // bS == 4
            bool strongCondition = Math.Abs(P0 - Q0) < ((alpha >> 2) + 2);
            bool useStrongP = !isChroma && aP && strongCondition;
            bool useStrongQ = !isChroma && aQ && strongCondition;

            if (useStrongP)
            {
                buf[p0] = ClipByte((P2 + 2 * P1 + 2 * P0 + 2 * Q0 + Q1 + 4) >> 3);
                buf[p1] = ClipByte((P2 + P1 + P0 + Q0 + 2) >> 2);
                buf[p2] = ClipByte((2 * P3 + 3 * P2 + P1 + P0 + Q0 + 4) >> 3);
            }
            else
            {
                buf[p0] = ClipByte((2 * P1 + P0 + Q1 + 2) >> 2);
            }

            if (useStrongQ)
            {
                buf[q0] = ClipByte((Q2 + 2 * Q1 + 2 * Q0 + 2 * P0 + P1 + 4) >> 3);
                buf[q1] = ClipByte((Q2 + Q1 + Q0 + P0 + 2) >> 2);
                buf[q2] = ClipByte((2 * Q3 + 3 * Q2 + Q1 + Q0 + P0 + 4) >> 3);
            }
            else
            {
                buf[q0] = ClipByte((2 * Q1 + Q0 + P1 + 2) >> 2);
            }
        }
    }

    private static int Clip(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    private static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
