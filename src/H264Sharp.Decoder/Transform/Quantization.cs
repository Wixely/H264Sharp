namespace H264Sharp.Decoder.Transform;

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

    // normAdjust8x8 from spec §8.5.9 / Table 7-2 (default flat scaling list).
    // Indexed as [qP % 6][i*8+j]. Same matrix as JM dequant_coef8 / OpenH264 g_kuiDequantCoeff8x8.
    private static readonly int[][] _normAdjust8x8 = new int[6][]
    {
        new[] {
            20,19,25,19,20,19,25,19,
            19,18,23,18,19,18,23,18,
            25,23,29,23,25,23,29,23,
            19,18,23,18,19,18,23,18,
            20,19,25,19,20,19,25,19,
            19,18,23,18,19,18,23,18,
            25,23,29,23,25,23,29,23,
            19,18,23,18,19,18,23,18,
        },
        new[] {
            22,21,28,21,22,21,28,21,
            21,19,26,19,21,19,26,19,
            28,26,32,26,28,26,32,26,
            21,19,26,19,21,19,26,19,
            22,21,28,21,22,21,28,21,
            21,19,26,19,21,19,26,19,
            28,26,32,26,28,26,32,26,
            21,19,26,19,21,19,26,19,
        },
        new[] {
            26,24,33,24,26,24,33,24,
            24,23,31,23,24,23,31,23,
            33,31,42,31,33,31,42,31,
            24,23,31,23,24,23,31,23,
            26,24,33,24,26,24,33,24,
            24,23,31,23,24,23,31,23,
            33,31,42,31,33,31,42,31,
            24,23,31,23,24,23,31,23,
        },
        new[] {
            28,26,35,26,28,26,35,26,
            26,25,33,25,26,25,33,25,
            35,33,45,33,35,33,45,33,
            26,25,33,25,26,25,33,25,
            28,26,35,26,28,26,35,26,
            26,25,33,25,26,25,33,25,
            35,33,45,33,35,33,45,33,
            26,25,33,25,26,25,33,25,
        },
        new[] {
            32,30,40,30,32,30,40,30,
            30,29,38,29,30,29,38,29,
            40,38,51,38,40,38,51,38,
            30,29,38,29,30,29,38,29,
            32,30,40,30,32,30,40,30,
            30,29,38,29,30,29,38,29,
            40,38,51,38,40,38,51,38,
            30,29,38,29,30,29,38,29,
        },
        new[] {
            36,34,46,34,36,34,46,34,
            34,32,43,32,34,32,43,32,
            46,43,58,43,46,43,58,43,
            34,32,43,32,34,32,43,32,
            36,34,46,34,36,34,46,34,
            34,32,43,32,34,32,43,32,
            46,43,58,43,46,43,58,43,
            34,32,43,32,34,32,43,32,
        },
    };

    /// <summary>
    /// 8x8 dequantization (spec §8.5.12.2). With flat scaling list (default),
    /// LevelScale8x8(qP%6,i,j) = normAdjust8x8 * 16. Two branches:
    ///   qP &gt;= 36:  dcoeff = (c * LevelScale8x8) &lt;&lt; (qP/6 - 6)
    ///   qP &lt;  36:  dcoeff = (c * LevelScale8x8 + (1 &lt;&lt; (5 - qP/6))) &gt;&gt; (6 - qP/6)
    /// Output is in spec dcoeff domain; the inverse 8x8 transform applies the terminal &gt;&gt;6.
    /// </summary>
    public static void Dequant8x8(scoped Span<int> coeffs, int qP)
    {
        int m = qP % 6;
        int qpDiv6 = qP / 6;
        int[] na = _normAdjust8x8[m];
        if (qP >= 36)
        {
            int shift = qpDiv6 - 6;
            for (int idx = 0; idx < 64; idx++)
            {
                // LevelScale8x8 = na * 16, fold the *16 with the left shift.
                coeffs[idx] = (coeffs[idx] * na[idx]) << (shift + 4);
            }
        }
        else
        {
            int shift = 6 - qpDiv6;
            int round = 1 << (5 - qpDiv6);
            for (int idx = 0; idx < 64; idx++)
            {
                coeffs[idx] = (coeffs[idx] * na[idx] * 16 + round) >> shift;
            }
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
