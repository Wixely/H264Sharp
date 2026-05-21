using H264Decoder.Bitstream;

namespace H264Decoder.Cavlc;

/// <summary>
/// CAVLC residual block decoder (spec §9.2). Port of the algorithm in
/// OpenH264's parse_mb_syn_cavlc.cpp, using the lookup tables in CavlcTables.
/// </summary>
internal static class CavlcResidual
{
    private const int MaxLevelPrefix = 15;

    /// <summary>
    /// Decode one residual block. <paramref name="coeffs"/> is filled in
    /// zig-zag scan order (caller supplies the zig-zag table for the position
    /// of each scanned coefficient). Returns the number of non-zero coefficients.
    /// </summary>
    /// <param name="reader">Bit reader positioned at the start of the block.</param>
    /// <param name="coeffs">Output buffer, length == <paramref name="maxNumCoeff"/>; cleared by this method.</param>
    /// <param name="maxNumCoeff">16 for 4x4 luma, 15 for AC-only 16x16 luma blocks, 4 for chroma DC, 15 for chroma AC.</param>
    /// <param name="nC">Predicted coefficient count for nC table selection; ignored when chromaDc.</param>
    /// <param name="chromaDc">True for 2x2 chroma DC blocks (uses the dedicated VLC).</param>
    public static int ReadResidualBlock(
        ref BitReader reader,
        scoped Span<int> coeffs,
        int maxNumCoeff,
        int nC,
        bool chromaDc)
    {
        coeffs.Clear();

        ReadCoeffToken(ref reader, nC, chromaDc, out int totalCoeff, out int trailingOnes);
        if (totalCoeff == 0)
        {
            return 0;
        }
        if (trailingOnes > 3 || totalCoeff > 16)
        {
            throw new InvalidDataException(
                $"CAVLC: invalid (TotalCoeff={totalCoeff}, TrailingOnes={trailingOnes})");
        }

        Span<int> levels = stackalloc int[16];
        ReadLevels(ref reader, totalCoeff, trailingOnes, levels);

        int zerosLeft = 0;
        if (totalCoeff < maxNumCoeff)
        {
            zerosLeft = ReadTotalZeros(ref reader, totalCoeff, chromaDc);
        }

        Span<int> runs = stackalloc int[16];
        ReadRunBefore(ref reader, totalCoeff, zerosLeft, runs);

        // Assemble coefficients in scan order. Spec §9.2.3:
        // The first decoded level is the highest-frequency non-zero coeff.
        // coeffNum starts at -1 and accumulates run+1 each step.
        int coeffNum = -1;
        for (int i = totalCoeff - 1; i >= 0; i--)
        {
            coeffNum += runs[i] + 1;
            coeffs[coeffNum] = levels[i];
        }
        return totalCoeff;
    }

    /// <summary>
    /// Decode one 8x8 luma residual block (spec §7.3.5.3 / §9.2). The block is encoded as
    /// 4 interleaved 4x4 sub-blocks; sub-block s contains scan positions s, s+4, s+8, ..., s+60
    /// of the 8x8 zigzag scan. Output <paramref name="coeffs64"/> is filled in 8x8 zigzag scan order.
    /// Returns the total non-zero count across all 4 sub-blocks.
    /// </summary>
    public static int ReadResidualBlock8x8(
        ref BitReader r,
        scoped Span<int> coeffs64,
        int nC0, int nC1, int nC2, int nC3)
    {
        coeffs64.Clear();
        Span<int> sub = stackalloc int[16];
        int total = 0;
        for (int s = 0; s < 4; s++)
        {
            int nC = s switch { 0 => nC0, 1 => nC1, 2 => nC2, _ => nC3 };
            sub.Clear();
            int nz = ReadResidualBlock(ref r, sub, maxNumCoeff: 16, nC, chromaDc: false);
            total += nz;
            for (int i = 0; i < 16; i++) coeffs64[s + i * 4] = sub[i];
        }
        return total;
    }

