namespace H264Decoder.Encoder.Transform;

/// <summary>H.264 forward 4x4 quantization. Spec §8.5.9 / Table 8-15.
/// Pairs with the decoder's Quantization.Dequant4x4Ac, DequantLumaDc, DequantChromaDc.</summary>
public static class ForwardQuantization
{
    // MF[m][pos] where m = qP % 6, pos: 0=both-even, 1=both-odd, 2=mixed (one even one odd).
    private static readonly int[,] _mf =
    {
        { 13107, 5243, 8066 },
        { 11916, 4660, 7490 },
        { 10082, 4194, 6554 },
        {  9362, 3647, 5825 },
        {  8192, 3355, 5243 },
        {  7282, 2893, 4559 },
    };

    private static int MfFor(int qP, int i, int j)
    {
        int m = qP % 6;
        int pos = (i % 2 == 0 && j % 2 == 0) ? 0
                : (i % 2 == 1 && j % 2 == 1) ? 1
                : 2;
        return _mf[m, pos];
    }

    /// <summary>Quantize a 4x4 AC block. Input in raster order: transformed coefficients (T*b*T^T).
    /// Output Z[i][j] in raster order suitable for the decoder's dequant.</summary>
    public static void Quant4x4Ac(Span<int> coeffs, int qP, bool intra)
    {
        int qBits = 15 + qP / 6;
        int f = (1 << qBits) / (intra ? 3 : 6);
        for (int idx = 0; idx < 16; idx++)
        {
            int i = (idx >> 2) & 3;
            int j = idx & 3;
            int w = coeffs[idx];
            int mf = MfFor(qP, i, j);
            int abs = w < 0 ? -w : w;
            int q = (int)(((long)abs * mf + f) >> qBits);
            coeffs[idx] = w < 0 ? -q : q;
        }
    }

    /// <summary>Quantize Intra_16x16 luma DC. Input is 16 forward-Hadamard-transformed DC values in
    /// raster order, where each input DC was itself the (0,0) of the forward 4x4 DCT of its sub-block
    /// (i.e., pre-scaled by 16). The +2 added to qBits accounts for the chained transform scaling
    /// (Hadamard 4x + DC pre-scale 16x = 64x, partially undone by the decoder's inverse 4x4 (+32)>>6).</summary>
    public static void QuantLumaDc(Span<int> dc, int qP)
    {
        int qBits = 15 + qP / 6;
        int qBitsDc = qBits + 2;
        int mf = _mf[qP % 6, 0];
        int fDc = (1 << qBitsDc) / 3;
        for (int idx = 0; idx < 16; idx++)
        {
            int w = dc[idx];
            int abs = w < 0 ? -w : w;
            int q = (int)(((long)abs * mf + fDc) >> qBitsDc);
            dc[idx] = w < 0 ? -q : q;
        }
    }

    /// <summary>Quantize chroma DC (2x2 block) after forward Hadamard. Input is the (0,0) of forward
    /// 4x4 DCT per sub-block (pre-scaled by 16). Pairs with decoder's DequantChromaDc.</summary>
    public static void QuantChromaDc(Span<int> dc, int qP)
    {
        int qBits = 15 + qP / 6;
        int qBitsDc = qBits + 1;
        int mf = _mf[qP % 6, 0];
        int fDc = (1 << qBitsDc) / 3;
        for (int idx = 0; idx < 4; idx++)
        {
            int w = dc[idx];
            int abs = w < 0 ? -w : w;
            int q = (int)(((long)abs * mf + fDc) >> qBitsDc);
            dc[idx] = w < 0 ? -q : q;
        }
    }
}
