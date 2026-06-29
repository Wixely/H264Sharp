namespace H264Sharp.Encoder.Mode;

/// <summary>MV-predictor for inter partitions on the encoder side. Mirrors the decoder's
/// spec §8.4.1.3.1 PredictMvForPartition formulas but reads per-4x4-block MV/ref-idx from
/// <see cref="MacroblockEncoderState"/>. Must match the decoder byte-for-byte so that
/// encoded mvd = actual_mv - predicted_mv decodes back to the same actual_mv.</summary>
internal static class PartitionMvPredictor
{
    /// <summary>Per-partition MV prediction. <paramref name="rawMbType"/> is the encoder's chosen
    /// raw mb_type (0=16x16, 1=16x8, 2=8x16, 3=P_8x8, or 0 as a "standard median" sentinel for
    /// sub-MB partitions inside P_8x8). <paramref name="bx"/>,<paramref name="by"/> are the
    /// partition's top-left 4x4-block-grid coordinates within the MB; <paramref name="bw"/>,
    /// <paramref name="bh"/> are partition width/height in 4x4 blocks.</summary>
    public static (int X, int Y) Predict(
        MacroblockEncoderState cur,
        int rawMbType, int partIdx,
        int bx, int by, int bw, int bh, int curRefIdx,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        var A = GetNeighbor(bx - 1, by,         cur, leftMb, topMb, topRightMb, topLeftMb);
        var B = GetNeighbor(bx,     by - 1,     cur, leftMb, topMb, topRightMb, topLeftMb);
        // C is at the position above-right of the partition's top-right 4x4 block.
        int cBx = bx + bw;
        int cBy = by - 1;
        var C = GetNeighbor(cBx, cBy, cur, leftMb, topMb, topRightMb, topLeftMb);
        if (!C.Avail)
        {
            C = GetNeighbor(bx - 1, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        }

        // Partition-specific overrides (only apply for raw mb_type 1 / 2 — the 16x8 / 8x16 cases).
        if (rawMbType == 1) // 16x8
        {
            if (partIdx == 0 && B.Avail && B.RefIdx == curRefIdx) return (B.MvX, B.MvY);
            if (partIdx == 1 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
        }
        else if (rawMbType == 2) // 8x16
        {
            if (partIdx == 0 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
            if (partIdx == 1 && C.Avail && C.RefIdx == curRefIdx) return (C.MvX, C.MvY);
        }

        // Spec rule: if B and C unavailable and A available, A substitutes for B and C → return A.
        if (!B.Avail && !C.Avail && A.Avail)
        {
            return (A.MvX, A.MvY);
        }

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

    private readonly struct Neighbor
    {
        public readonly bool Avail;
        public readonly int MvX, MvY, RefIdx;
        public Neighbor(bool a, int x, int y, int r) { Avail = a; MvX = x; MvY = y; RefIdx = r; }
    }

    private static Neighbor GetNeighbor(
        int bx, int by, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        MacroblockEncoderState? mb;
        int nbBx, nbBy;
        if (bx >= 0 && by >= 0 && bx <= 3 && by <= 3) { mb = cur; nbBx = bx; nbBy = by; }
        else if (bx < 0 && by >= 0 && by <= 3) { mb = leftMb; nbBx = 3; nbBy = by; }
        else if (by < 0 && bx >= 0 && bx <= 3) { mb = topMb; nbBx = bx; nbBy = 3; }
        else if (bx < 0 && by < 0) { mb = topLeftMb; nbBx = 3; nbBy = 3; }
        else if (bx > 3 && by < 0) { mb = topRightMb; nbBx = 0; nbBy = 3; }
        else { mb = null; nbBx = 0; nbBy = 0; }

        if (mb is null) return new Neighbor(false, 0, 0, -1);
        // A neighbor MB that is intra contributes refIdx=-1 (not equal to current refIdx 0).
        if (!mb.IsInter)
        {
            return new Neighbor(true, 0, 0, -1);
        }
        int idx = SpatialToRaster(nbBx, nbBy);
        int quadrant = (nbBx >> 1) + (nbBy >> 1) * 2;
        return new Neighbor(true, mb.MvL0XBlock[idx], mb.MvL0YBlock[idx], mb.RefIdxL08x8[quadrant]);
    }

    private static readonly int[] _spatialToRaster = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };
    private static int SpatialToRaster(int bx, int by) => _spatialToRaster[by * 4 + bx];

    private static int Median3(int a, int b, int c)
    {
        int min = Math.Min(a, Math.Min(b, c));
        int max = Math.Max(a, Math.Max(b, c));
        return a + b + c - min - max;
    }
}
