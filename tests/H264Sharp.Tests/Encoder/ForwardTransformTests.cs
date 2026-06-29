using H264Sharp.Encoder.Transform;

namespace H264Sharp.Tests.Encoder;

public class ForwardTransformTests
{
    [Fact]
    public void Forward4x4_FollowedByInverse4x4_RecoversInput_AfterDequant()
    {
        // For a small residual block, forward 4x4 then quant(qP=18) then dequant then inverse
        // should round-trip the residual (within a few LSB rounding error).
        Span<int> block = stackalloc int[16] {
            5,  3,  -2, 1,
            4,  2,  -1, 0,
            3,  1,   0, -1,
            2,  0,  -1, 0,
        };
        int[] original = block.ToArray();

        ForwardTransform.Forward4x4(block);
        ForwardQuantization.Quant4x4Ac(block, qP: 18, intra: true);

        // Now inverse path (decoder).
        H264Sharp.Decoder.Transform.Quantization_DequantPublic.Dequant4x4Ac(block, qP: 18);
        H264Sharp.Decoder.Transform.InverseTransform.Inverse4x4(block);

        int maxErr = 0;
        for (int i = 0; i < 16; i++)
        {
            int err = Math.Abs(block[i] - original[i]);
            if (err > maxErr) maxErr = err;
        }
        Assert.InRange(maxErr, 0, 4);
    }

    [Fact]
    public void ForwardHadamard4x4_FollowedByInverseHadamard4x4_Is16xIdentity()
    {
        // The forward + inverse Hadamard pair has no normalization in our implementation.
        // A DC delta at (0,0) is spread + recovered with 16x linear scale on round-trip
        // — this is calibrated for by the Intra_16x16 DC quant/dequant pair.
        Span<int> dc = stackalloc int[16] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        ForwardTransform.ForwardHadamard4x4(dc);
        H264Sharp.Decoder.Transform.InverseTransform.InverseHadamard4x4(dc);
        Assert.Equal(16, dc[0]);
        for (int i = 1; i < 16; i++) Assert.Equal(0, dc[i]);
    }
}
