namespace H264Decoder.Transform;

/// <summary>H.264 inverse quantization, default (flat) scaling list (spec §8.5.9 / Table 8-15).</summary>
internal static class Quantization
{
    // normAdjust4x4[m][pos] where m = qP % 6, pos ∈ {0=both-even, 1=both-odd, 2=mixed}.
    private static readonly int[,] _normAdjust =
    {
        { 10, 16, 13 },
        { 11, 18, 14 },
        { 13, 20, 16 },
        { 14, 23, 18 },
        { 16, 25, 20 },
        { 18, 29, 23 },
    };

    public static int LevelScale4x4(int qP, int i, int j)
    {
        int m = qP % 6;
        int pos = (i % 2 == 0 && j % 2 == 0) ? 0
                : (i % 2 == 1 && j % 2 == 1) ? 1
                : 2;
        return _normAdjust[m, pos];
    }

    /// <summary>
    /// 4x4 AC dequantization (spec §8.5.12.1 step 1): scale level values to transform coefficients.
    /// Operates in place on a 16-entry block in zig-zag-restored (raster) order.
    /// </summary>
    public static void Dequant4x4Ac(Span<int> coeffs, int qP)
    {
        int shift = qP / 6;
        for (int idx = 0; idx < 16; idx++)
        {
            int i = (idx >> 2) & 3;
            int j = idx & 3;
            coeffs[idx] = coeffs[idx] * LevelScale4x4(qP, i, j) << shift;
        }
    }

    /// <summary>
    /// Intra_16x16 luma DC dequantization (spec §8.5.10). After inverse Hadamard,
    /// each value is scaled to the DC coefficient that should be placed at position (0,0)
    /// of the corresponding 4x4 luma block before the inverse 4x4 transform.
    /// Formula matches OpenH264's WelsLumaDcDequantIdct:
    ///   dcY = (f * LevelScale * 2^(qP/6 + 4) + 32) >> 6
    /// equivalent to (f * (LevelScale &lt;&lt; 4) + 32) &gt;&gt; 6 with the qP/6 shift folded in.
    /// </summary>
    public static void DequantLumaDc(Span<int> dc, int qP)
    {
        int v = LevelScale4x4(qP, 0, 0);
        int qShift = qP / 6;
        if (qShift >= 2)
        {
            int shift = qShift - 2;
            for (int i = 0; i < 16; i++) dc[i] = dc[i] * v << shift;
        }
        else
        {
            int shift = 2 - qShift;       // 1 or 2
            int half = 1 << (shift - 1);  // 1 or 2
            for (int i = 0; i < 16; i++) dc[i] = (dc[i] * v + half) >> shift;
        }
    }

    /// <summary>Chroma DC dequantization (spec §8.5.11).</summary>
    public static void DequantChromaDc(Span<int> dc, int qP)
    {
        int v = LevelScale4x4(qP, 0, 0);
        int shift = qP / 6;
        for (int i = 0; i < 4; i++)
        {
            dc[i] = ((dc[i] * v) << shift) >> 1;
        }
    }
}
