using H264Decoder.Encoder.Bitstream;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Syntax;

/// <summary>Serialize a Baseline-profile PPS to RBSP bytes (spec §7.3.2.2).</summary>
public static class PpsWriter
{
    public static PictureParameterSet BuildBaseline(bool entropyCodingModeFlag = false)
    {
        return new PictureParameterSet
        {
            PicParameterSetId = 0,
            SeqParameterSetId = 0,
            EntropyCodingModeFlag = entropyCodingModeFlag,
            BottomFieldPicOrderInFramePresentFlag = false,
            NumSliceGroupsMinus1 = 0,
            NumRefIdxL0DefaultActiveMinus1 = 0,
            NumRefIdxL1DefaultActiveMinus1 = 0,
            WeightedPredFlag = false,
            WeightedBipredIdc = 0,
            PicInitQpMinus26 = 0,
            PicInitQsMinus26 = 0,
            ChromaQpIndexOffset = 0,
            DeblockingFilterControlPresentFlag = true,
            ConstrainedIntraPredFlag = false,
            RedundantPicCntPresentFlag = false,
            Transform8x8ModeFlag = false,
            SecondChromaQpIndexOffset = 0,
        };
    }

    public static byte[] Serialize(PictureParameterSet pps)
    {
        var w = new BitWriter(32);
        ExpGolombWriter.WriteUe(w, pps.PicParameterSetId);
        ExpGolombWriter.WriteUe(w, pps.SeqParameterSetId);
        w.WriteBit(pps.EntropyCodingModeFlag ? 1u : 0u);
        w.WriteBit(pps.BottomFieldPicOrderInFramePresentFlag ? 1u : 0u);
        ExpGolombWriter.WriteUe(w, pps.NumSliceGroupsMinus1);
        ExpGolombWriter.WriteUe(w, pps.NumRefIdxL0DefaultActiveMinus1);
        ExpGolombWriter.WriteUe(w, pps.NumRefIdxL1DefaultActiveMinus1);
        w.WriteBit(pps.WeightedPredFlag ? 1u : 0u);
        w.WriteBits(pps.WeightedBipredIdc, 2);
        ExpGolombWriter.WriteSe(w, pps.PicInitQpMinus26);
        ExpGolombWriter.WriteSe(w, pps.PicInitQsMinus26);
        ExpGolombWriter.WriteSe(w, pps.ChromaQpIndexOffset);
        w.WriteBit(pps.DeblockingFilterControlPresentFlag ? 1u : 0u);
        w.WriteBit(pps.ConstrainedIntraPredFlag ? 1u : 0u);
        w.WriteBit(pps.RedundantPicCntPresentFlag ? 1u : 0u);
        w.WriteRbspTrailingBits();
        return w.ToByteArray();
    }
}
