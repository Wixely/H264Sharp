using H264Decoder.Syntax;

namespace H264Decoder.Cabac;

/// <summary>
/// CABAC syntax for one I-slice macroblock (spec §7.3.5.1 + §9.3.3).
/// Supports Intra_4x4 (I_NxN, mb_type=0) and Intra_16x16 (mb_type 1..24). I_PCM not yet supported.
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
        if (mbTypeCode == 25)
        {
            return ParsePcmMb(cabac, mbAddress, qpYRunning, ref prevMbQpDeltaState);
        }
        return ParseIntraMbBody(cabac, mbTypeCode, leftMb, topMb, mbAddress,
                                ref qpYRunning, ref prevMbQpDeltaState);
    }

    /// <summary>
    /// Parse an I_PCM macroblock (spec §7.3.5.1 + §9.3.1.2): byte-align, read 256+64+64 raw
    /// samples, then re-initialize the arithmetic engine. QpY is unchanged.
    /// </summary>
    public static Macroblock ParsePcmMb(
        CabacDecoder cabac, int mbAddress, int qpYRunning, ref int prevMbQpDeltaState)
    {
        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = IntraMbType.FromISliceCodeword(25),
            IsPcm = true,
            QpY = qpYRunning,
        };
        cabac.ByteAlignBits();
        for (int i = 0; i < 256; i++) mb.PcmLuma[i] = cabac.ReadAlignedByte();
        for (int i = 0; i < 64; i++)  mb.PcmCb[i]   = cabac.ReadAlignedByte();
        for (int i = 0; i < 64; i++)  mb.PcmCr[i]   = cabac.ReadAlignedByte();
        cabac.Reinitialize();

        // Neighbor context: all NZC/cbf treated as maximum for I_PCM MBs.
        for (int i = 0; i < 16; i++) { mb.NonZeroCountLuma[i] = 16; mb.LumaAcCbf[i] = true; }
        mb.LumaDcCbf = true;
        for (int c = 0; c < 2; c++)
        {
            mb.ChromaDcCbf[c] = true;
            for (int i = 0; i < 4; i++) { mb.NonZeroCountChromaAc[c, i] = 16; mb.ChromaAcCbf[c, i] = true; }
        }
        // No mb_qp_delta consumed for I_PCM; reset the CABAC prev-state per spec.
        prevMbQpDeltaState = 0;
        return mb;
    }

    /// <summary>
    /// Parse the body of an intra MB given an already-decoded I-slice mb_type code (0..24).
    /// Callable from the P-slice intra branch after it has decoded the mb_type via ctxIdxOffset=17.
    /// </summary>
    public static Macroblock ParseIntraMbBody(
        CabacDecoder cabac,
        int mbTypeCode,
        Macroblock? leftMb,
        Macroblock? topMb,
        int mbAddress,
        ref int qpYRunning,
        ref int prevMbQpDeltaState)
    {
        var type = IntraMbType.FromISliceCodeword((uint)mbTypeCode);
        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = type,
        };

        if (type.PredMode == MbPartPredMode.Intra4x4)
        {
            // 16 luma 4x4 prediction modes (raster scan).
            for (int i = 0; i < 16; i++)
            {
                int prev = cabac.DecodeBin(68);
                if (prev == 1)
                {
                    mb.Intra4x4PredMode[i] = -1;
                }
                else
                {
                    // rem_intra4x4_pred_mode: 3 bins, all at ctx 69.
                    int r0 = cabac.DecodeBin(69);
                    int r1 = cabac.DecodeBin(69);
                    int r2 = cabac.DecodeBin(69);
                    mb.Intra4x4PredMode[i] = (r2 << 2) | (r1 << 1) | r0;
                }
            }
        }
        else
        {
            mb.CbpLuma = type.CbpLuma;
            mb.CbpChroma = type.CbpChroma;
        }

        // intra_chroma_pred_mode
        mb.ChromaPredMode = (IntraChromaPredMode)DecodeIntraChromaPredMode(cabac, leftMb, topMb);

        if (type.PredMode == MbPartPredMode.Intra4x4)
        {
            // coded_block_pattern parsed separately for I_NxN.
            int cbpLuma = DecodeCbpLumaIntra(cabac, leftMb, topMb);
            int cbpChroma = DecodeCbpChromaIntra(cabac, leftMb, topMb);
            mb.CbpLuma = cbpLuma;
            mb.CbpChroma = cbpChroma;

            if (cbpLuma != 0 || cbpChroma != 0)
            {
                int mbQpDelta = CabacCommon.DecodeMbQpDelta(cabac, ref prevMbQpDeltaState);
                qpYRunning = CabacCommon.Mod52(qpYRunning + mbQpDelta);
                mb.QpY = qpYRunning;
                ReadResidualIntra4x4(cabac, mb, leftMb, topMb);
            }
            else
            {
                mb.QpY = qpYRunning;
                prevMbQpDeltaState = 0;
            }
        }
        else
        {
            // Intra_16x16: qp_delta + residual always present (luma DC).
            int mbQpDelta = CabacCommon.DecodeMbQpDelta(cabac, ref prevMbQpDeltaState);
            qpYRunning = CabacCommon.Mod52(qpYRunning + mbQpDelta);
            mb.QpY = qpYRunning;
            ReadResidualIntra16x16(cabac, mb, leftMb, topMb);
        }
        return mb;
    }

    // ---------------------------------------------------------------------
    // mb_type (Table 9-36 + Table 9-39, ctxIdxOffset=3)
    // ---------------------------------------------------------------------
    private static int DecodeMbTypeI(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        int condA = (leftMb != null && IsNonINxNIntra(leftMb)) ? 1 : 0;
        int condB = (topMb != null && IsNonINxNIntra(topMb)) ? 1 : 0;

        int b0 = cabac.DecodeBin(3 + condA + condB);
        if (b0 == 0) return 0; // I_NxN

        if (cabac.DecodeTerminate() == 1) return 25; // I_PCM

        int mbType = 1;
        if (cabac.DecodeBin(6) == 1) mbType += 12;

        if (cabac.DecodeBin(7) == 1)
        {
            if (cabac.DecodeBin(8) == 1) mbType += 8;
            else mbType += 4;
        }
        if (cabac.DecodeBin(9) == 1) mbType += 2;
        if (cabac.DecodeBin(10) == 1) mbType += 1;
        return mbType;
    }

    /// <summary>
    /// Decode the intra mb_type suffix when reached from a non-I slice (offset=17 for P/SP).
    /// Caller has already decoded the "is intra" prefix bin. Returns I-slice mb_type 0..25.
    /// ctxIdxInc for the bins after the intra prefix: 0 (I_NxN flag), terminate, 1, 2, 2, 3, 3.
    /// </summary>
    public static int DecodeIntraMbTypeAtOffset(CabacDecoder cabac, int ctxIdxOffset)
    {
        int b0 = cabac.DecodeBin(ctxIdxOffset);
        if (b0 == 0) return 0; // I_NxN

        if (cabac.DecodeTerminate() == 1) return 25; // I_PCM

        int mbType = 1;
        if (cabac.DecodeBin(ctxIdxOffset + 1) == 1) mbType += 12;

        if (cabac.DecodeBin(ctxIdxOffset + 2) == 1)
        {
            if (cabac.DecodeBin(ctxIdxOffset + 2) == 1) mbType += 8;
            else mbType += 4;
        }
        if (cabac.DecodeBin(ctxIdxOffset + 3) == 1) mbType += 2;
        if (cabac.DecodeBin(ctxIdxOffset + 3) == 1) mbType += 1;
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
    // coded_block_pattern (intra MB; ctxIdxOffset luma=73, chroma=77/81).
    // ---------------------------------------------------------------------
    private static int DecodeCbpLumaIntra(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        int cbp = 0;
        for (int i = 0; i < 4; i++)
        {
            int cx = i & 1, cy = i >> 1;

            int condA;
            if (cx > 0) { int nb = cy * 2 + (cx - 1); condA = ((cbp >> nb) & 1) == 0 ? 1 : 0; }
            else if (leftMb == null || leftMb.IsSkipped) condA = 0;
            else { int extBit = (leftMb.CbpLuma >> (cy * 2 + 1)) & 1; condA = extBit == 0 ? 1 : 0; }

            int condB;
            if (cy > 0) { int nb = (cy - 1) * 2 + cx; condB = ((cbp >> nb) & 1) == 0 ? 1 : 0; }
            else if (topMb == null || topMb.IsSkipped) condB = 0;
            else { int extBit = (topMb.CbpLuma >> (2 + cx)) & 1; condB = extBit == 0 ? 1 : 0; }

            int bit = cabac.DecodeBin(73 + condA + 2 * condB);
            cbp |= bit << i;
        }
        return cbp;
    }

    private static int DecodeCbpChromaIntra(CabacDecoder cabac, Macroblock? leftMb, Macroblock? topMb)
    {
        int condA0 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma != 0) ? 1 : 0;
        int condB0 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma != 0) ? 1 : 0;
        int b0 = cabac.DecodeBin(77 + condA0 + 2 * condB0);
        if (b0 == 0) return 0;

        int condA1 = (leftMb != null && !leftMb.IsSkipped && leftMb.CbpChroma == 2) ? 1 : 0;
        int condB1 = (topMb != null && !topMb.IsSkipped && topMb.CbpChroma == 2) ? 1 : 0;
        int b1 = cabac.DecodeBin(81 + condA1 + 2 * condB1);
        return b1 == 1 ? 2 : 1;
    }

    // ---------------------------------------------------------------------
    // I_NxN residual: 16 luma 4x4 blocks (ctxBlockCat=2) gated by CBP,
    // plus chroma DC/AC. Unavailable neighbor condTermFlag = 1 (intra).
    // ---------------------------------------------------------------------
    private static void ReadResidualIntra4x4(
        CabacDecoder cabac, Macroblock mb, Macroblock? leftMb, Macroblock? topMb)
    {
        Span<int> coeffs = stackalloc int[16];

        for (int i = 0; i < 16; i++)
        {
            bool blockCoded = (mb.CbpLuma & (1 << (i >> 2))) != 0;
            if (!blockCoded)
            {
                mb.LumaAcCbf[i] = false;
                continue;
            }
            (int cA, int cB) = LumaAcNeighborCbfIntra(i, mb, leftMb, topMb);
            bool acCbf = CabacResidual.ReadResidualBlock(
                cabac, coeffs, maxNumCoeff: 16, ctxBlockCat: CabacResidual.CatLuma4x4,
                condTermFlagA: cA, condTermFlagB: cB);
            mb.LumaAcCbf[i] = acCbf;
            if (acCbf)
            {
                mb.NonZeroCountLuma[i] = 1;
                for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
            }
        }

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

    // ---------------------------------------------------------------------
    // Intra_16x16 residual: DC block + (optionally) 16 AC blocks + chroma DC/AC
    // ---------------------------------------------------------------------
    private static void ReadResidualIntra16x16(
        CabacDecoder cabac, Macroblock mb, Macroblock? leftMb, Macroblock? topMb)
    {
        Span<int> coeffs = stackalloc int[16];

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

        if (mb.CbpLuma != 0)
        {
            for (int i = 0; i < 16; i++)
            {
                (int cA, int cB) = LumaAcNeighborCbfIntra(i, mb, leftMb, topMb);
                bool acCbf = CabacResidual.ReadResidualBlock(
                    cabac, coeffs, maxNumCoeff: 15, ctxBlockCat: CabacResidual.CatIntra16x16Ac,
                    condTermFlagA: cA, condTermFlagB: cB);
                mb.LumaAcCbf[i] = acCbf;
                if (acCbf)
                {
                    mb.NonZeroCountLuma[i] = 1;
                    for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
                }
            }
        }

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

    private static readonly (int X, int Y)[] LumaBlockPos = MacroblockParser.LumaBlockPos;
    private static int SpatialToRaster(int x, int y) => MacroblockParser.SpatialToRaster(x, y);

    private static (int A, int B) LumaAcNeighborCbfIntra(int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
        (int x, int y) = LumaBlockPos[blockIdx];

        int condA;
        if (x > 0) condA = cur.LumaAcCbf[SpatialToRaster(x - 1, y)] ? 1 : 0;
        else if (leftMb == null) condA = 1;
        else condA = leftMb.LumaAcCbf[SpatialToRaster(3, y)] ? 1 : 0;

        int condB;
        if (y > 0) condB = cur.LumaAcCbf[SpatialToRaster(x, y - 1)] ? 1 : 0;
        else if (topMb == null) condB = 1;
        else condB = topMb.LumaAcCbf[SpatialToRaster(x, 3)] ? 1 : 0;

        return (condA, condB);
    }

    private static (int A, int B) ChromaAcNeighborCbf(int comp, int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
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
