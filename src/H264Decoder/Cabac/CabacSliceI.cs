using H264Decoder.Syntax;

namespace H264Decoder.Cabac;

/// <summary>
/// CABAC syntax for one I-slice macroblock (spec §7.3.5.1 + §9.3.3).
/// Currently supports Intra_16x16 mb_type (1..24); I_NxN and I_PCM throw NotSupported.
/// </summary>
internal static class CabacSliceI
{
    /// <summary>Parse one I-slice MB into a fully-populated Macroblock (no reconstruction).</summary>
    public static Macroblock ParseMb(
        CabacDecoder cabac,
        Macroblock? leftMb,
        Macroblock? topMb,
        int mbAddress,
        ref int qpYRunning,
        ref int prevMbQpDeltaState)
    {
        int mbTypeCode = DecodeMbTypeI(cabac, leftMb, topMb);
        if (mbTypeCode == 0)
        {
            throw new NotSupportedException("CABAC I_NxN (Intra_4x4) not yet implemented");
        }
        if (mbTypeCode == 25)
        {
            throw new NotSupportedException("CABAC I_PCM not yet implemented");
        }

        var type = IntraMbType.FromISliceCodeword((uint)mbTypeCode);
        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = type,
        };
        mb.CbpLuma = type.CbpLuma;
        mb.CbpChroma = type.CbpChroma;

        // intra_chroma_pred_mode
        mb.ChromaPredMode = (IntraChromaPredMode)DecodeIntraChromaPredMode(cabac, leftMb, topMb);

        // mb_qp_delta + residual: for Intra_16x16 always present (DC block at minimum).
        int mbQpDelta = DecodeMbQpDelta(cabac, ref prevMbQpDeltaState);
        qpYRunning = Mod52(qpYRunning + mbQpDelta);
        mb.QpY = qpYRunning;

