using H264Decoder.Picture;

namespace H264Decoder.Encoder.Mode;

/// <summary>Motion estimation for inter partitions. Stage A: integer-pel diamond search.
/// Stage B (phase 3): half-pel refinement (8 positions ±2 around integer best),
/// then quarter-pel refinement (8 positions ±1 around half-pel best). Sub-pel SAD
/// uses the spec-correct 6-tap luma interpolator via <see cref="MotionCompensationPublic"/>.</summary>
internal static class MotionEstimator
{
    /// <summary>Result of motion estimation: best MV (quarter-pel) and SAD cost at that MV.</summary>
    internal readonly record struct MeResult(int MvX, int MvY, int Sad);

    /// <summary>Run integer-pel ME for a 16x16 luma block (legacy entry-point retained for callers
    /// that only want integer-pel). Returns MV in quarter-pel units (multiples of 4).</summary>
    public static MeResult Search(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma,
        int blockX, int blockY,
        int startMvX, int startMvY,
        int searchRangePel,
        int maxSadEvals)
        => SearchBlock(refY, refW, refH, srcLuma, blockX, blockY,
            startMvX, startMvY, searchRangePel, maxSadEvals,
            bWidth: 16, bHeight: 16, enableSubpel: false);

    /// <summary>Run integer-pel diamond ME then optional half- and quarter-pel refinement,
    /// for any rectangular block size (16x16, 16x8, 8x16, 8x8, 8x4, 4x8, 4x4).</summary>
    public static MeResult SearchBlock(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma,
        int blockX, int blockY,
        int startMvX, int startMvY,
        int searchRangePel,
        int maxSadEvals,
        int bWidth, int bHeight,
        bool enableSubpel)
    {
        // Convert quarter-pel start MV to integer-pel deltas.
        int startIx = startMvX >> 2;
        int startIy = startMvY >> 2;
        Span<int> triedX = stackalloc int[128];
        Span<int> triedY = stackalloc int[128];
        int triedCount = 0;
        int evals = 0;

        int bestIx = startIx, bestIy = startIy;
        int bestSad = SadBlockInteger(refY, refW, refH, srcLuma, blockX + startIx, blockY + startIy, bWidth, bHeight);
        Mark(triedX, triedY, ref triedCount, startIx, startIy);
        evals++;

        if ((startIx | startIy) != 0 && evals < maxSadEvals)
        {
            int zeroSad = SadBlockInteger(refY, refW, refH, srcLuma, blockX, blockY, bWidth, bHeight);
            Mark(triedX, triedY, ref triedCount, 0, 0);
            evals++;
            if (zeroSad < bestSad)
            {
                bestSad = zeroSad;
                bestIx = 0;
                bestIy = 0;
            }
        }

        // Diamond search: 4 neighbors at ±1 from current best.
        (int dx, int dy)[] diamond = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        bool improved = true;
        while (improved && evals < maxSadEvals)
        {
            improved = false;
            int curIx = bestIx, curIy = bestIy;
            for (int d = 0; d < 4 && evals < maxSadEvals; d++)
            {
                int tx = curIx + diamond[d].dx;
                int ty = curIy + diamond[d].dy;
                if (Math.Abs(tx - startIx) > searchRangePel || Math.Abs(ty - startIy) > searchRangePel) continue;
                if (Contains(triedX, triedY, triedCount, tx, ty)) continue;
                int sad = SadBlockInteger(refY, refW, refH, srcLuma, blockX + tx, blockY + ty, bWidth, bHeight);
                Mark(triedX, triedY, ref triedCount, tx, ty);
                evals++;
                if (sad < bestSad)
                {
                    bestSad = sad;
                    bestIx = tx;
                    bestIy = ty;
                    improved = true;
                }
            }
        }

        // Convert integer-pel best to quarter-pel units.
        int bestQx = bestIx * 4;
        int bestQy = bestIy * 4;

        if (!enableSubpel)
        {
            return new MeResult(bestQx, bestQy, bestSad);
        }

        // Half-pel refinement: 8 positions ±2 quarter-pel units around integer best.
        bestSad = RefineSubpel(refY, refW, refH, srcLuma, blockX, blockY,
            ref bestQx, ref bestQy, bestSad, bWidth, bHeight, step: 2);

        // Quarter-pel refinement: 8 positions ±1 quarter-pel unit around half-pel best.
        bestSad = RefineSubpel(refY, refW, refH, srcLuma, blockX, blockY,
            ref bestQx, ref bestQy, bestSad, bWidth, bHeight, step: 1);

        return new MeResult(bestQx, bestQy, bestSad);
    }

