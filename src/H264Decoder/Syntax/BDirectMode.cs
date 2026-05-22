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
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null)
    {
        if (!sliceHeader.DirectSpatialMvPredFlag)
        {
            throw new NotSupportedException("Temporal direct mode not yet supported");
        }
        DeriveSpatialDirect(mb, 0, 0, 4, 4, leftMb, topMb, topRightMb, topLeftMb, colocatedMb);
    }

    /// <summary>Apply spatial direct mode for one 8x8 quadrant within a B_8x8 MB.</summary>
    public static void ApplyDirect8x8(
        Macroblock mb, int quadrant, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null)
    {
        if (!sliceHeader.DirectSpatialMvPredFlag)
        {
            throw new NotSupportedException("Temporal direct mode not yet supported");
        }
        int qx = (quadrant & 1) * 2, qy = (quadrant >> 1) * 2;
        DeriveSpatialDirect(mb, qx, qy, 2, 2, leftMb, topMb, topRightMb, topLeftMb, colocatedMb);
    }

    /// <summary>Spatial direct derivation (spec §8.4.1.2.2) for a rectangle of 4x4 blocks.
    /// Region covered: [bx0..bx0+bw, by0..by0+bh] in 4x4 units.</summary>
    private static void DeriveSpatialDirect(
        Macroblock mb, int bx0, int by0, int bw, int bh,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb)
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

        // Per-4x4 colocated-MV override (spec §8.4.1.2.2): for each 4x4 block whose
        // colocated L1[0] block has refIdx 0 and |MV| <= 1, force the direct MV to (0,0)
        // for the direction whose refIdx is 0. Short-term refs only — long-term refs
        // are excluded from this override (we don't yet support long-term refs).
        bool colIsIntra = colocatedMb is null
            || colocatedMb.IsPcm
            || (!colocatedMb.IsBInter && !colocatedMb.IsSkipped
                && colocatedMb.Type.PredMode != MbPartPredMode.PredL0);
        if (colocatedMb is not null && !colIsIntra)
        {
            for (int by = by0; by < by0 + bh; by++)
                for (int bx = bx0; bx < bx0 + bw; bx++)
                {
                    int idx = MacroblockParser.SpatialToRaster(bx, by);
                    int q = MacroblockParser.QuadrantOf(bx, by);
                    // Choose the L0 motion of the colocated MB (or its L1 if the colocated
                    // MB has no L0 — i.e., L1-only inter partition).
                    int colRefIdx, colMvX, colMvY;
                    if (colocatedMb.IsBInter || colocatedMb.IsBSkip)
                    {
                        bool colHasL0 = colocatedMb.PredFlagL0Block[idx] != 0;
                        if (colHasL0)
                        {
                            colRefIdx = colocatedMb.RefIdxL08x8[q];
                            colMvX = colocatedMb.MvL0XBlock[idx];
                            colMvY = colocatedMb.MvL0YBlock[idx];
                        }
                        else
                        {
                            colRefIdx = colocatedMb.RefIdxL18x8[q];
                            colMvX = colocatedMb.MvL1XBlock[idx];
                            colMvY = colocatedMb.MvL1YBlock[idx];
                        }
                    }
                    else
                    {
                        // P-slice colocated MB (including P_Skip).
                        colRefIdx = colocatedMb.RefIdxL08x8[q];
                        colMvX = colocatedMb.MvL0XBlock[idx];
                        colMvY = colocatedMb.MvL0YBlock[idx];
                    }
                    bool colSmall = colRefIdx == 0
                        && Math.Abs(colMvX) <= 1 && Math.Abs(colMvY) <= 1;
                    if (!colSmall) continue;
                    if (refL0 == 0)
                    {
                        mb.MvL0XBlock[idx] = 0; mb.MvL0YBlock[idx] = 0;
                    }
                    if (refL1 == 0)
                    {
                        mb.MvL1XBlock[idx] = 0; mb.MvL1YBlock[idx] = 0;
                    }
                }
        }

        // For B_Direct_16x16 (full MB), emit per-4x4 BInterPartitions so the
        // reconstructor sees any per-block MV variation introduced by the override.
        if (bw == 4 && bh == 4)
        {
            BPredDir dir;
            if (pf0 != 0 && pf1 != 0) dir = BPredDir.Bi;
            else if (pf0 != 0) dir = BPredDir.L0;
            else dir = BPredDir.L1;
            int r0 = refL0 < 0 ? 0 : refL0;
            int r1 = refL1 < 0 ? 0 : refL1;
            for (int by = 0; by < 4; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    int idx = MacroblockParser.SpatialToRaster(bx, by);
                    mb.BInterPartitions.Add(new BMvPartition(
                        bx * 4, by * 4, 4, 4, dir,
                        r0, mb.MvL0XBlock[idx], mb.MvL0YBlock[idx],
                        r1, mb.MvL1XBlock[idx], mb.MvL1YBlock[idx]));
                }
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
