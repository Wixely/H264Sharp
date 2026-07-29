namespace H264Sharp.Decoder.Syntax;

/// <summary>
/// Context required for temporal direct mode (spec §8.4.1.2.3): the current
/// picture's POC, L1[0]'s POC, and POCs for each entry in L0. Optional — when
/// null, only spatial direct is available.
/// </summary>
public sealed class TemporalDirectContext
{
    /// <summary>POC of the picture currently being decoded.</summary>
    public required int CurrentPoc { get; init; }
    /// <summary>POC of the L1[0] (colocated) reference picture.</summary>
    public required int Pic1Poc { get; init; }
    /// <summary>POCs of all L0 reference pictures, indexed by ref_idx_l0.</summary>
    public required int[] L0Pocs { get; init; }
    /// <summary>The colocated (L1[0]) picture's own L0 / L1 reference POCs, indexed by that
    /// picture's ref_idx. Used to map a colocated block's refIdxCol to the referenced picture's
    /// POC, which is then matched into the current L0 (§8.4.1.2.3). Null falls back to identity.</summary>
    public int[]? ColRefL0Pocs { get; init; }
    public int[]? ColRefL1Pocs { get; init; }
    /// <summary>Whether each current L0 reference is a long-term picture (parallel to L0Pocs).
    /// §8.4.1.2.3: when the L0 target is long-term, the colocated MV is used unscaled (mvL1 = 0).</summary>
    public bool[]? L0IsLongTerm { get; init; }
}

/// <summary>
/// B-slice direct mode (spec §8.4.1.2). Implements spatial direct (§8.4.1.2.2)
/// and temporal direct (§8.4.1.2.3); the slice header's direct_spatial_mv_pred_flag
/// selects between them.
/// </summary>
internal static class BDirectMode
{
    /// <summary>Apply direct mode for a full B_Direct_16x16 (or B_Skip) MB.</summary>
    public static void ApplyDirect16x16(
        Macroblock mb, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null,
        TemporalDirectContext? tdCtx = null,
        bool direct8x8Inference = true)
    {
        // Mark every 4x4 block as direct (B_Skip / B_Direct_16x16) for neighbor context use.
        for (int i = 0; i < 16; i++) mb.IsDirectBlock[i] = 1;
        if (sliceHeader.DirectSpatialMvPredFlag)
        {
            DeriveSpatialDirect(mb, 0, 0, 4, 4, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, direct8x8Inference);
        }
        else
        {
            DeriveTemporalDirect(mb, 0, 0, 4, 4, colocatedMb, tdCtx, direct8x8Inference);
        }
    }

    /// <summary>Apply direct mode for one 8x8 quadrant within a B_8x8 MB.</summary>
    public static void ApplyDirect8x8(
        Macroblock mb, int quadrant, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb = null,
        TemporalDirectContext? tdCtx = null,
        bool direct8x8Inference = true)
    {
        int qx = (quadrant & 1) * 2, qy = (quadrant >> 1) * 2;
        // Mark the four 4x4 blocks in this quadrant as direct (B_Direct_8x8 sub-MB).
        for (int yy = qy; yy < qy + 2; yy++)
            for (int xx = qx; xx < qx + 2; xx++)
                mb.IsDirectBlock[MacroblockParser.SpatialToRaster(xx, yy)] = 1;
        if (sliceHeader.DirectSpatialMvPredFlag)
        {
            DeriveSpatialDirect(mb, qx, qy, 2, 2, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, direct8x8Inference);
        }
        else
        {
            DeriveTemporalDirect(mb, qx, qy, 2, 2, colocatedMb, tdCtx, direct8x8Inference);
        }
    }

    /// <summary>The colocated 4x4 block used for a given (bx, by) under direct_8x8_inference: the
    /// outer-corner 4x4 of the containing 8x8 quadrant (§8.4.1.2.1). Returns (bx, by) unchanged
    /// when inference is off (per-4x4 colocated sampling).</summary>
    private static (int bx, int by) ColocatedSample(int bx, int by, bool direct8x8Inference)
    {
        if (!direct8x8Inference) return (bx, by);
        int q = MacroblockParser.QuadrantOf(bx, by);
        return ((q & 1) * 3, (q >> 1) * 3);
    }

