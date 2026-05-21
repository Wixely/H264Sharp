namespace H264Decoder.Syntax;

/// <summary>
/// Spec table 9-4: codeNum → coded_block_pattern mapping. Two columns:
/// intra (used for I-slice macroblocks) and inter (P-slice etc.).
/// </summary>
public static class CodedBlockPattern
{
    // From ITU-T H.264 Table 9-4, ChromaArrayType == 1 or 2 (4:2:0 / 4:2:2).
    // Indexed by codeNum (0..47). Each entry: { cbpIntra, cbpInter }.
    private static readonly byte[] _intraTable =
    [
        47, 31, 15,  0, 23, 27, 29, 30,  7, 11, 13, 14, 39, 43, 45, 46,
        16,  3,  5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44,  1,  2,  4,
         8, 17, 18, 20, 24,  6,  9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    private static readonly byte[] _interTable =
    [
         0, 16,  1,  2,  4,  8, 32,  3,  5, 10, 12, 15, 47,  7, 11, 13,
        14,  6,  9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24,  6,  9, 22, 25, 32, 26, 28, 30, 29, 19, 21, 38,
    ];

    public static int FromCodeNum(uint codeNum, bool intra)
    {
        if (codeNum >= 48)
        {
            throw new InvalidDataException($"coded_block_pattern codeNum {codeNum} out of range");
        }
        return intra ? _intraTable[codeNum] : _interTable[codeNum];
    }

    public static int LumaPart(int cbp) => cbp & 0x0F;
    public static int ChromaPart(int cbp) => cbp >> 4;
}
