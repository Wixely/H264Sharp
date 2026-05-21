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
        int chromaQpIndexOffset)
    {
        if (mb.Type.PredMode != MbPartPredMode.Intra16x16)
        {
            throw new NotSupportedException("MacroblockReconstructor: only Intra_16x16 supported (Stage 10 phase 1)");
        }

        ReconstructLumaIntra16x16(mb, picture, mbX, mbY);

        int qPc = ChromaQp(mb.QpY, chromaQpIndexOffset);
        ReconstructChroma(mb, picture, mbX, mbY, qPc);
    }

    // ---------------- Luma (Intra_16x16) ----------------
    private static void ReconstructLumaIntra16x16(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY)
    {
        // No neighbors yet (single-MB picture). All availability false.
        Span<byte> predBlock = stackalloc byte[256];
        IntraPrediction.PredictIntra16x16(
            mb.Type.I16x16PredMode,
            top: [], topAvail: false,
            left: [], leftAvail: false,
            topLeft: 0, topLeftAvail: false,
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

    // ---------------- Chroma ----------------
    private static void ReconstructChroma(
        Macroblock mb, DecodedPicture picture, int mbX, int mbY, int qPc)
    {
        Span<byte> predBlock = stackalloc byte[64];
        Span<int> dc = stackalloc int[4];
        Span<int> coeffsScan = stackalloc int[16];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int comp = 0; comp < 2; comp++)
        {
            IntraPrediction.PredictChroma8x8(
                mb.ChromaPredMode,
                top: [], topAvail: false,
                left: [], leftAvail: false,
                topLeft: 0, topLeftAvail: false,
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
                byte[] plane = comp == 0 ? picture.U : picture.V;
                int stride = picture.ChromaWidth;
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
