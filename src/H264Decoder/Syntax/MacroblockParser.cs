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

    public static int SpatialToRaster(int x, int y) => _spatialToRaster[y * 4 + x];

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
        Macroblock? topRightMb,
        Macroblock? topLeftMb,
        int mbAddress,
        ref int qpYRunning)
    {
        _ = sps; // currently no SPS-dependent fields in I-slice MB layer
        int startBit = reader.BitPosition;
        uint mbTypeCode = ExpGolomb.ReadUe(ref reader);
        bool isPSlice = sliceHeader.SliceType == SliceType.P;
        var type = isPSlice
            ? IntraMbType.FromPSliceCodeword(mbTypeCode)
            : IntraMbType.FromISliceCodeword(mbTypeCode);
        if (type.PredMode == MbPartPredMode.IPcm)
        {
            var pcmMb = new Macroblock
            {
                MbAddress = mbAddress,
                Type = type,
                ParseStartBit = startBit,
                IsPcm = true,
                QpY = qpYRunning,
            };
            // pcm_alignment_zero_bit loop: read zero bits until byte-aligned.
            reader.ByteAlign();
            for (int i = 0; i < 256; i++) pcmMb.PcmLuma[i] = (byte)reader.ReadBits(8);
            for (int i = 0; i < 64; i++)  pcmMb.PcmCb[i]   = (byte)reader.ReadBits(8);
            for (int i = 0; i < 64; i++)  pcmMb.PcmCr[i]   = (byte)reader.ReadBits(8);
            // Spec rule: neighbor NZC / cbf values for an I_PCM MB are treated as maximum.
            for (int i = 0; i < 16; i++) { pcmMb.NonZeroCountLuma[i] = 16; pcmMb.LumaAcCbf[i] = true; }
            pcmMb.LumaDcCbf = true;
            for (int c = 0; c < 2; c++)
            {
                pcmMb.ChromaDcCbf[c] = true;
                for (int i = 0; i < 4; i++) { pcmMb.NonZeroCountChromaAc[c, i] = 16; pcmMb.ChromaAcCbf[c, i] = true; }
            }
            pcmMb.ParseEndBit = reader.BitPosition;
            return pcmMb;
        }

        var mb = new Macroblock
        {
            MbAddress = mbAddress,
            Type = type,
            ParseStartBit = startBit,
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

        if (type.PredMode == MbPartPredMode.PredL0)
        {
            ParseInterMbPred(ref reader, mb, sliceHeader, leftMb, topMb, topRightMb, topLeftMb);
        }
        else
        {
            mb.ChromaPredMode = (IntraChromaPredMode)ExpGolomb.ReadUe(ref reader);
        }

        // For PredL0 the chroma_pred_mode is NOT in mb_pred (it's only for intra MBs).
        // The chroma prediction for inter MBs is derived from MC, not signalled.

        // coded_block_pattern
        if (type.PredMode == MbPartPredMode.Intra4x4 || type.PredMode == MbPartPredMode.PredL0)
        {
            uint cbpCode = ExpGolomb.ReadUe(ref reader);
            bool intraTable = type.PredMode == MbPartPredMode.Intra4x4;
            int cbp = CodedBlockPattern.FromCodeNum(cbpCode, intra: intraTable);
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

        mb.ParseEndBit = reader.BitPosition;
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
            _ = dcCount;
        }
        else // Intra4x4 or PredL0 — both use 16 full 4x4 luma blocks
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

    /// <summary>
    /// Predict the L0 motion vector for a P_L0_16x16 partition (spec §8.4.1.3.1).
    /// Median over neighbors A (left), B (top), C (top-right). Unavailable neighbors
    /// substitute mv=(0,0) and refIdx=-1. The top-right C falls back to top-left D
    /// when C is unavailable. A neighbor MB that is not inter-coded is treated as
    /// "ref mismatch" (refIdx differs from current's 0, contributing mv=(0,0) but
    /// with mismatched refIdx).
    /// </summary>
    private static (int X, int Y) PredictMv16x16(
        Macroblock cur,
        Macroblock? leftMb,    // A
        Macroblock? topMb,     // B
        Macroblock? topRightMb,// C
        Macroblock? topLeftMb) // D (fallback for C)
    {
        _ = cur;
        // Effective C: top-right if available, else top-left.
        Macroblock? cMb = topRightMb ?? topLeftMb;

        bool aAvail = leftMb is not null;
        bool bAvail = topMb is not null;
        bool cAvail = cMb is not null;

        // Spec rule: if B and C are unavailable but A is available, copy A into B and C.
        if (!bAvail && !cAvail && aAvail)
        {
            return (leftMb!.MvL0X, leftMb.MvL0Y);
        }

        // Per spec, a neighbor that is unavailable OR intra-coded gets mv=(0,0), refIdx=-1.
        (int x, int y, int refIdx) A = aAvail && leftMb!.Type.PredMode == MbPartPredMode.PredL0
            ? (leftMb.MvL0X, leftMb.MvL0Y, leftMb.RefIdxL0)
            : (0, 0, -1);
        (int x, int y, int refIdx) B = bAvail && topMb!.Type.PredMode == MbPartPredMode.PredL0
            ? (topMb.MvL0X, topMb.MvL0Y, topMb.RefIdxL0)
            : (0, 0, -1);
        (int x, int y, int refIdx) C = cAvail && cMb!.Type.PredMode == MbPartPredMode.PredL0
            ? (cMb.MvL0X, cMb.MvL0Y, cMb.RefIdxL0)
            : (0, 0, -1);

        int curRefIdx = cur.RefIdxL0;
        int matchCount = (A.refIdx == curRefIdx ? 1 : 0)
                       + (B.refIdx == curRefIdx ? 1 : 0)
                       + (C.refIdx == curRefIdx ? 1 : 0);

        if (matchCount == 1)
        {
            if (A.refIdx == curRefIdx) return (A.x, A.y);
            if (B.refIdx == curRefIdx) return (B.x, B.y);
            return (C.x, C.y);
        }

        return (Median3(A.x, B.x, C.x), Median3(A.y, B.y, C.y));
    }

    /// <summary>
    /// Derive the L0 motion vector for a P_Skip macroblock per spec §8.4.1.1.
    /// Returns (0,0) if either of the two needed neighbors (A=left, B=top) is
    /// unavailable OR has refIdx==0 with mv==(0,0); otherwise returns the
    /// regular 16x16 median MV prediction.
    /// </summary>
    public static (int X, int Y) DerivePSkipMv(
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // Look at the specific 4x4 neighbor blocks A (left, at (-1, 0)) and B (top, at (0, -1)).
        var synth = new Macroblock();
        var A = GetMvNeighbor(-1, 0, synth, leftMb, topMb, topRightMb, topLeftMb);
        var B = GetMvNeighbor(0, -1, synth, leftMb, topMb, topRightMb, topLeftMb);

        bool aUnavailOrZero = !A.Avail || (A.RefIdx == 0 && A.MvX == 0 && A.MvY == 0);
        bool bUnavailOrZero = !B.Avail || (B.RefIdx == 0 && B.MvX == 0 && B.MvY == 0);

        if (aUnavailOrZero || bUnavailOrZero) return (0, 0);

        // Otherwise: standard 16x16 median MV prediction with current refIdx = 0.
        return PredictMvForPartition(synth, 0, 0, 0, 0, 4, 4, 0,
            leftMb, topMb, topRightMb, topLeftMb);
    }

    private static int Median3(int a, int b, int c)
    {
        // Median of three values.
        int min = Math.Min(a, Math.Min(b, c));
        int max = Math.Max(a, Math.Max(b, c));
        return a + b + c - min - max;
    }

    // -----------------------------------------------------------------
    // Inter mb_pred / sub_mb_pred parsing (P_L0_16x16, 16x8, 8x16, 8x8)
    // -----------------------------------------------------------------
    private static void ParseInterMbPred(
        ref BitReader reader, Macroblock mb, SliceHeader sliceHeader,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        int rawMbType = mb.Type.RawMbType;
        bool isP8x8 = rawMbType == 3 || rawMbType == 4;
        bool refIdxForcedZero = rawMbType == 4; // P_8x8ref0
        uint maxRef = sliceHeader.NumRefIdxL0ActiveMinus1;

        var subMbTypes = isP8x8 ? new SubMbType[4] : null;
        if (isP8x8)
        {
            for (int i = 0; i < 4; i++)
            {
                uint code = ExpGolomb.ReadUe(ref reader);
                if (code > 3) throw new InvalidDataException($"P sub_mb_type {code} out of range");
                subMbTypes![i] = (SubMbType)code;
            }
        }

        // Read ref_idx_l0
        int[] refIdxPerQuadrant = new int[4]; // per 8x8 quadrant
        if (rawMbType <= 2)
        {
            int numMbPart = IntraMbType.NumMbPart(rawMbType);
            int[] partRefIdx = new int[numMbPart];
            for (int p = 0; p < numMbPart; p++)
            {
                partRefIdx[p] = maxRef > 0 ? (int)ExpGolomb.ReadTe(ref reader, maxRef) : 0;
            }
            ReplicateRefIdxAcross16x16Partitions(rawMbType, partRefIdx, refIdxPerQuadrant);
        }
        else // P_8x8 / P_8x8ref0
        {
            for (int q = 0; q < 4; q++)
            {
                if (refIdxForcedZero) refIdxPerQuadrant[q] = 0;
                else refIdxPerQuadrant[q] = maxRef > 0 ? (int)ExpGolomb.ReadTe(ref reader, maxRef) : 0;
            }
        }
        for (int q = 0; q < 4; q++) mb.RefIdxL08x8[q] = refIdxPerQuadrant[q];

        // Read mvds and apply MV prediction per partition.
        if (rawMbType <= 2)
        {
            ParseInterMbPred_NoSubMb(ref reader, mb, rawMbType, refIdxPerQuadrant,
                                     leftMb, topMb, topRightMb, topLeftMb);
        }
        else
        {
            ParseInterMbPred_P8x8(ref reader, mb, subMbTypes!, refIdxPerQuadrant,
                                  leftMb, topMb, topRightMb, topLeftMb);
        }

        // Convenience scalars: take partition 0's values.
        if (mb.InterPartitions.Count > 0)
        {
            var p0 = mb.InterPartitions[0];
            mb.RefIdxL0 = p0.RefIdxL0;
            mb.MvL0X = p0.MvL0X;
            mb.MvL0Y = p0.MvL0Y;
        }
    }

    /// <summary>Distribute the 1, 2, or 2 partition-refIdx values from mb_type 0/1/2 across the 4 8x8 quadrants.</summary>
    private static void ReplicateRefIdxAcross16x16Partitions(int rawMbType, int[] partRefIdx, int[] perQuadrant)
    {
        switch (rawMbType)
        {
            case 0: // 16x16: 1 refIdx, all 4 quadrants
                for (int q = 0; q < 4; q++) perQuadrant[q] = partRefIdx[0];
                break;
            case 1: // 16x8: refIdx[0]=top (q 0,1), refIdx[1]=bottom (q 2,3)
                perQuadrant[0] = partRefIdx[0]; perQuadrant[1] = partRefIdx[0];
                perQuadrant[2] = partRefIdx[1]; perQuadrant[3] = partRefIdx[1];
                break;
            case 2: // 8x16: refIdx[0]=left (q 0,2), refIdx[1]=right (q 1,3)
                perQuadrant[0] = partRefIdx[0]; perQuadrant[2] = partRefIdx[0];
                perQuadrant[1] = partRefIdx[1]; perQuadrant[3] = partRefIdx[1];
                break;
        }
    }

    private static void ParseInterMbPred_NoSubMb(
        ref BitReader reader, Macroblock mb, int rawMbType, int[] refIdxPerQuadrant,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // Partition layout (X, Y in pixels, W, H in pixels):
        var partRects = rawMbType switch
        {
            0 => new[] { (X: 0, Y: 0, W: 16, H: 16) },
            1 => new[] { (X: 0, Y: 0, W: 16, H: 8), (X: 0, Y: 8, W: 16, H: 8) },
            2 => new[] { (X: 0, Y: 0, W: 8, H: 16), (X: 8, Y: 0, W: 8, H: 16) },
            _ => throw new ArgumentOutOfRangeException(nameof(rawMbType)),
        };

        for (int p = 0; p < partRects.Length; p++)
        {
            int mvdX = ExpGolomb.ReadSe(ref reader);
            int mvdY = ExpGolomb.ReadSe(ref reader);

            // Refidx for this partition: the 8x8 quadrant that the partition's
            // top-left 4x4 block lives in.
            int curRefIdx = refIdxPerQuadrant[QuadrantOf(partRects[p].X / 4, partRects[p].Y / 4)];

            (int predX, int predY) = PredictMvForPartition(
                mb, rawMbType, p,
                partRects[p].X / 4, partRects[p].Y / 4, partRects[p].W / 4, partRects[p].H / 4,
                curRefIdx, leftMb, topMb, topRightMb, topLeftMb);

            int mvX = predX + mvdX;
            int mvY = predY + mvdY;

            mb.InterPartitions.Add(new MvPartition(partRects[p].X, partRects[p].Y, partRects[p].W, partRects[p].H, curRefIdx, mvX, mvY));
            FillBlockMvs(mb, partRects[p].X / 4, partRects[p].Y / 4, partRects[p].W / 4, partRects[p].H / 4, mvX, mvY);
        }
    }

    private static void ParseInterMbPred_P8x8(
        ref BitReader reader, Macroblock mb, SubMbType[] subMbTypes, int[] refIdxPerQuadrant,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // 4 8x8 quadrants, raster: 0=TL, 1=TR, 2=BL, 3=BR
        // Each 8x8 quadrant has its own sub_mb_type that further splits it.
        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 8;
            int qy = (q >> 1) * 8;
            var (subW, subH) = SubMbTypeOps.SubMbPartSize(subMbTypes[q]);
            int numSubParts = SubMbTypeOps.NumSubMbPart(subMbTypes[q]);

            for (int sp = 0; sp < numSubParts; sp++)
            {
                // Sub-partition layout within the 8x8 quadrant:
                //   8x8: 1 part at (0,0)
                //   8x4: 2 parts at (0,0) and (0,4)
                //   4x8: 2 parts at (0,0) and (4,0)
                //   4x4: 4 parts at (0,0), (4,0), (0,4), (4,4) (raster)
                int spx, spy;
                if (subW == 8 && subH == 8) { spx = 0; spy = 0; }
                else if (subW == 8 && subH == 4) { spx = 0; spy = sp * 4; }
                else if (subW == 4 && subH == 8) { spx = sp * 4; spy = 0; }
                else { spx = (sp & 1) * 4; spy = (sp >> 1) * 4; }

                int partX = qx + spx;
                int partY = qy + spy;
                int curRefIdx = refIdxPerQuadrant[q];

                int mvdX = ExpGolomb.ReadSe(ref reader);
                int mvdY = ExpGolomb.ReadSe(ref reader);

                // 8x8 sub-partitions use standard median prediction (no 16x8/8x16 override).
                (int predX, int predY) = PredictMvForPartition(
                    mb, 0 /*sentinel: treat as standard median*/, 0,
                    partX / 4, partY / 4, subW / 4, subH / 4,
                    curRefIdx, leftMb, topMb, topRightMb, topLeftMb);

                int mvX = predX + mvdX;
                int mvY = predY + mvdY;

                mb.InterPartitions.Add(new MvPartition(partX, partY, subW, subH, curRefIdx, mvX, mvY));
                FillBlockMvs(mb, partX / 4, partY / 4, subW / 4, subH / 4, mvX, mvY);
            }
        }
    }

    internal static int QuadrantOf(int bx, int by) => (bx >> 1) + (by >> 1) * 2;

    internal static void ReplicateRefIdxAcross16x16PartitionsPublic(int rawMbType, int[] partRefIdx, int[] perQuadrant)
        => ReplicateRefIdxAcross16x16Partitions(rawMbType, partRefIdx, perQuadrant);

    internal static void FillBlockMvds(Macroblock mb, int bx0, int by0, int bw, int bh, int mvdX, int mvdY)
    {
        for (int by = by0; by < by0 + bh; by++)
            for (int bx = bx0; bx < bx0 + bw; bx++)
            {
                int idx = _spatialToRaster[by * 4 + bx];
                mb.MvdL0XBlock[idx] = mvdX;
                mb.MvdL0YBlock[idx] = mvdY;
            }
    }

    internal static void FillBlockMvs(Macroblock mb, int bx0, int by0, int bw, int bh, int mvX, int mvY)
    {
        for (int by = by0; by < by0 + bh; by++)
            for (int bx = bx0; bx < bx0 + bw; bx++)
            {
                int idx = _spatialToRaster[by * 4 + bx];
                mb.MvL0XBlock[idx] = mvX;
                mb.MvL0YBlock[idx] = mvY;
            }
    }

    /// <summary>Compute the L0 MV prediction for a partition at (bx, by) of size (bwBlocks, bhBlocks) in 4x4-block units.</summary>
    internal static (int X, int Y) PredictMvForPartition(
        Macroblock cur, int rawMbType, int partIdx,
        int bx, int by, int bwBlocks, int bhBlocks, int curRefIdx,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // Gather A (left), B (top), C (top-right of partition top-right block), D (top-left of partition top-left).
        var A = GetMvNeighbor(bx - 1, by, cur, leftMb, topMb, topRightMb, topLeftMb);
        var B = GetMvNeighbor(bx, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        // C is at the position above-right of the partition's top-right 4x4 block.
        int cBx = bx + bwBlocks;
        int cBy = by - 1;
        var C = GetMvNeighbor(cBx, cBy, cur, leftMb, topMb, topRightMb, topLeftMb);
        // If C is not available, fall back to D (top-left).
        if (!C.Avail)
        {
            C = GetMvNeighbor(bx - 1, by - 1, cur, leftMb, topMb, topRightMb, topLeftMb);
        }

        // Spec §8.4.1.3.1 partition-specific overrides:
        if (rawMbType == 1) // 16x8
        {
            if (partIdx == 0 && B.Avail && B.RefIdx == curRefIdx) return (B.MvX, B.MvY);
            if (partIdx == 1 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
        }
        else if (rawMbType == 2) // 8x16
        {
            if (partIdx == 0 && A.Avail && A.RefIdx == curRefIdx) return (A.MvX, A.MvY);
            if (partIdx == 1 && C.Avail && C.RefIdx == curRefIdx) return (C.MvX, C.MvY);
        }

        // Spec rule: if B and C both unavailable and A available, copy A into B, C.
        if (!B.Avail && !C.Avail && A.Avail)
        {
            return (A.MvX, A.MvY);
        }

        // Otherwise standard median with substitution.
        int aX = A.Avail ? A.MvX : 0, aY = A.Avail ? A.MvY : 0, aR = A.Avail ? A.RefIdx : -1;
        int bX = B.Avail ? B.MvX : 0, bY = B.Avail ? B.MvY : 0, bR = B.Avail ? B.RefIdx : -1;
        int cX = C.Avail ? C.MvX : 0, cY = C.Avail ? C.MvY : 0, cR = C.Avail ? C.RefIdx : -1;

        int matchCount = (aR == curRefIdx ? 1 : 0) + (bR == curRefIdx ? 1 : 0) + (cR == curRefIdx ? 1 : 0);
        if (matchCount == 1)
        {
            if (aR == curRefIdx) return (aX, aY);
            if (bR == curRefIdx) return (bX, bY);
            return (cX, cY);
        }
        return (Median3(aX, bX, cX), Median3(aY, bY, cY));
    }

    internal readonly struct MvNeighbor
    {
        public readonly bool Avail;
        public readonly int MvX, MvY, RefIdx;
        public MvNeighbor(bool a, int x, int y, int r) { Avail = a; MvX = x; MvY = y; RefIdx = r; }
    }

    internal static MvNeighbor GetMvNeighborPublic(
        int bx, int by, Macroblock cur,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
        => GetMvNeighbor(bx, by, cur, leftMb, topMb, topRightMb, topLeftMb);

    private static MvNeighbor GetMvNeighbor(
        int bx, int by, Macroblock cur,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb)
    {
        // Resolve the MB containing the 4x4 block at (bx, by) (relative to current MB).
        Macroblock? mb;
        int nbBx, nbBy;
        if (bx >= 0 && by >= 0 && bx <= 3 && by <= 3) { mb = cur; nbBx = bx; nbBy = by; }
        else if (bx < 0 && by >= 0 && by <= 3) { mb = leftMb; nbBx = 3; nbBy = by; }
        else if (by < 0 && bx >= 0 && bx <= 3) { mb = topMb; nbBx = bx; nbBy = 3; }
        else if (bx < 0 && by < 0) { mb = topLeftMb; nbBx = 3; nbBy = 3; }
        else if (bx > 3 && by < 0) { mb = topRightMb; nbBx = 0; nbBy = 3; }
        else { mb = null; nbBx = 0; nbBy = 0; }

        if (mb is null) return new MvNeighbor(false, 0, 0, -1);
        if (mb.Type.PredMode != MbPartPredMode.PredL0)
        {
            // Intra neighbor: MV unavailable for prediction purposes (refIdx=-1).
            return new MvNeighbor(true, 0, 0, -1);
        }

        int idx = _spatialToRaster[nbBy * 4 + nbBx];
        int refIdx = mb.RefIdxL08x8[QuadrantOf(nbBx, nbBy)];
        return new MvNeighbor(true, mb.MvL0XBlock[idx], mb.MvL0YBlock[idx], refIdx);
    }
}
