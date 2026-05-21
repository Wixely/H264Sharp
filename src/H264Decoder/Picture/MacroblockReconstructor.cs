using H264Decoder.Prediction;
using H264Decoder.Syntax;
using H264Decoder.Transform;

namespace H264Decoder.Picture;

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
        DecodedPicture? referencePicture = null)
    {
        if (mb.Type.PredMode == MbPartPredMode.Intra16x16)
        {
            ReconstructLumaIntra16x16(mb, picture, mbX, mbY);
        }
        else if (mb.Type.PredMode == MbPartPredMode.Intra4x4)
        {
            ReconstructLumaIntra4x4(mb, picture, mbX, mbY, leftMb, topMb, topRightMb);
        }
        else if (mb.Type.PredMode == MbPartPredMode.PredL0)
        {
            if (referencePicture is null)
                throw new InvalidOperationException("PredL0 reconstruction requires a reference picture");
            ReconstructLumaInterP16x16(mb, picture, referencePicture, mbX, mbY);
        }
        else
        {
            throw new NotSupportedException($"MacroblockReconstructor: PredMode {mb.Type.PredMode} not supported");
        }

        int qPc = ChromaQp(mb.QpY, chromaQpIndexOffset);
        if (mb.Type.PredMode == MbPartPredMode.PredL0)
        {
            if (referencePicture is null)
                throw new InvalidOperationException("PredL0 chroma reconstruction requires a reference picture");
            ReconstructChromaInter(mb, picture, referencePicture, mbX, mbY, qPc);
        }
        else
        {
            ReconstructChroma(mb, picture, mbX, mbY, qPc);
        }
    }

    // ---------------- Luma (Intra_16x16) ----------------
    private static void ReconstructLumaIntra16x16(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY)
    {
        // Gather luma neighbor samples from the already-decoded picture.
        Span<byte> top = stackalloc byte[16];
        Span<byte> left = stackalloc byte[16];
        bool topAvail = mbY > 0;
        bool leftAvail = mbX > 0;
        bool topLeftAvail = topAvail && leftAvail;
        byte topLeft = 0;
        if (topAvail)
        {
            int srcY = mbY * 16 - 1;
            int srcX0 = mbX * 16;
            for (int i = 0; i < 16; i++) top[i] = picture.Y[srcY * picture.Width + srcX0 + i];
        }
        if (leftAvail)
        {
            int srcX = mbX * 16 - 1;
            int srcY0 = mbY * 16;
            for (int i = 0; i < 16; i++) left[i] = picture.Y[(srcY0 + i) * picture.Width + srcX];
        }
        if (topLeftAvail)
        {
            topLeft = picture.Y[(mbY * 16 - 1) * picture.Width + (mbX * 16 - 1)];
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
                    picture.Y[(py0 + yy) * picture.Width + (px0 + xx)] = ClipByte(v);
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

            // Gather neighbor samples (top, top-right, left, top-left).
            bool topAvail = py0 > 0;
            bool leftAvail = px0 > 0;
            bool topLeftAvail = topAvail && leftAvail;
            bool topRightAvail = ComputeTopRightAvail(i, bx, by, mbX, mbY,
                picture.Width, leftMb, topMb, topRightMb);

            if (topAvail)
            {
                int srcY = py0 - 1;
                for (int k = 0; k < 4; k++) top[k] = picture.Y[srcY * picture.Width + px0 + k];
                if (topRightAvail)
                {
                    for (int k = 0; k < 4; k++) top[4 + k] = picture.Y[srcY * picture.Width + px0 + 4 + k];
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
                for (int k = 0; k < 4; k++) left[k] = picture.Y[(py0 + k) * picture.Width + srcX];
            }
            byte topLeft = topLeftAvail
                ? picture.Y[(py0 - 1) * picture.Width + (px0 - 1)]
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
                    picture.Y[(py0 + yy) * picture.Width + (px0 + xx)] = ClipByte(v);
                }
        }
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
            return leftMb.Intra4x4Mode[MacroblockParser.SpatialToRaster(3, by)];
        }
        if (by < 0)
        {
            if (topMb is null) return -1;
            if (topMb.Type.PredMode != MbPartPredMode.Intra4x4) return 2;
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

    // ---------------- Luma (PredL0 / P_L0_16x16) ----------------
    private static void ReconstructLumaInterP16x16(
        Macroblock mb, DecodedPicture picture, DecodedPicture refPic, int mbX, int mbY)
    {
        // MV is in 1/4 pixel units. Integer-pel only at this stage.
        if ((mb.MvL0X & 3) != 0 || (mb.MvL0Y & 3) != 0)
        {
            throw new NotSupportedException(
                $"sub-pel luma MC not yet implemented (MV=({mb.MvL0X}, {mb.MvL0Y}) in 1/4-pel units)");
        }
        int dx = mb.MvL0X >> 2;
        int dy = mb.MvL0Y >> 2;

        Span<byte> predBlock = stackalloc byte[256];
        CopyLumaWithEdgeReplication(refPic, mbX * 16 + dx, mbY * 16 + dy, predBlock);

        // Now add residual per 4x4 block (CBP-gated, full 16-coeff blocks).
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
                    picture.Y[(py0 + yy) * picture.Width + (px0 + xx)] = ClipByte(v);
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
        Macroblock mb, DecodedPicture picture, DecodedPicture refPic, int mbX, int mbY, int qPc)
    {
        int cdx = mb.MvL0X >> 3;
        int cdy = mb.MvL0Y >> 3;

        Span<byte> predBlock = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int comp = 0; comp < 2; comp++)
        {
            byte[] refPlane = comp == 0 ? refPic.U : refPic.V;
            byte[] plane = comp == 0 ? picture.U : picture.V;
            int stride = picture.ChromaWidth;
            int srcXBase = mbX * 8 + cdx;
            int srcYBase = mbY * 8 + cdy;
            CopyChromaWithEdgeReplication(refPlane, refPic.ChromaWidth, refPic.ChromaHeight,
                                          srcXBase, srcYBase, predBlock);

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
        Macroblock mb, DecodedPicture picture, int mbX, int mbY, int qPc)
    {
        Span<byte> predBlock = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        Span<byte> top8 = stackalloc byte[8];
        Span<byte> left8 = stackalloc byte[8];
        bool topAvail = mbY > 0;
        bool leftAvail = mbX > 0;
        bool topLeftAvail = topAvail && leftAvail;

        for (int comp = 0; comp < 2; comp++)
        {
            byte[] plane = comp == 0 ? picture.U : picture.V;
            int stride = picture.ChromaWidth;
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
}
