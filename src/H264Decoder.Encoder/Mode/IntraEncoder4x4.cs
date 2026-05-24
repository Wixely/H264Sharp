using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cavlc;
using H264Decoder.Encoder.Transform;
using H264Decoder.Prediction;
using H264Decoder.Syntax;
using H264Decoder.Transform;

namespace H264Decoder.Encoder.Mode;

/// <summary>Encode one Intra_4x4 (I_NxN) macroblock. Mirrors the decoder's
/// reconstruction order (16 4x4 blocks in raster) so the encoder's reconstruction
/// matches what the decoder will produce. Also exposes a cost estimator so the
/// caller can choose between Intra_4x4 and Intra_16x16.</summary>
internal static class IntraEncoder4x4
{
    public static readonly (int X, int Y)[] LumaBlockPos = MacroblockParser.LumaBlockPos;

    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    /// <summary>Estimate the cost (rough SAD sum) of encoding the MB as Intra_4x4 WITHOUT actually
    /// emitting bits. Performs the per-block mode search using the current picture reconstruction
    /// (which must match the decoder's view). Used by the caller to choose between Intra_4x4 and
    /// Intra_16x16. The picture reconstruction is not modified.</summary>
    public static int EstimateMbSad(
        ReadOnlySpan<byte> srcY,
        int srcStrideY,
        byte[] picY, int picStrideY,
        int mbX, int mbY, int mbsPerRow,
        MacroblockEncoderState?[] mbStates, int mbAddress)
    {
        var leftMb = mbX > 0 ? mbStates[mbAddress - 1] : null;
        var topMb = mbY > 0 ? mbStates[mbAddress - mbsPerRow] : null;
        var topRightMb = (mbY > 0 && (mbX + 1) < mbsPerRow) ? mbStates[mbAddress - mbsPerRow + 1] : null;

        // Use a scratch reconstruction of the 16x16 MB region to keep the in-MB neighbors consistent
        // while we evaluate modes block-by-block. Initialize from the current picture.
        Span<byte> scratch = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                scratch[y * 16 + x] = (byte)0; // filled per-block as we sweep

        Span<int> blockModes = stackalloc int[16];
        for (int i = 0; i < 16; i++) blockModes[i] = -1;

        int totalSad = 0;
        Span<byte> top = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        Span<byte> predTry = stackalloc byte[16];
        Span<byte> srcBlk = stackalloc byte[16];

        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            int px0 = mbX * 16 + bx * 4;
            int py0 = mbY * 16 + by * 4;

            // Read source block.
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                    srcBlk[yy * 4 + xx] = srcY[(by * 4 + yy) * srcStrideY + (bx * 4 + xx)];

            // For estimation we use the actual reconstructed picture neighbors for cross-MB samples
            // and source-block pre-reconstruction for in-MB neighbors (estimate-only). This is an
            // approximation — real reconstruction may differ — but is a useful relative cost signal.
            bool topAvail = by > 0 || topMb != null;
            bool leftAvail = bx > 0 || leftMb != null;
            bool topLeftAvail =
                (by > 0 && bx > 0) ? true :
                (by == 0 && bx == 0) ? (leftMb != null && topMb != null) :
                (by == 0) ? (topMb != null) :
                (leftMb != null);
            bool topRightAvail = ComputeTopRightAvail(i, bx, by, mbX, mbsPerRow, topMb, topRightMb);

            GatherNeighborsForEstimate(picY, picStrideY, scratch, mbX, mbY, bx, by,
                topAvail, leftAvail, topLeftAvail, topRightAvail,
                top, left, out byte topLeft);

            int bestSad = int.MaxValue;
            int bestMode = -1;
            for (int m = 0; m < 9; m++)
            {
                if (!ModeAvailable(m, topAvail, leftAvail, topLeftAvail)) continue;
                IntraPrediction.PredictIntra4x4(
                    (IntraPrediction.Intra4x4Mode)m,
                    top, topAvail, topRightAvail,
                    left, leftAvail,
                    topLeft, topLeftAvail,
                    predTry);
                int sad = 0;
                for (int k = 0; k < 16; k++) sad += Math.Abs(srcBlk[k] - predTry[k]);
                if (sad < bestSad) { bestSad = sad; bestMode = m; }
            }
            blockModes[i] = bestMode;
            totalSad += bestSad;

            // Approximate reconstruction: use the prediction (since residual is small for chosen mode)
            // and write into the scratch so subsequent in-MB blocks see plausible neighbors.
            IntraPrediction.PredictIntra4x4(
                (IntraPrediction.Intra4x4Mode)bestMode,
                top, topAvail, topRightAvail,
                left, leftAvail,
                topLeft, topLeftAvail,
                predTry);
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                    scratch[(by * 4 + yy) * 16 + (bx * 4 + xx)] = predTry[yy * 4 + xx];
            // Also write into picY temporarily? No — we promised to not modify picture during estimate.
            _ = px0; _ = py0;
        }
        return totalSad;
    }

    /// <summary>Encode the MB as Intra_4x4. Writes syntax to <paramref name="w"/> and updates the
    /// picture reconstruction in <paramref name="picY"/>/U/V. Sets up <paramref name="state"/> for
    /// neighbor lookups.</summary>
    public static void EncodeIntra4x4(
        BitWriter w,
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] picY, byte[] picU, byte[] picV,
        int picStrideY, int picStrideC,
        int mbX, int mbY, int mbsPerRow,
        int qpY,
        MacroblockEncoderState?[] mbStates, int mbAddress)
    {
        var leftMb = mbX > 0 ? mbStates[mbAddress - 1] : null;
        var topMb = mbY > 0 ? mbStates[mbAddress - mbsPerRow] : null;
        var topRightMb = (mbY > 0 && (mbX + 1) < mbsPerRow) ? mbStates[mbAddress - mbsPerRow + 1] : null;

        var state = new MacroblockEncoderState
        {
            MbAddress = mbAddress,
            IsIntra4x4 = true,
            QpY = qpY,
        };

        // Per-block storage: actual mode, residual coefficients in zigzag order.
        int[] actualMode = new int[16];
        int[,] residualZig = new int[16, 16];

        Span<byte> top = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        Span<byte> predBlk = stackalloc byte[16];
        Span<byte> predTry = stackalloc byte[16];
        Span<byte> srcBlk = stackalloc byte[16];
        Span<int> coeffsRaster = stackalloc int[16];
        Span<int> coeffsScan = stackalloc int[16];

        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            int px0 = mbX * 16 + bx * 4;
            int py0 = mbY * 16 + by * 4;

            // Read source block.
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                    srcBlk[yy * 4 + xx] = srcY[(by * 4 + yy) * srcStrideY + (bx * 4 + xx)];

            bool topAvail = by > 0 || topMb != null;
            bool leftAvail = bx > 0 || leftMb != null;
            bool topLeftAvail =
                (by > 0 && bx > 0) ? true :
                (by == 0 && bx == 0) ? (leftMb != null && topMb != null) :
                (by == 0) ? (topMb != null) :
                (leftMb != null);
            bool topRightAvail = ComputeTopRightAvail(i, bx, by, mbX, mbsPerRow, topMb, topRightMb);

            // Sample neighbors from the running picture buffer (in-MB samples were written by
            // previous iterations after reconstruction).
            GatherNeighborSamplesFromPic(picY, picStrideY, px0, py0,
                topAvail, leftAvail, topLeftAvail, topRightAvail,
                top, left, out byte topLeft);

            // Try all 9 modes (subject to availability). Pick lowest SAD against source.
            int bestSad = int.MaxValue;
            int bestMode = 2; // DC fallback
            for (int m = 0; m < 9; m++)
            {
                if (!ModeAvailable(m, topAvail, leftAvail, topLeftAvail)) continue;
                IntraPrediction.PredictIntra4x4(
                    (IntraPrediction.Intra4x4Mode)m,
                    top, topAvail, topRightAvail,
                    left, leftAvail,
                    topLeft, topLeftAvail,
                    predTry);
                int sad = 0;
                for (int k = 0; k < 16; k++) sad += Math.Abs(srcBlk[k] - predTry[k]);
                if (sad < bestSad) { bestSad = sad; bestMode = m; }
            }
            actualMode[i] = bestMode;
            state.Intra4x4Mode[i] = bestMode;

            // Predict using the chosen mode.
            IntraPrediction.PredictIntra4x4(
                (IntraPrediction.Intra4x4Mode)bestMode,
                top, topAvail, topRightAvail,
                left, leftAvail,
                topLeft, topLeftAvail,
                predBlk);

            // Residual: source - prediction; forward 4x4 DCT; quant (intra=true).
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                    coeffsRaster[yy * 4 + xx] = srcBlk[yy * 4 + xx] - predBlk[yy * 4 + xx];
            ForwardTransform.Forward4x4(coeffsRaster);
            // 4x4 quant: spec §8.5.10 covers full 4x4 with DC for Intra_4x4 (no separate DC chain).
            Quant4x4Full(coeffsRaster, qpY);

            // Save zigzag-scanned coefficients (decoder expects scan order in mb.Luma[i,j]).
            for (int s = 0; s < 16; s++) coeffsScan[s] = coeffsRaster[ZigZag4x4[s]];
            for (int s = 0; s < 16; s++) residualZig[i, s] = coeffsScan[s];

            // Reconstruct: dequant + inverse 4x4 + add pred + clip; write into picY.
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                    coeffsRaster[yy * 4 + xx] = coeffsScan[0]; // placeholder; will rebuild
            // Rebuild raster from scan and dequantize.
            for (int k = 0; k < 16; k++) coeffsRaster[k] = 0;
            for (int s = 0; s < 16; s++) coeffsRaster[ZigZag4x4[s]] = coeffsScan[s];
            Dequant4x4Full(coeffsRaster, qpY);
            InverseTransform.Inverse4x4(coeffsRaster);
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int v = predBlk[yy * 4 + xx] + coeffsRaster[yy * 4 + xx];
                    byte clipped = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    picY[(py0 + yy) * picStrideY + (px0 + xx)] = clipped;
                    state.ReconY[(by * 4 + yy) * 16 + (bx * 4 + xx)] = clipped;
                }
            _ = bestSad;
        }

        // ---- Chroma encoding (same pipeline as Intra_16x16). ----
        Span<byte> srcCb = stackalloc byte[64];
        Span<byte> srcCr = stackalloc byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                srcCb[y * 8 + x] = srcU[y * srcStrideC + x];
                srcCr[y * 8 + x] = srcV[y * srcStrideC + x];
            }
        var chroma = EncodeChromaSharedShim(srcCb, srcCr, picU, picV, picStrideC, mbX, mbY, qpY,
            leftMb, topMb);

        // ---- Compute CBP-luma from non-zero counts (any of the 4 4x4 blocks in a quadrant). ----
        int cbpLuma = 0;
        for (int i = 0; i < 16; i++)
        {
            bool nz = false;
            for (int k = 0; k < 16; k++) if (residualZig[i, k] != 0) { nz = true; break; }
            if (nz) cbpLuma |= (1 << (i >> 2));
        }
        int cbpChroma = chroma.CbpChroma;

        // ---- Write macroblock_layer syntax (mb_type=0 = I_NxN). ----
        ExpGolombWriter.WriteUe(w, 0); // mb_type = 0

        // 16 × (prev_intra4x4_pred_mode_flag, [rem_intra4x4_pred_mode])
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = LumaBlockPos[i];
            int predicted = PredictIntra4x4ModeFromNeighbors(state, leftMb, topMb, bx, by);
            int chosen = actualMode[i];
            if (chosen == predicted)
            {
                w.WriteBit(1u);
            }
            else
            {
                w.WriteBit(0u);
                int rem = chosen < predicted ? chosen : chosen - 1;
                w.WriteBits((uint)rem, 3);
            }
        }

        // intra_chroma_pred_mode (ue).
        ExpGolombWriter.WriteUe(w, (uint)chroma.ChromaMode);

        // coded_block_pattern (ue, codeNum from intra-table inverse).
        int cbpValue = cbpLuma | (cbpChroma << 4);
        uint cbpCodeNum = (uint)CbpCodeNumIntra(cbpValue);
        ExpGolombWriter.WriteUe(w, cbpCodeNum);

        // mb_qp_delta + residual (only when CBP has any non-zero bit).
        bool hasResidual = (cbpLuma != 0) || (cbpChroma != 0);
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0); // mb_qp_delta = 0 (fixed QP)

            // ---- Luma 4x4 residual (16 coefficients per block, maxNumCoeff=16, intra=true). ----
            Span<int> blockScan = stackalloc int[16];
            for (int i8 = 0; i8 < 4; i8++)
            {
                bool quadCoded = (cbpLuma & (1 << i8)) != 0;
                // Iterate the 4 4x4 blocks within this 8x8 quadrant in raster order.
                int bxq = (i8 & 1) * 2;
                int byq = (i8 >> 1) * 2;
                for (int s = 0; s < 4; s++)
                {
                    int sx = bxq + (s & 1);
                    int sy = byq + (s >> 1);
                    int idx = MacroblockParser.SpatialToRaster(sx, sy);
                    if (!quadCoded)
                    {
                        state.NonZeroCountLuma[idx] = 0;
                        continue;
                    }
                    for (int k = 0; k < 16; k++) blockScan[k] = residualZig[idx, k];
                    int nC = ComputeNcLumaBlockFor(state, leftMb, topMb, idx);
                    CavlcEncoder.EncodeResidualBlock(w, blockScan, maxNumCoeff: 16, nC, chromaDc: false);
                    int nz = 0; for (int k = 0; k < 16; k++) if (blockScan[k] != 0) nz++;
                    state.NonZeroCountLuma[idx] = nz;
                }
            }

            // ---- Chroma DC (when chroma bit set) ----
            if ((cbpChroma & 3) != 0)
            {
                Span<int> dc = stackalloc int[4];
                for (int c = 0; c < 2; c++)
                {
                    for (int k = 0; k < 4; k++) dc[k] = chroma.ChromaDc[c, k];
                    CavlcEncoder.EncodeResidualBlock(w, dc, maxNumCoeff: 4, nC: 0, chromaDc: true);
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
                        int nC = ComputeNcChromaBlockFor(state, leftMb, topMb, c, i);
                        CavlcEncoder.EncodeResidualBlock(w, ac, maxNumCoeff: 15, nC, chromaDc: false);
                        int nz = 0; for (int k = 0; k < 15; k++) if (ac[k] != 0) nz++;
                        state.NonZeroCountChromaAc[c, i] = nz;
                    }
                }
            }
        }

        // Persist state.
        state.CbpLuma = cbpLuma;
        state.CbpChroma = cbpChroma;
        // Copy chroma reconstruction into state + picture.
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

    // -----------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------

    private static bool ModeAvailable(int mode, bool topAvail, bool leftAvail, bool topLeftAvail)
    {
        return mode switch
        {
            0 => topAvail,                                                   // Vertical
            1 => leftAvail,                                                  // Horizontal
            2 => true,                                                        // DC
            3 => topAvail,                                                   // DiagDownLeft
            4 => topAvail && leftAvail && topLeftAvail,                       // DiagDownRight
            5 => topAvail && leftAvail && topLeftAvail,                       // VerticalRight
            6 => topAvail && leftAvail && topLeftAvail,                       // HorizontalDown
            7 => topAvail,                                                   // VerticalLeft
            8 => leftAvail,                                                  // HorizontalUp
            _ => false,
        };
    }

    private static bool ComputeTopRightAvail(int i, int bx, int by, int mbX, int mbsPerRow,
        MacroblockEncoderState? topMb, MacroblockEncoderState? topRightMb)
    {
        // Mirrors decoder's ComputeTopRightAvail logic.
        if (bx == 3)
        {
            if (by == 0) return topRightMb != null;
            return false;
        }
        if (i == 3 || i == 11) return false;
        if (by == 0) return topMb != null;
        return true;
    }

    private static void GatherNeighborSamplesFromPic(
        byte[] picY, int picStride, int px0, int py0,
        bool topAvail, bool leftAvail, bool topLeftAvail, bool topRightAvail,
        Span<byte> top, Span<byte> left, out byte topLeft)
    {
        topLeft = 0;
        if (topAvail)
        {
            int sy = py0 - 1;
            for (int k = 0; k < 4; k++) top[k] = picY[sy * picStride + px0 + k];
            if (topRightAvail)
            {
                for (int k = 0; k < 4; k++) top[4 + k] = picY[sy * picStride + px0 + 4 + k];
            }
            else
            {
                byte fill = top[3];
                top[4] = fill; top[5] = fill; top[6] = fill; top[7] = fill;
            }
        }
        if (leftAvail)
        {
            int sx = px0 - 1;
            for (int k = 0; k < 4; k++) left[k] = picY[(py0 + k) * picStride + sx];
        }
        if (topLeftAvail)
        {
            topLeft = picY[(py0 - 1) * picStride + (px0 - 1)];
        }
    }

    /// <summary>Gather neighbors for the SAD estimator: cross-MB from picY, in-MB from
    /// the prediction-based scratch reconstruction of the current MB.</summary>
    private static void GatherNeighborsForEstimate(
        byte[] picY, int picStride, ReadOnlySpan<byte> scratch,
        int mbX, int mbY, int bx, int by,
        bool topAvail, bool leftAvail, bool topLeftAvail, bool topRightAvail,
        Span<byte> top, Span<byte> left, out byte topLeft)
    {
        topLeft = 0;
        int px0 = mbX * 16 + bx * 4;
        int py0 = mbY * 16 + by * 4;

        if (topAvail)
        {
            if (by > 0)
            {
                int srcRow = by * 4 - 1;
                for (int k = 0; k < 4; k++) top[k] = scratch[srcRow * 16 + bx * 4 + k];
            }
            else
            {
                int srcRow = py0 - 1;
                for (int k = 0; k < 4; k++) top[k] = picY[srcRow * picStride + px0 + k];
            }
            if (topRightAvail)
            {
                if (by > 0 && bx + 1 < 4)
                {
                    int srcRow = by * 4 - 1;
                    for (int k = 0; k < 4; k++) top[4 + k] = scratch[srcRow * 16 + bx * 4 + 4 + k];
                }
                else if (by > 0)
                {
                    byte fill = top[3]; top[4] = fill; top[5] = fill; top[6] = fill; top[7] = fill;
                }
                else
                {
                    int srcRow = py0 - 1;
                    for (int k = 0; k < 4; k++) top[4 + k] = picY[srcRow * picStride + px0 + 4 + k];
                }
            }
            else
            {
                byte fill = top[3]; top[4] = fill; top[5] = fill; top[6] = fill; top[7] = fill;
            }
        }
        if (leftAvail)
        {
            if (bx > 0)
            {
                int srcCol = bx * 4 - 1;
                for (int k = 0; k < 4; k++) left[k] = scratch[(by * 4 + k) * 16 + srcCol];
            }
            else
            {
                int srcCol = px0 - 1;
                for (int k = 0; k < 4; k++) left[k] = picY[(py0 + k) * picStride + srcCol];
            }
        }
        if (topLeftAvail)
        {
            if (bx > 0 && by > 0)
            {
                topLeft = scratch[(by * 4 - 1) * 16 + (bx * 4 - 1)];
            }
            else if (bx > 0)
            {
                topLeft = picY[(py0 - 1) * picStride + (mbX * 16 + bx * 4 - 1)];
            }
            else if (by > 0)
            {
                topLeft = picY[(mbY * 16 + by * 4 - 1) * picStride + (px0 - 1)];
            }
            else
            {
                topLeft = picY[(py0 - 1) * picStride + (px0 - 1)];
            }
        }
    }

    /// <summary>Spec §8.3.1.1: predicted mode = min(modeA, modeB) where unavailable / non-Intra4x4
    /// neighbors contribute -1 → fallback DC (2).</summary>
    internal static int PredictIntra4x4ModeFromNeighbors(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        int bx, int by)
    {
        int leftMode = NeighborMode(cur, leftMb, topMb, bx - 1, by);
        int topMode = NeighborMode(cur, leftMb, topMb, bx, by - 1);
        if (leftMode < 0 || topMode < 0) return 2;
        return leftMode < topMode ? leftMode : topMode;
    }

    private static int NeighborMode(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        int bx, int by)
    {
        if (bx >= 0 && by >= 0)
        {
            int idx = MacroblockParser.SpatialToRaster(bx, by);
            return cur.Intra4x4Mode[idx];
        }
        if (bx < 0)
        {
            if (leftMb == null) return -1;
            if (!leftMb.IsIntra4x4)
            {
                // Spec: neighbor available but not Intra_4x4 → treat as DC (2).
                return 2;
            }
            int idx = MacroblockParser.SpatialToRaster(3, by);
            return leftMb.Intra4x4Mode[idx];
        }
        if (by < 0)
        {
            if (topMb == null) return -1;
            if (!topMb.IsIntra4x4) return 2;
            int idx = MacroblockParser.SpatialToRaster(bx, 3);
            return topMb.Intra4x4Mode[idx];
        }
        return -1;
    }

    /// <summary>Map a CBP value (luma|chroma<<4) to its codeNum in the intra table.</summary>
    internal static int CbpCodeNumIntra(int cbp)
    {
        var t = _intraTable;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == cbp) return i;
        }
        throw new InvalidOperationException($"CBP value {cbp} not in intra table");
    }

    private static readonly byte[] _intraTable =
    {
        47, 31, 15,  0, 23, 27, 29, 30,  7, 11, 13, 14, 39, 43, 45, 46,
        16,  3,  5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44,  1,  2,  4,
         8, 17, 18, 20, 24,  6,  9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    };

    // -----------------------------------------------------------------------------------
    // 4x4 full-block quant / dequant (DC included). Mirrors decoder Quantization paths.
    // For Intra_4x4 there is no separate luma DC chain — DC is quantized as part of the
    // 4x4 block in the inter/intra-4x4 4x4 path.
    // -----------------------------------------------------------------------------------
    private static void Quant4x4Full(Span<int> blk, int qp)
    {
        // Reuse the encoder's Quant4x4Ac which quantizes positions 0..15 (it includes DC).
        // The decoder's Dequant4x4Ac also includes DC for non-Intra_16x16 paths.
        ForwardQuantization.Quant4x4Ac(blk, qp, intra: true);
    }

    private static void Dequant4x4Full(Span<int> blk, int qp)
    {
        Quantization_DequantPublic.Dequant4x4Ac(blk, qp);
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

    // ---- Bridge to MacroblockEncoder's private EncodeChroma helpers. ----
    private static EncodeChromaResult EncodeChromaSharedShim(
        ReadOnlySpan<byte> srcCb, ReadOnlySpan<byte> srcCr,
        byte[] picU, byte[] picV, int picStrideC,
        int mbX, int mbY, int qpY,
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb)
    {
        // Delegate to MacroblockEncoder.EncodeChromaPublic — exposed below via an internal helper.
        return MacroblockEncoder.EncodeChromaPublic(srcCb, srcCr, picU, picV, picStrideC,
            mbX, mbY, qpY, leftMb, topMb);
    }

    /// <summary>Result type returned by the shared chroma encoder.</summary>
    internal sealed class EncodeChromaResult
    {
        public IntraChromaPredMode ChromaMode;
        public int CbpChroma;
        public int[,] ChromaDc = new int[2, 4];
        public int[,,] ChromaAc = new int[2, 4, 15];
        public byte[] ReconU = new byte[64];
        public byte[] ReconV = new byte[64];
    }
}
