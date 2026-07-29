using H264Sharp.Decoder.Prediction;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Decoder.Transform;

namespace H264Sharp.Decoder.Picture;

/// <summary>
/// Reconstructs decoded YUV samples for one parsed macroblock.
/// Intra_16x16 only at this stage; I_NxN will be added next.
/// </summary>
internal static class MacroblockReconstructor
{
    // Spec Table 8-9: qPi (luma+offset, clipped to [0,51]) → qPc (chroma QP).
    private static readonly byte[] _qPcTable =
    [
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
        31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38,
        39, 39, 39, 39,
    ];

    public static int ChromaQp(int qPy, int chromaQpIndexOffset)
    {
        int qPi = qPy + chromaQpIndexOffset;
        if (qPi < 0) qPi = 0;
        else if (qPi > 51) qPi = 51;
        return _qPcTable[qPi];
    }

    public static void Reconstruct(
        Macroblock mb,
        DecodedPicture picture,
        int mbX, int mbY,
        int chromaQpIndexOffset,
        Macroblock? leftMb,
        Macroblock? topMb,
        Macroblock? topRightMb,
        IReadOnlyList<DecodedPicture>? refPicListL0 = null,
        IReadOnlyList<DecodedPicture>? refPicListL1 = null,
        PredWeightTable? predWeights = null,
        bool implicitBipred = false,
        bool explicitBipred = false)
    {
        if (mb.IsPcm)
        {
            ReconstructPcm(mb, picture, mbX, mbY);
            return;
        }
        if (mb.IsBInter || mb.IsBSkip)
        {
            if (refPicListL0 is null) throw new InvalidOperationException("B-inter reconstruction requires L0 ref list");
            ReconstructLumaInterB(mb, picture, refPicListL0, refPicListL1, mbX, mbY, implicitBipred,
                explicitBipred ? predWeights : null);
            int qPcB = ChromaQp(mb.QpY, chromaQpIndexOffset);
            ReconstructChromaInterB(mb, picture, refPicListL0, refPicListL1, mbX, mbY, qPcB, implicitBipred,
                explicitBipred ? predWeights : null);
            return;
        }
        if (mb.Type.PredMode == MbPartPredMode.Intra16x16)
        {
            ReconstructLumaIntra16x16(mb, picture, mbX, mbY, leftMb, topMb);
        }
        else if (mb.Type.PredMode == MbPartPredMode.Intra4x4)
        {
            if (mb.TransformSize8x8)
            {
                ReconstructLumaIntra8x8(mb, picture, mbX, mbY, leftMb, topMb, topRightMb);
            }
            else
            {
                ReconstructLumaIntra4x4(mb, picture, mbX, mbY, leftMb, topMb, topRightMb);
            }
        }
        else if (mb.Type.PredMode == MbPartPredMode.PredL0)
        {
            if (refPicListL0 is null || refPicListL0.Count == 0)
                throw new InvalidOperationException("PredL0 reconstruction requires a non-empty L0 reference list");
            ReconstructLumaInterP16x16(mb, picture, refPicListL0, mbX, mbY, predWeights);
        }
        else
        {
            throw new NotSupportedException($"MacroblockReconstructor: PredMode {mb.Type.PredMode} not supported");
        }

        int qPc = ChromaQp(mb.QpY, chromaQpIndexOffset);
        if (mb.Type.PredMode == MbPartPredMode.PredL0)
        {
            if (refPicListL0 is null || refPicListL0.Count == 0)
                throw new InvalidOperationException("PredL0 chroma reconstruction requires a non-empty L0 reference list");
            ReconstructChromaInter(mb, picture, refPicListL0, mbX, mbY, qPc, predWeights);
        }
        else
        {
            ReconstructChroma(mb, picture, mbX, mbY, qPc, leftMb, topMb);
        }
    }

