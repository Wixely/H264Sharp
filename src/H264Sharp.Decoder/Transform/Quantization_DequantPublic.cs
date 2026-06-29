namespace H264Sharp.Decoder.Transform;

/// <summary>Public façade over the internal Quantization dequant helpers so the encoder
/// (and its tests) can verify forward/inverse round-trips. The decode path itself does
/// not use this — it calls the internal class directly.</summary>
public static class Quantization_DequantPublic
{
    public static void Dequant4x4Ac(Span<int> coeffs, int qP) => Quantization.Dequant4x4Ac(coeffs, qP);
    public static void DequantLumaDc(Span<int> dc, int qP) => Quantization.DequantLumaDc(dc, qP);
    public static void DequantChromaDc(Span<int> dc, int qP) => Quantization.DequantChromaDc(dc, qP);
    public static int LevelScale4x4(int qP, int i, int j) => Quantization.LevelScale4x4(qP, i, j);
}