    /// <summary>Temporal direct derivation (spec §8.4.1.2.3) for a rectangle of 4x4 blocks.</summary>
    private static void DeriveTemporalDirect(
        Macroblock mb, int bx0, int by0, int bw, int bh,
        Macroblock? colocatedMb, TemporalDirectContext? tdCtx, bool direct8x8Inference)
    {
        if (tdCtx is null)
            throw new InvalidOperationException("Temporal direct mode requires TemporalDirectContext");

        // Detect intra colocated MB: all-zero MVs, refIdx 0 for L0 (long-term degenerate case).
        bool colIsIntra = colocatedMb is null
            || colocatedMb.IsPcm
            || (!colocatedMb.IsBInter && !colocatedMb.IsSkipped
                && colocatedMb.Type.PredMode != MbPartPredMode.PredL0);

        // Per-4x4 derivation.
        for (int by = by0; by < by0 + bh; by++)
            for (int bx = bx0; bx < bx0 + bw; bx++)
            {
                int idx = MacroblockParser.SpatialToRaster(bx, by);
                int q = MacroblockParser.QuadrantOf(bx, by);
                // §8.4.1.2.1: under direct_8x8_inference the colocated motion for a whole 8x8 is
                // taken from that 8x8's outer-corner 4x4, not each 4x4's own colocated block.
                var (cbx, cby) = ColocatedSample(bx, by, direct8x8Inference);
                int colIdx = MacroblockParser.SpatialToRaster(cbx, cby);
                int colQ = MacroblockParser.QuadrantOf(cbx, cby);

                int colRefIdx, colMvX, colMvY;
                bool colFromL1 = false; // colRefIdx indexes the colocated pic's L1 (not L0)
                bool colIsRef0; // colocated block's ref points at a valid (non-intra) entry
                if (colIsIntra)
                {
                    colRefIdx = -1;
                    colMvX = 0;
                    colMvY = 0;
                    colIsRef0 = false;
                }
                else if (colocatedMb!.IsBInter || colocatedMb.IsBSkip)
                {
                    // B-slice colocated MB: pick L0 motion if available, else L1.
                    bool colHasL0 = colocatedMb.PredFlagL0Block[colIdx] != 0;
                    if (colHasL0)
                    {
                        colRefIdx = colocatedMb.RefIdxL08x8[colQ];
                        colMvX = colocatedMb.MvL0XBlock[colIdx];
                        colMvY = colocatedMb.MvL0YBlock[colIdx];
                    }
                    else if (colocatedMb.PredFlagL1Block[colIdx] != 0)
                    {
                        colRefIdx = colocatedMb.RefIdxL18x8[colQ];
                        colMvX = colocatedMb.MvL1XBlock[colIdx];
                        colMvY = colocatedMb.MvL1YBlock[colIdx];
                        colFromL1 = true;
                    }
                    else
                    {
                        colRefIdx = -1;
                        colMvX = 0;
                        colMvY = 0;
                    }
                    colIsRef0 = colRefIdx >= 0;
                }
                else
                {
                    // P-slice colocated MB (including P_Skip): L0 is implicit.
                    colRefIdx = colocatedMb.RefIdxL08x8[colQ];
                    colMvX = colocatedMb.MvL0XBlock[colIdx];
                    colMvY = colocatedMb.MvL0YBlock[colIdx];
                    colIsRef0 = true;
                }

                // refIdxL0 (§8.4.1.2.3): the lowest current-L0 index that references the picture
                // the colocated block referenced. Resolve refIdxCol -> that picture's POC via the
                // colocated picture's own ref-list POCs, then match into the current L0.
                int refL0Idx;
                if (!colIsRef0)
                {
                    refL0Idx = 0;
                }
                else
                {
                    int[]? colPocs = colFromL1 ? tdCtx.ColRefL1Pocs : tdCtx.ColRefL0Pocs;
                    if (colPocs is not null && colRefIdx < colPocs.Length)
                    {
                        int refPoc = colPocs[colRefIdx];
                        int found = System.Array.IndexOf(tdCtx.L0Pocs, refPoc);
                        refL0Idx = found >= 0 ? found : 0;
                    }
                    else
                    {
                        // No ref-POC info — fall back to identity (correct for the single-ref case).
                        refL0Idx = colRefIdx < tdCtx.L0Pocs.Length ? colRefIdx : 0;
                    }
                }
                int refL1Idx = 0;

                int mvL0X, mvL0Y, mvL1X, mvL1Y;
                if (!colIsRef0)
                {
                    // Intra colocated: refIdxL0=0, refIdxL1=0, MVs zero.
                    mvL0X = 0; mvL0Y = 0; mvL1X = 0; mvL1Y = 0;
                }
                else
                {
                    int pic0Poc = tdCtx.L0Pocs[refL0Idx];
                    int pic1Poc = tdCtx.Pic1Poc;
                    int currPoc = tdCtx.CurrentPoc;
                    int tb = Clip3(-128, 127, currPoc - pic0Poc);
                    int td = Clip3(-128, 127, pic1Poc - pic0Poc);
                    bool l0IsLongTerm = tdCtx.L0IsLongTerm is not null
                        && refL0Idx < tdCtx.L0IsLongTerm.Length && tdCtx.L0IsLongTerm[refL0Idx];
                    if (td == 0 || l0IsLongTerm)
                    {
                        // §8.4.1.2.3: equal-POC or long-term L0 target — copy colMv onto L0, L1 = 0.
                        mvL0X = colMvX; mvL0Y = colMvY;
                        mvL1X = 0; mvL1Y = 0;
                    }
                    else
                    {
                        int tx = (16384 + (Math.Abs(td) >> 1)) / td;
                        int dsf = Clip3(-1024, 1023, (tb * tx + 32) >> 6);
                        mvL0X = (dsf * colMvX + 128) >> 8;
                        mvL0Y = (dsf * colMvY + 128) >> 8;
                        mvL1X = mvL0X - colMvX;
                        mvL1Y = mvL0Y - colMvY;
                    }
                }

                // Store per-block state.
                mb.MvL0XBlock[idx] = mvL0X;
                mb.MvL0YBlock[idx] = mvL0Y;
                mb.MvL1XBlock[idx] = mvL1X;
                mb.MvL1YBlock[idx] = mvL1Y;
                mb.PredFlagL0Block[idx] = 1;
                mb.PredFlagL1Block[idx] = 1;
                mb.RefIdxL08x8[q] = refL0Idx;
                mb.RefIdxL18x8[q] = refL1Idx;
            }

        // For B_Direct_16x16 (full MB), emit per-4x4 BInterPartitions so the
        // reconstructor sees per-block MV variation.
        if (bw == 4 && bh == 4)
        {
            for (int by = 0; by < 4; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    int idx = MacroblockParser.SpatialToRaster(bx, by);
                    int q = MacroblockParser.QuadrantOf(bx, by);
                    mb.BInterPartitions.Add(new BMvPartition(
                        bx * 4, by * 4, 4, 4, BPredDir.Bi,
                        mb.RefIdxL08x8[q], mb.MvL0XBlock[idx], mb.MvL0YBlock[idx],
                        mb.RefIdxL18x8[q], mb.MvL1XBlock[idx], mb.MvL1YBlock[idx]));
                }
        }
    }

