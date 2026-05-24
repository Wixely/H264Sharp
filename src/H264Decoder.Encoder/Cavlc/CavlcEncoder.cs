using H264Decoder.Encoder.Bitstream;

namespace H264Decoder.Encoder.Cavlc;

/// <summary>Forward CAVLC residual block encoder (spec §9.2). Inverse of the decoder's
/// CavlcResidual.ReadResidualBlock — takes a block of scan-order coefficients and emits
/// the coeff_token, signs, levels, total_zeros, and run_before codewords.</summary>
public static class CavlcEncoder
{
    /// <summary>Encode one CAVLC residual block. <paramref name="coeffs"/> is in scan order
    /// (length == maxNumCoeff). <paramref name="nC"/> is the predicted coefficient count
    /// from neighbor blocks (used to pick the coeff_token VLC table). For 2x2 chroma DC
    /// blocks set <paramref name="chromaDc"/> to true and pass nC=0.</summary>
    public static void EncodeResidualBlock(
        BitWriter w,
        ReadOnlySpan<int> coeffs,
        int maxNumCoeff,
        int nC,
        bool chromaDc)
    {
        // Count totalCoeff, find trailingOnes, collect levels (high-frequency first).
        int totalCoeff = 0;
        int lastNz = -1;
        for (int i = 0; i < maxNumCoeff; i++)
        {
            if (coeffs[i] != 0) { totalCoeff++; if (i > lastNz) lastNz = i; }
        }

        // coeff_token (spec §9.2.1.1).
        WriteCoeffToken(w, totalCoeff, CountTrailingOnes(coeffs, maxNumCoeff), nC, chromaDc);
        if (totalCoeff == 0) return;

        // Levels are scanned high-frequency-first (i.e. from lastNz down to 0).
        Span<int> levels = stackalloc int[16];
        int n = 0;
        for (int i = lastNz; i >= 0 && n < totalCoeff; i--)
        {
            if (coeffs[i] != 0) levels[n++] = coeffs[i];
        }

        // Trailing ones: first up-to-3 levels that are ±1 (counted from highest frequency).
        int trailingOnes = 0;
        for (int i = 0; i < totalCoeff && i < 3; i++)
        {
            int v = levels[i];
            if (v == 1 || v == -1) trailingOnes++;
            else break;
        }

        // Write trailing-ones sign bits in scan order: 0 = positive, 1 = negative.
        for (int i = 0; i < trailingOnes; i++)
        {
            w.WriteBit(levels[i] < 0 ? 1u : 0u);
        }

        // Write the remaining levels (non-trailing-one ones).
        WriteLevels(w, levels, totalCoeff, trailingOnes);

        // total_zeros + run_before (skipped when block is full).
        if (totalCoeff < maxNumCoeff)
        {
            int totalZeros = lastNz + 1 - totalCoeff;
            WriteTotalZeros(w, totalZeros, totalCoeff, chromaDc);
            WriteRunBefore(w, coeffs, totalCoeff, totalZeros, maxNumCoeff, lastNz);
        }
    }

    private static int CountTrailingOnes(ReadOnlySpan<int> coeffs, int maxNumCoeff)
    {
        int trailingOnes = 0;
        // Walk high-frequency first; count up-to-3 consecutive ±1 non-zero coefficients.
        for (int i = maxNumCoeff - 1; i >= 0; i--)
        {
            int v = coeffs[i];
            if (v == 0) continue;
            if (v == 1 || v == -1)
            {
                trailingOnes++;
                if (trailingOnes == 3) break;
            }
            else break;
        }
        return trailingOnes;
    }

    private static void WriteCoeffToken(BitWriter w, int totalCoeff, int trailingOnes, int nC, bool chromaDc)
    {
        (int len, uint code) = chromaDc
            ? CavlcEncoderTables.CoeffTokenChromaDc[totalCoeff, trailingOnes]
            : CavlcEncoderTables.CoeffToken[NcBin(nC), totalCoeff, trailingOnes];
        if (len == 0)
        {
            throw new InvalidOperationException(
                $"CAVLC encoder: no coeff_token for nC={nC} chromaDc={chromaDc} tc={totalCoeff} to={trailingOnes}");
        }
        w.WriteBits(code, len);
    }

    private static int NcBin(int nC) => nC switch
    {
        <= 1 => 0,
        <= 3 => 1,
        <= 7 => 2,
        _ => 3,
    };

