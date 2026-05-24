namespace H264Decoder.Encoder.Mode;

/// <summary>Integer-pel motion estimation for P_L0_16x16. Diamond-style search around
/// a starting point (predicted-MV neighbor median, or (0,0)) with early termination
/// when no improvement is found. Returns the best (mvX, mvY) in quarter-pel units
/// (always multiples of 4 since we don't probe sub-pel positions in phase 2).</summary>
internal static class MotionEstimator
{
    /// <summary>Result of motion estimation: best MV (quarter-pel) and SAD cost at that MV.</summary>
    internal readonly record struct MeResult(int MvX, int MvY, int Sad);

    /// <summary>Run integer-pel ME for a 16x16 luma block.</summary>
    public static MeResult Search(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma,
        int blockX, int blockY,
        int startMvX, int startMvY,
        int searchRangePel,
        int maxSadEvals)
    {
        // Integer-pel grid: convert start MV to integer-pel deltas (quarter-pel units >> 2).
        int startIx = startMvX >> 2;
        int startIy = startMvY >> 2;
        Span<int> triedX = stackalloc int[128];
        Span<int> triedY = stackalloc int[128];
        int triedCount = 0;
        int evals = 0;

        int bestIx = startIx, bestIy = startIy;
        int bestSad = Sad16x16(refY, refW, refH, srcLuma, blockX + startIx, blockY + startIy);
        Mark(triedX, triedY, ref triedCount, startIx, startIy);
        evals++;

        if ((startIx | startIy) != 0 && evals < maxSadEvals)
        {
            int zeroSad = Sad16x16(refY, refW, refH, srcLuma, blockX, blockY);
            Mark(triedX, triedY, ref triedCount, 0, 0);
            evals++;
            if (zeroSad < bestSad)
            {
                bestSad = zeroSad;
                bestIx = 0;
                bestIy = 0;
            }
        }

        // Diamond search: 4 neighbors at ±1 from current best. Continue until no improvement.
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
                int sad = Sad16x16(refY, refW, refH, srcLuma, blockX + tx, blockY + ty);
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

        return new MeResult(bestIx * 4, bestIy * 4, bestSad);
    }

    /// <summary>SAD between original 16x16 block and a reference-shifted block. Out-of-bounds
    /// reference samples use edge replication (clip to nearest valid sample).</summary>
    public static int Sad16x16(
        byte[] refY, int refW, int refH,
        ReadOnlySpan<byte> srcLuma, int refX, int refY0)
    {
        int sad = 0;
        for (int y = 0; y < 16; y++)
        {
            int ry = refY0 + y;
            if (ry < 0) ry = 0; else if (ry >= refH) ry = refH - 1;
            int rowBase = ry * refW;
            int srcBase = y * 16;
            for (int x = 0; x < 16; x++)
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

    /// <summary>Integer-pel luma MC into a 16x16 destination. Phase 2 only handles integer MVs;
    /// quarter-pel MV components below 4 in magnitude are truncated to integer by the caller.</summary>
    public static void IntegerLumaPredict(
        byte[] refY, int refW, int refH,
        int blockX, int blockY, int mvQpelX, int mvQpelY,
        Span<byte> dst16x16)
    {
        int srcX = blockX + (mvQpelX >> 2);
        int srcY = blockY + (mvQpelY >> 2);
        for (int y = 0; y < 16; y++)
        {
            int ry = srcY + y;
            if (ry < 0) ry = 0; else if (ry >= refH) ry = refH - 1;
            int rowBase = ry * refW;
            int dstBase = y * 16;
            for (int x = 0; x < 16; x++)
            {
                int rx = srcX + x;
                if (rx < 0) rx = 0; else if (rx >= refW) rx = refW - 1;
                dst16x16[dstBase + x] = refY[rowBase + rx];
            }
        }
    }

    /// <summary>Integer-pel chroma MC into an 8x8 destination. Chroma MV is luma MV / 2; phase 2
    /// ignores the chroma 1/8-pel fraction (treats it as integer chroma sample).</summary>
    public static void IntegerChromaPredict(
        byte[] refC, int refCw, int refCh,
        int blockCx, int blockCy, int mvLumaQpelX, int mvLumaQpelY,
        Span<byte> dst8x8)
    {
        int srcX = blockCx + (mvLumaQpelX >> 3);
        int srcY = blockCy + (mvLumaQpelY >> 3);
        for (int y = 0; y < 8; y++)
        {
            int ry = srcY + y;
            if (ry < 0) ry = 0; else if (ry >= refCh) ry = refCh - 1;
            int rowBase = ry * refCw;
            int dstBase = y * 8;
            for (int x = 0; x < 8; x++)
            {
                int rx = srcX + x;
                if (rx < 0) rx = 0; else if (rx >= refCw) rx = refCw - 1;
                dst8x8[dstBase + x] = refC[rowBase + rx];
            }
        }
    }
}
