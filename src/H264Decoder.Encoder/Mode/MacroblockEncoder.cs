using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cavlc;
using H264Decoder.Encoder.Transform;
using H264Decoder.Prediction;
using H264Decoder.Syntax;
using H264Decoder.Transform;

namespace H264Decoder.Encoder.Mode;

/// <summary>Encodes one Intra_16x16 macroblock: gather neighbors, predict, residual,
/// forward transform/quant, write CAVLC residual into the slice bit stream, and produce
/// the reconstructed samples in <paramref name="state"/> for future neighbors.</summary>
internal static class MacroblockEncoder
{
    public static readonly (int X, int Y)[] LumaBlockPos = MacroblockParser.LumaBlockPos;

    /// <summary>4x4 zig-zag scan: scanPos → raster position within the 4x4 block.
    /// Mirror of decoder's ScanOrder.ZigZag4x4.</summary>
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>Encode one Intra_16x16 MB into <paramref name="w"/> and update the picture
    /// reconstruction in <paramref name="picY"/>/U/V. Picks the best prediction mode by SAD
    /// across the 4 Intra_16x16 modes and 4 chroma modes (DC always, others where neighbors allow).</summary>
    public static void EncodeIntra16x16(
        BitWriter w,
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] picY, byte[] picU, byte[] picV,
        int picStrideY, int picStrideC,
        int mbX, int mbY, int mbsPerRow,
        int qpY,
        MacroblockEncoderState?[] mbStates,
        int mbAddress)
    {
        var leftMb = mbX > 0 ? mbStates[mbAddress - 1] : null;
        var topMb = mbY > 0 ? mbStates[mbAddress - mbsPerRow] : null;

        // Sample neighbors from the reconstructed picture.
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
        if (topLeftAvail)
        {
            topLeft = picY[(mbY * 16 - 1) * picStrideY + (mbX * 16 - 1)];
        }

        // Read source luma block (16x16).
        Span<byte> srcLuma = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                srcLuma[y * 16 + x] = srcY[y * srcStrideY + x];

        // Pick best Intra_16x16 mode by SAD against source. Always try DC; try Vertical if topAvail,
        // Horizontal if leftAvail, Plane if all neighbors available.
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
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = (Intra16x16PredMode)m;
                predTry.CopyTo(predBest);
            }
        }

        // ---- Forward transform/quant ----
        // 1) Forward 4x4 DCT on each sub-block of (src - pred). DC of each goes into a DC matrix.
        Span<int> luma4x4 = stackalloc int[256];     // 16 blocks of 16 coeffs raster, per block packed.
        Span<int> dcMatrix = stackalloc int[16];     // 16 DC values, in raster of 4x4 block position
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
            // Stash the AC residual back into luma4x4[i*16..(i+1)*16-1].
            for (int k = 0; k < 16; k++) luma4x4[i * 16 + k] = block[k];
        }

        // 2) Forward Hadamard on the DC matrix, then quantize.
        ForwardTransform.ForwardHadamard4x4(dcMatrix);
        ForwardQuantization.QuantLumaDc(dcMatrix, qpY);

        // 3) Quantize AC of each sub-block (skip position 0, the DC, since it'll be handled by DC chain).
        for (int i = 0; i < 16; i++)
        {
            Span<int> ac = luma4x4.Slice(i * 16, 16);
            int savedDc = ac[0];
            ac[0] = 0;
            ForwardQuantization.Quant4x4Ac(ac, qpY, intra: true);
            ac[0] = savedDc; // will be overwritten by DC pipeline on decode side; we keep raster, won't transmit.
        }

        // ---- Chroma encoding ----
        // Sample chroma source + neighbors, then encode.
        Span<byte> srcCb = stackalloc byte[64];
        Span<byte> srcCr = stackalloc byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                srcCb[y * 8 + x] = srcU[y * srcStrideC + x];
                srcCr[y * 8 + x] = srcV[y * srcStrideC + x];
            }

        var chromaResult = EncodeChroma(
            srcCb, srcCr, picU, picV, picStrideC, mbX, mbY, qpY,
            leftMb, topMb);

        // ---- Compute CbpLuma and CbpChroma from quantized coefficients ----
        // CbpLuma bit i set ⇒ any non-zero in 4x4 block within 8x8 quadrant i.
        // For Intra_16x16 the cbp must be one of {0, 15} per the I-slice mb_type table.
        int cbpLumaAny = 0;
        for (int i = 0; i < 16; i++)
        {
            // Skip DC slot — luma4x4[i*16+0] is the un-quantized DC, not transmitted as AC.
            for (int k = 1; k < 16; k++)
            {
                if (luma4x4[i * 16 + k] != 0) { cbpLumaAny = 15; break; }
            }
            if (cbpLumaAny == 15) break;
        }

        int cbpLuma = cbpLumaAny;
        int cbpChroma = chromaResult.CbpChroma;

        // mb_type per I-slice table (spec Table 7-11):
        //   mb_type 0 = I_NxN (we don't pick this)
        //   mb_type 1..24 = I_16x16 with (predMode, cbpLuma, cbpChroma) encoded by group
        //   mb_type 25 = I_PCM
        int predModeIdx = (int)bestMode;
        int group = ((cbpLuma == 0) ? 0 : 1) * 3
                  + (cbpChroma);
        // Map (cbpLuma flag, cbpChroma) to group index per Table 7-11:
        //   group 0 = (cbpLuma=0, cbpChroma=0)
        //   group 1 = (cbpLuma=0, cbpChroma=1)
        //   group 2 = (cbpLuma=0, cbpChroma=2)
        //   group 3 = (cbpLuma=15, cbpChroma=0)
        //   group 4 = (cbpLuma=15, cbpChroma=1)
        //   group 5 = (cbpLuma=15, cbpChroma=2)
        if (cbpLuma == 15) group = 3 + cbpChroma;
        else group = cbpChroma;
        int mbType = 1 + group * 4 + predModeIdx;

        // ---- Write macroblock_layer ----
        ExpGolombWriter.WriteUe(w, (uint)mbType);
        // mb_pred: intra_chroma_pred_mode (since Intra_16x16 uses MbPartPredMode.Intra16x16).
        ExpGolombWriter.WriteUe(w, (uint)chromaResult.ChromaMode);

        // For Intra_16x16, CBP is encoded in mb_type — no separate coded_block_pattern.
        // mb_qp_delta + residual are present iff cbpLuma!=0 || cbpChroma!=0 || Intra_16x16.
        // Intra_16x16 is always "has residual" because DC always carried.
        ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta = 0 (we use fixed QP)

        // Write residual.
        // a) Luma DC (16 coeffs in zig-zag order).
        Span<int> dcScan = stackalloc int[16];
        for (int i = 0; i < 16; i++) dcScan[i] = dcMatrix[ZigZag4x4[i]];
        // nC for DC: from neighbors of block 0 (raster idx 0).
        int ncDc = ComputeNcLumaBlock0(leftMb, topMb);
        CavlcEncoder.EncodeResidualBlock(w, dcScan, maxNumCoeff: 16, ncDc, chromaDc: false);

        // b) Luma AC: 16 blocks × 15 coeffs (positions 1..15 in zig-zag).
        var state = new MacroblockEncoderState
        {
            MbAddress = mbAddress,
            IsIntra16x16 = true,
            CbpLuma = cbpLuma,
            CbpChroma = cbpChroma,
            QpY = qpY,
        };

        if (cbpLuma != 0)
        {
            Span<int> acScan = stackalloc int[15];
            for (int i = 0; i < 16; i++)
            {
                Span<int> acRaster = luma4x4.Slice(i * 16, 16);
                // Take positions 1..15 in raster, mapped via zigzag scan (skip DC).
                for (int s = 1; s < 16; s++) acScan[s - 1] = acRaster[ZigZag4x4[s]];
                int nC = ComputeNcLumaBlockFor(state, leftMb, topMb, i);
                CavlcEncoder.EncodeResidualBlock(w, acScan, maxNumCoeff: 15, nC, chromaDc: false);
                int nz = 0; for (int k = 0; k < 15; k++) if (acScan[k] != 0) nz++;
                state.NonZeroCountLuma[i] = nz;
            }
        }

        // c) Chroma DC (2 components, 4 coeffs each, NO nC predictor used in the spec — chromaDc=true).
        if ((cbpChroma & 3) != 0)
        {
            Span<int> dc = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dc[k] = chromaResult.ChromaDc[c, k];
                CavlcEncoder.EncodeResidualBlock(w, dc, maxNumCoeff: 4, nC: 0, chromaDc: true);
            }
        }

        // d) Chroma AC: 4 blocks per component × 15 coeffs.
        if ((cbpChroma & 2) != 0)
        {
            Span<int> ac = stackalloc int[15];
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int k = 0; k < 15; k++) ac[k] = chromaResult.ChromaAc[c, i, k];
                    int nC = ComputeNcChromaBlockFor(state, leftMb, topMb, c, i);
                    CavlcEncoder.EncodeResidualBlock(w, ac, maxNumCoeff: 15, nC, chromaDc: false);
                    int nz = 0; for (int k = 0; k < 15; k++) if (ac[k] != 0) nz++;
                    state.NonZeroCountChromaAc[c, i] = nz;
                }
            }
        }

        // ---- Reconstruct the MB and write into picture buffers. ----
        // Inverse pipeline mirroring decoder's ReconstructLumaIntra16x16.
        ReconstructLumaIntra16x16(
            predBest, dcMatrix, luma4x4, qpY,
            picY, picStrideY, mbX, mbY, state.ReconY);
        // Chroma reconstruction handled inside EncodeChroma above; state's ReconU/V have been populated.
        chromaResult.ReconU.CopyTo(state.ReconU, 0);
        chromaResult.ReconV.CopyTo(state.ReconV, 0);
        // Write chroma reconstruction into picture buffer too.
        int cmbX = mbX * 8, cmbY = mbY * 8;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                picU[(cmbY + y) * picStrideC + cmbX + x] = state.ReconU[y * 8 + x];
                picV[(cmbY + y) * picStrideC + cmbX + x] = state.ReconV[y * 8 + x];
            }

        mbStates[mbAddress] = state;
    }

    /// <summary>Compute the best-Intra_16x16-mode SAD of the source MB against neighbors in the
    /// running picture buffer. Used by H264FrameEncoder to compare against Intra_4x4 cost.
    /// Does not modify the picture.</summary>
    public static int EstimateBestIntra16x16Sad(
        ReadOnlySpan<byte> srcY, int srcStrideY,
        byte[] picY, int picStrideY,
        int mbX, int mbY, int mbsPerRow,
        MacroblockEncoderState?[] mbStates, int mbAddress)
    {
        var leftMb = mbX > 0 ? mbStates[mbAddress - 1] : null;
        var topMb = mbY > 0 ? mbStates[mbAddress - mbsPerRow] : null;

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
        if (topLeftAvail)
        {
            topLeft = picY[(mbY * 16 - 1) * picStrideY + (mbX * 16 - 1)];
        }

        Span<byte> srcLuma = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                srcLuma[y * 16 + x] = srcY[y * srcStrideY + x];

        Span<byte> predTry = stackalloc byte[256];
        int bestSad = int.MaxValue;
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
            if (sad < bestSad) bestSad = sad;
        }
        return bestSad;
    }

    private static int ComputeNcLumaBlock0(MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        int nA = leftMb is null ? -1 : leftMb.NonZeroCountLuma[MacroblockParser.SpatialToRaster(3, 0)];
        int nB = topMb is null ? -1 : topMb.NonZeroCountLuma[MacroblockParser.SpatialToRaster(0, 3)];
        return ComputeNc(nA, nB);
    }

    private static int ComputeNcLumaBlockFor(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb, int blockIdx)
    {
        (int x, int y) = LumaBlockPos[blockIdx];
        int nA;
        if (x > 0) nA = cur.NonZeroCountLuma[MacroblockParser.SpatialToRaster(x - 1, y)];
        else if (leftMb != null) nA = leftMb.NonZeroCountLuma[MacroblockParser.SpatialToRaster(3, y)];
        else nA = -1;
        int nB;
        if (y > 0) nB = cur.NonZeroCountLuma[MacroblockParser.SpatialToRaster(x, y - 1)];
        else if (topMb != null) nB = topMb.NonZeroCountLuma[MacroblockParser.SpatialToRaster(x, 3)];
        else nB = -1;
        return ComputeNc(nA, nB);
    }

    private static int ComputeNcChromaBlockFor(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        int comp, int blockIdx)
    {
        int x = blockIdx & 1;
        int y = (blockIdx >> 1) & 1;
        int nA;
        if (x > 0) nA = cur.NonZeroCountChromaAc[comp, blockIdx - 1];
        else if (leftMb != null) nA = leftMb.NonZeroCountChromaAc[comp, blockIdx + 1];
        else nA = -1;
        int nB;
        if (y > 0) nB = cur.NonZeroCountChromaAc[comp, blockIdx - 2];
        else if (topMb != null) nB = topMb.NonZeroCountChromaAc[comp, blockIdx + 2];
        else nB = -1;
        return ComputeNc(nA, nB);
    }

    private static int ComputeNc(int nA, int nB)
    {
        bool aA = nA >= 0;
        bool bA = nB >= 0;
        if (aA && bA) return (nA + nB + 1) >> 1;
        if (aA) return nA;
        if (bA) return nB;
        return 0;
    }

    /// <summary>Mirror of the decoder's ReconstructLumaIntra16x16: rebuild reconstructed Y samples
    /// from the quantized DC matrix + AC blocks + prediction. Writes into both picY (in-place) and
    /// the per-MB ReconY buffer (for fast neighbor lookups in future MBs).</summary>
    private static void ReconstructLumaIntra16x16(
        ReadOnlySpan<byte> pred, Span<int> dcMatrixQ, Span<int> luma4x4Raster, int qpY,
        byte[] picY, int picStrideY, int mbX, int mbY, byte[] reconYOut)
    {
        // Run inverse Hadamard + DequantLumaDc on a copy of dcMatrixQ.
        Span<int> dc = stackalloc int[16];
        for (int i = 0; i < 16; i++) dc[i] = dcMatrixQ[i];
        InverseTransform.InverseHadamard4x4(dc);
        Quantization_DequantPublic.DequantLumaDc(dc, qpY);

        Span<int> coeffsRaster = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            // Build a 4x4 block: position 0 = recovered DC, positions 1..15 = AC raster (already quantized).
            coeffsRaster.Clear();
            // luma4x4Raster's first slot is the un-quantized DC; we ignore that and use dc[].
            for (int k = 1; k < 16; k++) coeffsRaster[k] = luma4x4Raster[i * 16 + k];
            // Insert DC at position 0.
            coeffsRaster[0] = dc[by * 4 + bx];
            // Dequant AC (excluding DC), then inverse 4x4.
            int dcSaved = coeffsRaster[0];
            coeffsRaster[0] = 0;
            Quantization_DequantPublic.Dequant4x4Ac(coeffsRaster, qpY);
            coeffsRaster[0] = dcSaved;
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

    /// <summary>Result bundle for chroma encoding: quantized coeffs + chroma mode + reconstructed samples.</summary>
    internal sealed class ChromaEncodeResult
    {
        public IntraChromaPredMode ChromaMode;
        public int CbpChroma;                       // 0/1/2
        public int[,] ChromaDc = new int[2, 4];     // scan-order per component
        public int[,,] ChromaAc = new int[2, 4, 15];// per component, per 4x4 block, AC coeffs
        public byte[] ReconU = new byte[64];
        public byte[] ReconV = new byte[64];
    }

    /// <summary>Cross-file bridge so <see cref="IntraEncoder4x4"/> can reuse the chroma encoding path.</summary>
    internal static IntraEncoder4x4.EncodeChromaResult EncodeChromaPublic(
        ReadOnlySpan<byte> srcCb, ReadOnlySpan<byte> srcCr,
        byte[] picU, byte[] picV, int picStrideC,
        int mbX, int mbY, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        var local = EncodeChroma(srcCb, srcCr, picU, picV, picStrideC, mbX, mbY, qpY, leftMb, topMb);
        var r = new IntraEncoder4x4.EncodeChromaResult
        {
            ChromaMode = local.ChromaMode,
            CbpChroma = local.CbpChroma,
        };
        for (int c = 0; c < 2; c++)
            for (int k = 0; k < 4; k++) r.ChromaDc[c, k] = local.ChromaDc[c, k];
        for (int c = 0; c < 2; c++)
            for (int b = 0; b < 4; b++)
                for (int k = 0; k < 15; k++) r.ChromaAc[c, b, k] = local.ChromaAc[c, b, k];
        local.ReconU.CopyTo(r.ReconU, 0);
        local.ReconV.CopyTo(r.ReconV, 0);
        return r;
    }

    private static ChromaEncodeResult EncodeChroma(
        ReadOnlySpan<byte> srcCb, ReadOnlySpan<byte> srcCr,
        byte[] picU, byte[] picV, int picStrideC,
        int mbX, int mbY, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        // Sample chroma neighbors.
        Span<byte> topU = stackalloc byte[8];
        Span<byte> leftU = stackalloc byte[8];
        Span<byte> topV = stackalloc byte[8];
        Span<byte> leftV = stackalloc byte[8];
        bool topAvail = topMb != null;
        bool leftAvail = leftMb != null;
        bool topLeftAvail = topAvail && leftAvail;
        byte topLeftU = 0, topLeftV = 0;
        if (topAvail)
        {
            int srcRow = mbY * 8 - 1;
            int srcCol0 = mbX * 8;
            for (int i = 0; i < 8; i++)
            {
                topU[i] = picU[srcRow * picStrideC + srcCol0 + i];
                topV[i] = picV[srcRow * picStrideC + srcCol0 + i];
            }
        }
        if (leftAvail)
        {
            int srcCol = mbX * 8 - 1;
            int srcRow0 = mbY * 8;
            for (int i = 0; i < 8; i++)
            {
                leftU[i] = picU[(srcRow0 + i) * picStrideC + srcCol];
                leftV[i] = picV[(srcRow0 + i) * picStrideC + srcCol];
            }
        }
        if (topLeftAvail)
        {
            topLeftU = picU[(mbY * 8 - 1) * picStrideC + (mbX * 8 - 1)];
            topLeftV = picV[(mbY * 8 - 1) * picStrideC + (mbX * 8 - 1)];
        }

        // Try all chroma modes, pick best by combined SAD.
        IntraChromaPredMode bestMode = IntraChromaPredMode.Dc;
        int bestSad = int.MaxValue;
        Span<byte> predUbest = stackalloc byte[64];
        Span<byte> predVbest = stackalloc byte[64];
        Span<byte> predUtry = stackalloc byte[64];
        Span<byte> predVtry = stackalloc byte[64];
        // Mode ordering follows IntraChromaPredMode enum: Dc=0, Horizontal=1, Vertical=2, Plane=3.
        bool[] modeOk = { true, leftAvail, topAvail, topAvail && leftAvail && topLeftAvail };
        for (int m = 0; m < 4; m++)
        {
            if (!modeOk[m]) continue;
            IntraPrediction.PredictChroma8x8(
                (IntraChromaPredMode)m,
                topU, topAvail, leftU, leftAvail, topLeftU, topLeftAvail, predUtry);
            IntraPrediction.PredictChroma8x8(
                (IntraChromaPredMode)m,
                topV, topAvail, leftV, leftAvail, topLeftV, topLeftAvail, predVtry);
            int sad = 0;
            for (int i = 0; i < 64; i++) sad += Math.Abs(srcCb[i] - predUtry[i]) + Math.Abs(srcCr[i] - predVtry[i]);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = (IntraChromaPredMode)m;
                predUtry.CopyTo(predUbest);
                predVtry.CopyTo(predVbest);
            }
        }

        // Quantize chroma. qPc derived from qPy via decoder's table.
        int qPc = ChromaQpFromLumaQp(qpY);

        var result = new ChromaEncodeResult { ChromaMode = bestMode };

        // Hoist all stackallocs out of loops (CA2014).
        Span<int> ac4x4 = stackalloc int[64];
        Span<int> dc2x2 = stackalloc int[4];
        Span<int> chBlock = stackalloc int[16];
        Span<int> dcDecoded = stackalloc int[4];
        Span<int> coeffsRaster = stackalloc int[16];

        // Per component: subtract pred, forward DCT each 4x4 sub-block, extract DC into 2x2,
        // forward 2x2 Hadamard + QuantChromaDc, then quantize AC. Then inverse path for reconstruction.
        for (int comp = 0; comp < 2; comp++)
        {
            ReadOnlySpan<byte> src = comp == 0 ? srcCb : srcCr;
            ReadOnlySpan<byte> pred = comp == 0 ? predUbest : predVbest;
            ac4x4.Clear();
            dc2x2.Clear();
            // 4 sub-blocks of 4x4: arranged as 0(TL) 1(TR) / 2(BL) 3(BR).
            for (int b = 0; b < 4; b++)
            {
                int bx = b & 1;
                int by = (b >> 1) & 1;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int sx = bx * 4 + xx, sy = by * 4 + yy;
                        chBlock[yy * 4 + xx] = src[sy * 8 + sx] - pred[sy * 8 + sx];
                    }
                ForwardTransform.Forward4x4(chBlock);
                dc2x2[b] = chBlock[0];
                for (int k = 0; k < 16; k++) ac4x4[b * 16 + k] = chBlock[k];
            }
            // Forward 2x2 Hadamard on DC + quant.
            ForwardTransform.ForwardHadamard2x2(dc2x2);
            ForwardQuantization.QuantChromaDc(dc2x2, qPc);
            for (int k = 0; k < 4; k++) result.ChromaDc[comp, k] = dc2x2[k];
            // Quantize AC (skip DC slot since it'll be substituted on decode).
            for (int b = 0; b < 4; b++)
            {
                Span<int> ac = ac4x4.Slice(b * 16, 16);
                int saved = ac[0]; ac[0] = 0;
                ForwardQuantization.Quant4x4Ac(ac, qPc, intra: true);
                ac[0] = saved;
            }
            // Save AC (positions 1..15 of zig-zag) per block.
            for (int b = 0; b < 4; b++)
            {
                Span<int> ac = ac4x4.Slice(b * 16, 16);
                for (int s = 1; s < 16; s++) result.ChromaAc[comp, b, s - 1] = ac[ZigZag4x4[s]];
            }

            // ---- Reconstruction: mirror the decoder. ----
            // Inverse 2x2 Hadamard + DequantChromaDc.
            for (int k = 0; k < 4; k++) dcDecoded[k] = result.ChromaDc[comp, k];
            InverseTransform.InverseHadamard2x2(dcDecoded);
            Quantization_DequantPublic.DequantChromaDc(dcDecoded, qPc);

            byte[] recon = comp == 0 ? result.ReconU : result.ReconV;
            for (int b = 0; b < 4; b++)
            {
                int bx = b & 1;
                int by = (b >> 1) & 1;
                Span<int> ac = ac4x4.Slice(b * 16, 16);
                // AC is already raster (with DC slot still containing pre-quant value); we re-quant→raster path.
                for (int k = 0; k < 16; k++) coeffsRaster[k] = ac[k];
                coeffsRaster[0] = dcDecoded[b];
                int saved = coeffsRaster[0];
                coeffsRaster[0] = 0;
                Quantization_DequantPublic.Dequant4x4Ac(coeffsRaster, qPc);
                coeffsRaster[0] = saved;
                InverseTransform.Inverse4x4(coeffsRaster);
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int v = pred[(by * 4 + yy) * 8 + (bx * 4 + xx)] + coeffsRaster[yy * 4 + xx];
                        recon[(by * 4 + yy) * 8 + (bx * 4 + xx)] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    }
            }
        }

        // CbpChroma: 0 if all chroma coeffs zero, 1 if only DC has non-zero, 2 if any AC has non-zero.
        bool anyAc = false, anyDc = false;
        for (int c = 0; c < 2; c++)
        {
            for (int k = 0; k < 4; k++) if (result.ChromaDc[c, k] != 0) anyDc = true;
            for (int b = 0; b < 4; b++)
                for (int k = 0; k < 15; k++) if (result.ChromaAc[c, b, k] != 0) anyAc = true;
        }
        result.CbpChroma = anyAc ? 2 : (anyDc ? 1 : 0);
        return result;
    }

    // Decoder's Table 8-9 for QpC from qPi.
    private static readonly byte[] _qpcTable =
    {
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
        31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38,
        39, 39, 39, 39,
    };

    private static int ChromaQpFromLumaQp(int qPy)
    {
        int qPi = qPy; // chroma_qp_index_offset = 0 for our PPS.
        if (qPi < 0) qPi = 0;
        else if (qPi > 51) qPi = 51;
        return _qpcTable[qPi];
    }
}
