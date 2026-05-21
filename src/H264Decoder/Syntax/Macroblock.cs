namespace H264Decoder.Syntax;

/// <summary>One motion partition within a P-slice MB. Pixel coordinates are MB-relative
/// (X, Y in 0..15 and width/height in 4..16). MV is in quarter-pixel units.</summary>
public readonly record struct MvPartition(int X, int Y, int Width, int Height, int RefIdxL0, int MvL0X, int MvL0Y);


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

    /// <summary>Convenience: L0 reference index for the FIRST partition of inter MBs.</summary>
    public int RefIdxL0 { get; set; }

    /// <summary>Convenience: L0 motion vector of the FIRST partition, in quarter-pixel units.</summary>
    public int MvL0X { get; set; }
    public int MvL0Y { get; set; }

    /// <summary>Per-4x4-block L0 motion vector (X, in quarter-pixel units). Raster scan order.
    /// Same MV is replicated within a motion partition.</summary>
    public int[] MvL0XBlock { get; } = new int[16];
    public int[] MvL0YBlock { get; } = new int[16];

    /// <summary>RefIdx per 8x8 quadrant (raster scan: 0=TL, 1=TR, 2=BL, 3=BR).
    /// For mb_type 0/1/2 the refIdx is per partition; we replicate to per-quadrant.</summary>
    public int[] RefIdxL08x8 { get; } = new int[4];

    /// <summary>Motion partitions for this MB (list of (x, y, w, h, refIdx, mvX, mvY)).
    /// One entry for P_L0_16x16, two for 16x8 / 8x16, up to sixteen for P_8x8 with 4x4 sub-blocks.</summary>
    public List<MvPartition> InterPartitions { get; set; } = new();

    /// <summary>Diagnostic: bit position in the slice RBSP where this MB's parsing started/ended.</summary>
    public int ParseStartBit { get; set; }
    public int ParseEndBit { get; set; }
}
