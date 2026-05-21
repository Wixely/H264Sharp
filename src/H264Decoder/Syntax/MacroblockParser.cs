using H264Decoder.Bitstream;
using H264Decoder.Cavlc;

namespace H264Decoder.Syntax;

/// <summary>
/// Parses one macroblock from a CAVLC-coded I-slice (spec §7.3.5).
/// </summary>
public static class MacroblockParser
{
    /// <summary>4x4 luma block raster-order → spatial 4x4-grid index (column, row).</summary>
    public static readonly (int X, int Y)[] LumaBlockPos =
    [
        (0, 0), (1, 0), (0, 1), (1, 1),
        (2, 0), (3, 0), (2, 1), (3, 1),
        (0, 2), (1, 2), (0, 3), (1, 3),
        (2, 2), (3, 2), (2, 3), (3, 3),
    ];

    private static readonly int[] _spatialToRaster = BuildSpatialToRaster();

    private static int[] BuildSpatialToRaster()
    {
        var r = new int[16];
        for (int i = 0; i < 16; i++)
        {
            (int x, int y) = LumaBlockPos[i];
            r[y * 4 + x] = i;
        }
        return r;
    }

    public static Macroblock Parse(
        ref BitReader reader,
        SequenceParameterSet sps,
        PictureParameterSet pps,
        SliceHeader sliceHeader,
        Macroblock? leftMb,
        Macroblock? topMb,
        int mbAddress,
        ref int qpYRunning)
    {
        _ = sps; // currently no SPS-dependent fields in I-slice MB layer
        uint mbTypeCode = ExpGolomb.ReadUe(ref reader);
        var type = IntraMbType.FromISliceCodeword(mbTypeCode);
        if (type.PredMode == MbPartPredMode.IPcm)
        {
            throw new NotSupportedException("I_PCM macroblocks not yet supported");
        }

        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = type,
        };

        // mb_pred
        if (type.PredMode == MbPartPredMode.Intra4x4)
        {
            for (int i = 0; i < 16; i++)
            {
                bool prev = reader.ReadBit() == 1;
                if (prev)
                {
                    mb.Intra4x4PredMode[i] = -1; // signal "use predicted mode"
                }
                else
                {
                    mb.Intra4x4PredMode[i] = (int)reader.ReadBits(3);
                }
            }
        }
        mb.ChromaPredMode = (IntraChromaPredMode)ExpGolomb.ReadUe(ref reader);

        // coded_block_pattern
        if (type.PredMode == MbPartPredMode.Intra4x4)
        {
            uint cbpCode = ExpGolomb.ReadUe(ref reader);
            int cbp = CodedBlockPattern.FromCodeNum(cbpCode, intra: true);
            mb.CbpLuma = CodedBlockPattern.LumaPart(cbp);
            mb.CbpChroma = CodedBlockPattern.ChromaPart(cbp);
        }
        else
        {
            mb.CbpLuma = type.CbpLuma;
            mb.CbpChroma = type.CbpChroma;
        }

        // mb_qp_delta + residual: present iff any luma/chroma bits set OR Intra_16x16
        bool hasResidual = mb.CbpLuma != 0 || mb.CbpChroma != 0
                           || type.PredMode == MbPartPredMode.Intra16x16;
        if (hasResidual)
        {
            int mbQpDelta = ExpGolomb.ReadSe(ref reader);
            qpYRunning = Mod52(qpYRunning + mbQpDelta);
            mb.QpY = qpYRunning;
            ParseResidual(ref reader, mb, leftMb, topMb);
        }
        else
        {
            mb.QpY = qpYRunning;
        }

