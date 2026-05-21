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
/// Decoded I-slice mb_type (spec Table 7-11). For mb_type values 1..24 the
/// type encodes (Intra_16x16 prediction mode, CBP luma, CBP chroma) jointly.
/// </summary>
public readonly record struct IntraMbType(
    int RawMbType,
    MbPartPredMode PredMode,
    Intra16x16PredMode I16x16PredMode,
    int CbpLuma,
    int CbpChroma)
{
    /// <summary>
    /// P-slice mb_type decoding (spec Table 7-13). Returns the mapped type plus,
    /// for P_L0_16x16 (mb_type=0), the PredL0 partition. For mb_type values >= 5
    /// in a P-slice, they encode I-slice types (with offset 5).
    /// </summary>
    public static IntraMbType FromPSliceCodeword(uint mbType)
    {
        // Table 7-13: 0=P_L0_16x16, 1=P_L0_L0_16x8, 2=P_L0_L0_8x16, 3=P_8x8, 4=P_8x8ref0
        if (mbType == 0)
        {
            return new IntraMbType((int)mbType, MbPartPredMode.PredL0, default, 0, 0);
        }
        if (mbType >= 1 && mbType <= 4)
        {
            throw new NotSupportedException($"P-slice mb_type {mbType} (multi-partition) not yet supported");
        }
        if (mbType >= 5 && mbType <= 30)
        {
            // Intra MB inside P-slice — offset is 5.
            return FromISliceCodeword(mbType - 5);
        }
        throw new InvalidDataException($"P-slice mb_type {mbType} out of range");
    }

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

        // Entries 1..24 are six groups of four:
        //   group g = (mbType - 1) / 4
        //   pred  p = (mbType - 1) % 4    -> Intra16x16PredMode
        // group  CbpLuma  CbpChroma
        //   0     0        0
        //   1     0        1
        //   2     0        2
        //   3     15       0
        //   4     15       1
        //   5     15       2
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
