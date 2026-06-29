namespace H264Sharp.Decoder.Picture;

/// <summary>
/// H.264 motion compensation (spec §8.4.2). All 16 luma sub-pel positions
/// (6-tap half-pel + quarter-pel bilinear) plus 1/8-pel chroma bilinear,
/// with edge-replication padding when MVs point outside the reference.
///
/// The implementation mirrors OpenH264's mc.cpp McLuma_c / McChroma_c dispatch
/// table but is written against the spec's formulas directly. Output blocks
/// can be any of {16, 8, 4} wide × {16, 8, 4} tall — sized for P_L0_16x16,
/// 16x8, 8x16, 8x8, 8x4, 4x8, 4x4 partitions.
/// </summary>
internal static class MotionCompensation
{
    /// <summary>Apply luma MC for a block of given size at the given position.</summary>
    /// <param name="refY">Reference picture Y plane (no stride).</param>
    /// <param name="refW">Reference picture width.</param>
    /// <param name="refH">Reference picture height.</param>
    /// <param name="blockX">Current picture X of the partition's top-left luma sample.</param>
    /// <param name="blockY">Current picture Y of the partition's top-left luma sample.</param>
    /// <param name="mvX">L0 motion vector X in quarter-pel units.</param>
    /// <param name="mvY">L0 motion vector Y in quarter-pel units.</param>
    /// <param name="bWidth">Partition width (4, 8, or 16).</param>
    /// <param name="bHeight">Partition height (4, 8, or 16).</param>
    /// <param name="dst">Output buffer, size bWidth*bHeight, row-major.</param>
    public static void LumaPredict(
        byte[] refY, int refW, int refH,
        int blockX, int blockY,
        int mvX, int mvY,
        int bWidth, int bHeight,
        Span<byte> dst)
    {
        int xFrac = mvX & 3;
        int yFrac = mvY & 3;
        int srcX = blockX + (mvX >> 2);
        int srcY = blockY + (mvY >> 2);

        // Fast path: integer-pel.
        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < bHeight; y++)
                for (int x = 0; x < bWidth; x++)
                    dst[y * bWidth + x] = ClampedSample(refY, refW, refH, srcX + x, srcY + y);
            return;
        }

        // Padded reference for filter taps. Need 2 on each side hor + ver (filter is 6-tap
        // centered between samples 2 and 3, so reaches -2..+3 around each output position).
        int padW = bWidth + 5;
        int padH = bHeight + 5;
        // The "+(-2, -2)" offsets so pad[2, 2] corresponds to src position 0,0.
        Span<byte> pad = stackalloc byte[padW * padH];
        FillPaddedRefBlock(refY, refW, refH, srcX - 2, srcY - 2, padW, padH, pad);

        // Produce the (xFrac, yFrac) sub-pel block.
        // Reference position (in pad coords): pad[2 + j, 2 + i] = refY[srcX + j, srcY + i].
        switch ((xFrac, yFrac))
        {
            // Integer-pel cases that may still need pad-based reads (already covered above, but kept for completeness):
            case (0, 0):
                CopyFromPad(pad, padW, 2, 2, bWidth, bHeight, dst);
                break;
            case (1, 0): // average of (0,0) and (2,0)
                AvgHalfH(pad, padW, 2, 2, bWidth, bHeight, dst, integerOffsetX: 0);
                break;
            case (2, 0): // pure half-pel horizontal
                HalfH(pad, padW, 2, 2, bWidth, bHeight, dst);
                break;
            case (3, 0): // average of (2,0) and (0,0)+1 integer
                AvgHalfH(pad, padW, 2, 2, bWidth, bHeight, dst, integerOffsetX: 1);
                break;
            case (0, 1):
                AvgHalfV(pad, padW, 2, 2, bWidth, bHeight, dst, integerOffsetY: 0);
                break;
            case (0, 2):
                HalfV(pad, padW, 2, 2, bWidth, bHeight, dst);
                break;
            case (0, 3):
                AvgHalfV(pad, padW, 2, 2, bWidth, bHeight, dst, integerOffsetY: 1);
                break;
            case (2, 2):
                HalfHV(pad, padW, 2, 2, bWidth, bHeight, dst);
                break;
            case (1, 1):
                AvgTwo(BuildHalfH(pad, padW, 2, 2, bWidth, bHeight), BuildHalfV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (3, 1):
                AvgTwo(BuildHalfH(pad, padW, 2, 2, bWidth, bHeight), BuildHalfV(pad, padW, 3, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (1, 3):
                AvgTwo(BuildHalfH(pad, padW, 2, 3, bWidth, bHeight), BuildHalfV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (3, 3):
                AvgTwo(BuildHalfH(pad, padW, 2, 3, bWidth, bHeight), BuildHalfV(pad, padW, 3, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (1, 2):
                AvgTwo(BuildHalfV(pad, padW, 2, 2, bWidth, bHeight), BuildHalfHV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (3, 2):
                AvgTwo(BuildHalfV(pad, padW, 3, 2, bWidth, bHeight), BuildHalfHV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (2, 1):
                AvgTwo(BuildHalfH(pad, padW, 2, 2, bWidth, bHeight), BuildHalfHV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
            case (2, 3):
                AvgTwo(BuildHalfH(pad, padW, 2, 3, bWidth, bHeight), BuildHalfHV(pad, padW, 2, 2, bWidth, bHeight), bWidth, bHeight, dst);
                break;
        }
    }

    /// <summary>1/8-pel chroma bilinear MC. MV is in 1/4-pel luma units; lower 3 bits are the chroma sub-pel.</summary>
    public static void ChromaPredict(
        byte[] refC, int refW, int refH,
        int blockX, int blockY,
        int mvLumaX, int mvLumaY,
        int bWidth, int bHeight,
        Span<byte> dst)
    {
        int xFrac = mvLumaX & 7;
        int yFrac = mvLumaY & 7;
        int srcX = blockX + (mvLumaX >> 3);
        int srcY = blockY + (mvLumaY >> 3);

        if (xFrac == 0 && yFrac == 0)
        {
            for (int y = 0; y < bHeight; y++)
                for (int x = 0; x < bWidth; x++)
                    dst[y * bWidth + x] = ClampedSample(refC, refW, refH, srcX + x, srcY + y);
            return;
        }

        // Bilinear weights (spec §8.4.2.2.2).
        int A = (8 - xFrac) * (8 - yFrac);
        int B = xFrac * (8 - yFrac);
        int C = (8 - xFrac) * yFrac;
        int D = xFrac * yFrac;

        for (int y = 0; y < bHeight; y++)
        {
            for (int x = 0; x < bWidth; x++)
            {
                int p00 = ClampedSample(refC, refW, refH, srcX + x,     srcY + y);
                int p01 = ClampedSample(refC, refW, refH, srcX + x + 1, srcY + y);
                int p10 = ClampedSample(refC, refW, refH, srcX + x,     srcY + y + 1);
                int p11 = ClampedSample(refC, refW, refH, srcX + x + 1, srcY + y + 1);
                int v = (A * p00 + B * p01 + C * p10 + D * p11 + 32) >> 6;
                dst[y * bWidth + x] = (byte)v;
            }
        }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------
    private static byte ClampedSample(byte[] plane, int W, int H, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= W) x = W - 1;
        if (y < 0) y = 0; else if (y >= H) y = H - 1;
        return plane[y * W + x];
    }

    private static void FillPaddedRefBlock(byte[] plane, int W, int H, int srcX, int srcY, int padW, int padH, Span<byte> pad)
    {
        for (int yy = 0; yy < padH; yy++)
            for (int xx = 0; xx < padW; xx++)
                pad[yy * padW + xx] = ClampedSample(plane, W, H, srcX + xx, srcY + yy);
    }

    private static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // 6-tap filter on 6 8-bit samples → 16-bit intermediate.
    private static int Tap6(int s0, int s1, int s2, int s3, int s4, int s5) =>
        s0 - 5 * s1 + 20 * s2 + 20 * s3 - 5 * s4 + s5;

    private static void CopyFromPad(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst)
    {
        for (int y = 0; y < bHeight; y++)
            for (int x = 0; x < bWidth; x++)
                dst[y * bWidth + x] = pad[(padY + y) * padW + padX + x];
    }

    // Half-pel horizontal: output[x,y] = ((s[-2]-5s[-1]+20s[0]+20s[+1]-5s[+2]+s[+3]) + 16) >> 5, clipped.
    private static void HalfH(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst)
    {
        for (int y = 0; y < bHeight; y++)
        {
            int row = (padY + y) * padW + padX;
            for (int x = 0; x < bWidth; x++)
            {
                int v = Tap6(pad[row + x - 2], pad[row + x - 1], pad[row + x], pad[row + x + 1], pad[row + x + 2], pad[row + x + 3]);
                dst[y * bWidth + x] = ClipByte((v + 16) >> 5);
            }
        }
    }

    private static void HalfV(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst)
    {
        for (int y = 0; y < bHeight; y++)
        {
            for (int x = 0; x < bWidth; x++)
            {
                int col = padX + x;
                int yy = padY + y;
                int v = Tap6(
                    pad[(yy - 2) * padW + col], pad[(yy - 1) * padW + col],
                    pad[(yy) * padW + col],     pad[(yy + 1) * padW + col],
                    pad[(yy + 2) * padW + col], pad[(yy + 3) * padW + col]);
                dst[y * bWidth + x] = ClipByte((v + 16) >> 5);
            }
        }
    }

    private static void HalfHV(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst)
    {
        // First produce 16-bit horizontal-filtered intermediates for (bHeight + 5) rows.
        int tmpH = bHeight + 5;
        Span<int> tmp = stackalloc int[bWidth * tmpH];
        for (int y = 0; y < tmpH; y++)
        {
            int yy = padY + y - 2; // pad-relative row for the -2..+3 vertical range
            for (int x = 0; x < bWidth; x++)
            {
                int row = yy * padW + (padX + x);
                tmp[y * bWidth + x] = Tap6(pad[row - 2], pad[row - 1], pad[row], pad[row + 1], pad[row + 2], pad[row + 3]);
            }
        }
        // Now apply vertical 6-tap on the 16-bit horizontal-filtered intermediates.
        for (int y = 0; y < bHeight; y++)
        {
            for (int x = 0; x < bWidth; x++)
            {
                int v = Tap6(
                    tmp[(y) * bWidth + x],     tmp[(y + 1) * bWidth + x],
                    tmp[(y + 2) * bWidth + x], tmp[(y + 3) * bWidth + x],
                    tmp[(y + 4) * bWidth + x], tmp[(y + 5) * bWidth + x]);
                dst[y * bWidth + x] = ClipByte((v + 512) >> 10);
            }
        }
    }

    // Average integer-pel at (padX+intOffsetX, padY) with half-pel-horizontal at (padX, padY).
    private static void AvgHalfH(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst, int integerOffsetX)
    {
        Span<byte> half = stackalloc byte[bWidth * bHeight];
        HalfH(pad, padW, padX, padY, bWidth, bHeight, half);
        for (int y = 0; y < bHeight; y++)
            for (int x = 0; x < bWidth; x++)
            {
                int integer = pad[(padY + y) * padW + (padX + integerOffsetX + x)];
                dst[y * bWidth + x] = (byte)((integer + half[y * bWidth + x] + 1) >> 1);
            }
    }

    private static void AvgHalfV(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight, Span<byte> dst, int integerOffsetY)
    {
        Span<byte> half = stackalloc byte[bWidth * bHeight];
        HalfV(pad, padW, padX, padY, bWidth, bHeight, half);
        for (int y = 0; y < bHeight; y++)
            for (int x = 0; x < bWidth; x++)
            {
                int integer = pad[(padY + integerOffsetY + y) * padW + (padX + x)];
                dst[y * bWidth + x] = (byte)((integer + half[y * bWidth + x] + 1) >> 1);
            }
    }

    private static byte[] BuildHalfH(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight)
    {
        byte[] r = new byte[bWidth * bHeight];
        HalfH(pad, padW, padX, padY, bWidth, bHeight, r);
        return r;
    }

    private static byte[] BuildHalfV(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight)
    {
        byte[] r = new byte[bWidth * bHeight];
        HalfV(pad, padW, padX, padY, bWidth, bHeight, r);
        return r;
    }

    private static byte[] BuildHalfHV(ReadOnlySpan<byte> pad, int padW, int padX, int padY, int bWidth, int bHeight)
    {
        byte[] r = new byte[bWidth * bHeight];
        HalfHV(pad, padW, padX, padY, bWidth, bHeight, r);
        return r;
    }

    private static void AvgTwo(byte[] a, byte[] b, int bWidth, int bHeight, Span<byte> dst)
    {
        int n = bWidth * bHeight;
        for (int i = 0; i < n; i++) dst[i] = (byte)((a[i] + b[i] + 1) >> 1);
    }
}
