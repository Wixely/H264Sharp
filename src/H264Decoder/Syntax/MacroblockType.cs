namespace H264Decoder.Syntax;

public enum MbPartPredMode
{
    Intra4x4,
    Intra16x16,
    IPcm,
    PredL0,       // single 16x16 inter partition, L0 reference
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
