namespace H264Decoder.Encoder.Mode;

/// <summary>Per-macroblock state retained across encoding so left/top neighbors can be queried
/// (mirrors the subset of decoder's Macroblock fields the encoder side needs for nC/CBP context).</summary>
internal sealed class MacroblockEncoderState
{
    public int MbAddress;
    public bool IsIntra16x16;
    public int CbpLuma;       // 0..15
    public int CbpChroma;     // 0..2
    public int QpY;           // current QP
    public int[] NonZeroCountLuma = new int[16];        // per 4x4 block raster
    public int[,] NonZeroCountChromaAc = new int[2, 4]; // per component, per 4x4 block

    /// <summary>Reconstructed Y plane for this MB (16x16, raster). Used by future neighbors for intra
    /// prediction. The encoder must match the decoder's reconstruction byte-for-byte so the predicted
    /// samples used during encoding equal those the decoder will reconstruct.</summary>
    public byte[] ReconY = new byte[256];
    public byte[] ReconU = new byte[64];
    public byte[] ReconV = new byte[64];

    // ---- Inter (P-slice) fields ----
    /// <summary>True if this MB was emitted as P_L0_16x16 (inter, not intra).</summary>
    public bool IsInterP16x16;
    /// <summary>True if this MB was a P_Skip (no syntax emitted; mb_skip_run counted it).</summary>
    public bool IsSkipped;
    /// <summary>L0 motion vector for the whole 16x16 partition (quarter-pel units).</summary>
    public int MvL0X;
    public int MvL0Y;
    /// <summary>L0 ref index (0 in phase 2 — single reference frame).</summary>
    public int RefIdxL0;
}
