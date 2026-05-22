namespace H264Decoder.Syntax;

public enum MbPartPredMode
{
    Intra4x4,
    Intra16x16,
    IPcm,
    PredL0,       // single 16x16 inter partition, L0 reference
    PredL1,       // B-slice: L1-only inter partition
    BiPred,       // B-slice: bipred (L0 + L1) inter partition
    Direct,       // B-slice: B_Direct_16x16 (no MV signal)
}

public enum Intra16x16PredMode
{
    Vertical = 0,
    Horizontal = 1,
    Dc = 2,
    Plane = 3,
}

public enum IntraChromaPredMode
{
    Dc = 0,
    Horizontal = 1,
    Vertical = 2,
    Plane = 3,
}

/// <summary>
/// Decoded mb_type (spec Table 7-11 for I-slice, 7-13 for P-slice).
/// </summary>
public readonly record struct IntraMbType(
    int RawMbType,
    MbPartPredMode PredMode,
    Intra16x16PredMode I16x16PredMode,
    int CbpLuma,
    int CbpChroma)
{
    /// <summary>
    /// P-slice mb_type decoding (spec Table 7-13). Values 0..4 are inter partition
    /// configurations; 5..30 are intra MB types with offset 5; 31 is I_PCM.
    /// </summary>
    public static IntraMbType FromPSliceCodeword(uint mbType)
    {
        if (mbType <= 4)
        {
            return new IntraMbType((int)mbType, MbPartPredMode.PredL0, default, 0, 0);
        }
        if (mbType >= 5 && mbType <= 30)
        {
            return FromISliceCodeword(mbType - 5);
        }
        throw new InvalidDataException($"P-slice mb_type {mbType} out of range");
    }

    /// <summary>For inter P mb_types, returns the number of motion partitions (1, 2, 2, 4, 4).</summary>
    public static int NumMbPart(int rawPMbType) => rawPMbType switch
    {
        0 => 1,
        1 => 2,
        2 => 2,
        3 => 4,
        4 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(rawPMbType)),
    };

    /// <summary>Pixel size of one motion partition for mb_type 0..2. For mb_type 3/4 the
    /// partition is 8x8 and further split by sub_mb_type.</summary>
    public static (int Width, int Height) MbPartSize(int rawPMbType) => rawPMbType switch
    {
        0 => (16, 16),
        1 => (16, 8),
        2 => (8, 16),
        3 => (8, 8),
        4 => (8, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(rawPMbType)),
    };

    public static IntraMbType FromISliceCodeword(uint mbType)
    {
        if (mbType == 0)
        {
            return new IntraMbType(0, MbPartPredMode.Intra4x4, default, 0, 0);
        }
        if (mbType == 25)
        {
            return new IntraMbType(25, MbPartPredMode.IPcm, default, 0, 0);
        }
        if (mbType > 25)
        {
            throw new InvalidDataException($"I-slice mb_type {mbType} out of range");
        }

        int g = ((int)mbType - 1) / 4;
        int p = ((int)mbType - 1) % 4;
        (int cbpLuma, int cbpChroma) = g switch
        {
            0 => (0, 0),
            1 => (0, 1),
            2 => (0, 2),
            3 => (15, 0),
            4 => (15, 1),
            5 => (15, 2),
            _ => throw new InvalidDataException($"unreachable mb_type group {g}"),
        };
        return new IntraMbType((int)mbType, MbPartPredMode.Intra16x16,
            (Intra16x16PredMode)p, cbpLuma, cbpChroma);
    }
}

public enum SubMbType
{
    PL0_8x8 = 0,
    PL0_8x4 = 1,
    PL0_4x8 = 2,
    PL0_4x4 = 3,
}

