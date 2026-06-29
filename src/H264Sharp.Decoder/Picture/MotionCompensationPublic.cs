namespace H264Sharp.Decoder.Picture;

/// <summary>Public façade over the internal MotionCompensation helpers so the encoder
/// can produce sub-pel predicted samples for candidate SAD computation in ME.</summary>
public static class MotionCompensationPublic
{
    /// <summary>Apply luma MC (6-tap half-pel + bilinear quarter-pel) for a block at sub-pel MV.</summary>
    public static void LumaPredict(
        byte[] refY, int refW, int refH,
        int blockX, int blockY,
        int mvX, int mvY,
        int bWidth, int bHeight,
        Span<byte> dst)
        => MotionCompensation.LumaPredict(refY, refW, refH, blockX, blockY, mvX, mvY, bWidth, bHeight, dst);

    /// <summary>Apply chroma MC (1/8-pel bilinear) for a block at sub-pel chroma MV.</summary>
    public static void ChromaPredict(
        byte[] refC, int refW, int refH,
        int blockX, int blockY,
        int mvLumaX, int mvLumaY,
        int bWidth, int bHeight,
        Span<byte> dst)
        => MotionCompensation.ChromaPredict(refC, refW, refH, blockX, blockY, mvLumaX, mvLumaY, bWidth, bHeight, dst);
}
