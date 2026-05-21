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
