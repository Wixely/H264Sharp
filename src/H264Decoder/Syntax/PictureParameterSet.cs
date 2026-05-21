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

    /// <summary>High-profile-only: when true, an MB may signal transform_size_8x8_flag.
    /// False for Baseline PPSes (where the field is absent in the bitstream).</summary>
    public bool Transform8x8ModeFlag { get; init; }

    /// <summary>High-profile-only: chroma_qp_index_offset for Cr (Cb still uses ChromaQpIndexOffset).
    /// Defaults to ChromaQpIndexOffset when the High extension is absent.</summary>
    public int SecondChromaQpIndexOffset { get; init; }

    public static PictureParameterSet Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new BitReader(rbsp);
        uint ppsId = ExpGolomb.ReadUe(ref r);
        uint spsId = ExpGolomb.ReadUe(ref r);
        bool entropyCoding = r.ReadBit() == 1;
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

        // High-profile extension (spec §7.3.2.2): only present when more_rbsp_data() is true.
        bool transform8x8Mode = false;
        int secondChromaQpOffset = chromaQpOffset;
        if (r.MoreRbspData())
        {
            transform8x8Mode = r.ReadBit() == 1;
            bool picScalingMatrixPresent = r.ReadBit() == 1;
            if (picScalingMatrixPresent)
            {
                throw new NotSupportedException("PPS pic_scaling_matrix_present_flag=1 not supported (custom scaling lists)");
            }
            secondChromaQpOffset = ExpGolomb.ReadSe(ref r);
        }

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
            Transform8x8ModeFlag = transform8x8Mode,
            SecondChromaQpIndexOffset = secondChromaQpOffset,
        };
    }
}
