using H264Decoder.Cabac;

namespace H264Decoder.Tests.Cabac;

/// <summary>
/// Round-trip tests for <see cref="CabacResidual.ReadResidualBlock8x8"/>. Each test
/// builds an 8x8 luma residual pattern, encodes it via a private mirror of the
/// reader's bin-emission sequence (CabacEncoder + the same Table 9-43 maps), then
/// decodes through <c>ReadResidualBlock8x8</c> and asserts byte-equal recovery.
///
/// Pattern of failure would localize a CABAC bug to the residual reader itself
/// rather than upstream parser orchestration.
/// </summary>
public class CabacResidual8x8RoundTripTests
{
    private const int SliceQp = 18;

    // Spec Table 9-43, cat=5 frame-coded — must match the maps in CabacResidual.
    private static readonly byte[] SigMap5Frame =
    {
         0,  1,  2,  3,  4,  5,  5,  4,  4,  3,
         3,  4,  4,  4,  5,  5,  4,  4,  4,  4,
         3,  3,  6,  7,  7,  7,  8,  9, 10,  9,
         8,  7,  7,  6, 11, 12, 13, 11,  6,  7,
         8,  9, 14, 10,  9,  8,  6, 11, 12, 13,
        11,  6,  9, 14, 10,  9, 11, 12, 13, 11,
        14, 10, 12,
    };

    private static readonly byte[] LastMap5Frame =
    {
        0, 1, 1, 1, 1, 1, 1, 1,   // pos 0..7
        1, 1, 1, 1, 1, 1, 1, 1,   // pos 8..15
        2, 2, 2, 2, 2, 2, 2, 2,   // pos 16..23
        2, 2, 2, 2, 2, 2, 2, 2,   // pos 24..31
        3, 3, 3, 3, 3, 3, 3, 3,   // pos 32..39
        4, 4, 4, 4, 4, 4, 4, 4,   // pos 40..47
        5, 5, 5, 5, 6, 6, 6, 6,   // pos 48..55
        7, 7, 7, 7, 8, 8, 8,      // pos 56..62
    };

    private const int CtxSig5Start = 402;
    private const int CtxLast5Start = 417;
    private const int CtxAbs5Start = 426;

    private static CabacContexts MakeContexts()
    {
        var ctx = new CabacContexts(CabacInitTable.ContextCount);
        for (int i = 0; i < CabacInitTable.ContextCount; i++)
        {
            sbyte m = CabacInitTable.MN[i, 0, 0];
            sbyte n = CabacInitTable.MN[i, 0, 1];
            if (m == CabacInitTable.CtxNA) continue;
            ctx.Initialize(i, m, n, SliceQp);
        }
        return ctx;
    }

    /// <summary>
    /// Mirror of <see cref="CabacResidual.ReadResidualBlock8x8"/>: emit the bin
    /// sequence that the reader expects to decode the given 8x8 coefficient block.
    /// </summary>
    private static void EncodeResidualBlock8x8(CabacEncoder enc, int[] coeffs)
    {
        if (coeffs.Length != 64) throw new ArgumentException("expected 64 coeffs");

        // Determine last non-zero scan position.
        int lastNz = -1;
        for (int i = 63; i >= 0; i--) { if (coeffs[i] != 0) { lastNz = i; break; } }
        if (lastNz < 0) throw new ArgumentException("block must have at least one non-zero coeff");

        // 1) significant_coeff_flag / last_significant_coeff_flag (positions 0..62).
        for (int i = 0; i < 63; i++)
        {
            int sig = coeffs[i] != 0 ? 1 : 0;
            enc.EncodeBin(CtxSig5Start + SigMap5Frame[i], sig);
            if (sig == 1)
            {
                int last = (i == lastNz) ? 1 : 0;
                enc.EncodeBin(CtxLast5Start + LastMap5Frame[i], last);
                if (last == 1) break;
            }
        }
        // If lastNz == 63 we skipped emitting sig/last for position 63 — it's implicit.

        // 2) Reverse-scan abs level + sign (UEGk, k=0, uCoff=14).
        int numDecodAbsLevelEq1 = 0;
        int numDecodAbsLevelGt1 = 0;
        for (int i = 63; i >= 0; i--)
        {
            int v = coeffs[i];
            if (v == 0) continue;
            int absLevel = v < 0 ? -v : v;
            int absMinus1 = absLevel - 1;

            int ctx0 = (numDecodAbsLevelGt1 != 0)
                ? 0
                : Math.Min(4, 1 + numDecodAbsLevelEq1);
            if (absMinus1 == 0)
            {
                enc.EncodeBin(CtxAbs5Start + ctx0, 0);
                numDecodAbsLevelEq1++;
            }
            else
            {
                enc.EncodeBin(CtxAbs5Start + ctx0, 1);
                int ctxK = CtxAbs5Start + 5 + Math.Min(4, numDecodAbsLevelGt1);
                int prefixOnes = 1;
                while (prefixOnes < 14 && prefixOnes < absMinus1)
                {
                    enc.EncodeBin(ctxK, 1);
                    prefixOnes++;
                }
                if (absMinus1 < 14)
                {
                    enc.EncodeBin(ctxK, 0); // terminate truncated unary
                }
                else
                {
                    // prefixOnes == 14, then EG0 suffix in bypass.
                    int suffix = absMinus1 - 14;
                    EncodeExpGolombBypass(enc, suffix, k: 0);
                }
                numDecodAbsLevelGt1++;
            }

            int sign = v < 0 ? 1 : 0;
            enc.EncodeBypass(sign);
        }
    }