    // ---------------------------------------------------------------------
    // coeff_token (TotalCoeff + TrailingOnes)
    // ---------------------------------------------------------------------
    private static void ReadCoeffToken(
        ref BitReader reader,
        int nC,
        bool chromaDc,
        out int totalCoeff,
        out int trailingOnes)
    {
        int indexVlc;

        if (chromaDc)
        {
            uint v = reader.PeekBits(8);
            indexVlc = CavlcTables.VlcChromaTable[v * 2 + 0];
            int count = CavlcTables.VlcChromaTable[v * 2 + 1];
            if (count == 0)
            {
                throw new InvalidDataException("CAVLC: invalid ChromaDC coeff_token");
            }
            reader.Skip(count);
        }
        else
        {
            if (nC < 0 || nC > 16)
            {
                throw new InvalidDataException($"CAVLC: nC out of range ({nC})");
            }
            int ncMapIdx = CavlcTables.NcMapTable[nC];

            if (ncMapIdx <= 2)
            {
                uint v = reader.PeekBits(8);
                int threshold = CavlcTables.VlcTableNeedMoreBitsThread[ncMapIdx];

                if (v < threshold)
                {
                    // Long codeword: consume the 8 prefix bits, then look up in sub-table.
                    reader.Skip(8);
                    int extraBits = MoreBitsCountFor(ncMapIdx, (int)v);
                    uint sub = reader.PeekBits(extraBits);
                    byte[] subTable = SubTableFor(ncMapIdx, (int)v);
                    indexVlc = subTable[sub * 2 + 0];
                    int subCount = subTable[sub * 2 + 1];
                    if (subCount == 0)
                    {
                        throw new InvalidDataException("CAVLC: invalid coeff_token (sub)");
                    }
                    reader.Skip(subCount);
                }
                else
                {
                    byte[] table = PrimaryTableFor(ncMapIdx);
                    indexVlc = table[v * 2 + 0];
                    int count = table[v * 2 + 1];
                    if (count == 0)
                    {
                        throw new InvalidDataException("CAVLC: invalid coeff_token (primary)");
                    }
                    reader.Skip(count);
                }
            }
            else
            {
                // ncMapIdx == 3 (nC >= 8): fixed 6-bit table.
                uint v = reader.PeekBits(6);
                reader.Skip(6);
                indexVlc = CavlcTables.VlcTable_3[v * 2 + 0];
                int count = CavlcTables.VlcTable_3[v * 2 + 1];
                if (count == 0)
                {
                    throw new InvalidDataException("CAVLC: invalid coeff_token (nC>=8)");
                }
            }
        }

        trailingOnes = CavlcTables.VlcTrailingOneTotalCoeffTable[indexVlc * 2 + 0];
        totalCoeff = CavlcTables.VlcTrailingOneTotalCoeffTable[indexVlc * 2 + 1];
    }

    private static byte[] PrimaryTableFor(int ncMapIdx) => ncMapIdx switch
    {
        0 => CavlcTables.VlcTable_0,
        1 => CavlcTables.VlcTable_1,
        2 => CavlcTables.VlcTable_2,
        _ => throw new ArgumentOutOfRangeException(nameof(ncMapIdx)),
    };