        ReadResidualIntra16x16(cabac, mb, leftMb, topMb);
        return mb;
    }

    // ---------------------------------------------------------------------
    // mb_type (Table 9-36 + Table 9-39, ctxIdxOffset=3)
    // ---------------------------------------------------------------------
    private static int DecodeMbTypeI(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        // condTermFlagN = (mbN available) && (mbN is intra) && (mbN is NOT I_NxN)
        int condA = (leftMb != null && IsNonINxNIntra(leftMb)) ? 1 : 0;
        int condB = (topMb != null && IsNonINxNIntra(topMb)) ? 1 : 0;

        int b0 = cabac.DecodeBin(3 + condA + condB);
        if (b0 == 0) return 0; // I_NxN

        if (cabac.DecodeTerminate() == 1) return 25; // I_PCM

        int mbType = 1;
        if (cabac.DecodeBin(6) == 1) mbType += 12;      // CodedBlockPatternLuma flag (cbpL = 15)

        if (cabac.DecodeBin(7) == 1)
        {
            if (cabac.DecodeBin(8) == 1) mbType += 8;   // cbpC = 2
            else mbType += 4;                            // cbpC = 1
        }
        if (cabac.DecodeBin(9) == 1) mbType += 2;       // Intra16x16PredMode bit 1
        if (cabac.DecodeBin(10) == 1) mbType += 1;      // Intra16x16PredMode bit 0
        return mbType;
    }

    private static bool IsNonINxNIntra(Macroblock mb)
    {
        var pm = mb.Type.PredMode;
        return (pm == MbPartPredMode.Intra16x16 || pm == MbPartPredMode.IPcm);
    }

    // ---------------------------------------------------------------------
    // intra_chroma_pred_mode (TU max=3, ctxIdxOffset=64)
    // ---------------------------------------------------------------------
    private static int DecodeIntraChromaPredMode(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        // condTermFlagN = (mbN available) && (mbN is intra) && (mbN is NOT I_PCM)
        //               && (IntraChromaPredMode(N) != 0)
        int condA = (leftMb != null && IsIntraNonPcm(leftMb)
                     && leftMb.ChromaPredMode != IntraChromaPredMode.Dc) ? 1 : 0;
        int condB = (topMb != null && IsIntraNonPcm(topMb)
                     && topMb.ChromaPredMode != IntraChromaPredMode.Dc) ? 1 : 0;

        int b0 = cabac.DecodeBin(64 + condA + condB);
        if (b0 == 0) return 0;
        int b1 = cabac.DecodeBin(67);
        if (b1 == 0) return 1;
        int b2 = cabac.DecodeBin(67);
        return b2 == 0 ? 2 : 3;
    }

    private static bool IsIntraNonPcm(Macroblock mb)
    {
        var pm = mb.Type.PredMode;
        return pm == MbPartPredMode.Intra4x4 || pm == MbPartPredMode.Intra16x16;
    }

    // ---------------------------------------------------------------------
    // mb_qp_delta (ctxIdxOffset=60; binarization: signed unary)
    // ---------------------------------------------------------------------
    private static int DecodeMbQpDelta(CabacDecoder cabac, ref int prevNonZeroState)
    {
        int b = cabac.DecodeBin(60 + prevNonZeroState);
        if (b == 0)
        {
            prevNonZeroState = 0;
            return 0;
        }
        int n = 1;
        int next = cabac.DecodeBin(62);
        while (next == 1)
        {
            n++;
            if (n > 60) throw new InvalidDataException("mb_qp_delta unary runaway");
            next = cabac.DecodeBin(63);
        }
        prevNonZeroState = 1;
        // Signed mapping: 0→0, 1→1, 2→-1, 3→2, 4→-2 ...
        return (n & 1) == 1 ? (n + 1) / 2 : -(n / 2);
    }

    private static int Mod52(int v)
    {
        int r = v % 52;
        return r < 0 ? r + 52 : r;
    }

    // ---------------------------------------------------------------------
    // Intra_16x16 residual: DC block + (optionally) 16 AC blocks + chroma DC/AC
    // ---------------------------------------------------------------------
    private static void ReadResidualIntra16x16(
        CabacDecoder cabac, Macroblock mb, Macroblock? leftMb, Macroblock? topMb)
    {
        Span<int> coeffs = stackalloc int[16];

        // ---- Luma DC (16 coeffs, ctxBlockCat=0) ----
        // For Intra16x16 DC block: neighbor cbf comes from neighbor's LumaDcCbf field.
        // For unavailable neighbor in an intra MB, condTermFlag=1.
        int condA = (leftMb == null) ? 1 : (leftMb.LumaDcCbf ? 1 : 0);
        int condB = (topMb == null) ? 1 : (topMb.LumaDcCbf ? 1 : 0);
        bool dcCbf = CabacResidual.ReadResidualBlock(
            cabac, coeffs, maxNumCoeff: 16, ctxBlockCat: CabacResidual.CatIntra16x16Dc,
            condTermFlagA: condA, condTermFlagB: condB);
        mb.LumaDcCbf = dcCbf;
        if (dcCbf)
        {
            for (int j = 0; j < 16; j++) mb.LumaDc[j] = coeffs[j];
        }

        // ---- Luma AC (only if CbpLuma != 0; 16 blocks, ctxBlockCat=1) ----
        if (mb.CbpLuma != 0)
        {
            for (int i = 0; i < 16; i++)
            {
                // Each 4x4 luma block has its own AC residual. ctxIdxInc neighbors come from
                // luma 4x4 block left/top: use the per-block LumaAcCbf tracking.
                (int cA, int cB) = LumaAcNeighborCbf(i, mb, leftMb, topMb);
                bool acCbf = CabacResidual.ReadResidualBlock(
                    cabac, coeffs, maxNumCoeff: 15, ctxBlockCat: CabacResidual.CatIntra16x16Ac,
                    condTermFlagA: cA, condTermFlagB: cB);
                mb.LumaAcCbf[i] = acCbf;
                if (acCbf)
                {
                    mb.NonZeroCountLuma[i] = 1; // marker — only presence is consumed downstream
                    for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
                }
            }
        }

        // ---- Chroma DC (4 coeffs each, ctxBlockCat=3) ----
        if ((mb.CbpChroma & 3) != 0)
        {
            Span<int> dcCoeffs = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                int caC = (leftMb == null) ? 1 : (leftMb.ChromaDcCbf[c] ? 1 : 0);
                int cbC = (topMb == null) ? 1 : (topMb.ChromaDcCbf[c] ? 1 : 0);
                bool cbf = CabacResidual.ReadResidualBlock(
                    cabac, dcCoeffs, maxNumCoeff: 4, ctxBlockCat: CabacResidual.CatChromaDc,
                    condTermFlagA: caC, condTermFlagB: cbC);
                mb.ChromaDcCbf[c] = cbf;
                if (cbf)
                {
                    for (int j = 0; j < 4; j++) mb.ChromaDc[c, j] = dcCoeffs[j];
                }
            }
        }

        // ---- Chroma AC (4 blocks per component, ctxBlockCat=4) ----
        if ((mb.CbpChroma & 2) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    (int cA, int cB) = ChromaAcNeighborCbf(c, i, mb, leftMb, topMb);
                    bool acCbf = CabacResidual.ReadResidualBlock(
                        cabac, coeffs, maxNumCoeff: 15, ctxBlockCat: CabacResidual.CatChromaAc,
                        condTermFlagA: cA, condTermFlagB: cB);
                    mb.ChromaAcCbf[c, i] = acCbf;
                    if (acCbf)
                    {
                        mb.NonZeroCountChromaAc[c, i] = 1;
                        for (int j = 0; j < 16; j++) mb.ChromaAc[c, i, j] = coeffs[j];
                    }
                }
            }
        }
    }

    // Per-4x4-block luma raster scan position → spatial (x, y) in MB.
    private static readonly (int X, int Y)[] LumaBlockPos = MacroblockParser.LumaBlockPos;
    private static int SpatialToRaster(int x, int y) => MacroblockParser.SpatialToRaster(x, y);

    private static (int A, int B) LumaAcNeighborCbf(int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
        (int x, int y) = LumaBlockPos[blockIdx];

        int condA;
        if (x > 0) condA = cur.LumaAcCbf[SpatialToRaster(x - 1, y)] ? 1 : 0;
        else if (leftMb == null) condA = 1; // intra MB, unavailable neighbor
        else condA = leftMb.LumaAcCbf[SpatialToRaster(3, y)] ? 1 : 0;

        int condB;
        if (y > 0) condB = cur.LumaAcCbf[SpatialToRaster(x, y - 1)] ? 1 : 0;
        else if (topMb == null) condB = 1;
        else condB = topMb.LumaAcCbf[SpatialToRaster(x, 3)] ? 1 : 0;

        return (condA, condB);
    }

    private static (int A, int B) ChromaAcNeighborCbf(int comp, int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
        // 2x2 chroma block grid layout:
        //   0 1
        //   2 3
        int x = blockIdx & 1;
        int y = (blockIdx >> 1) & 1;

        int condA;
        if (x > 0) condA = cur.ChromaAcCbf[comp, blockIdx - 1] ? 1 : 0;
        else if (leftMb == null) condA = 1;
        else condA = leftMb.ChromaAcCbf[comp, blockIdx + 1] ? 1 : 0;

        int condB;
        if (y > 0) condB = cur.ChromaAcCbf[comp, blockIdx - 2] ? 1 : 0;
        else if (topMb == null) condB = 1;
        else condB = topMb.ChromaAcCbf[comp, blockIdx + 2] ? 1 : 0;

        return (condA, condB);
    }
}
