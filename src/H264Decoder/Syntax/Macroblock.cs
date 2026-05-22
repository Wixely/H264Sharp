namespace H264Decoder.Syntax;

/// <summary>One motion partition within a P-slice MB. Pixel coordinates are MB-relative
/// (X, Y in 0..15 and width/height in 4..16). MV is in quarter-pixel units.</summary>
public readonly record struct MvPartition(int X, int Y, int Width, int Height, int RefIdxL0, int MvL0X, int MvL0Y);

/// <summary>One motion partition within a B-slice MB. PredDir indicates which lists are active.</summary>
public readonly record struct BMvPartition(
    int X, int Y, int Width, int Height,
    BPredDir Dir,
    int RefIdxL0, int MvL0X, int MvL0Y,
    int RefIdxL1, int MvL1X, int MvL1Y);


/// <summary>
/// One parsed macroblock — fully decoded syntax + residual coefficients, but
/// NOT yet inverse-transformed or reconstructed. Allocated fresh per MB.
/// </summary>
public sealed class Macroblock
{
    public int MbAddress { get; init; }
    public IntraMbType Type { get; init; }

    /// <summary>True when this MB is a P_Skip (P-slice only): no syntax was read; MV derived per §8.4.1.1.</summary>
    public bool IsSkipped { get; init; }

    /// <summary>True when this MB is I_PCM (raw samples; no prediction/transform).</summary>
    public bool IsPcm { get; set; }

    /// <summary>I_PCM raw luma samples (16x16 raster order). Valid only when <see cref="IsPcm"/>.</summary>
    public byte[] PcmLuma { get; } = new byte[256];

    /// <summary>I_PCM raw Cb samples (8x8 raster order). Valid only when <see cref="IsPcm"/>.</summary>
    public byte[] PcmCb { get; } = new byte[64];

    /// <summary>I_PCM raw Cr samples (8x8 raster order). Valid only when <see cref="IsPcm"/>.</summary>
    public byte[] PcmCr { get; } = new byte[64];
    /// <summary>transform_size_8x8_flag (spec §7.3.5.1). When true, luma residual uses 4 8x8 blocks
    /// and (for I_NxN) Intra_8x8 prediction. Stage-(1) plumbing only — decoder currently rejects true.</summary>
    public bool TransformSize8x8 { get; set; }

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

    /// <summary>4 entries — raw Intra_8x8 prediction codewords (I_NxN with 8x8 transform):
    /// -1 means "use the neighbor-predicted mode", otherwise a 3-bit rem_mode value.</summary>
    public int[] Intra8x8PredMode { get; } = new int[4];

    /// <summary>4 entries — resolved Intra_8x8 modes (0..8) after applying the
    /// prediction-from-neighbors rule. Set by the reconstructor.</summary>
    public int[] Intra8x8Mode { get; } = new int[4];

    /// <summary>4 8x8 luma residual blocks (64 coefficients each, 8x8 zigzag scan order).</summary>
    public int[,] Luma8x8 { get; } = new int[4, 64];

    /// <summary>Per-8x8-block non-zero count summed across the 4 CAVLC sub-blocks.</summary>
    public int[] NonZeroCountLuma8x8 { get; } = new int[4];

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

    // ---- CABAC coded_block_flag tracking (per-block, used as neighbor context for §9.3.3.1.1.9) ----
    /// <summary>coded_block_flag for Intra16x16 luma DC block (single block).</summary>
    public bool LumaDcCbf { get; set; }
    /// <summary>coded_block_flag for each of the 16 4x4 luma blocks (raster scan).</summary>
    public bool[] LumaAcCbf { get; } = new bool[16];
    /// <summary>coded_block_flag for the 2 chroma DC blocks (one per component).</summary>
    public bool[] ChromaDcCbf { get; } = new bool[2];
    /// <summary>coded_block_flag for the 4 chroma AC blocks per component.</summary>
    public bool[,] ChromaAcCbf { get; } = new bool[2, 4];

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

    /// <summary>Per-4x4-block L0 motion vector difference (X), used as CABAC neighbor context for mvd parsing.</summary>
    public int[] MvdL0XBlock { get; } = new int[16];
    public int[] MvdL0YBlock { get; } = new int[16];

    /// <summary>RefIdx per 8x8 quadrant (raster scan: 0=TL, 1=TR, 2=BL, 3=BR).
    /// For mb_type 0/1/2 the refIdx is per partition; we replicate to per-quadrant.</summary>
    public int[] RefIdxL08x8 { get; } = new int[4];

    /// <summary>Motion partitions for this MB (list of (x, y, w, h, refIdx, mvX, mvY)).
    /// One entry for P_L0_16x16, two for 16x8 / 8x16, up to sixteen for P_8x8 with 4x4 sub-blocks.</summary>
    public List<MvPartition> InterPartitions { get; set; } = new();

    // ---- B-slice inter fields ----

    /// <summary>True iff this MB is a B-slice inter MB (uses BInterPartitions instead of InterPartitions).</summary>
    public bool IsBInter { get; set; }

    /// <summary>True when this MB is a B_Skip (no syntax; MV derived via direct mode).</summary>
    public bool IsBSkip { get; set; }

    /// <summary>B-slice motion partitions with both L0 and L1 fields.</summary>
    public List<BMvPartition> BInterPartitions { get; set; } = new();

    /// <summary>Per-4x4 L1 MV X (quarter-pel) and refIdx-per-quadrant (parallel to L0 arrays).</summary>
    public int[] MvL1XBlock { get; } = new int[16];
    public int[] MvL1YBlock { get; } = new int[16];
    public int[] MvdL1XBlock { get; } = new int[16];
    public int[] MvdL1YBlock { get; } = new int[16];
    public int[] RefIdxL18x8 { get; } = new int[4];

    /// <summary>Per-4x4 predFlagL0/L1 (1 if direction active, else 0). For neighbor MV-prediction
    /// context selection. For P-slice MBs predFlagL0Block[i] = 1 and predFlagL1Block[i] = 0.</summary>
    public byte[] PredFlagL0Block { get; } = new byte[16];
    public byte[] PredFlagL1Block { get; } = new byte[16];

    /// <summary>Diagnostic: bit position in the slice RBSP where this MB's parsing started/ended.</summary>
    public int ParseStartBit { get; set; }
    public int ParseEndBit { get; set; }
}
