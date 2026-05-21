namespace H264Decoder.Transform;

/// <summary>
/// H.264 integer inverse transforms (spec §8.5.12). All operate in-place on 16-entry
/// (4x4) or 4-entry (2x2) blocks in raster order.
/// </summary>
public static class InverseTransform
{
    /// <summary>Inverse 4x4 integer transform (spec §8.5.12.2).</summary>
    public static void Inverse4x4(Span<int> b)
    {
        Span<int> tmp = stackalloc int[16];

        // Row transform: c = T^-1 * b
        for (int i = 0; i < 4; i++)
        {
            int b0 = b[i * 4 + 0];
            int b1 = b[i * 4 + 1];
            int b2 = b[i * 4 + 2];
            int b3 = b[i * 4 + 3];

            int e0 = b0 + b2;
            int e1 = b0 - b2;
            int e2 = (b1 >> 1) - b3;
            int e3 = b1 + (b3 >> 1);

            tmp[i * 4 + 0] = e0 + e3;
            tmp[i * 4 + 1] = e1 + e2;
            tmp[i * 4 + 2] = e1 - e2;
            tmp[i * 4 + 3] = e0 - e3;
        }

        // Column transform: r = c * T^-1; final shift (+32)>>6 applied here.
        for (int j = 0; j < 4; j++)
        {
            int c0 = tmp[0 * 4 + j];
            int c1 = tmp[1 * 4 + j];
            int c2 = tmp[2 * 4 + j];
            int c3 = tmp[3 * 4 + j];

            int g0 = c0 + c2;
            int g1 = c0 - c2;
            int g2 = (c1 >> 1) - c3;
            int g3 = c1 + (c3 >> 1);

            int h0 = g0 + g3;
            int h1 = g1 + g2;
            int h2 = g1 - g2;
            int h3 = g0 - g3;

            b[0 * 4 + j] = (h0 + 32) >> 6;
            b[1 * 4 + j] = (h1 + 32) >> 6;
            b[2 * 4 + j] = (h2 + 32) >> 6;
            b[3 * 4 + j] = (h3 + 32) >> 6;
        }
    }

    /// <summary>Inverse 4x4 Hadamard for Intra_16x16 DC luma (spec §8.5.10).</summary>
    public static void InverseHadamard4x4(Span<int> b)
    {
        Span<int> tmp = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int b0 = b[i * 4 + 0];
            int b1 = b[i * 4 + 1];
            int b2 = b[i * 4 + 2];
            int b3 = b[i * 4 + 3];

            int e0 = b0 + b2;
            int e1 = b0 - b2;
            int e2 = b1 - b3;
            int e3 = b1 + b3;

            tmp[i * 4 + 0] = e0 + e3;
            tmp[i * 4 + 1] = e1 + e2;
            tmp[i * 4 + 2] = e1 - e2;
            tmp[i * 4 + 3] = e0 - e3;
        }

        for (int j = 0; j < 4; j++)
        {
            int c0 = tmp[0 * 4 + j];
            int c1 = tmp[1 * 4 + j];
            int c2 = tmp[2 * 4 + j];
            int c3 = tmp[3 * 4 + j];

            int g0 = c0 + c2;
            int g1 = c0 - c2;
            int g2 = c1 - c3;
            int g3 = c1 + c3;

            b[0 * 4 + j] = g0 + g3;
            b[1 * 4 + j] = g1 + g2;
            b[2 * 4 + j] = g1 - g2;
            b[3 * 4 + j] = g0 - g3;
        }
    }

    /// <summary>
    /// Inverse 8x8 integer transform (spec §8.5.10.2). Operates in place on a 64-entry
    /// block in raster order: 1-D inverse applied to each row then each column.
    /// Final shift (+32)&gt;&gt;6 applied after the column pass.
    /// </summary>
    public static void Inverse8x8(Span<int> b)
    {
        Span<int> tmp = stackalloc int[64];

        for (int i = 0; i < 8; i++) Row8(b.Slice(i * 8, 8), tmp.Slice(i * 8, 8));

        Span<int> col = stackalloc int[8];
        Span<int> outCol = stackalloc int[8];
        for (int j = 0; j < 8; j++)
        {
            for (int k = 0; k < 8; k++) col[k] = tmp[k * 8 + j];
            Row8(col, outCol);
            for (int k = 0; k < 8; k++) b[k * 8 + j] = (outCol[k] + 32) >> 6;
        }
    }

    // 1-D 8-point inverse transform (spec §8.5.10.2 equations 8-338..8-345).
    private static void Row8(ReadOnlySpan<int> c, Span<int> o)
    {
        int c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7];

        int h0 = c0 + c4;
        int h1 = -c3 + c5 - c7 - (c7 >> 1);
        int h2 = c0 - c4;
        int h3 = c1 + c7 - c3 - (c3 >> 1);
        int h4 = (c2 >> 1) - c6;
        int h5 = -c1 + c7 + c5 + (c5 >> 1);
        int h6 = c2 + (c6 >> 1);
        int h7 = c3 + c5 + c1 + (c1 >> 1);

        int k0 = h0 + h6;
        int k1 = h1 + (h7 >> 2);
        int k2 = h2 + h4;
        int k3 = h3 + (h5 >> 2);
        int k4 = h2 - h4;
        int k5 = (h3 >> 2) - h5;
        int k6 = h0 - h6;
        int k7 = h7 - (h1 >> 2);

        o[0] = k0 + k7;
        o[1] = k2 + k5;
        o[2] = k4 + k3;
        o[3] = k6 + k1;
        o[4] = k6 - k1;
        o[5] = k4 - k3;
        o[6] = k2 - k5;
        o[7] = k0 - k7;
    }

    /// <summary>Inverse 2x2 Hadamard for chroma DC (spec §8.5.11.1). Block layout: [TL, TR, BL, BR].</summary>
    public static void InverseHadamard2x2(Span<int> b)
    {
        int tl = b[0];
        int tr = b[1];
        int bl = b[2];
        int br = b[3];

        int t0 = tl + tr;
        int t1 = tl - tr;
        int t2 = bl + br;
        int t3 = bl - br;

        b[0] = t0 + t2;
        b[1] = t1 + t3;
        b[2] = t0 - t2;
        b[3] = t1 - t3;
    }
}