    // ---------------- I_PCM ----------------
    private static void ReconstructPcm(Macroblock mb, DecodedPicture picture, int mbX, int mbY)
    {
        int px0 = mbX * 16, py0 = mbY * 16;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                picture.Y[(py0 + y) * picture.BufferWidth + (px0 + x)] = mb.PcmLuma[y * 16 + x];
        int cx0 = mbX * 8, cy0 = mbY * 8;
        int cStride = picture.ChromaBufferWidth;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                picture.U[(cy0 + y) * cStride + (cx0 + x)] = mb.PcmCb[y * 8 + x];
                picture.V[(cy0 + y) * cStride + (cx0 + x)] = mb.PcmCr[y * 8 + x];
            }
    }

    // ---------------- Luma (Intra_16x16) ----------------
    private static void ReconstructLumaIntra16x16(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY,
        Macroblock? leftMb, Macroblock? topMb)
    {
        // Gather luma neighbor samples from the already-decoded picture. Per spec
        // §6.4.11.1, a neighbor MB in a different slice is unavailable — we receive
        // null for cross-slice neighbors here, so use the MB pointers rather than
        // raw mbX/mbY > 0 checks.
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[16];
        bool topAvail = topMb != null;
        bool leftAvail = leftMb != null;
        bool topLeftAvail = topAvail && leftAvail;
        byte topLeft = 0;
        if (topAvail)
        {
            int srcY = mbY * 16 - 1;
            int srcX0 = mbX * 16;
            for (int i = 0; i < 16; i++) top[i] = picture.Y[srcY * picture.BufferWidth + srcX0 + i];
        }
        if (leftAvail)
        {
            int srcX = mbX * 16 - 1;
            int srcY0 = mbY * 16;
            for (int i = 0; i < 16; i++) left[i] = picture.Y[(srcY0 + i) * picture.BufferWidth + srcX];
        }
        if (topLeftAvail)
        {
            topLeft = picture.Y[(mbY * 16 - 1) * picture.BufferWidth + (mbX * 16 - 1)];
        }

        Span<byte> predBlock = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            mb.Type.I16x16PredMode,
            top, topAvail, left, leftAvail,
            topLeft, topLeftAvail,
            predBlock);

        // Inverse-Hadamard + dequant the DC luma block.
        Span<int> dc = stackalloc int[16];
        // mb.LumaDc holds 16 values in zig-zag scan order (per CAVLC scan with maxNumCoeff=16).
        ScanOrder.Unzigzag4x4(mb.LumaDc, dc);
        InverseTransform.InverseHadamard4x4(dc);
        Quantization.DequantLumaDc(dc, mb.QpY);

        // For each of 16 4x4 luma sub-blocks, combine DC + AC, inverse transform, add to prediction.
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int i = 0; i < 16; i++)
        {
            // DC value at the matching position in the Hadamard-decoded block.
            // Per spec, the i-th block's DC sits at position (dcX, dcY) in the 4x4 DC block,
            // where (dcX, dcY) is the block's 4x4-grid coordinate.
            (int blkX, int blkY) = MacroblockParser.LumaBlockPos[i];
            int dcValue = dc[blkY * 4 + blkX];

            bool acCoded = (mb.CbpLuma & (1 << (i >> 2))) != 0;

            // mb.Luma[i, 0..14] are AC coefficients in scan order positions 1..15 of the 4x4 block.
            // Build a scan-order array where position 0 = DC, positions 1..15 = AC.
            coeffsScan[0] = dcValue;
            if (acCoded)
            {
                for (int k = 0; k < 15; k++) coeffsScan[k + 1] = mb.Luma[i, k];
            }
            else
            {
                for (int k = 1; k < 16; k++) coeffsScan[k] = 0;
            }

            ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);

            // Dequant AC (positions 1..15 in raster — but uniformly applying Dequant4x4Ac
            // is fine: position 0 is the DC and gets multiplied too, but we have *already*
            // applied the DC dequant. Trick: temporarily zero the DC, dequant the AC, restore DC.
            int dcSaved = coeffsRaster[0];
            coeffsRaster[0] = 0;
            Quantization.Dequant4x4Ac(coeffsRaster, mb.QpY);
            coeffsRaster[0] = dcSaved;

            InverseTransform.Inverse4x4(coeffsRaster);

            // Add to prediction and clip into the picture. The 4x4 block lives at
            // (mbX*16 + blkX*4, mbY*16 + blkY*4).
            int px0 = mbX * 16 + blkX * 4;
            int py0 = mbY * 16 + blkY * 4;
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int pred = predBlock[(blkY * 4 + yy) * 16 + (blkX * 4 + xx)];
                    int v = pred + coeffsRaster[yy * 4 + xx];
                    picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                }
        }
    }

    // ---------------- Luma (I_NxN / Intra_4x4) ----------------
    private static void ReconstructLumaIntra4x4(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb)
    {
        Span<byte> top = stackalloc byte[8];
        Span<byte> left = stackalloc byte[4];
        Span<byte> predBlock = stackalloc byte[16];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = MacroblockParser.LumaBlockPos[i];
            int px0 = mbX * 16 + bx * 4;
            int py0 = mbY * 16 + by * 4;

            // Resolve actual prediction mode from neighbor blocks (spec §8.3.1.1).
            int predicted = PredictIntra4x4Mode(mb, leftMb, topMb, bx, by);
            int raw = mb.Intra4x4PredMode[i];
            int actual = raw < 0
                ? predicted
                : (raw < predicted ? raw : raw + 1);
            mb.Intra4x4Mode[i] = actual;

            // Gather neighbor samples (top, top-right, left, top-left). At MB-internal
            // 4x4 boundaries the neighbor is always in the current MB; at MB edges it
            // must come from the matching neighbor MB, which is null for cross-slice
            // (spec §6.4.11.1) and thus unavailable.
            bool topAvail = by > 0 || topMb != null;
            bool leftAvail = bx > 0 || leftMb != null;
            bool topLeftAvail;
            if (by > 0 && bx > 0) topLeftAvail = true;
            else if (by == 0 && bx == 0) topLeftAvail = leftMb != null && topMb != null;
            else if (by == 0) topLeftAvail = topMb != null; // top-left sample is in topMb's bottom row
            else topLeftAvail = leftMb != null; // top-left sample is in leftMb's right column
            bool topRightAvail = ComputeTopRightAvail(i, bx, by, mbX, mbY,
                picture.BufferWidth, leftMb, topMb, topRightMb);

            if (topAvail)
            {
                int srcY = py0 - 1;
                for (int k = 0; k < 4; k++) top[k] = picture.Y[srcY * picture.BufferWidth + px0 + k];
                if (topRightAvail)
                {
                    for (int k = 0; k < 4; k++) top[4 + k] = picture.Y[srcY * picture.BufferWidth + px0 + 4 + k];
                }
                else
                {
                    byte fill = top[3];
                    top[4] = fill; top[5] = fill; top[6] = fill; top[7] = fill;
                }
            }
            if (leftAvail)
            {
                int srcX = px0 - 1;
                for (int k = 0; k < 4; k++) left[k] = picture.Y[(py0 + k) * picture.BufferWidth + srcX];
            }
            byte topLeft = topLeftAvail
                ? picture.Y[(py0 - 1) * picture.BufferWidth + (px0 - 1)]
                : (byte)0;

            // Predict
            IntraPrediction.PredictIntra4x4(
                (IntraPrediction.Intra4x4Mode)actual,
                top, topAvail, topRightAvail,
                left, leftAvail,
                topLeft, topLeftAvail,
                predBlock);

            // Residual: 16 zigzag-scanned coefficients in mb.Luma[i, 0..15]
            bool coded = (mb.CbpLuma & (1 << (i >> 2))) != 0;
            if (coded)
            {
                for (int k = 0; k < 16; k++) coeffsScan[k] = mb.Luma[i, k];
                ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);
                Quantization.Dequant4x4Ac(coeffsRaster, mb.QpY);
                InverseTransform.Inverse4x4(coeffsRaster);
            }
            else
            {
                coeffsRaster.Clear();
            }

            // Add prediction + residual, clip, write to picture.
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int v = predBlock[yy * 4 + xx] + coeffsRaster[yy * 4 + xx];
                    picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                }
        }
    }

    // ---------------- Luma (I_NxN / Intra_8x8) ----------------
    private static void ReconstructLumaIntra8x8(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb)
    {
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[8];
        Span<byte> ft = stackalloc byte[16];
        Span<byte> fl = stackalloc byte[8];
        Span<byte> predBlock = stackalloc byte[64];
        Span<int> coeffsScan = stackalloc int[64];
        Span<int> coeffsRaster = stackalloc int[64];

        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = i8 & 1;       // 8x8 block x within MB (0 or 1)
            int by = (i8 >> 1) & 1; // 8x8 block y within MB
            int px0 = mbX * 16 + bx * 8;
            int py0 = mbY * 16 + by * 8;

            // Per spec §6.4.11.1: a neighbor MB in a different slice is unavailable.
            // For inner 8x8 blocks (by>0 or bx>0) the neighbor is in the current MB.
            bool topAvail = by > 0 || topMb != null;
            bool leftAvail = bx > 0 || leftMb != null;
            bool topLeftAvail;
            if (by > 0 && bx > 0) topLeftAvail = true;
            else if (by == 0 && bx == 0) topLeftAvail = leftMb != null && topMb != null;
            else if (by == 0) topLeftAvail = topMb != null;
            else topLeftAvail = leftMb != null;
            // Top-right availability for 8x8 blocks within an MB:
            //   i8 == 0 (TL): TR samples are in top MB (row above), available iff topMb exists.
            //   i8 == 1 (TR): TR samples are in top-right MB, available iff topRightMb exists.
            //   i8 == 2 (BL): TR samples are within current MB (already-decoded TR 8x8 block).
            //   i8 == 3 (BR): TR samples are in the right-neighbor MB, not yet decoded.
            bool topRightAvail;
            if (i8 == 0) topRightAvail = topMb != null;
            else if (i8 == 1) topRightAvail = topRightMb != null && mbX * 16 + 16 < picture.BufferWidth;
            else if (i8 == 2) topRightAvail = true;
            else topRightAvail = false;

            // Gather neighbor samples from the already-reconstructed picture.
            if (topAvail)
            {
                int srcY = py0 - 1;
                for (int k = 0; k < 8; k++) top[k] = picture.Y[srcY * picture.BufferWidth + px0 + k];
                if (topRightAvail)
                {
                    for (int k = 0; k < 8; k++) top[8 + k] = picture.Y[srcY * picture.BufferWidth + px0 + 8 + k];
                }
                else
                {
                    byte fill = top[7];
                    for (int k = 0; k < 8; k++) top[8 + k] = fill;
                }
            }
            if (leftAvail)
            {
                int srcX = px0 - 1;
                for (int k = 0; k < 8; k++) left[k] = picture.Y[(py0 + k) * picture.BufferWidth + srcX];
            }
            byte topLeft = topLeftAvail ? picture.Y[(py0 - 1) * picture.BufferWidth + (px0 - 1)] : (byte)0;

            // Mandatory [1,2,1]/4 filter on neighbor samples.
            IntraPrediction.Intra8x8PredFilter(
                top, topAvail, topRightAvail,
                left, leftAvail,
                topLeft, topLeftAvail,
                ft, fl, out byte ftl);

            // Resolve prediction mode from neighbor 8x8 blocks (spec §8.3.1.1 generalized).
            int predicted = PredictIntra8x8Mode(mb, leftMb, topMb, bx, by);
            int raw = mb.Intra8x8PredMode[i8];
            int actual = raw < 0
                ? predicted
                : (raw < predicted ? raw : raw + 1);
            mb.Intra8x8Mode[i8] = actual;

            IntraPrediction.PredictIntra8x8(
                (IntraPrediction.Intra8x8Mode)actual,
                ft, topAvail, fl, leftAvail, ftl, topLeftAvail, predBlock);

            bool coded = (mb.CbpLuma & (1 << i8)) != 0;
            if (coded)
            {
                for (int k = 0; k < 64; k++) coeffsScan[k] = mb.Luma8x8[i8, k];
                ScanOrder.Unzigzag8x8(coeffsScan, coeffsRaster);
                Quantization.Dequant8x8(coeffsRaster, mb.QpY);
                InverseTransform.Inverse8x8(coeffsRaster);
            }
            else
            {
                coeffsRaster.Clear();
            }

            for (int yy = 0; yy < 8; yy++)
                for (int xx = 0; xx < 8; xx++)
                {
                    int v = predBlock[yy * 8 + xx] + coeffsRaster[yy * 8 + xx];
                    picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                }
        }
    }

    /// <summary>Predicted Intra_8x8 mode from neighbor 8x8 blocks (spec §8.3.1.1 adapted).</summary>
    private static int PredictIntra8x8Mode(
        Macroblock mb, Macroblock? leftMb, Macroblock? topMb, int bx, int by)
    {
        int leftMode = NeighborIntra8x8Mode(mb, leftMb, topMb, bx - 1, by);
        int topMode = NeighborIntra8x8Mode(mb, leftMb, topMb, bx, by - 1);
        if (leftMode < 0 || topMode < 0) return 2; // DC fallback
        return leftMode < topMode ? leftMode : topMode;
    }

    private static int NeighborIntra8x8Mode(
        Macroblock cur, Macroblock? leftMb, Macroblock? topMb, int bx, int by)
    {
        if (bx >= 0 && by >= 0)
        {
            return cur.Intra8x8Mode[by * 2 + bx];
        }
        if (bx < 0)
        {
            if (leftMb is null) return -1;
            if (leftMb.Type.PredMode != MbPartPredMode.Intra4x4) return 2;
            if (leftMb.TransformSize8x8)
            {
                return leftMb.Intra8x8Mode[by * 2 + 1];
            }
            // Neighbor uses Intra_4x4: use the 4x4 block at (3, by*2) of the left MB
            // (the upper-right 4x4 within the corresponding left 8x8 block).
            return leftMb.Intra4x4Mode[MacroblockParser.SpatialToRaster(3, by * 2)];
        }
        if (by < 0)
        {
            if (topMb is null) return -1;
            if (topMb.Type.PredMode != MbPartPredMode.Intra4x4) return 2;
            if (topMb.TransformSize8x8)
            {
                return topMb.Intra8x8Mode[1 * 2 + bx];
            }
            return topMb.Intra4x4Mode[MacroblockParser.SpatialToRaster(bx * 2, 3)];
        }
        return -1;
    }

    /// <summary>
    /// Predicted Intra_4x4 mode from neighbor blocks (spec §8.3.1.1).
    /// Returns 2 (DC) if either neighbor is unavailable or non-Intra_4x4.
    /// </summary>
    private static int PredictIntra4x4Mode(
        Macroblock mb, Macroblock? leftMb, Macroblock? topMb, int bx, int by)
    {
        int leftMode = NeighborIntra4x4Mode(mb, leftMb, topMb, bx - 1, by, isLeft: true);
        int topMode = NeighborIntra4x4Mode(mb, leftMb, topMb, bx, by - 1, isLeft: false);

        if (leftMode < 0 || topMode < 0) return 2; // DC fallback
        return leftMode < topMode ? leftMode : topMode;
    }

    private static int NeighborIntra4x4Mode(
        Macroblock cur, Macroblock? leftMb, Macroblock? topMb,
        int bx, int by, bool isLeft)
    {
        // Returns -1 only when the neighbor MB is *unavailable*. When the neighbor MB
        // is available but not Intra_4x4 (e.g. Intra_16x16), the spec treats its mode
        // as DC (2) for the purpose of predicting the current block's mode.
        if (bx >= 0 && by >= 0)
        {
            int neighborIdx = MacroblockParser.SpatialToRaster(bx, by);
            return cur.Intra4x4Mode[neighborIdx];
        }
        if (bx < 0)
        {
            if (leftMb is null) return -1;
            if (leftMb.Type.PredMode != MbPartPredMode.Intra4x4) return 2;
            if (leftMb.TransformSize8x8)
            {
                // Neighbor is Intra_8x8: use the 8x8-block mode of the block containing
                // the corresponding 4x4 position. Spec §8.3.1.1.
                // 4x4 (bx=3, by) lies in 8x8 block at (1, by>>1).
                return leftMb.Intra8x8Mode[(by >> 1) * 2 + 1];
            }
            return leftMb.Intra4x4Mode[MacroblockParser.SpatialToRaster(3, by)];
        }
        if (by < 0)
        {
            if (topMb is null) return -1;
            if (topMb.Type.PredMode != MbPartPredMode.Intra4x4) return 2;
            if (topMb.TransformSize8x8)
            {
                // 4x4 (bx, by=3) lies in 8x8 block at (bx>>1, 1).
                return topMb.Intra8x8Mode[1 * 2 + (bx >> 1)];
            }
            return topMb.Intra4x4Mode[MacroblockParser.SpatialToRaster(bx, 3)];
        }
        _ = isLeft;
        return -1;
    }

    /// <summary>
    /// Whether the top-right 4-sample neighbor of an Intra_4x4 block has already
    /// been reconstructed. Some in-MB scan positions never have top-right available
    /// (i ∈ {3, 7, 11, 13, 15}) because the block to the upper-right is decoded later
    /// in scan order or lives in the not-yet-decoded right-neighbor MB.
    /// </summary>
    private static bool ComputeTopRightAvail(
        int i, int bx, int by, int mbX, int mbY, int pictureWidth,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb)
    {
        _ = leftMb;
        // Out-of-MB cases (when bx == 3) cross the right MB boundary.
        if (bx == 3)
        {
            if (by == 0)
            {
                // Top-right is in the top-right neighbor MB; available iff that MB exists.
                if (topRightMb is null) return false;
                return mbX * 16 + 16 < pictureWidth;
            }
            // Other right-column blocks (by > 0): top-right lies in the right-neighbor
            // MB which hasn't been decoded yet.
            return false;
        }

        // In-MB cases. Per the standard scan-order rule, these are unavailable:
        //   i == 3 (block (1,1)): TR at (2,0) = i=4, not yet decoded
        //   i == 11 (block (1,3)): TR at (2,2) = i=12, not yet decoded
        if (i == 3 || i == 11) return false;

        // Top edge (by == 0) and bx < 3: TR is in the top neighbor MB, available iff topMb exists.
        if (by == 0)
        {
            return topMb != null;
        }

        // Otherwise the TR block is within the same MB and already decoded.
        return true;
    }

    // ---------------- Luma (PredL0, all P partition shapes) ----------------
    private static void ReconstructLumaInterP16x16(
        Macroblock mb, DecodedPicture picture, IReadOnlyList<DecodedPicture> refPicListL0, int mbX, int mbY,
        PredWeightTable? predWeights)
    {
        // Build the 16x16 prediction by running MC for each motion partition.
        Span<byte> predBlock = stackalloc byte[256];
        Span<byte> partPred = stackalloc byte[256]; // worst case 16x16
        foreach (var part in mb.InterPartitions)
        {
            int n = part.Width * part.Height;
            Span<byte> partOut = partPred[..n];
            int refIdx = part.RefIdxL0 < refPicListL0.Count ? part.RefIdxL0 : 0;
            var refPic = refPicListL0[refIdx];
            MotionCompensation.LumaPredict(
                refPic.Y, refPic.BufferWidth, refPic.BufferHeight,
                mbX * 16 + part.X, mbY * 16 + part.Y,
                part.MvL0X, part.MvL0Y,
                part.Width, part.Height, partOut);
            // Apply weighted prediction (§8.4.2.3.2) when the slice carries a pred_weight_table.
            if (predWeights is not null && refIdx < predWeights.LumaWeightL0.Length)
            {
                int wd = predWeights.LumaLog2WeightDenom;
                int w = predWeights.LumaWeightL0[refIdx];
                int o = predWeights.LumaOffsetL0[refIdx];
                ApplyExplicitWeightL0(partOut, w, o, wd);
            }
            for (int yy = 0; yy < part.Height; yy++)
                for (int xx = 0; xx < part.Width; xx++)
                    predBlock[(part.Y + yy) * 16 + (part.X + xx)] = partOut[yy * part.Width + xx];
        }

        // Residual: 8x8 transform path when transform_size_8x8_flag is set, else 16 4x4 blocks.
        if (mb.TransformSize8x8)
        {
            ApplyLumaInter8x8Residual(mb, picture, mbX, mbY, predBlock);
        }
        else
        {
            Span<int> coeffsScan = stackalloc int[16];
            Span<int> coeffsRaster = stackalloc int[16];

            for (int i = 0; i < 16; i++)
            {
                (int bx, int by) = MacroblockParser.LumaBlockPos[i];
                int px0 = mbX * 16 + bx * 4;
                int py0 = mbY * 16 + by * 4;

                bool coded = (mb.CbpLuma & (1 << (i >> 2))) != 0;
                if (coded)
                {
                    for (int k = 0; k < 16; k++) coeffsScan[k] = mb.Luma[i, k];
                    ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);
                    Quantization.Dequant4x4Ac(coeffsRaster, mb.QpY);
                    InverseTransform.Inverse4x4(coeffsRaster);
                }
                else
                {
                    coeffsRaster.Clear();
                }

                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int pred = predBlock[(by * 4 + yy) * 16 + (bx * 4 + xx)];
                        int v = pred + coeffsRaster[yy * 4 + xx];
                        picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                    }
            }
        }
    }

    /// <summary>Add inter 8x8 residual onto a 16x16 MC prediction buffer and write to the picture.</summary>
    private static void ApplyLumaInter8x8Residual(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY, Span<byte> predBlock)
    {
        Span<int> coeffsScan = stackalloc int[64];
        Span<int> coeffsRaster = stackalloc int[64];
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = i8 & 1, by = (i8 >> 1) & 1;
            int px0 = mbX * 16 + bx * 8;
            int py0 = mbY * 16 + by * 8;
            bool coded = (mb.CbpLuma & (1 << i8)) != 0;
            if (coded)
            {
                for (int k = 0; k < 64; k++) coeffsScan[k] = mb.Luma8x8[i8, k];
                ScanOrder.Unzigzag8x8(coeffsScan, coeffsRaster);
                Quantization.Dequant8x8(coeffsRaster, mb.QpY);
                InverseTransform.Inverse8x8(coeffsRaster);
            }
            else
            {
                coeffsRaster.Clear();
            }
            for (int yy = 0; yy < 8; yy++)
                for (int xx = 0; xx < 8; xx++)
                {
                    int pred = predBlock[(by * 8 + yy) * 16 + (bx * 8 + xx)];
                    int v = pred + coeffsRaster[yy * 8 + xx];
                    picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                }
        }
    }

    /// <summary>Apply explicit weighted bipred sample combination (spec §8.4.2.3.2):
    /// pred = Clip1( ((p0*w0 + p1*w1 + (1&lt;&lt;wd)) >> (wd+1)) + ((o0 + o1 + 1) >> 1) ) when wd>=1,
    /// else Clip1(p0*w0 + p1*w1 + ((o0 + o1 + 1) >> 1)).</summary>
    private static void ApplyExplicitBipredWeights(
        Span<byte> p0, Span<byte> p1, Span<byte> dst, int w0, int w1, int o0, int o1, int wd)
    {
        int offsetCombined = (o0 + o1 + 1) >> 1;
        if (wd >= 1)
        {
            int round = 1 << wd;
            int shift = wd + 1;
            for (int i = 0; i < dst.Length; i++)
            {
                int v = ((p0[i] * w0 + p1[i] * w1 + round) >> shift) + offsetCombined;
                dst[i] = ClipByte(v);
            }
        }
        else
        {
            for (int i = 0; i < dst.Length; i++)
            {
                int v = p0[i] * w0 + p1[i] * w1 + offsetCombined;
                dst[i] = ClipByte(v);
            }
        }
    }

    /// <summary>Apply explicit single-list weighted prediction in place (spec §8.4.2.3.2).</summary>
    private static void ApplyExplicitWeightL0(Span<byte> samples, int w, int o, int denom)
    {
        if (denom != 0)
        {
            int round = 1 << (denom - 1);
            for (int i = 0; i < samples.Length; i++)
            {
                int v = ((samples[i] * w + round) >> denom) + o;
                samples[i] = ClipByte(v);
            }
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                int v = samples[i] * w + o;
                samples[i] = ClipByte(v);
            }
        }
    }

    /// <summary>Copy 16x16 luma block from reference picture starting at (srcX, srcY) using edge replication for off-picture samples.</summary>
    private static void CopyLumaWithEdgeReplication(DecodedPicture src, int srcX, int srcY, Span<byte> dst16x16)
    {
        int W = src.Width, H = src.Height;
        for (int yy = 0; yy < 16; yy++)
        {
            int yy2 = srcY + yy;
            if (yy2 < 0) yy2 = 0; else if (yy2 >= H) yy2 = H - 1;
            for (int xx = 0; xx < 16; xx++)
            {
                int xx2 = srcX + xx;
                if (xx2 < 0) xx2 = 0; else if (xx2 >= W) xx2 = W - 1;
                dst16x16[yy * 16 + xx] = src.Y[yy2 * W + xx2];
            }
        }
    }

    /// <summary>Reconstruct chroma for an inter MB. Chroma MV is derived from luma MV.</summary>
    private static void ReconstructChromaInter(
        Macroblock mb, DecodedPicture picture, IReadOnlyList<DecodedPicture> refPicListL0, int mbX, int mbY, int qPc,
        PredWeightTable? predWeights = null)
    {
        Span<byte> predBlock = stackalloc byte[64];
        Span<byte> partPred = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int comp = 0; comp < 2; comp++)
        {
            byte[] plane = comp == 0 ? picture.U : picture.V;
            int stride = picture.ChromaBufferWidth;
            // For each motion partition, do chroma MC on the corresponding 8x8 region scaled to half size.
            foreach (var part in mb.InterPartitions)
            {
                int refIdx = part.RefIdxL0 < refPicListL0.Count ? part.RefIdxL0 : 0;
                var refPic = refPicListL0[refIdx];
                byte[] refPlane = comp == 0 ? refPic.U : refPic.V;
                int cx = part.X / 2;
                int cy = part.Y / 2;
                int cw = part.Width / 2;
                int ch = part.Height / 2;
                int n = cw * ch;
                Span<byte> partOut = partPred[..n];
                MotionCompensation.ChromaPredict(
                    refPlane, refPic.ChromaBufferWidth, refPic.ChromaBufferHeight,
                    mbX * 8 + cx, mbY * 8 + cy,
                    part.MvL0X, part.MvL0Y,
                    cw, ch, partOut);
                if (predWeights is not null && refIdx < predWeights.ChromaWeightL0.GetLength(0))
                {
                    int wd = predWeights.ChromaLog2WeightDenom;
                    int w = predWeights.ChromaWeightL0[refIdx, comp];
                    int o = predWeights.ChromaOffsetL0[refIdx, comp];
                    ApplyExplicitWeightL0(partOut, w, o, wd);
                }
                for (int yy = 0; yy < ch; yy++)
                    for (int xx = 0; xx < cw; xx++)
                        predBlock[(cy + yy) * 8 + (cx + xx)] = partOut[yy * cw + xx];
            }

            dc.Clear();
            if ((mb.CbpChroma & 3) != 0)
            {
                for (int k = 0; k < 4; k++) dc[k] = mb.ChromaDc[comp, k];
            }
            InverseTransform.InverseHadamard2x2(dc);
            Quantization.DequantChromaDc(dc, qPc);

            for (int b = 0; b < 4; b++)
            {
                int subX = b & 1;
                int subY = (b >> 1) & 1;
                int dcValue = dc[subY * 2 + subX];
                bool acCoded = (mb.CbpChroma & 2) != 0;
                coeffsScan[0] = dcValue;
                if (acCoded)
                {
                    for (int k = 0; k < 15; k++) coeffsScan[k + 1] = mb.ChromaAc[comp, b, k];
                }
                else
                {
                    for (int k = 1; k < 16; k++) coeffsScan[k] = 0;
                }
                ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);
                int dcSaved = coeffsRaster[0];
                coeffsRaster[0] = 0;
                Quantization.Dequant4x4Ac(coeffsRaster, qPc);
                coeffsRaster[0] = dcSaved;
                InverseTransform.Inverse4x4(coeffsRaster);

                int px0 = mbX * 8 + subX * 4;
                int py0 = mbY * 8 + subY * 4;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int pred = predBlock[(subY * 4 + yy) * 8 + (subX * 4 + xx)];
                        int v = pred + coeffsRaster[yy * 4 + xx];
                        plane[(py0 + yy) * stride + (px0 + xx)] = ClipByte(v);
                    }
            }
        }
    }

    private static void CopyChromaWithEdgeReplication(byte[] src, int W, int H, int srcX, int srcY, Span<byte> dst8x8)
    {
        for (int yy = 0; yy < 8; yy++)
        {
            int yy2 = srcY + yy;
            if (yy2 < 0) yy2 = 0; else if (yy2 >= H) yy2 = H - 1;
            for (int xx = 0; xx < 8; xx++)
            {
                int xx2 = srcX + xx;
                if (xx2 < 0) xx2 = 0; else if (xx2 >= W) xx2 = W - 1;
                dst8x8[yy * 8 + xx] = src[yy2 * W + xx2];
            }
        }
    }

    // ---------------- Chroma ----------------
    private static void ReconstructChroma(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY, int qPc,
        Macroblock? leftMb, Macroblock? topMb)
    {
        Span<byte> predBlock = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        Span<byte> top8 = stackalloc byte[8];
        Span<byte> left8 = stackalloc byte[8];
        // Cross-slice neighbors are passed as null (spec §6.4.11.1) — use the MB
        // pointers rather than raw mbX/mbY > 0.
        bool topAvail = topMb != null;
        bool leftAvail = leftMb != null;
        bool topLeftAvail = topAvail && leftAvail;

        for (int comp = 0; comp < 2; comp++)
        {
            byte[] plane = comp == 0 ? picture.U : picture.V;
            int stride = picture.ChromaBufferWidth;
            if (topAvail)
            {
                int srcY = mbY * 8 - 1;
                int srcX0 = mbX * 8;
                for (int i = 0; i < 8; i++) top8[i] = plane[srcY * stride + srcX0 + i];
            }
            if (leftAvail)
            {
                int srcX = mbX * 8 - 1;
                int srcY0 = mbY * 8;
                for (int i = 0; i < 8; i++) left8[i] = plane[(srcY0 + i) * stride + srcX];
            }
            byte topLeft = topLeftAvail
                ? plane[(mbY * 8 - 1) * stride + (mbX * 8 - 1)]
                : (byte)0;

            IntraPrediction.PredictChroma8x8(
                mb.ChromaPredMode,
                top8, topAvail, left8, leftAvail,
                topLeft, topLeftAvail,
                predBlock);

            // Chroma DC: 4 values in [TL, TR, BL, BR] order (raster).
            dc.Clear();
            if ((mb.CbpChroma & 3) != 0)
            {
                for (int k = 0; k < 4; k++) dc[k] = mb.ChromaDc[comp, k];
            }
            InverseTransform.InverseHadamard2x2(dc);
            Quantization.DequantChromaDc(dc, qPc);

            // 4 chroma 4x4 blocks per component, arranged in 2x2:
            //   blockIdx 0=TL, 1=TR, 2=BL, 3=BR
            for (int b = 0; b < 4; b++)
            {
                int subX = b & 1;
                int subY = (b >> 1) & 1;
                int dcValue = dc[subY * 2 + subX];

                bool acCoded = (mb.CbpChroma & 2) != 0;
                coeffsScan[0] = dcValue;
                if (acCoded)
                {
                    for (int k = 0; k < 15; k++) coeffsScan[k + 1] = mb.ChromaAc[comp, b, k];
                }
                else
                {
                    for (int k = 1; k < 16; k++) coeffsScan[k] = 0;
                }

                ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);

                int dcSaved = coeffsRaster[0];
                coeffsRaster[0] = 0;
                Quantization.Dequant4x4Ac(coeffsRaster, qPc);
                coeffsRaster[0] = dcSaved;

                InverseTransform.Inverse4x4(coeffsRaster);

                int px0 = mbX * 8 + subX * 4;
                int py0 = mbY * 8 + subY * 4;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int pred = predBlock[(subY * 4 + yy) * 8 + (subX * 4 + xx)];
                        int v = pred + coeffsRaster[yy * 4 + xx];
                        plane[(py0 + yy) * stride + (px0 + xx)] = ClipByte(v);
                    }
            }
        }
    }

    private static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Implicit weighted bipred weights per spec §8.4.2.3.2 (weighted_bipred_idc==2).
    /// Returns (w0, w1) on a /64 scale to combine as (w0*p0 + w1*p1 + 32) >> 6.
    /// Short-term refs only — long-term refs use the equal-weight fallback (32,32).</summary>
    private static (int w0, int w1) ImplicitBipredWeights(int currPoc, int pocL0, int pocL1)
    {
        int diff = pocL1 - pocL0;
        if (diff == 0) return (32, 32);
        int td = Clip3(-128, 127, pocL1 - pocL0);
        int tb = Clip3(-128, 127, currPoc - pocL0);
        if (td == 0) return (32, 32);
        int tx = (16384 + (Math.Abs(td) >> 1)) / td;
        int dsf = Clip3(-1024, 1023, (tb * tx + 32) >> 6);
        if (dsf < -256 || dsf > 515) return (32, 32);
        int w1 = dsf >> 2;
        int w0 = 64 - w1;
        return (w0, w1);
    }

    private static int Clip3(int lo, int hi, int v) => v < lo ? lo : v > hi ? hi : v;

    // ---------------- Luma (B-slice inter) ----------------
    private static void ReconstructLumaInterB(
        Macroblock mb, DecodedPicture picture,
        IReadOnlyList<DecodedPicture> refL0, IReadOnlyList<DecodedPicture>? refL1, int mbX, int mbY,
        bool implicitBipred, PredWeightTable? explicitWeights)
    {
        Span<byte> predBlock = stackalloc byte[256];
        Span<byte> p0 = stackalloc byte[256];
        Span<byte> p1 = stackalloc byte[256];
        Span<byte> outBuf256 = stackalloc byte[256];
        foreach (var part in mb.BInterPartitions)
        {
            int n = part.Width * part.Height;
            Span<byte> outBuf = outBuf256[..n];

            bool useL0 = part.Dir == BPredDir.L0 || part.Dir == BPredDir.Bi;
            bool useL1 = part.Dir == BPredDir.L1 || part.Dir == BPredDir.Bi;
            if (useL0)
            {
                int ri = part.RefIdxL0 < 0 ? 0 : (part.RefIdxL0 < refL0.Count ? part.RefIdxL0 : 0);
                var rp = refL0[ri];
                MotionCompensation.LumaPredict(rp.Y, rp.BufferWidth, rp.BufferHeight,
                    mbX * 16 + part.X, mbY * 16 + part.Y,
                    part.MvL0X, part.MvL0Y, part.Width, part.Height, p0[..n]);
            }
            if (useL1)
            {
                if (refL1 is null || refL1.Count == 0)
                    throw new InvalidOperationException("B-inter L1 reconstruction needs non-empty L1 list");
                int ri = part.RefIdxL1 < 0 ? 0 : (part.RefIdxL1 < refL1.Count ? part.RefIdxL1 : 0);
                var rp = refL1[ri];
                MotionCompensation.LumaPredict(rp.Y, rp.BufferWidth, rp.BufferHeight,
                    mbX * 16 + part.X, mbY * 16 + part.Y,
                    part.MvL1X, part.MvL1Y, part.Width, part.Height, p1[..n]);
            }

            if (useL0 && useL1)
            {
                int ri0bi = part.RefIdxL0 < 0 ? 0 : (part.RefIdxL0 < refL0.Count ? part.RefIdxL0 : 0);
                int ri1bi = part.RefIdxL1 < 0 ? 0 : (refL1 != null && part.RefIdxL1 < refL1.Count ? part.RefIdxL1 : 0);
                if (explicitWeights is not null)
                {
                    int wd = explicitWeights.LumaLog2WeightDenom;
                    int w0 = ri0bi < explicitWeights.LumaWeightL0.Length ? explicitWeights.LumaWeightL0[ri0bi] : (1 << wd);
                    int w1 = explicitWeights.LumaWeightL1 != null && ri1bi < explicitWeights.LumaWeightL1.Length ? explicitWeights.LumaWeightL1[ri1bi] : (1 << wd);
                    int o0 = ri0bi < explicitWeights.LumaOffsetL0.Length ? explicitWeights.LumaOffsetL0[ri0bi] : 0;
                    int o1 = explicitWeights.LumaOffsetL1 != null && ri1bi < explicitWeights.LumaOffsetL1.Length ? explicitWeights.LumaOffsetL1[ri1bi] : 0;
                    ApplyExplicitBipredWeights(p0[..n], p1[..n], outBuf, w0, w1, o0, o1, wd);
                }
                else if (implicitBipred)
                {
                    var (w0, w1) = ImplicitBipredWeights(picture.PicOrderCnt, refL0[ri0bi].PicOrderCnt, refL1![ri1bi].PicOrderCnt);
                    for (int i = 0; i < n; i++)
                    {
                        int v = (w0 * p0[i] + w1 * p1[i] + 32) >> 6;
                        outBuf[i] = ClipByte(v);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++) outBuf[i] = (byte)((p0[i] + p1[i] + 1) >> 1);
                }
            }
            else if (useL0)
            {
                p0[..n].CopyTo(outBuf);
                // §8.4.2.3.2: uni-directional predictions in an explicit-weighted slice are weighted too.
                if (explicitWeights is not null)
                {
                    int ri = part.RefIdxL0 < 0 ? 0 : part.RefIdxL0;
                    int wd = explicitWeights.LumaLog2WeightDenom;
                    int w = ri < explicitWeights.LumaWeightL0.Length ? explicitWeights.LumaWeightL0[ri] : (1 << wd);
                    int o = ri < explicitWeights.LumaOffsetL0.Length ? explicitWeights.LumaOffsetL0[ri] : 0;
                    ApplyExplicitWeightL0(outBuf[..n], w, o, wd);
                }
            }
            else
            {
                p1[..n].CopyTo(outBuf);
                if (explicitWeights is not null && explicitWeights.LumaWeightL1 is not null)
                {
                    int ri = part.RefIdxL1 < 0 ? 0 : part.RefIdxL1;
                    int wd = explicitWeights.LumaLog2WeightDenom;
                    int w = ri < explicitWeights.LumaWeightL1.Length ? explicitWeights.LumaWeightL1[ri] : (1 << wd);
                    int o = explicitWeights.LumaOffsetL1 != null && ri < explicitWeights.LumaOffsetL1.Length ? explicitWeights.LumaOffsetL1[ri] : 0;
                    ApplyExplicitWeightL0(outBuf[..n], w, o, wd);
                }
            }

            for (int yy = 0; yy < part.Height; yy++)
                for (int xx = 0; xx < part.Width; xx++)
                    predBlock[(part.Y + yy) * 16 + (part.X + xx)] = outBuf[yy * part.Width + xx];
        }

        if (mb.TransformSize8x8)
        {
            ApplyLumaInter8x8Residual(mb, picture, mbX, mbY, predBlock);
        }
        else
        {
            // Add residual per 4x4 block (CBP-gated).
            Span<int> coeffsScan = stackalloc int[16];
            Span<int> coeffsRaster = stackalloc int[16];
            for (int i = 0; i < 16; i++)
            {
                (int bx, int by) = MacroblockParser.LumaBlockPos[i];
                int px0 = mbX * 16 + bx * 4;
                int py0 = mbY * 16 + by * 4;

                bool coded = (mb.CbpLuma & (1 << (i >> 2))) != 0;
                if (coded)
                {
                    for (int k = 0; k < 16; k++) coeffsScan[k] = mb.Luma[i, k];
                    ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);
                    Quantization.Dequant4x4Ac(coeffsRaster, mb.QpY);
                    InverseTransform.Inverse4x4(coeffsRaster);
                }
                else
                {
                    coeffsRaster.Clear();
                }
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int pred = predBlock[(by * 4 + yy) * 16 + (bx * 4 + xx)];
                        int v = pred + coeffsRaster[yy * 4 + xx];
                        picture.Y[(py0 + yy) * picture.BufferWidth + (px0 + xx)] = ClipByte(v);
                    }
            }
        }
    }

    private static void ReconstructChromaInterB(
        Macroblock mb, DecodedPicture picture,
        IReadOnlyList<DecodedPicture> refL0, IReadOnlyList<DecodedPicture>? refL1,
        int mbX, int mbY, int qPc, bool implicitBipred, PredWeightTable? explicitWeights)
    {
        Span<byte> predBlock = stackalloc byte[64];
        Span<byte> p0 = stackalloc byte[64];
        Span<byte> p1 = stackalloc byte[64];
        Span<byte> outBuf64 = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int comp = 0; comp < 2; comp++)
        {
            byte[] plane = comp == 0 ? picture.U : picture.V;
            int stride = picture.ChromaBufferWidth;
            foreach (var part in mb.BInterPartitions)
            {
                int cx = part.X / 2, cy = part.Y / 2, cw = part.Width / 2, ch = part.Height / 2;
                int n = cw * ch;
                Span<byte> outBuf = outBuf64[..n];
                bool useL0 = part.Dir == BPredDir.L0 || part.Dir == BPredDir.Bi;
                bool useL1 = part.Dir == BPredDir.L1 || part.Dir == BPredDir.Bi;
                if (useL0)
                {
                    int ri = part.RefIdxL0 < 0 ? 0 : (part.RefIdxL0 < refL0.Count ? part.RefIdxL0 : 0);
                    var rp = refL0[ri];
                    byte[] refPlane = comp == 0 ? rp.U : rp.V;
                    MotionCompensation.ChromaPredict(refPlane, rp.ChromaBufferWidth, rp.ChromaBufferHeight,
                        mbX * 8 + cx, mbY * 8 + cy, part.MvL0X, part.MvL0Y, cw, ch, p0[..n]);
                }
                if (useL1)
                {
                    if (refL1 is null) throw new InvalidOperationException("B chroma L1 needs ref list");
                    int ri = part.RefIdxL1 < 0 ? 0 : (part.RefIdxL1 < refL1.Count ? part.RefIdxL1 : 0);
                    var rp = refL1[ri];
                    byte[] refPlane = comp == 0 ? rp.U : rp.V;
                    MotionCompensation.ChromaPredict(refPlane, rp.ChromaBufferWidth, rp.ChromaBufferHeight,
                        mbX * 8 + cx, mbY * 8 + cy, part.MvL1X, part.MvL1Y, cw, ch, p1[..n]);
                }
                if (useL0 && useL1)
                {
                    int ri0bi = part.RefIdxL0 < 0 ? 0 : (part.RefIdxL0 < refL0.Count ? part.RefIdxL0 : 0);
                    int ri1bi = part.RefIdxL1 < 0 ? 0 : (refL1 != null && part.RefIdxL1 < refL1.Count ? part.RefIdxL1 : 0);
                    if (explicitWeights is not null)
                    {
                        int wd = explicitWeights.ChromaLog2WeightDenom;
                        int w0 = ri0bi < explicitWeights.ChromaWeightL0.GetLength(0) ? explicitWeights.ChromaWeightL0[ri0bi, comp] : (1 << wd);
                        int w1 = explicitWeights.ChromaWeightL1 != null && ri1bi < explicitWeights.ChromaWeightL1.GetLength(0) ? explicitWeights.ChromaWeightL1[ri1bi, comp] : (1 << wd);
                        int o0 = ri0bi < explicitWeights.ChromaOffsetL0.GetLength(0) ? explicitWeights.ChromaOffsetL0[ri0bi, comp] : 0;
                        int o1 = explicitWeights.ChromaOffsetL1 != null && ri1bi < explicitWeights.ChromaOffsetL1.GetLength(0) ? explicitWeights.ChromaOffsetL1[ri1bi, comp] : 0;
                        ApplyExplicitBipredWeights(p0[..n], p1[..n], outBuf, w0, w1, o0, o1, wd);
                    }
                    else if (implicitBipred)
                    {
                        var (w0, w1) = ImplicitBipredWeights(picture.PicOrderCnt, refL0[ri0bi].PicOrderCnt, refL1![ri1bi].PicOrderCnt);
                        for (int i = 0; i < n; i++)
                        {
                            int v = (w0 * p0[i] + w1 * p1[i] + 32) >> 6;
                            outBuf[i] = ClipByte(v);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n; i++) outBuf[i] = (byte)((p0[i] + p1[i] + 1) >> 1);
                    }
                }
                else if (useL0)
                {
                    p0[..n].CopyTo(outBuf);
                    // §8.4.2.3.2: uni-directional predictions in an explicit-weighted slice are weighted.
                    if (explicitWeights is not null)
                    {
                        int ri = part.RefIdxL0 < 0 ? 0 : part.RefIdxL0;
                        int wd = explicitWeights.ChromaLog2WeightDenom;
                        int w = ri < explicitWeights.ChromaWeightL0.GetLength(0) ? explicitWeights.ChromaWeightL0[ri, comp] : (1 << wd);
                        int o = ri < explicitWeights.ChromaOffsetL0.GetLength(0) ? explicitWeights.ChromaOffsetL0[ri, comp] : 0;
                        ApplyExplicitWeightL0(outBuf[..n], w, o, wd);
                    }
                }
                else
                {
                    p1[..n].CopyTo(outBuf);
                    if (explicitWeights is not null && explicitWeights.ChromaWeightL1 is not null)
                    {
                        int ri = part.RefIdxL1 < 0 ? 0 : part.RefIdxL1;
                        int wd = explicitWeights.ChromaLog2WeightDenom;
                        int w = ri < explicitWeights.ChromaWeightL1.GetLength(0) ? explicitWeights.ChromaWeightL1[ri, comp] : (1 << wd);
                        int o = explicitWeights.ChromaOffsetL1 != null && ri < explicitWeights.ChromaOffsetL1.GetLength(0) ? explicitWeights.ChromaOffsetL1[ri, comp] : 0;
                        ApplyExplicitWeightL0(outBuf[..n], w, o, wd);
                    }
                }
                for (int yy = 0; yy < ch; yy++)
                    for (int xx = 0; xx < cw; xx++)
                        predBlock[(cy + yy) * 8 + (cx + xx)] = outBuf[yy * cw + xx];
            }

            dc.Clear();
            if ((mb.CbpChroma & 3) != 0)
            {
                for (int k = 0; k < 4; k++) dc[k] = mb.ChromaDc[comp, k];
            }
            InverseTransform.InverseHadamard2x2(dc);
            Quantization.DequantChromaDc(dc, qPc);

            for (int b = 0; b < 4; b++)
            {
                int subX = b & 1, subY = (b >> 1) & 1;
                int dcValue = dc[subY * 2 + subX];
                bool acCoded = (mb.CbpChroma & 2) != 0;
                coeffsScan[0] = dcValue;
                if (acCoded) { for (int k = 0; k < 15; k++) coeffsScan[k + 1] = mb.ChromaAc[comp, b, k]; }
                else         { for (int k = 1; k < 16; k++) coeffsScan[k] = 0; }
                ScanOrder.Unzigzag4x4(coeffsScan, coeffsRaster);
                int dcSaved = coeffsRaster[0];
                coeffsRaster[0] = 0;
                Quantization.Dequant4x4Ac(coeffsRaster, qPc);
                coeffsRaster[0] = dcSaved;
                InverseTransform.Inverse4x4(coeffsRaster);

                int px0 = mbX * 8 + subX * 4;
                int py0 = mbY * 8 + subY * 4;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                    {
                        int pred = predBlock[(subY * 4 + yy) * 8 + (subX * 4 + xx)];
                        int v = pred + coeffsRaster[yy * 4 + xx];
                        plane[(py0 + yy) * stride + (px0 + xx)] = ClipByte(v);
                    }
            }
        }
    }
}
