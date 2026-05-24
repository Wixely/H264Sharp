using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cavlc;
using H264Decoder.Encoder.Transform;
using H264Decoder.Syntax;
using H264Decoder.Transform;

namespace H264Decoder.Encoder.Mode;

/// <summary>Inter-MB (P_L0_16x16 / P_Skip) encoding for P-slices. Mirrors
/// MacroblockEncoder.EncodeIntra16x16 but with motion-compensated prediction
/// from a single L0 reference picture.</summary>
internal static class MacroblockEncoderInter
{
    private static readonly int[] ZigZag4x4 = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };

    private static readonly int[] _cbpToCodeNumInter = BuildCbpToCodeNum();

    private static int[] BuildCbpToCodeNum()
    {
        // Spec Table 9-4 inter column.
        ReadOnlySpan<byte> inter = new byte[]
        {
             0, 16,  1,  2,  4,  8, 32,  3,  5, 10, 12, 15, 47,  7, 11, 13,
            14,  6,  9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
            17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
        };
        var r = new int[64];
        for (int i = 0; i < r.Length; i++) r[i] = -1;
        for (int code = 0; code < inter.Length; code++) r[inter[code]] = code;
        return r;
    }

    /// <summary>Result of computing inter residual + reconstruction. Carries reconstructed samples
    /// and quantized coefficients so the encoder can decide whether to emit P_L0 or P_Skip.</summary>
    internal sealed class InterEncodeBundle
    {
        public int Sad;
        public int CbpLuma;
        public int CbpChroma;
        public int[] Luma4x4 = new int[256];
        public int[,] ChromaDc = new int[2, 4];
        public int[,,] ChromaAc = new int[2, 4, 15];
        public byte[] ReconY = new byte[256];
        public byte[] ReconU = new byte[64];
        public byte[] ReconV = new byte[64];
        public byte[] PredY = new byte[256];
        public byte[] PredU = new byte[64];
        public byte[] PredV = new byte[64];
    }

    /// <summary>Predict, residual, forward+inverse transform, and reconstruct for one P_L0_16x16 MB.
    /// Does NOT emit any bitstream — caller decides between P_L0 and P_Skip based on the cbp result.</summary>
    public static InterEncodeBundle BuildInterCandidate(
        ReadOnlySpan<byte> srcY, ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV,
        int srcStrideY, int srcStrideC,
        byte[] refY, byte[] refU, byte[] refV,
        int refW, int refH, int refCw, int refCh,
        int mbX, int mbY, int qpY,
        int mvX, int mvY)
    {
        // Single 16x16 partition shortcut: produce 16x16 prediction with one MC call, then
        // hand off to the shared multi-partition pipeline. Chroma uses the same single MV.
        var bundle = new InterEncodeBundle();
        Span<byte> pred = bundle.PredY;
        MotionEstimator.LumaPredictBlock(refY, refW, refH,
            mbX * 16, mbY * 16, mvX, mvY, 16, 16, pred);
        BuildInterCandidateFromPrediction(
            bundle, srcY, srcStrideY, qpY);
        int qPc = ChromaQpFromLumaQp(qpY);
        // Chroma: single MV for the whole 8x8 (matches 16x16 luma partition).
        MotionEstimator.ChromaPredictBlock(refU, refCw, refCh, mbX * 8, mbY * 8, mvX, mvY, 8, 8, bundle.PredU);
        MotionEstimator.ChromaPredictBlock(refV, refCw, refCh, mbX * 8, mbY * 8, mvX, mvY, 8, 8, bundle.PredV);
        EncodeChromaFromPrediction(srcU, srcV, srcStrideC, qPc, bundle);
        return bundle;
    }

    /// <summary>Run forward residual + reconstruction for a 16x16 MB whose 16x16 luma prediction
    /// is already in <paramref name="bundle"/>.PredY. SAD, CbpLuma, Luma4x4 coeffs, and ReconY are filled.</summary>
    internal static void BuildInterCandidateFromPrediction(
        InterEncodeBundle bundle,
        ReadOnlySpan<byte> srcY, int srcStrideY, int qpY)
    {
        Span<byte> pred = bundle.PredY;
        Span<byte> srcLuma = stackalloc byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                srcLuma[y * 16 + x] = srcY[y * srcStrideY + x];

        int sad = 0;
        for (int i = 0; i < 256; i++) sad += Math.Abs(srcLuma[i] - pred[i]);
        bundle.Sad = sad;

        Span<int> block = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = MacroblockEncoder.LumaBlockPos[i];
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int sx = bx * 4 + xx;
                    int sy = by * 4 + yy;
                    block[yy * 4 + xx] = srcLuma[sy * 16 + sx] - pred[sy * 16 + sx];
                }
            ForwardTransform.Forward4x4(block);
            ForwardQuantization.Quant4x4Ac(block, qpY, intra: false);
            for (int k = 0; k < 16; k++) bundle.Luma4x4[i * 16 + k] = block[k];
        }

        int cbpLuma = 0;
        for (int q = 0; q < 4; q++)
        {
            bool any = false;
            for (int s = 0; s < 4; s++)
            {
                int rasterIdx = q * 4 + s;
                for (int k = 0; k < 16; k++)
                {
                    if (bundle.Luma4x4[rasterIdx * 16 + k] != 0) { any = true; break; }
                }
                if (any) break;
            }
            if (any) cbpLuma |= 1 << q;
        }
        bundle.CbpLuma = cbpLuma;

        Span<int> coeffs = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            (int bx, int by) = MacroblockEncoder.LumaBlockPos[i];
            int q = i >> 2;
            bool coded = (cbpLuma & (1 << q)) != 0;
            if (coded)
            {
                for (int k = 0; k < 16; k++) coeffs[k] = bundle.Luma4x4[i * 16 + k];
                Quantization_DequantPublic.Dequant4x4Ac(coeffs, qpY);
                InverseTransform.Inverse4x4(coeffs);
            }
            else
            {
                coeffs.Clear();
            }
            for (int yy = 0; yy < 4; yy++)
                for (int xx = 0; xx < 4; xx++)
                {
                    int v = pred[(by * 4 + yy) * 16 + (bx * 4 + xx)] + coeffs[yy * 4 + xx];
                    byte clipped = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    bundle.ReconY[(by * 4 + yy) * 16 + (bx * 4 + xx)] = clipped;
                }
        }
    }

    /// <summary>Emit the P_L0_16x16 macroblock_layer() syntax + residual for an already-built
    /// inter candidate. Writes mb_type, mvd, CBP, qp_delta, residual into <paramref name="w"/>.
    /// Updates <paramref name="state"/> with per-block NZC and reconstruction byte arrays.</summary>
    public static void EmitP_L0_16x16(
        BitWriter w,
        InterEncodeBundle bundle,
        int qpY,
        int mvdX, int mvdY,
        int mvX, int mvY,
        int refIdxBits,
        MacroblockEncoderState? leftMb,
        MacroblockEncoderState? topMb,
        MacroblockEncoderState state)
    {
        ExpGolombWriter.WriteUe(w, 0); // P_L0_16x16
        if (refIdxBits > 0)
        {
            w.WriteBit(1);
        }
        ExpGolombWriter.WriteSe(w, mvdX);
        ExpGolombWriter.WriteSe(w, mvdY);
        int cbp = bundle.CbpLuma | (bundle.CbpChroma << 4);
        int code = _cbpToCodeNumInter[cbp];
        if (code < 0) throw new InvalidOperationException($"unmappable inter CBP {cbp}");
        ExpGolombWriter.WriteUe(w, (uint)code);
        bool hasResidual = bundle.CbpLuma != 0 || bundle.CbpChroma != 0;
        if (hasResidual)
        {
            ExpGolombWriter.WriteSe(w, 0);
        }

        state.IsInter = true;
        state.IsInterP16x16 = true;
        state.IsIntra16x16 = false;
        state.RawMbType = 0;
        state.CbpLuma = bundle.CbpLuma;
        state.CbpChroma = bundle.CbpChroma;
        state.QpY = qpY;
        state.MvL0X = mvX;
        state.MvL0Y = mvY;
        state.RefIdxL0 = 0;

        Span<int> coeffsScan = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int q = i >> 2;
            bool coded = (bundle.CbpLuma & (1 << q)) != 0;
            if (!coded)
            {
                state.NonZeroCountLuma[i] = 0;
                continue;
            }
            for (int s = 0; s < 16; s++) coeffsScan[s] = bundle.Luma4x4[i * 16 + ZigZag4x4[s]];
            int nC = NcLumaBlock(state, leftMb, topMb, i);
            CavlcEncoder.EncodeResidualBlock(w, coeffsScan, maxNumCoeff: 16, nC, chromaDc: false);
            int nz = 0; for (int k = 0; k < 16; k++) if (coeffsScan[k] != 0) nz++;
            state.NonZeroCountLuma[i] = nz;
        }
        if ((bundle.CbpChroma & 3) != 0)
        {
            Span<int> dc = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                for (int k = 0; k < 4; k++) dc[k] = bundle.ChromaDc[c, k];
                CavlcEncoder.EncodeResidualBlock(w, dc, maxNumCoeff: 4, nC: 0, chromaDc: true);
            }
        }
        if ((bundle.CbpChroma & 2) != 0)
        {
            Span<int> ac = stackalloc int[15];
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    for (int k = 0; k < 15; k++) ac[k] = bundle.ChromaAc[c, i, k];
                    int nC = NcChromaBlock(state, leftMb, topMb, c, i);
                    CavlcEncoder.EncodeResidualBlock(w, ac, maxNumCoeff: 15, nC, chromaDc: false);
                    int nz = 0; for (int k = 0; k < 15; k++) if (ac[k] != 0) nz++;
                    state.NonZeroCountChromaAc[c, i] = nz;
                }
            }
        }

        bundle.ReconY.CopyTo(state.ReconY, 0);
        bundle.ReconU.CopyTo(state.ReconU, 0);
        bundle.ReconV.CopyTo(state.ReconV, 0);
    }

    /// <summary>Write the reconstructed samples for an inter MB into the picture buffers.</summary>
    public static void StoreReconToPicture(
        InterEncodeBundle bundle,
        byte[] picY, byte[] picU, byte[] picV,
        int picStrideY, int picStrideC,
        int mbX, int mbY)
    {
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                picY[(mbY * 16 + y) * picStrideY + (mbX * 16 + x)] = bundle.ReconY[y * 16 + x];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                picU[(mbY * 8 + y) * picStrideC + (mbX * 8 + x)] = bundle.ReconU[y * 8 + x];
                picV[(mbY * 8 + y) * picStrideC + (mbX * 8 + x)] = bundle.ReconV[y * 8 + x];
            }
    }

    /// <summary>Forward chroma residual + reconstruction given that bundle.PredU/V is already
    /// filled with chroma-MC samples. Sets bundle.ChromaDc/Ac/CbpChroma/ReconU/V.</summary>
    internal static void EncodeChromaFromPrediction(
        ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, int srcStrideC,
        int qPc,
        InterEncodeBundle bundle)
    {
        Span<byte> predU = bundle.PredU;
        Span<byte> predV = bundle.PredV;

        Span<byte> srcCb = stackalloc byte[64];
        Span<byte> srcCr = stackalloc byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                srcCb[y * 8 + x] = srcU[y * srcStrideC + x];
                srcCr[y * 8 + x] = srcV[y * srcStrideC + x];
            }

        Span<int> ac4x4 = stackalloc int[64];
        Span<int> dc2x2 = stackalloc int[4];
        Span<int> chBlock = stackalloc int[16];
        Span<int> dcDecoded = stackalloc int[4];
        Span<int> coeffsRaster = stackalloc int[16];

        for (int comp = 0; comp < 2; comp++)
        {
            ReadOnlySpan<byte> src = comp == 0 ? srcCb : srcCr;
            ReadOnlySpan<byte> pred = comp == 0 ? predU : predV;
            ac4x4.Clear();
            dc2x2.Clear();
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
            ForwardTransform.ForwardHadamard2x2(dc2x2);
            ForwardQuantization.QuantChromaDc(dc2x2, qPc);
            for (int k = 0; k < 4; k++) bundle.ChromaDc[comp, k] = dc2x2[k];
            for (int b = 0; b < 4; b++)
            {
                Span<int> ac = ac4x4.Slice(b * 16, 16);
                int saved = ac[0]; ac[0] = 0;
                ForwardQuantization.Quant4x4Ac(ac, qPc, intra: false);
                ac[0] = saved;
            }
            for (int b = 0; b < 4; b++)
            {
                Span<int> ac = ac4x4.Slice(b * 16, 16);
                for (int s = 1; s < 16; s++) bundle.ChromaAc[comp, b, s - 1] = ac[ZigZag4x4[s]];
            }

            for (int k = 0; k < 4; k++) dcDecoded[k] = bundle.ChromaDc[comp, k];
            InverseTransform.InverseHadamard2x2(dcDecoded);
            Quantization_DequantPublic.DequantChromaDc(dcDecoded, qPc);

            byte[] recon = comp == 0 ? bundle.ReconU : bundle.ReconV;
            for (int b = 0; b < 4; b++)
            {
                int bx = b & 1;
                int by = (b >> 1) & 1;
                Span<int> ac = ac4x4.Slice(b * 16, 16);
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

        bool anyAc = false, anyDc = false;
        for (int c = 0; c < 2; c++)
        {
            for (int k = 0; k < 4; k++) if (bundle.ChromaDc[c, k] != 0) anyDc = true;
            for (int b = 0; b < 4; b++)
                for (int k = 0; k < 15; k++) if (bundle.ChromaAc[c, b, k] != 0) anyAc = true;
        }
        bundle.CbpChroma = anyAc ? 2 : (anyDc ? 1 : 0);
    }

    /// <summary>P_Skip MV derivation per spec §8.4.1.1. Uses block (0,0) MV of left/top neighbor
    /// for partitioned MBs (instead of the scalar MvL0X/Y which only reflects partition 0).</summary>
    public static (int X, int Y) DerivePSkipMv(
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        // Neighbor block coordinates for the current MB's block (0,0) per §8.4.1.1:
        //   A = left MB's block at (3,0) → raster idx 5
        //   B = top MB's block at (0,3) → raster idx 10
        int aMvX = 0, aMvY = 0, aRefIdx = -1;
        if (leftMb is not null && leftMb.IsInter)
        {
            aMvX = leftMb.MvL0XBlock[5];
            aMvY = leftMb.MvL0YBlock[5];
            aRefIdx = leftMb.RefIdxL08x8[1]; // quadrant 1 = TR (where block (3,0) lives)
        }
        int bMvX = 0, bMvY = 0, bRefIdx = -1;
        if (topMb is not null && topMb.IsInter)
        {
            bMvX = topMb.MvL0XBlock[10];
            bMvY = topMb.MvL0YBlock[10];
            bRefIdx = topMb.RefIdxL08x8[2]; // quadrant 2 = BL (where block (0,3) lives)
        }
        bool aUnavailOrZero = leftMb is null
            || (leftMb.IsInter && aRefIdx == 0 && aMvX == 0 && aMvY == 0);
        bool bUnavailOrZero = topMb is null
            || (topMb.IsInter && bRefIdx == 0 && bMvX == 0 && bMvY == 0);
        if (aUnavailOrZero || bUnavailOrZero) return (0, 0);
        return PredictMvMedian(leftMb, topMb, topRightMb, topLeftMb);
    }

    /// <summary>Predict the 16x16 partition MV: median of A=left, B=top, C=top-right (or D=top-left if C absent).
    /// Uses the per-block MV of the appropriate neighbor block (spec §8.4.1.3.1).</summary>
    public static (int X, int Y) PredictMvMedian(
        MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        MacroblockEncoderState? topRightMb, MacroblockEncoderState? topLeftMb)
    {
        // Use the shared partition predictor with rawMbType=0 sentinel and curRefIdx=0 — gives the
        // standard median over A=block(-1,0) / B=block(0,-1) / C=block(4,-1)|D=block(-1,-1).
        // Need a stand-in for the "current" MB's state; we build a dummy with all zero blocks.
        var dummy = new MacroblockEncoderState();
        return PartitionMvPredictor.Predict(
            dummy, rawMbType: 0, partIdx: 0,
            bx: 0, by: 0, bw: 4, bh: 4, curRefIdx: 0,
            leftMb, topMb, topRightMb, topLeftMb);
    }

    /// <summary>Lookup table: cbp → CAVLC code num for inter (Table 9-4 inter column). -1 = unmappable.</summary>
    public static int CbpToCodeNumInter(int cbp) => _cbpToCodeNumInter[cbp];

    /// <summary>Compute luma 4x4 block nC predictor for a given block index within the current MB.</summary>
    public static int NcLumaBlock(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb, int blockIdx)
        => NcLumaBlockImpl(cur, leftMb, topMb, blockIdx);

    /// <summary>Compute chroma AC 4x4 block nC predictor for a given component / block index.</summary>
    public static int NcChromaBlock(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb,
        int comp, int blockIdx)
        => NcChromaBlockImpl(cur, leftMb, topMb, comp, blockIdx);

    private static int NcLumaBlockImpl(
        MacroblockEncoderState cur, MacroblockEncoderState? leftMb, MacroblockEncoderState? topMb, int blockIdx)
    {
        (int x, int y) = MacroblockEncoder.LumaBlockPos[blockIdx];
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

    private static int NcChromaBlockImpl(
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

    private static readonly byte[] _qpcTable =
    {
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
        31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38,
        39, 39, 39, 39,
    };

    private static int ChromaQpFromLumaQp(int qPy)
    {
        int qPi = qPy;
        if (qPi < 0) qPi = 0;
        else if (qPi > 51) qPi = 51;
        return _qpcTable[qPi];
    }

    /// <summary>Public wrapper over the QPy → QPc table for cross-module callers.</summary>
    public static int ChromaQpFromLuma(int qPy) => ChromaQpFromLumaQp(qPy);
}