    private static void EncodeExpGolombBypass(CabacEncoder enc, int value, int k)
    {
        // Inverse of CabacResidual.ReadExpGolombBypass: value = ((1<<leading)-1)<<k + suffix
        // with `suffix` being (leading+k) bits.
        int leading = 0;
        int threshold = (1 << k); // first leading-1 group: values < threshold need 0 leading-ones.
        long remaining = value;
        while (remaining >= threshold)
        {
            remaining -= threshold;
            threshold <<= 1;
            leading++;
        }
        for (int i = 0; i < leading; i++) enc.EncodeBypass(1);
        enc.EncodeBypass(0);
        int suffixBits = leading + k;
        for (int i = suffixBits - 1; i >= 0; i--) enc.EncodeBypass((int)((remaining >> i) & 1));
    }

    private static void AssertRoundTrip(int[] inputCoeffs)
    {
        var enc = new CabacEncoder(MakeContexts());
        EncodeResidualBlock8x8(enc, inputCoeffs);
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        var outputCoeffs = new int[64];
        CabacResidual.ReadResidualBlock8x8(dec, outputCoeffs);
        Assert.Equal(inputCoeffs, outputCoeffs);
    }

    [Fact]
    public void RoundTrip_SingleLowPositionCoeff()
    {
        var c = new int[64];
        c[0] = 3;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_SingleHighPositionCoeff_Implicit63()
    {
        var c = new int[64];
        c[63] = 1; // exercises the implicit-last code path
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_TwoCoeffs_MagnitudeBelowEgBoundary()
    {
        var c = new int[64];
        c[0] = -5;
        c[1] = 7;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_HighMagnitudeUnaryBoundary14()
    {
        // absLevel=14, absMinus1=13 — 13 unary 1s then 0; no EG0 suffix.
        var c = new int[64];
        c[0] = 14;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_HighMagnitudeUnarySaturation15()
    {
        // absLevel=15, absMinus1=14 — prefix saturates (14 unary 1s), EG0 suffix = 0.
        var c = new int[64];
        c[0] = 15;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_VeryHighMagnitudes()
    {
        // Forces multiple EG0 suffix paths with varying leading-1 counts.
        var c = new int[64];
        c[0] = 32;
        c[5] = -100;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_DenseLowQpPattern()
    {
        // Typical-ish low-QP residual: many small coefficients clustered low.
        var c = new int[64];
        c[0] = 7; c[1] = -3; c[2] = 4; c[3] = -1; c[4] = 2;
        c[5] = -1; c[6] = 1; c[8] = -2; c[10] = 1; c[12] = -1;
        c[15] = 1;
        AssertRoundTrip(c);
    }

    [Fact]
    public void RoundTrip_AllPositionsSignificantWorstCase()
    {
        var c = new int[64];
        for (int i = 0; i < 64; i++)
        {
            // Alternating signs, small magnitudes — fully significant block.
            c[i] = ((i & 1) == 0) ? 1 : -1;
        }
        AssertRoundTrip(c);
    }
}
