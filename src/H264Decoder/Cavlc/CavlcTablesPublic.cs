using H264Decoder.Bitstream;

namespace H264Decoder.Cavlc;

/// <summary>Public peek helpers over the internal CAVLC tables, used by the encoder
/// to invert (decode-then-record) the symbol-to-codeword mappings at startup.
/// Not intended for general decode paths.</summary>
public static class CavlcTablesPublic
{
    /// <summary>Look up VLC index from VlcTable_3 at the given 6-bit code value.</summary>
    public static int VlcTable3VlcIndex(uint code) => CavlcTables.VlcTable_3[code * 2 + 0];
    public static int VlcTable3BitCount(uint code) => CavlcTables.VlcTable_3[code * 2 + 1];

    public static int TrailingOneFromVlcIdx(int vlcIdx) =>
        CavlcTables.VlcTrailingOneTotalCoeffTable[vlcIdx * 2 + 0];
    public static int TotalCoeffFromVlcIdx(int vlcIdx) =>
        CavlcTables.VlcTrailingOneTotalCoeffTable[vlcIdx * 2 + 1];

    /// <summary>Decode one coeff_token codeword from <paramref name="r"/>. Returns
    /// (totalCoeff, trailingOnes, bitsConsumed). Throws on invalid input.</summary>
    public static (int TotalCoeff, int TrailingOnes, int Consumed) PeekCoeffToken(
        ref BitReader r, int nC, bool chromaDc)
    {
        int start = r.BitPosition;
        ReadCoeffToken(ref r, nC, chromaDc, out int tc, out int to);
        return (tc, to, r.BitPosition - start);
    }

    /// <summary>Decode one total_zeros codeword. Returns (totalZeros, bitsConsumed).</summary>
    public static (int TotalZeros, int Consumed) PeekTotalZeros(
        ref BitReader r, int totalCoeff, bool chromaDc)
    {
        int start = r.BitPosition;
        int tz = ReadTotalZeros(ref r, totalCoeff, chromaDc);
        return (tz, r.BitPosition - start);
    }

    /// <summary>Decode one run_before codeword (single iteration). Returns (run, bitsConsumed).</summary>
    public static (int Run, int Consumed) PeekRunBefore(ref BitReader r, int zerosLeft)
    {
        int start = r.BitPosition;
        int run = ReadOneRunBefore(ref r, zerosLeft);
        return (run, r.BitPosition - start);
    }

    // The bodies below are extracted from CavlcResidual to remain consistent with the live decoder.
    private static void ReadCoeffToken(ref BitReader reader, int nC, bool chromaDc,
        out int totalCoeff, out int trailingOnes)
    {
        int indexVlc;

        if (chromaDc)
        {
            uint v = reader.PeekBits(8);
            indexVlc = CavlcTables.VlcChromaTable[v * 2 + 0];
            int count = CavlcTables.VlcChromaTable[v * 2 + 1];
            if (count == 0) throw new InvalidDataException("CAVLC: invalid ChromaDC coeff_token");
            reader.Skip(count);
        }
        else
        {
            if (nC < 0 || nC > 16) throw new InvalidDataException("CAVLC: nC out of range");
            int ncMapIdx = CavlcTables.NcMapTable[nC];

            if (ncMapIdx <= 2)
            {
                uint v = reader.PeekBits(8);
                int threshold = CavlcTables.VlcTableNeedMoreBitsThread[ncMapIdx];

                if (v < threshold)
                {
                    reader.Skip(8);
                    int extraBits = MoreBitsCountFor(ncMapIdx, (int)v);
                    uint sub = reader.PeekBits(extraBits);
                    byte[] subTable = SubTableFor(ncMapIdx, (int)v);
                    indexVlc = subTable[sub * 2 + 0];
                    int subCount = subTable[sub * 2 + 1];
                    if (subCount == 0) throw new InvalidDataException("CAVLC: invalid coeff_token (sub)");
                    reader.Skip(subCount);
                }
                else
                {
                    byte[] table = PrimaryTableFor(ncMapIdx);
                    indexVlc = table[v * 2 + 0];
                    int count = table[v * 2 + 1];
                    if (count == 0) throw new InvalidDataException("CAVLC: invalid coeff_token (primary)");
                    reader.Skip(count);
                }
            }
            else
            {
                uint v = reader.PeekBits(6);
                reader.Skip(6);
                indexVlc = CavlcTables.VlcTable_3[v * 2 + 0];
                int count = CavlcTables.VlcTable_3[v * 2 + 1];
                if (count == 0) throw new InvalidDataException("CAVLC: invalid coeff_token (nC>=8)");
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
        if (consumed == 0) throw new InvalidDataException("CAVLC: invalid total_zeros codeword");
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

    private static int ReadOneRunBefore(ref BitReader reader, int zerosLeft)
    {
        if (zerosLeft <= 0) return 0;
        int n = CavlcTables.ZeroLeftBitNumMap[zerosLeft];
        uint v = reader.PeekBits(n);
        int run;
        if (zerosLeft < 7)
        {
            byte[] table = ZeroLeftTableFor(zerosLeft - 1);
            run = table[v * 2 + 0];
            int consumed = table[v * 2 + 1];
            if (consumed == 0) throw new InvalidDataException("CAVLC: invalid run_before");
            reader.Skip(consumed);
        }
        else
        {
            reader.Skip(n);
            int tableRun = CavlcTables.ZeroLeftTable6[v * 2 + 0];
            if (tableRun < 7)
            {
                run = tableRun;
            }
            else
            {
                int extraZeros = 0;
                while (reader.ReadBit() == 0)
                {
                    extraZeros++;
                    if (extraZeros > 25) throw new InvalidDataException("CAVLC: run_before runaway");
                }
                run = extraZeros + 7;
                if (run > zerosLeft) throw new InvalidDataException("CAVLC: run_before > zerosLeft");
            }
        }
        return run;
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