        return mb;
    }

    private static int Mod52(int v)
    {
        // QpY wraps modulo 52 per spec §7.4.5.
        int r = v % 52;
        return r < 0 ? r + 52 : r;
    }

    private static void ParseResidual(
        ref BitReader reader,
        Macroblock mb,
        Macroblock? leftMb,
        Macroblock? topMb)
    {
        Span<int> coeffs = stackalloc int[16];

        if (mb.Type.PredMode == MbPartPredMode.Intra16x16)
        {
            // Intra16x16 luma DC: 16 coefficients, nC computed from neighbors of block 0.
            int ncDc = LumaNcForBlock(0, mb, leftMb, topMb);
            coeffs.Clear();
            int dcCount = CavlcResidual.ReadResidualBlock(ref reader, coeffs, 16, ncDc, chromaDc: false);
            coeffs.CopyTo(mb.LumaDc);

            if (mb.CbpLuma != 0)
            {
                for (int i = 0; i < 16; i++)
                {
                    if ((mb.CbpLuma & (1 << (i >> 2))) == 0) continue;
                    int nc = LumaNcForBlock(i, mb, leftMb, topMb);
                    coeffs.Clear();
                    int n = CavlcResidual.ReadResidualBlock(ref reader, coeffs, 15, nc, chromaDc: false);
                    mb.NonZeroCountLuma[i] = n;
                    for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
                }
            }
            // Block 0 stores DC nz-count (used for AC nC of subsequent MB blocks would mix; per spec
            // the AC blocks use their own count and the DC block's count is not used for nC).
            _ = dcCount;
        }
        else // Intra4x4
        {
            for (int i = 0; i < 16; i++)
            {
                if ((mb.CbpLuma & (1 << (i >> 2))) == 0) continue;
                int nc = LumaNcForBlock(i, mb, leftMb, topMb);
                coeffs.Clear();
                int n = CavlcResidual.ReadResidualBlock(ref reader, coeffs, 16, nc, chromaDc: false);
                mb.NonZeroCountLuma[i] = n;
                for (int j = 0; j < 16; j++) mb.Luma[i, j] = coeffs[j];
            }
        }

        // Chroma DC (one 2x2 block per component)
        if ((mb.CbpChroma & 3) != 0)
        {
            Span<int> dcCoeffs = stackalloc int[4];
            for (int c = 0; c < 2; c++)
            {
                dcCoeffs.Clear();
                CavlcResidual.ReadResidualBlock(ref reader, dcCoeffs, 4, nC: 0, chromaDc: true);
                for (int j = 0; j < 4; j++) mb.ChromaDc[c, j] = dcCoeffs[j];
            }
        }

        // Chroma AC (4 blocks per component) — only if CbpChroma bit 1 set
        if ((mb.CbpChroma & 2) != 0)
        {
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 4; i++)
                {
                    int nc = ChromaNcForBlock(c, i, mb, leftMb, topMb);
                    coeffs.Clear();
                    int n = CavlcResidual.ReadResidualBlock(ref reader, coeffs, 15, nc, chromaDc: false);
                    mb.NonZeroCountChromaAc[c, i] = n;
                    for (int j = 0; j < 16; j++) mb.ChromaAc[c, i, j] = coeffs[j];
                }
            }
        }
    }

    private static int LumaNcForBlock(int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
        (int x, int y) = LumaBlockPos[blockIdx];

        int nA;
        if (x > 0)
        {
            nA = cur.NonZeroCountLuma[_spatialToRaster[y * 4 + (x - 1)]];
        }
        else if (leftMb != null)
        {
            nA = leftMb.NonZeroCountLuma[_spatialToRaster[y * 4 + 3]];
        }
        else
        {
            nA = -1; // "not available"
        }

        int nB;
        if (y > 0)
        {
            nB = cur.NonZeroCountLuma[_spatialToRaster[(y - 1) * 4 + x]];
        }
        else if (topMb != null)
        {
            nB = topMb.NonZeroCountLuma[_spatialToRaster[3 * 4 + x]];
        }
        else
        {
            nB = -1;
        }

        return ComputeNc(nA, nB);
    }

    private static int ChromaNcForBlock(int comp, int blockIdx, Macroblock cur, Macroblock? leftMb, Macroblock? topMb)
    {
        // 4 chroma 4x4 blocks per component arranged in a 2x2 grid:
        //   0 1
        //   2 3
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
        bool aAvail = nA >= 0;
        bool bAvail = nB >= 0;
        if (aAvail && bAvail) return (nA + nB + 1) >> 1;
        if (aAvail) return nA;
        if (bAvail) return nB;
        return 0;
    }
}
