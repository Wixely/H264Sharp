using H264Decoder.Bitstream;

namespace H264Decoder.Cavlc;

/// <summary>Public façade over CavlcResidual.ReadResidualBlock for encoder tests
/// that need to decode and verify a hand-crafted residual without going through
/// the full macroblock parser.</summary>
public static class CavlcResidualPublic
{
    public static int ReadResidualBlock(
        ref BitReader reader,
        scoped Span<int> coeffs,
        int maxNumCoeff,
        int nC,
        bool chromaDc)
        => CavlcResidual.ReadResidualBlock(ref reader, coeffs, maxNumCoeff, nC, chromaDc);
}
