using H264Decoder.Bitstream;
using H264Decoder.Cavlc;

namespace H264Decoder.Encoder.Cavlc;

/// <summary>H.264 CAVLC encoder tables. Built once at startup by INVERTING the decoder's
/// existing parser: exhaustively try every (length, codeword) pair, feed it through the
/// decoder, and record the (length, code) for the resulting symbol. Guarantees byte-exact
/// round-trip with the decoder.</summary>
internal static class CavlcEncoderTables
{
    /// <summary>coeff_token. Indexed [ncBin][totalCoeff (0..16)][trailingOnes (0..3)].
    /// ncBin: 0=nC∈[0,1], 1=[2,3], 2=[4,7], 3=[8,16] (fixed 6-bit). Length 0 ⇒ unused.</summary>
    public static readonly (int Length, uint Code)[,,] CoeffToken = BuildCoeffToken();

    /// <summary>Chroma DC coeff_token. Indexed [totalCoeff (0..4)][trailingOnes (0..3)].</summary>
    public static readonly (int Length, uint Code)[,] CoeffTokenChromaDc = BuildCoeffTokenChromaDc();

    /// <summary>total_zeros for 4x4 luma/AC. Indexed [totalCoeff-1 (0..14)][totalZeros].</summary>
    public static readonly (int Length, uint Code)[][] TotalZeros4x4 = BuildTotalZeros4x4();

    /// <summary>total_zeros for chroma DC. Indexed [totalCoeff-1 (0..2)][totalZeros].</summary>
    public static readonly (int Length, uint Code)[][] TotalZerosChromaDc = BuildTotalZerosChromaDc();

    /// <summary>run_before. Indexed [zerosLeft (1..16)][run]. For zerosLeft >= 7 with run >= 7
    /// the encoder synthesizes the codeword itself rather than using this table.</summary>
    public static readonly (int Length, uint Code)[][] RunBefore = BuildRunBefore();

    private static byte[] PadToBuffer(uint code, int len)
    {
        var buf = new byte[4];
        ulong padded = (ulong)code << (64 - len);
        buf[0] = (byte)((padded >> 56) & 0xFF);
        buf[1] = (byte)((padded >> 48) & 0xFF);
        buf[2] = (byte)((padded >> 40) & 0xFF);
        buf[3] = (byte)((padded >> 32) & 0xFF);
        return buf;
    }

    private static (int Length, uint Code)[,,] BuildCoeffToken()
    {
        var t = new (int, uint)[4, 17, 4];
        for (int ncBin = 0; ncBin <= 2; ncBin++)
        {
            int nC = ncBin switch { 0 => 0, 1 => 2, _ => 4 };
            for (int len = 1; len <= 16; len++)
            {
                for (uint code = 0; code < (1u << len); code++)
                {
                    try
                    {
                        var buf = PadToBuffer(code, len);
                        var r = new BitReader(buf);
                        var p = CavlcTablesPublic.PeekCoeffToken(ref r, nC, chromaDc: false);
                        if (p.Consumed != len) continue;
                        if (p.TotalCoeff <= 16 && p.TrailingOnes <= 3
                            && t[ncBin, p.TotalCoeff, p.TrailingOnes].Item1 == 0)
                        {
                            t[ncBin, p.TotalCoeff, p.TrailingOnes] = (len, code);
                        }
                    }
                    catch { }
                }
            }
        }
        // nC >= 8: fixed 6-bit codes.
        for (uint code = 0; code < 64; code++)
        {
            try
            {
                var buf = PadToBuffer(code, 6);
                var r = new BitReader(buf);
                var p = CavlcTablesPublic.PeekCoeffToken(ref r, nC: 8, chromaDc: false);
                if (p.Consumed != 6) continue;
                if (p.TotalCoeff <= 16 && p.TrailingOnes <= 3
                    && t[3, p.TotalCoeff, p.TrailingOnes].Item1 == 0)
                {
                    t[3, p.TotalCoeff, p.TrailingOnes] = (6, code);
                }
            }
            catch { }
        }
        return t;
    }

    private static (int Length, uint Code)[,] BuildCoeffTokenChromaDc()
    {
        var t = new (int, uint)[5, 4];
        for (int len = 1; len <= 8; len++)
        {
            for (uint code = 0; code < (1u << len); code++)
            {
                try
                {
                    var buf = PadToBuffer(code, len);
                    var r = new BitReader(buf);
                    var p = CavlcTablesPublic.PeekCoeffToken(ref r, nC: 0, chromaDc: true);
                    if (p.Consumed != len) continue;
                    if (p.TotalCoeff <= 4 && p.TrailingOnes <= 3 && t[p.TotalCoeff, p.TrailingOnes].Item1 == 0)
                    {
                        t[p.TotalCoeff, p.TrailingOnes] = (len, code);
                    }
                }
                catch { }
            }
        }
        return t;
    }

    private static (int Length, uint Code)[][] BuildTotalZeros4x4()
    {
        var t = new (int, uint)[15][];
        for (int tc = 1; tc <= 15; tc++)
        {
            int maxTz = 16 - tc;
            var entry = new (int, uint)[maxTz + 1];
            for (int len = 1; len <= 9; len++)
            {
                for (uint code = 0; code < (1u << len); code++)
                {
                    try
                    {
                        var buf = PadToBuffer(code, len);
                        var r = new BitReader(buf);
                        var p = CavlcTablesPublic.PeekTotalZeros(ref r, tc, chromaDc: false);
                        if (p.Consumed != len) continue;
                        if (p.TotalZeros <= maxTz && entry[p.TotalZeros].Item1 == 0)
                        {
                            entry[p.TotalZeros] = (len, code);
                        }
                    }
                    catch { }
                }
            }
            t[tc - 1] = entry;
        }
        return t;
    }

    private static (int Length, uint Code)[][] BuildTotalZerosChromaDc()
    {
        var t = new (int, uint)[3][];
        for (int tc = 1; tc <= 3; tc++)
        {
            int maxTz = 4 - tc;
            var entry = new (int, uint)[maxTz + 1];
            for (int len = 1; len <= 3; len++)
            {
                for (uint code = 0; code < (1u << len); code++)
                {
                    try
                    {
                        var buf = PadToBuffer(code, len);
                        var r = new BitReader(buf);
                        var p = CavlcTablesPublic.PeekTotalZeros(ref r, tc, chromaDc: true);
                        if (p.Consumed != len) continue;
                        if (p.TotalZeros <= maxTz && entry[p.TotalZeros].Item1 == 0)
                        {
                            entry[p.TotalZeros] = (len, code);
                        }
                    }
                    catch { }
                }
            }
            t[tc - 1] = entry;
        }
        return t;
    }

    private static (int Length, uint Code)[][] BuildRunBefore()
    {
        var t = new (int, uint)[17][];
        for (int zl = 1; zl <= 16; zl++)
        {
            var entry = new (int, uint)[zl + 1];
            for (int len = 1; len <= 11; len++)
            {
                for (uint code = 0; code < (1u << len); code++)
                {
                    try
                    {
                        var buf = PadToBuffer(code, len);
                        var r = new BitReader(buf);
                        var p = CavlcTablesPublic.PeekRunBefore(ref r, zl);
                        if (p.Consumed != len) continue;
                        if (p.Run <= zl && entry[p.Run].Item1 == 0)
                        {
                            entry[p.Run] = (len, code);
                        }
                    }
                    catch { }
                }
            }
            t[zl] = entry;
        }
        return t;
    }
}
