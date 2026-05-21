using H264Decoder.Picture;
using H264Decoder.Syntax;

namespace H264Decoder.Loop;

/// <summary>
/// H.264 in-loop deblocking filter (spec §8.7). I-slice paths only:
/// boundary strength is 4 at macroblock edges and 3 at internal 4x4 edges,
/// since every block is intra. Filter formulas follow §8.7.2.2 (bS in {1,2,3})
/// and §8.7.2.3 (bS == 4).
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

            int qPLeft = mbX > 0 ? mbs[addr - 1]!.QpY : mb.QpY;
            int qPTop = mbY > 0 ? mbs[addr - mbsPerRow]!.QpY : mb.QpY;
            bool leftIsPcm = mbX > 0 && mbs[addr - 1]!.IsPcm;
            bool topIsPcm  = mbY > 0 && mbs[addr - mbsPerRow]!.IsPcm;

            // Luma vertical edges (left edges of MB cols 0, 4, 8, 12).
            // x==0 is the MB boundary; only filter if there is a left neighbor.
            for (int x = 0; x < 16; x += 4)
            {
                bool isMbEdge = x == 0;
                if (isMbEdge && (mbX == 0 || !filterMbEdges || leftIsPcm)) continue;
                int qPp = isMbEdge ? qPLeft : mb.QpY;
                int qPq = mb.QpY;
                int qPavg = (qPp + qPq + 1) >> 1;
                int bS = isMbEdge ? 4 : 3;
                FilterLumaVertical(pic, mbX * 16 + x, mbY * 16, qPavg, bS,
                    sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2);
            }
            // Luma horizontal edges (top edges of MB rows 0, 4, 8, 12).
            for (int y = 0; y < 16; y += 4)
            {
                bool isMbEdge = y == 0;
                if (isMbEdge && (mbY == 0 || !filterMbEdges || topIsPcm)) continue;
                int qPp = isMbEdge ? qPTop : mb.QpY;
                int qPq = mb.QpY;
                int qPavg = (qPp + qPq + 1) >> 1;
                int bS = isMbEdge ? 4 : 3;
                FilterLumaHorizontal(pic, mbX * 16, mbY * 16 + y, qPavg, bS,
                    sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2);
            }
            // Chroma: 2 vertical edges per component (left edge + middle), 2 horizontal.
            int qPc = MacroblockReconstructor.ChromaQp(mb.QpY, chromaQpIndexOffset);
            int qPcLeft = MacroblockReconstructor.ChromaQp(qPLeft, chromaQpIndexOffset);
            int qPcTop = MacroblockReconstructor.ChromaQp(qPTop, chromaQpIndexOffset);

            for (int comp = 0; comp < 2; comp++)
            {
                byte[] plane = comp == 0 ? pic.U : pic.V;
                int stride = pic.ChromaWidth;
                for (int x = 0; x < 8; x += 4)
                {
                    bool isMbEdge = x == 0;
                    if (isMbEdge && (mbX == 0 || !filterMbEdges || leftIsPcm)) continue;
                    int qPavg = ((isMbEdge ? qPcLeft : qPc) + qPc + 1) >> 1;
                    int bS = isMbEdge ? 4 : 3;
                    FilterChromaVertical(plane, stride, mbX * 8 + x, mbY * 8, qPavg, bS,
                        sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2);
                }
                for (int y = 0; y < 8; y += 4)
                {
                    bool isMbEdge = y == 0;
                    if (isMbEdge && (mbY == 0 || !filterMbEdges || topIsPcm)) continue;
                    int qPavg = ((isMbEdge ? qPcTop : qPc) + qPc + 1) >> 1;
                    int bS = isMbEdge ? 4 : 3;
                    FilterChromaHorizontal(plane, stride, mbX * 8, mbY * 8 + y, qPavg, bS,
                        sliceAlphaC0OffsetDiv2, sliceBetaOffsetDiv2);
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

    private static void FilterLumaVertical(DecodedPicture pic, int edgeX, int mbY0, int qP, int bS, int aOff, int bOff)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        // 16 horizontal 4-sample edges stacked vertically (one row per edge).
        for (int row = 0; row < 16; row++)
        {
            int y = mbY0 + row;
            int b = y * pic.Width + edgeX;
            FilterEdge1D(pic.Y, p3: b - 4, p2: b - 3, p1: b - 2, p0: b - 1,
                                q0: b, q1: b + 1, q2: b + 2, q3: b + 3,
                                alpha, beta, indexA, bS, isChroma: false);
        }
    }

    private static void FilterLumaHorizontal(DecodedPicture pic, int mbX0, int edgeY, int qP, int bS, int aOff, int bOff)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        int s = pic.Width;
        for (int col = 0; col < 16; col++)
        {
            int x = mbX0 + col;
            int b = edgeY * s + x;
            FilterEdge1D(pic.Y, p3: b - 4 * s, p2: b - 3 * s, p1: b - 2 * s, p0: b - s,
                                q0: b, q1: b + s, q2: b + 2 * s, q3: b + 3 * s,
                                alpha, beta, indexA, bS, isChroma: false);
        }
    }

    private static void FilterChromaVertical(byte[] plane, int stride, int edgeX, int mbY0, int qP, int bS, int aOff, int bOff)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        for (int row = 0; row < 8; row++)
        {
            int y = mbY0 + row;
            int b = y * stride + edgeX;
            FilterEdge1D(plane, p3: b - 4, p2: b - 3, p1: b - 2, p0: b - 1,
                                q0: b, q1: b + 1, q2: b + 2, q3: b + 3,
                                alpha, beta, indexA, bS, isChroma: true);
        }
    }

    private static void FilterChromaHorizontal(byte[] plane, int stride, int mbX0, int edgeY, int qP, int bS, int aOff, int bOff)
    {
        (int alpha, int beta, int indexA) = GetAlphaBeta(qP, aOff, bOff);
        if (alpha == 0 && beta == 0) return;
        for (int col = 0; col < 8; col++)
        {
            int x = mbX0 + col;
            int b = edgeY * stride + x;
            FilterEdge1D(plane, p3: b - 4 * stride, p2: b - 3 * stride, p1: b - 2 * stride, p0: b - stride,
                                q0: b, q1: b + stride, q2: b + 2 * stride, q3: b + 3 * stride,
                                alpha, beta, indexA, bS, isChroma: true);
        }
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
