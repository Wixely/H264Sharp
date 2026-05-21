using H264Decoder.Bitstream;

namespace H264Decoder.Syntax;

/// <summary>
/// H.264 Picture Parameter Set, Baseline-profile subset (spec §7.3.2.2).
/// </summary>
public sealed class PictureParameterSet
{
    public required uint PicParameterSetId { get; init; }
    public required uint SeqParameterSetId { get; init; }
    public required bool EntropyCodingModeFlag { get; init; }
    public required bool BottomFieldPicOrderInFramePresentFlag { get; init; }
    public required uint NumSliceGroupsMinus1 { get; init; }
    public required uint NumRefIdxL0DefaultActiveMinus1 { get; init; }
    public required uint NumRefIdxL1DefaultActiveMinus1 { get; init; }
    public required bool WeightedPredFlag { get; init; }
    public required uint WeightedBipredIdc { get; init; }
    public required int PicInitQpMinus26 { get; init; }
    public required int PicInitQsMinus26 { get; init; }
    public required int ChromaQpIndexOffset { get; init; }
    public required bool DeblockingFilterControlPresentFlag { get; init; }
    public required bool ConstrainedIntraPredFlag { get; init; }
    public required bool RedundantPicCntPresentFlag { get; init; }

    public static PictureParameterSet Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new BitReader(rbsp);
        uint ppsId = ExpGolomb.ReadUe(ref r);
        uint spsId = ExpGolomb.ReadUe(ref r);
        bool entropyCoding = r.ReadBit() == 1;
        if (entropyCoding)
        {
            throw new NotSupportedException("PPS entropy_coding_mode_flag=1 (CABAC) not supported");
        }
        bool bottomFieldPoc = r.ReadBit() == 1;
        uint numSliceGroupsMinus1 = ExpGolomb.ReadUe(ref r);
        if (numSliceGroupsMinus1 > 0)
        {
            throw new NotSupportedException("PPS num_slice_groups_minus1>0 (FMO) not supported");
        }
        uint numRefL0 = ExpGolomb.ReadUe(ref r);
        uint numRefL1 = ExpGolomb.ReadUe(ref r);
        bool weightedPred = r.ReadBit() == 1;
        uint weightedBipred = r.ReadBits(2);
        int picInitQpMinus26 = ExpGolomb.ReadSe(ref r);
        int picInitQsMinus26 = ExpGolomb.ReadSe(ref r);
        int chromaQpOffset = ExpGolomb.ReadSe(ref r);
        bool deblockingFilterControl = r.ReadBit() == 1;
        bool constrainedIntra = r.ReadBit() == 1;
        bool redundantPicCnt = r.ReadBit() == 1;
        // Baseline stops here; transform_8x8_mode etc. belong to High profile.

        return new PictureParameterSet
        {
            PicParameterSetId = ppsId,
            SeqParameterSetId = spsId,
            EntropyCodingModeFlag = entropyCoding,
            BottomFieldPicOrderInFramePresentFlag = bottomFieldPoc,
            NumSliceGroupsMinus1 = numSliceGroupsMinus1,
            NumRefIdxL0DefaultActiveMinus1 = numRefL0,
            NumRefIdxL1DefaultActiveMinus1 = numRefL1,
            WeightedPredFlag = weightedPred,
            WeightedBipredIdc = weightedBipred,
            PicInitQpMinus26 = picInitQpMinus26,
            PicInitQsMinus26 = picInitQsMinus26,
            ChromaQpIndexOffset = chromaQpOffset,
            DeblockingFilterControlPresentFlag = deblockingFilterControl,
            ConstrainedIntraPredFlag = constrainedIntra,
            RedundantPicCntPresentFlag = redundantPicCnt,
        };
    }
}
