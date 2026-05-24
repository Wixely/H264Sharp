using H264Decoder.Cabac;
using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Mode;
using H264Decoder.Encoder.Transform;
using H264Decoder.Prediction;
using H264Decoder.Syntax;
using H264Decoder.Transform;

namespace H264Decoder.Encoder.Cabac;

/// <summary>Encode one Intra_16x16 macroblock as CABAC (entropy_coding_mode_flag=1). The
/// transform/quant/prediction pipeline mirrors the existing CAVLC path; only the
/// entropy coding stage switches from CAVLC to CABAC. Used by H264FrameEncoder
/// when EnableCabac is true and the chosen MB mode is Intra_16x16.</summary>
internal static class CabacMbEncoder
{
    public static readonly (int X, int Y)[] LumaBlockPos = MacroblockParser.LumaBlockPos;
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>Encode one I-slice Intra_16x16 macroblock through CABAC.</summary>
    public static void EncodeIntra16x16(
        CabacEncoder cabac,
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] picY, byte[] picU, byte[] picV,
        int picStrideY, int picStrideC,
        int mbX, int mbY, int mbsPerRow,
        int qpY,
        MacroblockEncoderState?[] mbStates,
        int mbAddress,
        ref int prevMbQpDeltaState)
    {
        var leftMb = mbX > 0 ? mbStates[mbAddress - 1] : null;
        var topMb = mbY > 0 ? mbStates[mbAddress - mbsPerRow] : null;

        // ---- Reuse Intra_16x16 mode + transform/quant pipeline. ----
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[16];
        bool topAvail = topMb != null;
        bool leftAvail = leftMb != null;
        bool topLeftAvail = topAvail && leftAvail;
        byte topLeft = 0;
        if (topAvail)
        {
            int srcRow = mbY * 16 - 1;
            int srcCol0 = mbX * 16;
            for (int i = 0; i < 16; i++) top[i] = picY[srcRow * picStrideY + srcCol0 + i];
        }
        if (leftAvail)
        {
            int srcCol = mbX * 16 - 1;
            int srcRow0 = mbY * 16;
            for (int i = 0; i < 16; i++) left[i] = picY[(srcRow0 + i) * picStrideY + srcCol];
        }
        if (topLeftAvail) topLeft = picY[(mbY * 16 - 1) * picStrideY + (mbX * 16 - 1)];

        Span<byte> srcLuma = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                srcLuma[y * 16 + x] = srcY[y * srcStrideY + x];

        Intra16x16PredMode bestMode = Intra16x16PredMode.Dc;
        int bestSad = int.MaxValue;
        Span<byte> predBest = stackalloc byte[256];
        Span<byte> predTry = stackalloc byte[256];
        bool[] modeOk = { topAvail, leftAvail, true, topAvail && leftAvail && topLeftAvail };
        for (int m = 0; m < 4; m++)
        {
            if (!modeOk[m]) continue;
            IntraPrediction.PredictIntra16x16(
                (Intra16x16PredMode)m,
                top, topAvail, left, leftAvail, topLeft, topLeftAvail,
                predTry);
            int sad = 0;
            for (int i = 0; i < 256; i++) sad += Math.Abs(srcLuma[i] - predTry[i]);
            if (sad < bestSad) { bestSad = sad; bestMode = (Intra16x16PredMode)m; predTry.CopyTo(predBest); }
        }

        // Forward DCT + DC-Hadamard + quant (mirror MacroblockEncoder.EncodeIntra16x16).
        Span<int> luma4x4 = stackalloc int[256];
        Span<int> dcMatrix = stackalloc int[16];
        Span<int> block = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int sx = bx * 4 + xx;
                    int sy = by * 4 + yy;
                    block[yy * 4 + xx] = srcLuma[sy * 16 + sx] - predBest[sy * 16 + sx];
                }
            ForwardTransform.Forward4x4(block);
            dcMatrix[by * 4 + bx] = block[0];
            for (int k = 0; k < 16; k++) luma4x4[i * 16 + k] = block[k];
        }
        ForwardTransform.ForwardHadamard4x4(dcMatrix);
        ForwardQuantization.QuantLumaDc(dcMatrix, qpY);
        for (int i = 0; i < 16; i++)
        {
            Span<int> ac = luma4x4.Slice(i * 16, 16);
            int savedDc = ac[0]; ac[0] = 0;
            ForwardQuantization.Quant4x4Ac(ac, qpY, intra: true);
            ac[0] = savedDc;
        }

        // Chroma encoding (delegates to existing helper).
        Span<byte> srcCb = stackalloc byte[64];
        Span<byte> srcCr = stackalloc byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                srcCb[y * 8 + x] = srcU[y * srcStrideC + x];
                srcCr[y * 8 + x] = srcV[y * srcStrideC + x];
            }
        var chroma = MacroblockEncoder.EncodeChromaPublic(srcCb, srcCr, picU, picV, picStrideC,
            mbX, mbY, qpY, leftMb, topMb);

        // CbpLuma for Intra_16x16: 0 or 15 (AC presence is all-or-nothing).
        int cbpLumaAny = 0;
        for (int i = 0; i < 16; i++)
        {
            for (int k = 1; k < 16; k++)
                if (luma4x4[i * 16 + k] != 0) { cbpLumaAny = 15; break; }
            if (cbpLumaAny == 15) break;
        }
        int cbpLuma = cbpLumaAny;
        int cbpChroma = chroma.CbpChroma;
        int p = (int)bestMode;
        int group = (cbpLuma == 15) ? (3 + cbpChroma) : (cbpChroma);
        int mbType = 1 + group * 4 + p;

        // ---- Emit CABAC syntax ----
        CabacEncSlice.EncodeMbTypeI(cabac, mbType, leftMb, topMb);
        CabacEncSlice.EncodeIntraChromaPredMode(cabac, (int)chroma.ChromaMode, leftMb, topMb);
        // For Intra_16x16, no separate CBP — encoded in mb_type. qp_delta always present.
        CabacEncSlice.EncodeMbQpDelta(cabac, 0, ref prevMbQpDeltaState);

        // ---- Encode CABAC residual: luma DC (Cat=0), then luma AC (Cat=1), then chroma DC/AC. ----
        Span<int> dcScan = stackalloc int[16];
        for (int s = 0; s < 16; s++) dcScan[s] = dcMatrix[ZigZag4x4[s]];

        var state = new MacroblockEncoderState
        {
            MbAddress = mbAddress,
            IsIntra16x16 = true,
            CbpLuma = cbpLuma,
            CbpChroma = cbpChroma,
            QpY = qpY,
            ChromaPredMode = chroma.ChromaMode,
        };

        // Luma DC: condA/condB from neighbors' LumaDcCbf; unavailable neighbor → 1 (intra default).
        int dcCondA = (leftMb == null) ? 1 : (leftMb.LumaDcCbf ? 1 : 0);
        int dcCondB = (topMb == null) ? 1 : (topMb.LumaDcCbf ? 1 : 0);
        bool dcHasAny = CabacEncResidual.EncodeResidualBlock(
            cabac, dcScan, maxNumCoeff: 16, ctxBlockCat: CabacEncResidual.CatIntra16x16Dc,
            condTermFlagA: dcCondA, condTermFlagB: dcCondB);
        state.LumaDcCbf = dcHasAny;

        if (cbpLuma != 0)
        {
            Span<int> acScan = stackalloc int[15];
            for (int i = 0; i < 16; i++)
            {
                Span<int> acRaster = luma4x4.Slice(i * 16, 16);
                for (int s = 1; s < 16; s++) acScan[s - 1] = acRaster[ZigZag4x4[s]];
                (int cA, int cB) = LumaAcNeighborCbfIntra(i, state, leftMb, topMb);
                bool acHasAny = CabacEncResidual.EncodeResidualBlock(
                    cabac, acScan, maxNumCoeff: 15, ctxBlockCat: CabacEncResidual.CatIntra16x16Ac,
                    condTermFlagA: cA, condTermFlagB: cB);
                state.LumaAcCbf[i] = acHasAny;
                if (acHasAny) state.NonZeroCountLuma[i] = 1;
            }
        }

        if ((cbpChroma & 3) != 0)
        {
            Span<int> dcChroma = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dcChroma[k] = chroma.ChromaDc[c, k];
                int caC = (leftMb == null) ? 1 : (leftMb.ChromaDcCbf[c] ? 1 : 0);
                int cbC = (topMb == null) ? 1 : (topMb.ChromaDcCbf[c] ? 1 : 0);
                bool cbf = CabacEncResidual.EncodeResidualBlock(
                    cabac, dcChroma, maxNumCoeff: 4, ctxBlockCat: CabacEncResidual.CatChromaDc,
                    condTermFlagA: caC, condTermFlagB: cbC);
                state.ChromaDcCbf[c] = cbf;
            }
        }

        if ((cbpChroma & 2) != 0)
        {
            Span<int> ac = stackalloc int[15];
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int k = 0; k < 15; k++) ac[k] = chroma.ChromaAc[c, i, k];
                    (int cA, int cB) = ChromaAcNeighborCbfIntra(c, i, state, leftMb, topMb);
                    bool cbf = CabacEncResidual.EncodeResidualBlock(
                        cabac, ac, maxNumCoeff: 15, ctxBlockCat: CabacEncResidual.CatChromaAc,
                        condTermFlagA: cA, condTermFlagB: cB);
                    state.ChromaAcCbf[c, i] = cbf;
                    if (cbf) state.NonZeroCountChromaAc[c, i] = 1;
                }
            }
        }

        // ---- Reconstruct + write into picture (mirror MacroblockEncoder.ReconstructLumaIntra16x16). ----
        ReconstructLumaIntra16x16(predBest, dcMatrix, luma4x4, qpY,
            picY, picStrideY, mbX, mbY, state.ReconY);
        chroma.ReconU.CopyTo(state.ReconU, 0);
        chroma.ReconV.CopyTo(state.ReconV, 0);
        int cmbX = mbX * 8, cmbY = mbY * 8;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                picU[(cmbY + y) * picStrideC + cmbX + x] = state.ReconU[y * 8 + x];
                picV[(cmbY + y) * picStrideC + cmbX + x] = state.ReconV[y * 8 + x];
            }

        mbStates[mbAddress] = state;
    }

    private static void ReconstructLumaIntra16x16(
        ReadOnlySpan<byte> pred, Span<int> dcMatrixQ, Span<int> luma4x4Raster, int qpY,
        byte[] picY, int picStrideY, int mbX, int mbY, byte[] reconYOut)
    {
        Span<int> dc = stackalloc int[16];
        for (int i = 0; i < 16; i++) dc[i] = dcMatrixQ[i];
        InverseTransform.InverseHadamard4x4(dc);
        Quantization_DequantPublic.DequantLumaDc(dc, qpY);

        Span<int> coeffsRaster = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            coeffsRaster.Clear();
            for (int k = 1; k < 16; k++) coeffsRaster[k] = luma4x4Raster[i * 16 + k];
            coeffsRaster[0] = dc[by * 4 + bx];
            int saved = coeffsRaster[0]; coeffsRaster[0] = 0;
            Quantization_DequantPublic.Dequant4x4Ac(coeffsRaster, qpY);
            coeffsRaster[0] = saved;
            InverseTransform.Inverse4x4(coeffsRaster);
            int px0 = mbX * 16 + bx * 4;
            int py0 = mbY * 16 + by * 4;
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int v = pred[(by * 4 + yy) * 16 + (bx * 4 + xx)] + coeffsRaster[yy * 4 + xx];
                    byte clipped = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    picY[(py0 + yy) * picStrideY + (px0 + xx)] = clipped;
                    reconYOut[(by * 4 + yy) * 16 + (bx * 4 + xx)] = clipped;
                }
        }
    }

    private static (int A, int B) LumaAcNeighborCbfIntra(int blockIdx, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        (int x, int y) = LumaBlockPos[blockIdx];
        int condA;
        if (x > 0) condA = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x - 1, y)] ? 1 : 0;
        else if (leftMb == null) condA = 1;
        else condA = leftMb.LumaAcCbf[MacroblockParser.SpatialToRaster(3, y)] ? 1 : 0;
        int condB;
        if (y > 0) condB = cur.LumaAcCbf[MacroblockParser.SpatialToRaster(x, y - 1)] ? 1 : 0;
        else if (topMb == null) condB = 1;
        else condB = topMb.LumaAcCbf[MacroblockParser.SpatialToRaster(x, 3)] ? 1 : 0;
        return (condA, condB);
    }

    private static (int A, int B) ChromaAcNeighborCbfIntra(int comp, int blockIdx, MacroblockEncoderState cur,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
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

    /// <summary>Encode one I_NxN (Intra_4x4) macroblock as CABAC. The prediction +
    /// transform/quant/reconstruction pipeline is shared with the CAVLC Intra_4x4 path via
    /// <see cref="IntraEncoder4x4.PrepareIntra4x4"/>; only the syntax stage differs. Inverse of
    /// decoder's <c>CabacSliceI.ParseIntraMbBody</c> Intra_4x4 branch.</summary>
    public static void EncodeIntra4x4(
        CabacEncoder cabac,
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] picY, byte[] picU, byte[] picV,
        int picStrideY, int picStrideC,
        int mbX, int mbY, int mbsPerRow,
        int qpY,
        MacroblockEncoderState?[] mbStates,
        int mbAddress,
        ref int prevMbQpDeltaState)
    {
        var prep = IntraEncoder4x4.PrepareIntra4x4(
            srcY, srcU, srcV, srcStrideY, srcStrideC,
            picY, picU, picV, picStrideY, picStrideC,
            mbX, mbY, mbsPerRow, qpY, mbStates, mbAddress);
        var state = prep.State;
        var leftMb = prep.LeftMb;
        var topMb = prep.TopMb;
        int cbpLuma = prep.CbpLuma;
        int cbpChroma = prep.CbpChroma;
        var chroma = prep.Chroma;

        // ---- Syntax: mb_type=0 (I_NxN) ----
        CabacEncSlice.EncodeMbTypeI(cabac, mbType: 0, leftMb, topMb);

        // ---- 16 × (prev_intra4x4_pred_mode_flag, [rem_intra4x4_pred_mode]) ----
        // Spec §8.3.1.1: predicted mode = min(neighborLeft, neighborTop) with -1 sentinel
        // collapsing to DC. We compare actualMode vs predicted: equal → flag=1, otherwise
        // flag=0 + 3 bits rem = chosen<predicted ? chosen : chosen-1.
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = IntraEncoder4x4.LumaBlockPos[i];
            int predicted = IntraEncoder4x4.PredictIntra4x4ModeFromNeighbors(state, leftMb, topMb, bx, by);
            int chosen = prep.ActualMode[i];
            if (chosen == predicted)
            {
                CabacEncSlice.EncodePrevIntra4x4PredModeFlag(cabac, useNeighborPrediction: true);
            }
            else
            {
                CabacEncSlice.EncodePrevIntra4x4PredModeFlag(cabac, useNeighborPrediction: false);
                int rem = chosen < predicted ? chosen : chosen - 1;
                CabacEncSlice.EncodeRemIntra4x4PredMode(cabac, rem);
            }
        }

        // ---- intra_chroma_pred_mode ----
        CabacEncSlice.EncodeIntraChromaPredMode(cabac, (int)chroma.ChromaMode, leftMb, topMb);

        // ---- coded_block_pattern (luma 4 bins + chroma 1..2 bins) ----
        CabacEncSlice.EncodeCbpLumaIntra(cabac, cbpLuma, leftMb, topMb);
        CabacEncSlice.EncodeCbpChromaIntra(cabac, cbpChroma, leftMb, topMb);

        // ---- mb_qp_delta + residual (only when cbp != 0) ----
        bool hasResidual = (cbpLuma != 0) || (cbpChroma != 0);
        if (!hasResidual)
        {
            // Decoder resets prevMbQpDeltaState=0 when no qp_delta is read; mirror that.
            prevMbQpDeltaState = 0;
            mbStates[mbAddress] = state;
            return;
        }
        CabacEncSlice.EncodeMbQpDelta(cabac, mbQpDelta: 0, ref prevMbQpDeltaState);

        // ---- Luma 4x4 residual: 16 blocks, gated by cbpLuma bit per 8x8 quadrant. ----
        Span<int> blockScan = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            bool quadCoded = (cbpLuma & (1 << (i >> 2))) != 0;
            if (!quadCoded)
            {
                state.LumaAcCbf[i] = false;
                state.NonZeroCountLuma[i] = 0;
                continue;
            }
            for (int k = 0; k < 16; k++) blockScan[k] = prep.ResidualZig[i, k];
            (int cA, int cB) = LumaAcNeighborCbfIntra(i, state, leftMb, topMb);
            bool acCbf = CabacEncResidual.EncodeResidualBlock(
                cabac, blockScan, maxNumCoeff: 16, ctxBlockCat: CabacEncResidual.CatLuma4x4,
                condTermFlagA: cA, condTermFlagB: cB);
            state.LumaAcCbf[i] = acCbf;
            // NonZeroCountLuma is used by the CAVLC nC neighbor lookups; CABAC neighbor lookups go
            // through LumaAcCbf instead, so the count itself need not match — but mirror the
            // decoder's "set to 1 when any non-zero" so CAVLC neighbors of a future MB see a
            // consistent count if entropy switches.
            state.NonZeroCountLuma[i] = acCbf ? 1 : 0;
        }

        // ---- Chroma DC ----
        if ((cbpChroma & 3) != 0)
        {
            Span<int> dc = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dc[k] = chroma.ChromaDc[c, k];
                int caC = (leftMb == null) ? 1 : (leftMb.ChromaDcCbf[c] ? 1 : 0);
                int cbC = (topMb == null) ? 1 : (topMb.ChromaDcCbf[c] ? 1 : 0);
                bool cbf = CabacEncResidual.EncodeResidualBlock(
                    cabac, dc, maxNumCoeff: 4, ctxBlockCat: CabacEncResidual.CatChromaDc,
                    condTermFlagA: caC, condTermFlagB: cbC);
                state.ChromaDcCbf[c] = cbf;
            }
        }

        // ---- Chroma AC ----
        if ((cbpChroma & 2) != 0)
        {
            Span<int> ac = stackalloc int[15];
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int k = 0; k < 15; k++) ac[k] = chroma.ChromaAc[c, i, k];
                    (int cA, int cB) = ChromaAcNeighborCbfIntra(c, i, state, leftMb, topMb);
                    bool cbf = CabacEncResidual.EncodeResidualBlock(
                        cabac, ac, maxNumCoeff: 15, ctxBlockCat: CabacEncResidual.CatChromaAc,
                        condTermFlagA: cA, condTermFlagB: cB);
                    state.ChromaAcCbf[c, i] = cbf;
                    state.NonZeroCountChromaAc[c, i] = cbf ? 1 : 0;
                }
            }
        }

        mbStates[mbAddress] = state;
    }
}