    private static void WriteLevels(BitWriter w, ReadOnlySpan<int> levels, int totalCoeff, int trailingOnes)
    {
        // suffixLength state machine matching the decoder's ReadLevels exactly.
        int suffixLength = (totalCoeff > 10 && trailingOnes < 3) ? 1 : 0;

        for (int i = trailingOnes; i < totalCoeff; i++)
        {
            int level = levels[i];
            int absLevel = level < 0 ? -level : level;

            // The first non-trailing-ones level is biased by +2 (per the decoder's "if i == trailingOnes && trailingOnes < 3").
            int adjustedAbs = absLevel;
            if (i == trailingOnes && trailingOnes < 3)
            {
                adjustedAbs -= 1; // decoder adds 1 to magnitude via levelCode adjustment; reverse here.
            }

            // levelCode = (adjustedAbs - 1) * 2 + (sign == 0 ? 0 : 1)
            // sign 0 = positive, sign 1 = negative (matches decoder)
            int sign = level < 0 ? 1 : 0;
            int levelCode = (adjustedAbs - 1) * 2 + sign;

            // Determine level_prefix and suffix bits based on levelCode and suffixLength.
            int levelPrefix;
            int suffixBits;
            int suffixSize = suffixLength;

            if (suffixLength > 0)
            {
                levelPrefix = levelCode >> suffixLength;
                if (levelPrefix < 15)
                {
                    // Prefix 0..14 are normal: levelCode = (prefix << suffixLength) + suffix.
                    suffixBits = levelCode & ((1 << suffixLength) - 1);
                }
                else
                {
                    // Escape: levelPrefix=15, levelCode = (15<<suffixLength) + 12-bit suffix.
                    levelPrefix = 15;
                    int escVal = levelCode - (15 << suffixLength);
                    suffixSize = 12;
                    suffixBits = escVal;
                }
            }
            else
            {
                // suffixLength == 0 special handling per spec.
                if (levelCode < 14)
                {
                    levelPrefix = levelCode;
                    suffixBits = 0;
                    suffixSize = 0;
                }
                else if (levelCode < 30)
                {
                    levelPrefix = 14;
                    suffixSize = 4;
                    suffixBits = levelCode - 14;
                }
                else
                {
                    levelPrefix = 15;
                    suffixSize = 12;
                    suffixBits = levelCode - 30;
                }
            }

            // Write level_prefix as unary: (levelPrefix) zero bits then a 1.
            for (int k = 0; k < levelPrefix; k++) w.WriteBit(0);
            w.WriteBit(1);
            if (suffixSize > 0) w.WriteBits((uint)suffixBits, suffixSize);

            if (suffixLength == 0) suffixLength = 1;
            int threshold = 3 << (suffixLength - 1);
            if (absLevel > threshold && suffixLength < 6) suffixLength++;
        }
    }

    private static void WriteTotalZeros(BitWriter w, int totalZeros, int totalCoeff, bool chromaDc)
    {
        var entry = chromaDc
            ? CavlcEncoderTables.TotalZerosChromaDc[totalCoeff - 1][totalZeros]
            : CavlcEncoderTables.TotalZeros4x4[totalCoeff - 1][totalZeros];
        if (entry.Length == 0)
        {
            throw new InvalidOperationException(
                $"CAVLC encoder: no total_zeros for tc={totalCoeff} tz={totalZeros} chromaDc={chromaDc}");
        }
        w.WriteBits(entry.Code, entry.Length);
    }

    private static void WriteRunBefore(
        BitWriter w, ReadOnlySpan<int> coeffs, int totalCoeff, int totalZeros, int maxNumCoeff, int lastNz)
    {
        // Build the per-coefficient run_before values (high-frequency first).
        // run[i] = number of zeros between levels[i] and levels[i+1] (or the start for i=last).
        Span<int> runs = stackalloc int[16];
        int zerosLeft = totalZeros;
        int prevPos = lastNz;
        int idx = 0;
        for (int i = lastNz - 1; i >= 0 && idx < totalCoeff - 1; i--)
        {
            if (coeffs[i] != 0)
            {
                int run = prevPos - i - 1;
                runs[idx++] = run;
                prevPos = i;
            }
        }
        _ = maxNumCoeff;

        // The last decoded run (lowest frequency) is implicit (= zerosLeft) and not written.
        for (int i = 0; i < totalCoeff - 1; i++)
        {
            if (zerosLeft <= 0) break;
            int run = runs[i];
            WriteOneRunBefore(w, run, zerosLeft);
            zerosLeft -= run;
        }
    }

    private static void WriteOneRunBefore(BitWriter w, int run, int zerosLeft)
    {
        if (zerosLeft < 7 || run < 7)
        {
            var entry = CavlcEncoderTables.RunBefore[zerosLeft][run];
            if (entry.Length == 0)
            {
                throw new InvalidOperationException(
                    $"CAVLC encoder: no run_before for zerosLeft={zerosLeft} run={run}");
            }
            w.WriteBits(entry.Code, entry.Length);
            return;
        }
        // zerosLeft >= 7 AND run >= 7: synthesize the extended codeword.
        // Format: 3 zero bits (prefix 7) then (run-7) zero bits then a 1.
        int extraZeros = run - 7;
        for (int i = 0; i < 3; i++) w.WriteBit(0);
        for (int i = 0; i < extraZeros; i++) w.WriteBit(0);
        w.WriteBit(1);
    }
}