    /// <summary>Probe 8 sub-pel positions at +/- <paramref name="step"/> quarter-pel units around the
    /// current best MV. Updates <paramref name="bestQx"/>/<paramref name="bestQy"/> and returns new best SAD.</summary>
    private static int RefineSubpel(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma,
        int blockX, int blockY,
        ref int bestQx, ref int bestQy, int bestSad,
        int bWidth, int bHeight, int step)
    {
        (int dx, int dy)[] eight =
        {
            (-step, -step), (0, -step), (step, -step),
            (-step,  0),                 (step,  0),
            (-step,  step), (0,  step), (step,  step),
        };
        Span<byte> pred = stackalloc byte[256];
        Span<byte> predSlice = pred[..(bWidth * bHeight)];
        int curBx = bestQx, curBy = bestQy;
        for (int i = 0; i < eight.Length; i++)
        {
            int tx = curBx + eight[i].dx;
            int ty = curBy + eight[i].dy;
            MotionCompensationPublic.LumaPredict(refY, refW, refH, blockX, blockY, tx, ty, bWidth, bHeight, predSlice);
            int sad = 0;
            for (int y = 0; y < bHeight; y++)
                for (int x = 0; x < bWidth; x++)
                    sad += Math.Abs(srcLuma[y * bWidth + x] - predSlice[y * bWidth + x]);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestQx = tx;
                bestQy = ty;
            }
        }
        return bestSad;
    }

    /// <summary>SAD between original 16x16 block and a reference-shifted block at integer-pel offset.
    /// Edge replication on out-of-bounds.</summary>
    public static int Sad16x16(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma, int refX, int refY0)
        => SadBlockInteger(refY, refW, refH, srcLuma, refX, refY0, 16, 16);

    /// <summary>Generic-size integer-pel SAD.</summary>
    public static int SadBlockInteger(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma, int refX, int refY0, int bWidth, int bHeight)
    {
        int sad = 0;
        for (int y = 0; y < bHeight; y++)
        {
            int ry = refY0 + y;
            if (ry < 0) ry = 0; else if (ry >= refH) ry = refH - 1;
            int rowBase = ry * refW;
            int srcBase = y * bWidth;
            for (int x = 0; x < bWidth; x++)
            {
                int rx = refX + x;
                if (rx < 0) rx = 0; else if (rx >= refW) rx = refW - 1;
                sad += Math.Abs(srcLuma[srcBase + x] - refY[rowBase + rx]);
            }
        }
        return sad;
    }

    private static void Mark(Span<int> tx, Span<int> ty, ref int count, int x, int y)
    {
        if (count < tx.Length) { tx[count] = x; ty[count] = y; count++; }
    }

    private static bool Contains(ReadOnlySpan<int> tx, ReadOnlySpan<int> ty, int count, int x, int y)
    {
        for (int i = 0; i < count; i++) if (tx[i] == x && ty[i] == y) return true;
        return false;
    }

    /// <summary>Luma MC into a destination buffer at any sub-pel MV. Wraps the spec interpolator.</summary>
    public static void LumaPredictBlock(
        byte[] refY, int refW, int refH,
        int blockX, int blockY, int mvQpelX, int mvQpelY,
        int bWidth, int bHeight,
        Span<byte> dst)
        => MotionCompensationPublic.LumaPredict(refY, refW, refH, blockX, blockY, mvQpelX, mvQpelY, bWidth, bHeight, dst);

    /// <summary>Chroma MC into a destination buffer at any sub-pel MV. Wraps the spec 1/8-pel bilinear.</summary>
    public static void ChromaPredictBlock(
        byte[] refC, int refCw, int refCh,
        int blockCx, int blockCy, int mvLumaQpelX, int mvLumaQpelY,
        int bWidth, int bHeight,
        Span<byte> dst)
        => MotionCompensationPublic.ChromaPredict(refC, refCw, refCh, blockCx, blockCy, mvLumaQpelX, mvLumaQpelY, bWidth, bHeight, dst);

    /// <summary>Integer-pel luma MC into a 16x16 destination (legacy path; preserved for callers
    /// that already truncate MVs to integer-pel).</summary>
    public static void IntegerLumaPredict(
        byte[] refY, int refW, int refH,
        int blockX, int blockY, int mvQpelX, int mvQpelY,
        Span<byte> dst16x16)
    {
        // Mask MV to integer-pel for legacy callers.
        int intMvX = (mvQpelX >> 2) << 2;
        int intMvY = (mvQpelY >> 2) << 2;
        MotionCompensationPublic.LumaPredict(refY, refW, refH, blockX, blockY, intMvX, intMvY, 16, 16, dst16x16);
    }

    /// <summary>Integer-pel chroma MC into an 8x8 destination (legacy path).</summary>
    public static void IntegerChromaPredict(
        byte[] refC, int refCw, int refCh,
        int blockCx, int blockCy, int mvLumaQpelX, int mvLumaQpelY,
        Span<byte> dst8x8)
    {
        // Mask MV to integer-chroma (1/8-pel resolution -> mask low 3 bits).
        int intMvX = (mvLumaQpelX >> 3) << 3;
        int intMvY = (mvLumaQpelY >> 3) << 3;
        MotionCompensationPublic.ChromaPredict(refC, refCw, refCh, blockCx, blockCy, intMvX, intMvY, 8, 8, dst8x8);
    }
}
