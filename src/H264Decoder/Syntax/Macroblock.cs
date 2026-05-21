namespace H264Decoder.Syntax;

/// <summary>
/// One parsed macroblock — fully decoded syntax + residual coefficients, but
/// NOT yet inverse-transformed or reconstructed. Allocated fresh per MB.
/// </summary>
public sealed class Macroblock
{
    public int MbAddress { get; init; }
    public IntraMbType Type { get; init; }
    public IntraChromaPredMode ChromaPredMode { get; set; }
    public int CbpLuma { get; set; }
    public int CbpChroma { get; set; }
    public int QpY { get; set; }

    /// <summary>16 entries — raw Intra_4x4 prediction codewords (I_NxN only):
    /// -1 means "use the neighbor-predicted mode", otherwise a 3-bit rem_mode value (0..7).</summary>
    public int[] Intra4x4PredMode { get; } = new int[16];

    /// <summary>16 entries — resolved Intra_4x4 modes (0..8) after applying the
    /// prediction-from-neighbors rule. Set by the reconstructor.</summary>
    public int[] Intra4x4Mode { get; } = new int[16];

    /// <summary>Intra_16x16 DC block — 16 DC coefficients in zig-zag scan order.</summary>
    public int[] LumaDc { get; } = new int[16];

    /// <summary>16 4x4 luma residual blocks. For Intra_16x16 the [0] slot is unused.</summary>
    public int[,] Luma { get; } = new int[16, 16];

    /// <summary>2 chroma DC blocks (one per component), 4 coefficients each.</summary>
    public int[,] ChromaDc { get; } = new int[2, 4];

    /// <summary>2 components × 4 4x4 AC blocks × 16 coeffs (the [0] slot is unused).</summary>
    public int[,,] ChromaAc { get; } = new int[2, 4, 16];

    /// <summary>Per-block luma non-zero count, used by future macroblocks as left/top context.</summary>
    public int[] NonZeroCountLuma { get; } = new int[16];

    /// <summary>Per-block chroma AC non-zero count.</summary>
    public int[,] NonZeroCountChromaAc { get; } = new int[2, 4];

    // ---- P-slice inter fields (for P_L0_16x16 currently) ----

    /// <summary>L0 reference index for inter MBs (P_L0_16x16). 0 for the default single-ref case.</summary>
    public int RefIdxL0 { get; set; }

    /// <summary>Motion vector L0 in quarter-pixel units. (X, Y).</summary>
    public int MvL0X { get; set; }
    public int MvL0Y { get; set; }

    /// <summary>Diagnostic: bit position in the slice RBSP where this MB's parsing started/ended.</summary>
    public int ParseStartBit { get; set; }
    public int ParseEndBit { get; set; }
}