    private static byte[] SubTableFor(int ncMapIdx, int subIdx) => ncMapIdx switch
    {
        0 => subIdx switch
        {
            0 => CavlcTables.VlcTable_0_0,
            1 => CavlcTables.VlcTable_0_1,
            2 => CavlcTables.VlcTable_0_2,
            3 => CavlcTables.VlcTable_0_3,
            _ => throw new InvalidDataException("CAVLC: invalid sub-table index"),
        },
        1 => subIdx switch
        {
            0 => CavlcTables.VlcTable_1_0,
            1 => CavlcTables.VlcTable_1_1,
            2 => CavlcTables.VlcTable_1_2,
            3 => CavlcTables.VlcTable_1_3,
            _ => throw new InvalidDataException("CAVLC: invalid sub-table index"),
        },
        2 => subIdx switch
        {
            0 => CavlcTables.VlcTable_2_0,
            1 => CavlcTables.VlcTable_2_1,
            2 => CavlcTables.VlcTable_2_2,
            3 => CavlcTables.VlcTable_2_3,
            4 => CavlcTables.VlcTable_2_4,
            5 => CavlcTables.VlcTable_2_5,
            6 => CavlcTables.VlcTable_2_6,
            7 => CavlcTables.VlcTable_2_7,
            _ => throw new InvalidDataException("CAVLC: invalid sub-table index"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(ncMapIdx)),
    };

    private static int MoreBitsCountFor(int ncMapIdx, int subIdx) => ncMapIdx switch
    {
        0 => CavlcTables.VlcTableMoreBitsCount0[subIdx],
        1 => CavlcTables.VlcTableMoreBitsCount1[subIdx],
        2 => CavlcTables.VlcTableMoreBitsCount2[subIdx],
        _ => throw new ArgumentOutOfRangeException(nameof(ncMapIdx)),
    };

    // ---------------------------------------------------------------------
    // levels (signed coefficient values, scanned high-frequency first)
    // ---------------------------------------------------------------------
    private static void ReadLevels(
        ref BitReader reader,
        int totalCoeff,
        int trailingOnes,
        scoped Span<int> levels)
    {
        for (int i = 0; i < trailingOnes; i++)
        {
            // sign bit: 0 -> +1, 1 -> -1
            uint bit = reader.ReadBit();
            levels[i] = bit == 0 ? 1 : -1;
        }

        int suffixLength = (totalCoeff > 10 && trailingOnes < 3) ? 1 : 0;

        for (int i = trailingOnes; i < totalCoeff; i++)
        {
            int prefixBits = ReadLevelPrefix(ref reader);
            if (prefixBits > MaxLevelPrefix)
            {
                throw new InvalidDataException("CAVLC: level_prefix overflow");
            }
            int levelPrefix = prefixBits;

            int suffixLengthSize = suffixLength;
            int levelCode = levelPrefix << suffixLength;

            if (levelPrefix >= 14)
            {
                if (levelPrefix == 14 && suffixLength == 0)
                {
                    suffixLengthSize = 4;
                }
                else if (levelPrefix == 15)
                {
                    suffixLengthSize = 12;
                    if (suffixLength == 0)
                    {
                        levelCode += 15;
                    }
                }
            }

            if (suffixLengthSize > 0)
            {
                levelCode += (int)reader.ReadBits(suffixLengthSize);
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            int sign = levelCode & 1;
            int magnitude = (levelCode + 2) >> 1;
            levels[i] = sign == 0 ? magnitude : -magnitude;

            if (suffixLength == 0)
            {
                suffixLength = 1;
            }
            int threshold = 3 << (suffixLength - 1);
            int absLevel = levels[i] >= 0 ? levels[i] : -levels[i];
            if (absLevel > threshold && suffixLength < 6)
            {
                suffixLength++;
            }
        }
    }

    private static int ReadLevelPrefix(ref BitReader reader)
    {
        int count = 0;
        while (reader.ReadBit() == 0)
        {
            count++;
            if (count > 25)
            {
                throw new InvalidDataException("CAVLC: level_prefix runaway");
            }
        }
        return count;
    }

    // ---------------------------------------------------------------------
    // total_zeros
    // ---------------------------------------------------------------------
    private static int ReadTotalZeros(ref BitReader reader, int totalCoeff, bool chromaDc)
    {
        byte[] bitNumMap = chromaDc
            ? CavlcTables.TotalZerosBitNumChromaMap
            : CavlcTables.TotalZerosBitNumMap;
        int idx = totalCoeff - 1;
        int count = bitNumMap[idx];
        uint v = reader.PeekBits(count);

        byte[] table = chromaDc ? ChromaTotalZerosTableFor(idx) : LumaTotalZerosTableFor(idx);
        int zerosLeft = table[v * 2 + 0];
        int consumed = table[v * 2 + 1];
        if (consumed == 0)
        {
            throw new InvalidDataException("CAVLC: invalid total_zeros codeword");
        }
        reader.Skip(consumed);
        return zerosLeft;
    }

    private static byte[] LumaTotalZerosTableFor(int idx) => idx switch
    {
        0 => CavlcTables.TotalZerosTable0,
        1 => CavlcTables.TotalZerosTable1,
        2 => CavlcTables.TotalZerosTable2,
        3 => CavlcTables.TotalZerosTable3,
        4 => CavlcTables.TotalZerosTable4,
        5 => CavlcTables.TotalZerosTable5,
        6 => CavlcTables.TotalZerosTable6,
        7 => CavlcTables.TotalZerosTable7,
        8 => CavlcTables.TotalZerosTable8,
        9 => CavlcTables.TotalZerosTable9,
        10 => CavlcTables.TotalZerosTable10,
        11 => CavlcTables.TotalZerosTable11,
        12 => CavlcTables.TotalZerosTable12,
        13 => CavlcTables.TotalZerosTable13,
        14 => CavlcTables.TotalZerosTable14,
        _ => throw new ArgumentOutOfRangeException(nameof(idx)),
    };

    private static byte[] ChromaTotalZerosTableFor(int idx) => idx switch
    {
        0 => CavlcTables.TotalZerosChromaTable0,
        1 => CavlcTables.TotalZerosChromaTable1,
        2 => CavlcTables.TotalZerosChromaTable2,
        _ => throw new ArgumentOutOfRangeException(nameof(idx)),
    };

    // ---------------------------------------------------------------------
    // run_before
    // ---------------------------------------------------------------------
    private static void ReadRunBefore(
        ref BitReader reader,
        int totalCoeff,
        int zerosLeft,
        scoped Span<int> runs)
    {
        for (int i = 0; i < totalCoeff - 1; i++)
        {
            if (zerosLeft <= 0)
            {
                for (int j = i; j < totalCoeff; j++)
                {
                    runs[j] = 0;
                }
                return;
            }

            int n = CavlcTables.ZeroLeftBitNumMap[zerosLeft];
            uint v = reader.PeekBits(n);

            int run;
            if (zerosLeft < 7)
            {
                byte[] table = ZeroLeftTableFor(zerosLeft - 1);
                run = table[v * 2 + 0];
                int consumed = table[v * 2 + 1];
                if (consumed == 0)
                {
                    throw new InvalidDataException("CAVLC: invalid run_before");
                }
                reader.Skip(consumed);
            }
            else
            {
                // zerosLeft >= 7: ZeroLeftTable6 covers runs 0..6.
                // Runs >= 7 are encoded as 3 leading zeros + level_prefix-style suffix.
                reader.Skip(n);
                int tableRun = CavlcTables.ZeroLeftTable6[v * 2 + 0];
                if (tableRun < 7)
                {
                    run = tableRun;
                }
                else
                {
                    int extraZeros = ReadLevelPrefix(ref reader);
                    run = extraZeros + 6;
                    if (run > zerosLeft)
                    {
                        throw new InvalidDataException("CAVLC: run_before > zerosLeft");
                    }
                }
            }

            runs[i] = run;
            zerosLeft -= run;
        }

        runs[totalCoeff - 1] = zerosLeft;
    }

    private static byte[] ZeroLeftTableFor(int idx) => idx switch
    {
        0 => CavlcTables.ZeroLeftTable0,
        1 => CavlcTables.ZeroLeftTable1,
        2 => CavlcTables.ZeroLeftTable2,
        3 => CavlcTables.ZeroLeftTable3,
        4 => CavlcTables.ZeroLeftTable4,
        5 => CavlcTables.ZeroLeftTable5,
        6 => CavlcTables.ZeroLeftTable6,
        _ => throw new ArgumentOutOfRangeException(nameof(idx)),
    };
}
