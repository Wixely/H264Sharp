using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Mode;

/// <summary>Per-macroblock state retained across encoding so left/top neighbors can be queried
/// (mirrors the subset of decoder's Macroblock fields the encoder side needs for nC/CBP context).</summary>
internal sealed class MacroblockEncoderState
{
    public int MbAddress;
    public bool IsIntra16x16;
    /// <summary>True if this MB was emitted as I_NxN (Intra_4x4).</summary>
    public bool IsIntra4x4;
    public int CbpLuma;       // 0..15
    public int CbpChroma;     // 0..2
    public int QpY;           // current QP
    public int[] NonZeroCountLuma = new int[16];        // per 4x4 block raster
    public int[,] NonZeroCountChromaAc = new int[2, 4]; // per component, per 4x4 block
    /// <summary>Per-4x4-block actual intra prediction mode (0..8) for Intra_4x4 MBs.
    /// Default -1 for non-Intra_4x4 blocks. Used as neighbor source for predicted-mode in subsequent MBs.</summary>
    public int[] Intra4x4Mode = FilledNeg1_16();
    private static int[] FilledNeg1_16() { var a = new int[16]; for (int i = 0; i < 16; i++) a[i] = -1; return a; }
    /// <summary>Intra chroma prediction mode (used by CABAC neighbor lookups).</summary>
    public IntraChromaPredMode ChromaPredMode;
    /// <summary>Per-4x4-block AC coded_block_flag (used by CABAC residual condTerm derivation in subsequent MBs).</summary>
    public bool[] LumaAcCbf = new bool[16];
    /// <summary>Luma Intra_16x16 DC coded_block_flag.</summary>
    public bool LumaDcCbf;
    /// <summary>Per-component chroma DC coded_block_flag (used by CABAC).</summary>
    public bool[] ChromaDcCbf = new bool[2];
    /// <summary>Per-component, per-4x4-block chroma AC coded_block_flag.</summary>
    public bool[,] ChromaAcCbf = new bool[2, 4];

    /// <summary>Reconstructed Y plane for this MB (16x16, raster). Used by future neighbors for intra
    /// prediction. The encoder must match the decoder's reconstruction byte-for-byte so the predicted
    /// samples used during encoding equal those the decoder will reconstruct.</summary>
    public byte[] ReconY = new byte[256];
    public byte[] ReconU = new byte[64];
    public byte[] ReconV = new byte[64];

    // ---- Inter (P-slice) fields ----
    /// <summary>True if this MB was emitted as P_L0_16x16 (inter, not intra).</summary>
    public bool IsInterP16x16;
    /// <summary>True if this MB was emitted as any inter type (P_L0_16x16 / 16x8 / 8x16 / P_8x8 / Skip).</summary>
    public bool IsInter;
    /// <summary>True if this MB was a P_Skip (no syntax emitted; mb_skip_run counted it).</summary>
    public bool IsSkipped;
    /// <summary>Raw mb_type value (0=P_L0_16x16, 1=P_L0_L0_16x8, 2=P_L0_L0_8x16, 3=P_8x8). -1 for non-inter.</summary>
    public int RawMbType = -1;
    /// <summary>Convenience MV for the whole MB (partition 0's MV).</summary>
    public int MvL0X;
    public int MvL0Y;
    /// <summary>L0 ref index (0 — single reference frame).</summary>
    public int RefIdxL0;
    /// <summary>Per-4x4-block L0 MV (raster index 0..15) — used as neighbor MV source for the next MB's predictor.</summary>
    public int[] MvL0XBlock = new int[16];
    public int[] MvL0YBlock = new int[16];
    /// <summary>Per-8x8-quadrant L0 ref idx (raster: 0=TL, 1=TR, 2=BL, 3=BR).</summary>
    public int[] RefIdxL08x8 = new int[4];
}
