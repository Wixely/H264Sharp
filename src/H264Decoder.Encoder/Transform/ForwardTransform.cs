namespace H264Decoder.Encoder.Transform;

/// <summary>Forward 4x4 integer DCT and 4x4 Hadamard (spec §8.5.8 / §8.5.10).
/// Outputs are in spec coefficient domain — the matching dequant + inverse transform
/// pair from the decoder produces samples scaled by 1.</summary>
public static class ForwardTransform
{
    /// <summary>Forward 4x4 integer transform. Operates in place on a 16-entry block in raster order.
    /// Input is a residual (pred-subtracted samples). Output matches the decoder's expected
    /// pre-quant coefficient domain: dequant + Inverse4x4 + (+32)>>6 returns the residual.</summary>
    public static void Forward4x4(Span<int> b)
    {
        Span<int> tmp = stackalloc int[16];
        // Row transform: c = T * b
        for (int i = 0; i < 4; i++)
        {
            int b0 = b[i * 4 + 0];
            int b1 = b[i * 4 + 1];
            int b2 = b[i * 4 + 2];
            int b3 = b[i * 4 + 3];

            int e0 = b0 + b3;
            int e1 = b1 + b2;
            int e2 = b1 - b2;
            int e3 = b0 - b3;

            tmp[i * 4 + 0] = e0 + e1;
            tmp[i * 4 + 1] = (e3 << 1) + e2;
            tmp[i * 4 + 2] = e0 - e1;
            tmp[i * 4 + 3] = e3 - (e2 << 1);
        }
        // Column transform: r = c * T^T (same matrix applied vertically).
        for (int j = 0; j < 4; j++)
        {
            int c0 = tmp[0 * 4 + j];
            int c1 = tmp[1 * 4 + j];
            int c2 = tmp[2 * 4 + j];
            int c3 = tmp[3 * 4 + j];

            int g0 = c0 + c3;
            int g1 = c1 + c2;
            int g2 = c1 - c2;
            int g3 = c0 - c3;

            b[0 * 4 + j] = g0 + g1;
            b[1 * 4 + j] = (g3 << 1) + g2;
            b[2 * 4 + j] = g0 - g1;
            b[3 * 4 + j] = g3 - (g2 << 1);
        }
    }

    /// <summary>Forward 4x4 Hadamard for Intra_16x16 luma DC (inverse is InverseHadamard4x4 followed
    /// by DequantLumaDc which divides by 2 — so the forward path normalizes by 1 after both passes,
    /// matching the spec's "transform + scale by 1/2" decomposition).</summary>
    public static void ForwardHadamard4x4(Span<int> b)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int b0 = b[i * 4 + 0];
            int b1 = b[i * 4 + 1];
            int b2 = b[i * 4 + 2];
            int b3 = b[i * 4 + 3];

            int e0 = b0 + b3;
            int e1 = b1 + b2;
            int e2 = b1 - b2;
            int e3 = b0 - b3;

            tmp[i * 4 + 0] = e0 + e1;
            tmp[i * 4 + 1] = e3 + e2;
            tmp[i * 4 + 2] = e0 - e1;
            tmp[i * 4 + 3] = e3 - e2;
        }
        for (int j = 0; j < 4; j++)
        {
            int c0 = tmp[0 * 4 + j];
            int c1 = tmp[1 * 4 + j];
            int c2 = tmp[2 * 4 + j];
            int c3 = tmp[3 * 4 + j];

            int g0 = c0 + c3;
            int g1 = c1 + c2;
            int g2 = c1 - c2;
            int g3 = c0 - c3;

            b[0 * 4 + j] = g0 + g1;
            b[1 * 4 + j] = g3 + g2;
            b[2 * 4 + j] = g0 - g1;
            b[3 * 4 + j] = g3 - g2;
        }
    }

    /// <summary>Forward 2x2 Hadamard for chroma DC. Layout: [TL, TR, BL, BR].
    /// Inverse is InverseHadamard2x2 followed by DequantChromaDc (which applies the ">> 1").</summary>
    public static void ForwardHadamard2x2(Span<int> b)
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
