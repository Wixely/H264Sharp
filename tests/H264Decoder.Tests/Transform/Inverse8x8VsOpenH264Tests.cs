using H264Decoder.Transform;

namespace H264Decoder.Tests.Transform;

// Compares our Inverse8x8 to OpenH264's IdctResAddPred8x8_c reference implementation
// for a variety of inputs. Catches bugs in our inverse 8x8 transform.
public sealed class Inverse8x8VsOpenH264Tests
{
    // Direct C# port of OpenH264 v2.4.1 IdctResAddPred8x8_c (decode_mb_aux.cpp lines 79-167)
    // computing iRes only (no add-pred). Output is pre-shift (no +32>>6 applied).
    private static void OpenH264Idct8x8_Raw(ReadOnlySpan<short> pRs, Span<short> iRes)
    {
        Span<short> p = stackalloc short[8];
        Span<short> b = stackalloc short[8];
        Span<short> a = stackalloc short[4];
        Span<short> iTmp = stackalloc short[64];

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++) p[j] = pRs[j + (i << 3)];
            a[0] = (short)(p[0] + p[4]);
            a[1] = (short)(p[0] - p[4]);
            a[2] = (short)(p[6] - (p[2] >> 1));
            a[3] = (short)(p[2] + (p[6] >> 1));
            b[0] = (short)(a[0] + a[3]);
            b[2] = (short)(a[1] - a[2]);
            b[4] = (short)(a[1] + a[2]);
            b[6] = (short)(a[0] - a[3]);
            a[0] = (short)(-p[3] + p[5] - p[7] - (p[7] >> 1));
            a[1] = (short)(p[1] + p[7] - p[3] - (p[3] >> 1));
            a[2] = (short)(-p[1] + p[7] + p[5] + (p[5] >> 1));
            a[3] = (short)(p[3] + p[5] + p[1] + (p[1] >> 1));
            b[1] = (short)(a[0] + (a[3] >> 2));
            b[3] = (short)(a[1] + (a[2] >> 2));
            b[5] = (short)(a[2] - (a[1] >> 2));
            b[7] = (short)(a[3] - (a[0] >> 2));
            iTmp[0 + (i << 3)] = (short)(b[0] + b[7]);
            iTmp[1 + (i << 3)] = (short)(b[2] - b[5]);
            iTmp[2 + (i << 3)] = (short)(b[4] + b[3]);
            iTmp[3 + (i << 3)] = (short)(b[6] + b[1]);
            iTmp[4 + (i << 3)] = (short)(b[6] - b[1]);
            iTmp[5 + (i << 3)] = (short)(b[4] - b[3]);
            iTmp[6 + (i << 3)] = (short)(b[2] + b[5]);
            iTmp[7 + (i << 3)] = (short)(b[0] - b[7]);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++) p[j] = iTmp[i + (j << 3)];
            a[0] = (short)(p[0] + p[4]);
            a[1] = (short)(p[0] - p[4]);
            a[2] = (short)(p[6] - (p[2] >> 1));
            a[3] = (short)(p[2] + (p[6] >> 1));
            b[0] = (short)(a[0] + a[3]);
            b[2] = (short)(a[1] - a[2]);
            b[4] = (short)(a[1] + a[2]);
            b[6] = (short)(a[0] - a[3]);
            a[0] = (short)(-p[3] + p[5] - p[7] - (p[7] >> 1));
            a[1] = (short)(p[1] + p[7] - p[3] - (p[3] >> 1));
            a[2] = (short)(-p[1] + p[7] + p[5] + (p[5] >> 1));
            a[3] = (short)(p[3] + p[5] + p[1] + (p[1] >> 1));
            b[1] = (short)(a[0] + (a[3] >> 2));
            b[7] = (short)(a[3] - (a[0] >> 2));
            b[3] = (short)(a[1] + (a[2] >> 2));
            b[5] = (short)(a[2] - (a[1] >> 2));
            iRes[(0 << 3) + i] = (short)(b[0] + b[7]);
            iRes[(1 << 3) + i] = (short)(b[2] - b[5]);
            iRes[(2 << 3) + i] = (short)(b[4] + b[3]);
            iRes[(3 << 3) + i] = (short)(b[6] + b[1]);
            iRes[(4 << 3) + i] = (short)(b[6] - b[1]);
            iRes[(5 << 3) + i] = (short)(b[4] - b[3]);
            iRes[(6 << 3) + i] = (short)(b[2] + b[5]);
            iRes[(7 << 3) + i] = (short)(b[0] - b[7]);
        }
    }

    // OH g_kuiDequantCoeff8x8 row for one QP = NormAdjust8x8[qP%6][i] * 16 (per common_tables.cpp).
    private static readonly int[][] _ohDequantCoeff8x8 = BuildOhDequantTable();

    private static int[][] BuildOhDequantTable()
    {
        // NormAdjust8x8 from spec (same matrix our Quantization.cs uses).
        int[][] na = new int[6][]
        {
            new[] { 20,19,25,19,20,19,25,19,19,18,23,18,19,18,23,18,25,23,29,23,25,23,29,23,19,18,23,18,19,18,23,18,20,19,25,19,20,19,25,19,19,18,23,18,19,18,23,18,25,23,29,23,25,23,29,23,19,18,23,18,19,18,23,18 },
            new[] { 22,21,28,21,22,21,28,21,21,19,26,19,21,19,26,19,28,26,32,26,28,26,32,26,21,19,26,19,21,19,26,19,22,21,28,21,22,21,28,21,21,19,26,19,21,19,26,19,28,26,32,26,28,26,32,26,21,19,26,19,21,19,26,19 },
            new[] { 26,24,33,24,26,24,33,24,24,23,31,23,24,23,31,23,33,31,42,31,33,31,42,31,24,23,31,23,24,23,31,23,26,24,33,24,26,24,33,24,24,23,31,23,24,23,31,23,33,31,42,31,33,31,42,31,24,23,31,23,24,23,31,23 },
            new[] { 28,26,35,26,28,26,35,26,26,25,33,25,26,25,33,25,35,33,45,33,35,33,45,33,26,25,33,25,26,25,33,25,28,26,35,26,28,26,35,26,26,25,33,25,26,25,33,25,35,33,45,33,35,33,45,33,26,25,33,25,26,25,33,25 },
            new[] { 32,30,40,30,32,30,40,30,30,29,38,29,30,29,38,29,40,38,51,38,40,38,51,38,30,29,38,29,30,29,38,29,32,30,40,30,32,30,40,30,30,29,38,29,30,29,38,29,40,38,51,38,40,38,51,38,30,29,38,29,30,29,38,29 },
            new[] { 36,34,46,34,36,34,46,34,34,32,43,32,34,32,43,32,46,43,58,43,46,43,58,43,34,32,43,32,34,32,43,32,36,34,46,34,36,34,46,34,34,32,43,32,34,32,43,32,46,43,58,43,46,43,58,43,34,32,43,32,34,32,43,32 },
        };
        var r = new int[6][];
        for (int q = 0; q < 6; q++)
        {
            r[q] = new int[64];
            for (int i = 0; i < 64; i++) r[q][i] = na[q][i] * 16;
        }
        return r;
    }

    // Direct port of OH parse_mb_syn_cabac.cpp ParseResidualBlockCabac8x8 line 1429-1430 dequant.
    private static void OpenH264Dequant8x8(Span<int> coeffs, int qP)
    {
        int[] mul = _ohDequantCoeff8x8[qP % 6];
        for (int i = 0; i < 64; i++)
        {
            int c = coeffs[i];
            coeffs[i] = qP >= 36
                ? (c * mul[i]) * (1 << (qP / 6 - 6))
                : (c * mul[i] + (1 << (5 - qP / 6))) >> (6 - qP / 6);
        }
    }

    [Fact]
    public void Dequant8x8_MatchesOpenH264_AllQpsAndInputs()
    {
        Span<int> ours = stackalloc int[64];
        Span<int> oh = stackalloc int[64];
        var rng = new Random(7);
        for (int qP = 0; qP <= 51; qP++)
        {
            for (int trial = 0; trial < 20; trial++)
            {
                for (int i = 0; i < 64; i++) ours[i] = oh[i] = rng.Next(-256, 257);
                Quantization.Dequant8x8(ours, qP);
                OpenH264Dequant8x8(oh, qP);
                for (int i = 0; i < 64; i++)
                {
                    Assert.True(ours[i] == oh[i],
                        $"qP={qP} trial={trial} pos={i}: ours={ours[i]} OH={oh[i]}");
                }
            }
        }
    }

    [Fact]
    public void Inverse8x8_MatchesOpenH264_ForRealisticInputs()
    {
        // Realistic dequantized 8x8 coefficient magnitudes typically fit in [-512, 512].
        // OH's intermediate stays in int16 (~32k); larger inputs would overflow OH's int16.
        Span<short> iRes = stackalloc short[64];
        Span<int> our = stackalloc int[64];
        var rng = new Random(42);
        for (int trial = 0; trial < 1000; trial++)
        {
            short[] coeffs = new short[64];
            for (int i = 0; i < 64; i++) coeffs[i] = (short)(rng.Next(-512, 513));

            OpenH264Idct8x8_Raw(coeffs, iRes);
            for (int i = 0; i < 64; i++) our[i] = coeffs[i];
            InverseTransform.Inverse8x8(our);

            for (int i = 0; i < 64; i++)
            {
                int oh = (32 + iRes[i]) >> 6;
                Assert.True(oh == our[i],
                    $"Trial {trial} pos {i}: OH={oh} ours={our[i]} (raw OH iRes={iRes[i]})");
            }
        }
    }
}