    private static int Clip3(int lo, int hi, int v) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Spatial direct derivation (spec §8.4.1.2.2) for a rectangle of 4x4 blocks.
    /// Region covered: [bx0..bx0+bw, by0..by0+bh] in 4x4 units.</summary>
    private static void DeriveSpatialDirect(
        Macroblock mb, int bx0, int by0, int bw, int bh,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb, bool direct8x8Inference)
    {
        // §8.4.1.2.2: refIdxL0/L1 and the base mvL0/mvL1 are derived ONCE for the whole macroblock
        // using its 16x16-partition neighbors A=(-1,0), B=(0,-1), C=(4,-1) — the SAME values for
        // every 4x4 block, regardless of whether this is B_Direct_16x16 or a B_Direct_8x8 quadrant.
        // (bx0/by0/bw/bh below select only which blocks this call fills.) Block-level variation
        // comes solely from the per-4x4 colZeroFlag override further down.
        int refL0 = MinPositiveRef(mb, 0, 0, 4, leftMb, topMb, topRightMb, topLeftMb, listX: 0);
        int refL1 = MinPositiveRef(mb, 0, 0, 4, leftMb, topMb, topRightMb, topLeftMb, listX: 1);

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
                mb, 0, 0, 0, 0, 4, 4, refL0, listX: 0,
                leftMb, topMb, topRightMb, topLeftMb);
        }
        if (refL1 >= 0 && !noRefs)
        {
            (mvL1X, mvL1Y) = MacroblockParser.PredictMvForPartitionListB(
                mb, 0, 0, 0, 0, 4, 4, refL1, listX: 1,
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
                    // §8.4.1.2.1: under direct_8x8_inference sample the colocated block at the 8x8's
                    // outer corner rather than per-4x4.
                    var (cbx, cby) = ColocatedSample(bx, by, direct8x8Inference);
                    int colIdx = MacroblockParser.SpatialToRaster(cbx, cby);
                    int colQ = MacroblockParser.QuadrantOf(cbx, cby);
                    // Choose the L0 motion of the colocated MB (or its L1 if the colocated
                    // MB has no L0 — i.e., L1-only inter partition).
                    int colRefIdx, colMvX, colMvY;
                    if (colocatedMb.IsBInter || colocatedMb.IsBSkip)
                    {
                        bool colHasL0 = colocatedMb.PredFlagL0Block[colIdx] != 0;
                        if (colHasL0)
                        {
                            colRefIdx = colocatedMb.RefIdxL08x8[colQ];
                            colMvX = colocatedMb.MvL0XBlock[colIdx];
                            colMvY = colocatedMb.MvL0YBlock[colIdx];
                        }
                        else
                        {
                            colRefIdx = colocatedMb.RefIdxL18x8[colQ];
                            colMvX = colocatedMb.MvL1XBlock[colIdx];
                            colMvY = colocatedMb.MvL1YBlock[colIdx];
                        }
                    }
                    else
                    {
                        // P-slice colocated MB (including P_Skip).
                        colRefIdx = colocatedMb.RefIdxL08x8[colQ];
                        colMvX = colocatedMb.MvL0XBlock[colIdx];
                        colMvY = colocatedMb.MvL0YBlock[colIdx];
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
