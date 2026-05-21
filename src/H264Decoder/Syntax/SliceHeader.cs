using H264Decoder.Bitstream;

namespace H264Decoder.Syntax;

public enum SliceType
{
    P = 0,
    B = 1,
    I = 2,
    SP = 3,
    SI = 4,
}

/// <summary>
/// H.264 slice header — I-slice subset (spec §7.3.3).
/// </summary>
public sealed class SliceHeader
{
    public required uint FirstMbInSlice { get; init; }
    public required uint SliceTypeRaw { get; init; }   // 0..9
    public required SliceType SliceType { get; init; } // SliceTypeRaw % 5
    public required bool AllSlicesSameType { get; init; } // raw >= 5
    public required uint PicParameterSetId { get; init; }
    public required uint FrameNum { get; init; }
    public required bool IdrPicFlag { get; init; }
    public required uint IdrPicId { get; init; }
    public required uint PicOrderCntLsb { get; init; }
    public required int DeltaPicOrderCntBottom { get; init; }

    // dec_ref_pic_marking — IDR variant only
    public required bool NoOutputOfPriorPicsFlag { get; init; }
    public required bool LongTermReferenceFlag { get; init; }

    public required int SliceQpDelta { get; init; }
    public required uint DisableDeblockingFilterIdc { get; init; }
    public required int SliceAlphaC0OffsetDiv2 { get; init; }
    public required int SliceBetaOffsetDiv2 { get; init; }

    // P-slice fields
    public uint NumRefIdxL0ActiveMinus1 { get; init; }
    public bool NumRefIdxActiveOverrideFlag { get; init; }

    public int SliceQpY(PictureParameterSet pps) => 26 + pps.PicInitQpMinus26 + SliceQpDelta;

    public static SliceHeader Parse(
        ReadOnlySpan<byte> rbsp,
        NalUnit nalHeader,
        SequenceParameterSet sps,
        PictureParameterSet pps)
    {
        bool idrPicFlag = nalHeader.NalUnitType == NalUnitType.SliceIdr;
        var r = new BitReader(rbsp);

        uint firstMbInSlice = ExpGolomb.ReadUe(ref r);
        uint sliceTypeRaw = ExpGolomb.ReadUe(ref r);
        if (sliceTypeRaw > 9)
        {
            throw new InvalidDataException($"slice_type {sliceTypeRaw} out of range");
        }
        var sliceType = (SliceType)(sliceTypeRaw % 5);
        if (sliceType != SliceType.I && sliceType != SliceType.P)
        {
            throw new NotSupportedException($"slice_type {sliceType} not supported (I/P only)");
        }
        bool allSame = sliceTypeRaw >= 5;
        uint ppsId = ExpGolomb.ReadUe(ref r);

        int frameNumBits = (int)sps.Log2MaxFrameNumMinus4 + 4;
        uint frameNum = r.ReadBits(frameNumBits);

        // separate_colour_plane_flag is 0 for Baseline (no colour_plane_id)
        // frame_mbs_only_flag=1 so no field_pic_flag / bottom_field_flag

        uint idrPicId = 0;
        if (idrPicFlag)
        {
            idrPicId = ExpGolomb.ReadUe(ref r);
        }

        uint picOrderCntLsb = 0;
        int deltaPicOrderCntBottom = 0;
        if (sps.PicOrderCntType == 0)
        {
            int lsbBits = (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4;
            picOrderCntLsb = r.ReadBits(lsbBits);
            if (pps.BottomFieldPicOrderInFramePresentFlag)
            {
                deltaPicOrderCntBottom = ExpGolomb.ReadSe(ref r);
            }
        }
        // pic_order_cnt_type==1 already rejected in SPS parser.
        // pic_order_cnt_type==2: no extra fields.

        if (pps.RedundantPicCntPresentFlag)
        {
            _ = ExpGolomb.ReadUe(ref r); // redundant_pic_cnt
        }

        // P-slice specific: num_ref_idx_active_override + ref_pic_list_modification
        bool numRefIdxOverride = false;
        uint numRefIdxL0ActiveMinus1 = pps.NumRefIdxL0DefaultActiveMinus1;
        if (sliceType == SliceType.P)
        {
            numRefIdxOverride = r.ReadBit() == 1;
            if (numRefIdxOverride)
            {
                numRefIdxL0ActiveMinus1 = ExpGolomb.ReadUe(ref r);
            }
            // ref_pic_list_modification for P-slice: reads ref_pic_list_modification_flag_l0
            bool listModL0 = r.ReadBit() == 1;
            if (listModL0)
            {
                while (true)
                {
                    uint op = ExpGolomb.ReadUe(ref r);
                    if (op == 3) break;
                    _ = ExpGolomb.ReadUe(ref r); // abs_diff_pic_num_minus1 / long_term_pic_num
                }
            }
        }

        // No pred_weight_table for our subset (weighted_pred_flag is 0 in baseline x264 default).

        bool noOutputPriorPics = false;
        bool longTermRef = false;
        if (nalHeader.NalRefIdc != 0)
        {
            if (idrPicFlag)
            {
                noOutputPriorPics = r.ReadBit() == 1;
                longTermRef = r.ReadBit() == 1;
            }
            else
            {
                bool adaptive = r.ReadBit() == 1;
                if (adaptive)
                {
                    // memory_management_control_operation loop — rare for our pipeline; reject.
                    throw new NotSupportedException(
                        "adaptive_ref_pic_marking_mode_flag=1 not supported");
                }
            }
        }

        // entropy_coding_mode_flag=0, slice_type=I -> no cabac_init_idc
        int sliceQpDelta = ExpGolomb.ReadSe(ref r);

        uint disableDeblockingIdc = 0;
        int alphaOffset = 0;
        int betaOffset = 0;
        if (pps.DeblockingFilterControlPresentFlag)
        {
            disableDeblockingIdc = ExpGolomb.ReadUe(ref r);
            if (disableDeblockingIdc != 1)
            {
                alphaOffset = ExpGolomb.ReadSe(ref r) * 2;
                betaOffset = ExpGolomb.ReadSe(ref r) * 2;
            }
        }

        // slice_group_change_cycle only if num_slice_groups>1 (already rejected in PPS)

        return new SliceHeader
        {
            FirstMbInSlice = firstMbInSlice,
            SliceTypeRaw = sliceTypeRaw,
            SliceType = sliceType,
            AllSlicesSameType = allSame,
            PicParameterSetId = ppsId,
            FrameNum = frameNum,
            IdrPicFlag = idrPicFlag,
            IdrPicId = idrPicId,
            PicOrderCntLsb = picOrderCntLsb,
            DeltaPicOrderCntBottom = deltaPicOrderCntBottom,
            NoOutputOfPriorPicsFlag = noOutputPriorPics,
            LongTermReferenceFlag = longTermRef,
            SliceQpDelta = sliceQpDelta,
            DisableDeblockingFilterIdc = disableDeblockingIdc,
            SliceAlphaC0OffsetDiv2 = alphaOffset / 2,
            SliceBetaOffsetDiv2 = betaOffset / 2,
            NumRefIdxL0ActiveMinus1 = numRefIdxL0ActiveMinus1,
            NumRefIdxActiveOverrideFlag = numRefIdxOverride,
        };
    }
}