public static class SubMbTypeOps
{
    public static int NumSubMbPart(SubMbType t) => t switch
    {
        SubMbType.PL0_8x8 => 1,
        SubMbType.PL0_8x4 => 2,
        SubMbType.PL0_4x8 => 2,
        SubMbType.PL0_4x4 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static (int Width, int Height) SubMbPartSize(SubMbType t) => t switch
    {
        SubMbType.PL0_8x8 => (8, 8),
        SubMbType.PL0_8x4 => (8, 4),
        SubMbType.PL0_4x8 => (4, 8),
        SubMbType.PL0_4x4 => (4, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
}

/// <summary>B-slice sub_mb_type (spec Table 7-17). 13 entries.</summary>
public enum BSubMbType
{
    Direct_8x8 = 0,
    L0_8x8     = 1,
    L1_8x8     = 2,
    Bi_8x8     = 3,
    L0_8x4     = 4,
    L0_4x8     = 5,
    L1_8x4     = 6,
    L1_4x8     = 7,
    Bi_8x4     = 8,
    Bi_4x8     = 9,
    L0_4x4     = 10,
    L1_4x4     = 11,
    Bi_4x4     = 12,
}

/// <summary>
/// One partition's pred-direction within a B-slice MB.
/// </summary>
public enum BPredDir { Direct, L0, L1, Bi }

/// <summary>
/// Decoded B-slice mb_type info (spec Table 7-14). 23 inter codes (0..22) plus 23..48 intra reuse.
/// </summary>
public readonly record struct BMbTypeInfo(
    int RawMbType,
    int NumMbPart,
    int PartWidth,
    int PartHeight,
    BPredDir Dir0,
    BPredDir Dir1)
{
    public BPredDir DirForPart(int p) => p == 0 ? Dir0 : Dir1;
}

public static class BSubMbTypeOps
{
    public static int NumSubMbPart(BSubMbType t) => t switch
    {
        BSubMbType.Direct_8x8 => 4,  // Direct treats 8x8 as 4 4x4 sub-blocks per spec
        BSubMbType.L0_8x8 or BSubMbType.L1_8x8 or BSubMbType.Bi_8x8 => 1,
        BSubMbType.L0_8x4 or BSubMbType.L1_8x4 or BSubMbType.Bi_8x4 => 2,
        BSubMbType.L0_4x8 or BSubMbType.L1_4x8 or BSubMbType.Bi_4x8 => 2,
        BSubMbType.L0_4x4 or BSubMbType.L1_4x4 or BSubMbType.Bi_4x4 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static (int W, int H) SubMbPartSize(BSubMbType t) => t switch
    {
        BSubMbType.Direct_8x8 => (4, 4),
        BSubMbType.L0_8x8 or BSubMbType.L1_8x8 or BSubMbType.Bi_8x8 => (8, 8),
        BSubMbType.L0_8x4 or BSubMbType.L1_8x4 or BSubMbType.Bi_8x4 => (8, 4),
        BSubMbType.L0_4x8 or BSubMbType.L1_4x8 or BSubMbType.Bi_4x8 => (4, 8),
        BSubMbType.L0_4x4 or BSubMbType.L1_4x4 or BSubMbType.Bi_4x4 => (4, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static BPredDir Dir(BSubMbType t) => t switch
    {
        BSubMbType.Direct_8x8 => BPredDir.Direct,
        BSubMbType.L0_8x8 or BSubMbType.L0_8x4 or BSubMbType.L0_4x8 or BSubMbType.L0_4x4 => BPredDir.L0,
        BSubMbType.L1_8x8 or BSubMbType.L1_8x4 or BSubMbType.L1_4x8 or BSubMbType.L1_4x4 => BPredDir.L1,
        _ => BPredDir.Bi,
    };
}

public static class BMbType
{
    // Spec Table 7-14: B-slice mb_type 0..22 — inter modes; 23..48 are intra (mb_type-23 = I-slice code).
    // Format: (NumMbPart, PartW, PartH, Dir0, Dir1).
    private static readonly BMbTypeInfo[] _table = BuildTable();

    private static BMbTypeInfo[] BuildTable()
    {
        BPredDir D0 = BPredDir.Direct, L0 = BPredDir.L0, L1 = BPredDir.L1, Bi = BPredDir.Bi;
        var t = new BMbTypeInfo[23];
        // 0: B_Direct_16x16 — single direct partition (size 16x16 for spatial direct; sub-treat by direct mode).
        t[0]  = new BMbTypeInfo(0,  1, 16, 16, D0, D0);
        // 1..3: 16x16 L0/L1/Bi.
        t[1]  = new BMbTypeInfo(1,  1, 16, 16, L0, L0);
        t[2]  = new BMbTypeInfo(2,  1, 16, 16, L1, L1);
        t[3]  = new BMbTypeInfo(3,  1, 16, 16, Bi, Bi);
        // 4..15: 16x8 partitions (2 parts of 16x8) with 6 direction combos × {L0L0, L1L1, BiBi, L0L1, L1L0, L0Bi, L1Bi, BiL0, BiL1}
        // Actually spec 7-14:
        //  4 B_L0_L0_16x8, 5 B_L0_L0_8x16,
        //  6 B_L1_L1_16x8, 7 B_L1_L1_8x16,
        //  8 B_L0_L1_16x8, 9 B_L0_L1_8x16,
        // 10 B_L1_L0_16x8,11 B_L1_L0_8x16,
        // 12 B_L0_Bi_16x8,13 B_L0_Bi_8x16,
        // 14 B_L1_Bi_16x8,15 B_L1_Bi_8x16,
        // 16 B_Bi_L0_16x8,17 B_Bi_L0_8x16,
        // 18 B_Bi_L1_16x8,19 B_Bi_L1_8x16,
        // 20 B_Bi_Bi_16x8,21 B_Bi_Bi_8x16,
        // 22 B_8x8.
        t[4]  = new BMbTypeInfo(4,  2, 16, 8, L0, L0);
        t[5]  = new BMbTypeInfo(5,  2, 8, 16, L0, L0);
        t[6]  = new BMbTypeInfo(6,  2, 16, 8, L1, L1);
        t[7]  = new BMbTypeInfo(7,  2, 8, 16, L1, L1);
        t[8]  = new BMbTypeInfo(8,  2, 16, 8, L0, L1);
        t[9]  = new BMbTypeInfo(9,  2, 8, 16, L0, L1);
        t[10] = new BMbTypeInfo(10, 2, 16, 8, L1, L0);
        t[11] = new BMbTypeInfo(11, 2, 8, 16, L1, L0);
        t[12] = new BMbTypeInfo(12, 2, 16, 8, L0, Bi);
        t[13] = new BMbTypeInfo(13, 2, 8, 16, L0, Bi);
        t[14] = new BMbTypeInfo(14, 2, 16, 8, L1, Bi);
        t[15] = new BMbTypeInfo(15, 2, 8, 16, L1, Bi);
        t[16] = new BMbTypeInfo(16, 2, 16, 8, Bi, L0);
        t[17] = new BMbTypeInfo(17, 2, 8, 16, Bi, L0);
        t[18] = new BMbTypeInfo(18, 2, 16, 8, Bi, L1);
        t[19] = new BMbTypeInfo(19, 2, 8, 16, Bi, L1);
        t[20] = new BMbTypeInfo(20, 2, 16, 8, Bi, Bi);
        t[21] = new BMbTypeInfo(21, 2, 8, 16, Bi, Bi);
        // 22: B_8x8 — 4 sub-blocks, each its own sub_mb_type carries direction.
        t[22] = new BMbTypeInfo(22, 4, 8, 8, D0, D0);
        return t;
    }

    public static bool IsInter(uint code) => code <= 22;

    public static BMbTypeInfo Info(int rawMbType) => _table[rawMbType];
}
