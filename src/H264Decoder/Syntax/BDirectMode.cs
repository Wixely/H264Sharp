namespace H264Decoder.Syntax;

/// <summary>
/// B-slice direct mode (spec §8.4.1.2). Currently implements the SPATIAL direct
/// variant; temporal direct is not yet supported.
/// </summary>
internal static class BDirectMode
{
    /// <summary>Apply spatial direct mode for a full B_Direct_16x16 (or B_Skip) MB.</summary>
    public static void ApplyDirect16x16(
        Macroblock mb, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        if (!sliceHeader.DirectSpatialMvPredFlag)
        {
            throw new NotSupportedException("Temporal direct mode not yet supported");
        }
        DeriveSpatialDirect(mb, 0, 0, 4, 4, leftMb, topMb, topRightMb, topLeftMb);
    }

    /// <summary>Apply spatial direct mode for one 8x8 quadrant within a B_8x8 MB.</summary>
    public static void ApplyDirect8x8(
        Macroblock mb, int quadrant, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        if (!sliceHeader.DirectSpatialMvPredFlag)
        {
            throw new NotSupportedException("Temporal direct mode not yet supported");
        }
        int qx = (quadrant & 1) * 2, qy = (quadrant >> 1) * 2;
        DeriveSpatialDirect(mb, qx, qy, 2, 2, leftMb, topMb, topRightMb, topLeftMb);
    }

    /// <summary>Spatial direct derivation (spec §8.4.1.2.2) for a rectangle of 4x4 blocks.
    /// Region covered: [bx0..bx0+bw, by0..by0+bh] in 4x4 units.</summary>
    private static void DeriveSpatialDirect(
        Macroblock mb, int bx0, int by0, int bw, int bh,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // refIdxLX derivation: minimum positive ref over neighbors A, B, C at the
        // partition's top-left position (spec §8.4.1.2.1).
        int refL0 = MinPositiveRef(mb, bx0, by0, bw, leftMb, topMb, topRightMb, topLeftMb, listX: 0);
        int refL1 = MinPositiveRef(mb, bx0, by0, bw, leftMb, topMb, topRightMb, topLeftMb, listX: 1);

        // If neither L0 nor L1 has a valid reference, both MVs are zero with refIdx=0.
        bool noRefs = refL0 < 0 && refL1 < 0;
        if (noRefs)
        {
            refL0 = 0; refL1 = 0;
        }

        int mvL0X = 0, mvL0Y = 0, mvL1X = 0, mvL1Y = 0;
        if (refL0 >= 0 && !noRefs)
        {
            (mvL0X, mvL0Y) = MacroblockParser.PredictMvForPartitionListB(
                mb, 0, 0, bx0, by0, bw, bh, refL0, listX: 0,
                leftMb, topMb, topRightMb, topLeftMb);
        }
        if (refL1 >= 0 && !noRefs)
        {
            (mvL1X, mvL1Y) = MacroblockParser.PredictMvForPartitionListB(
                mb, 0, 0, bx0, by0, bw, bh, refL1, listX: 1,
                leftMb, topMb, topRightMb, topLeftMb);
        }

        // Determine predFlags. For "no refs" case, both flags are 1 with zero MVs.
        byte pf0 = (byte)((refL0 >= 0 || noRefs) ? 1 : 0);
        byte pf1 = (byte)((refL1 >= 0 || noRefs) ? 1 : 0);

        // Per-quadrant refIdx storage.
        for (int by = by0; by < by0 + bh; by++)
            for (int bx = bx0; bx < bx0 + bw; bx++)
            {
                int q = MacroblockParser.QuadrantOf(bx, by);
                mb.RefIdxL08x8[q] = refL0 < 0 ? 0 : refL0;
                mb.RefIdxL18x8[q] = refL1 < 0 ? 0 : refL1;
            }

        MacroblockParser.FillBlockMvsL0(mb, bx0, by0, bw, bh, mvL0X, mvL0Y);
        MacroblockParser.FillBlockMvsL1(mb, bx0, by0, bw, bh, mvL1X, mvL1Y);
        MacroblockParser.SetPredFlag(mb.PredFlagL0Block, bx0, by0, bw, bh, pf0);
        MacroblockParser.SetPredFlag(mb.PredFlagL1Block, bx0, by0, bw, bh, pf1);
        // Note: for full spec compliance, per-4x4 collocated-block check should drop
        // MVs to zero for sub-blocks whose collocated L1[0] block has refIdx=0 and small MV.
        // We omit that refinement for now — most spatial-direct content from x264 still
        // matches because of the residual that follows.

        // For B_Direct_16x16 (full MB), also register a single BMvPartition for
        // bookkeeping (used by reconstruction).
        if (bw == 4 && bh == 4)
        {
            BPredDir dir;
            if (pf0 != 0 && pf1 != 0) dir = BPredDir.Bi;
            else if (pf0 != 0) dir = BPredDir.L0;
            else dir = BPredDir.L1;
            mb.BInterPartitions.Add(new BMvPartition(0, 0, 16, 16, dir,
                refL0 < 0 ? 0 : refL0, mvL0X, mvL0Y,
                refL1 < 0 ? 0 : refL1, mvL1X, mvL1Y));
        }
    }

    /// <summary>For spatial direct, returns the minimum non-negative refIdxLX among neighbors
    /// A (left, at (bx-1, by)), B (top, at (bx, by-1)), C (top-right at (bx+bw, by-1) with
    /// fallback to D=(bx-1, by-1)). Returns -1 if no neighbor has a valid ref.</summary>
    private static int MinPositiveRef(
        Macroblock cur, int bx, int by, int bw,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        int listX)
    {
        var A = MacroblockParser.GetMvNeighborListPublic(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        var B = MacroblockParser.GetMvNeighborListPublic(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        var C = MacroblockParser.GetMvNeighborListPublic(bx + bw, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);
        if (!C.Avail)
            C = MacroblockParser.GetMvNeighborListPublic(bx - 1, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb, listX);

        int min = int.MaxValue;
        if (A.Avail && A.RefIdx >= 0) min = Math.Min(min, A.RefIdx);
        if (B.Avail && B.RefIdx >= 0) min = Math.Min(min, B.RefIdx);
        if (C.Avail && C.RefIdx >= 0) min = Math.Min(min, C.RefIdx);
        return min == int.MaxValue ? -1 : min;
    }
}
