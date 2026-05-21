namespace H264Decoder.Syntax;

public enum MbPartPredMode
{
    Intra4x4,
    Intra16x16,
    IPcm,
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
