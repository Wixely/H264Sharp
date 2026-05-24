using H264Decoder.Encoder.Bitstream;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Syntax;

/// <summary>Serialize a slice header (spec §7.3.3). Supports IDR I-slices and non-IDR P-slices.</summary>
public static class SliceHeaderWriter
{
    /// <summary>Encode the slice header for an IDR I-slice into the supplied BitWriter.
    /// The slice header is the leading portion of the slice NAL RBSP; macroblock_layer()
    /// data and rbsp_trailing_bits follow in the same RBSP.</summary>
    public static void Write(
        BitWriter w,
        SequenceParameterSet sps,
        PictureParameterSet pps,
        uint frameNum,
        uint idrPicId,
        int sliceQpDelta,
        uint disableDeblockingFilterIdc)
    {
        ExpGolombWriter.WriteUe(w, 0); // first_mb_in_slice
        // slice_type = 7 (all-I, "all slices in this picture are I"); spec §7.4.3.
        ExpGolombWriter.WriteUe(w, 7);
        ExpGolombWriter.WriteUe(w, pps.PicParameterSetId);
        int frameNumBits = (int)sps.Log2MaxFrameNumMinus4 + 4;
        w.WriteBits(frameNum, frameNumBits);
        // frame_mbs_only=1: no field_pic_flag / bottom_field_flag.
        // IDR: idr_pic_id
        ExpGolombWriter.WriteUe(w, idrPicId);
        if (sps.PicOrderCntType == 0)
        {
            int lsbBits = (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4;
            w.WriteBits(0, lsbBits); // pic_order_cnt_lsb = 0 for IDR
            // No bottom_field_pic_order_in_frame_present_flag in our PPS.
        }
        // pic_order_cnt_type==2 has no extra fields.
        if (pps.RedundantPicCntPresentFlag)
        {
            ExpGolombWriter.WriteUe(w, 0);
        }
        // IDR: dec_ref_pic_marking carries no_output_of_prior_pics_flag + long_term_reference_flag.
        // (NalRefIdc != 0 for IDR.)
        w.WriteBit(0); // no_output_of_prior_pics_flag
        w.WriteBit(0); // long_term_reference_flag
        // CABAC entropy mode: when PPS flag is set, cabac_init_idc is signalled here for I-slices too
        // per spec §7.3.3 (cabac_init_idc presence is gated by entropy_coding_mode_flag && slice_type != I/SI).
        // Wait — actually for I/SI slices the cabac_init_idc is NOT present per spec; only for P/B/SP.
        // So we omit it here even when CABAC is on for I-slice.
        ExpGolombWriter.WriteSe(w, sliceQpDelta);
        if (pps.DeblockingFilterControlPresentFlag)
        {
            ExpGolombWriter.WriteUe(w, disableDeblockingFilterIdc);
            if (disableDeblockingFilterIdc != 1)
            {
                ExpGolombWriter.WriteSe(w, 0); // slice_alpha_c0_offset_div2
                ExpGolombWriter.WriteSe(w, 0); // slice_beta_offset_div2
            }
        }
    }

    /// <summary>Encode the slice header for a non-IDR P-slice (single L0 reference).
    /// Sets slice_type=5 (all-P), num_ref_idx_active_override_flag=0, no ref list modification,
    /// no pred_weight_table, no adaptive ref-pic marking. Caller supplies nal_ref_idc != 0.
    /// When the PPS has entropy_coding_mode_flag=1, cabac_init_idc is signalled after the
    /// dec_ref_pic_marking block per spec §7.3.3.</summary>
    public static void WritePSlice(
        BitWriter w,
        SequenceParameterSet sps,
        PictureParameterSet pps,
        uint frameNum,
        int sliceQpDelta,
        uint disableDeblockingFilterIdc,
        uint cabacInitIdc = 0)
    {
        ExpGolombWriter.WriteUe(w, 0); // first_mb_in_slice
        // slice_type = 5 (all-P, "all slices in this picture are P"); spec Table 7-6.
        ExpGolombWriter.WriteUe(w, 5);
        ExpGolombWriter.WriteUe(w, pps.PicParameterSetId);
        int frameNumBits = (int)sps.Log2MaxFrameNumMinus4 + 4;
        w.WriteBits(frameNum, frameNumBits);
        // frame_mbs_only=1: no field_pic_flag / bottom_field_flag.
        // Non-IDR: no idr_pic_id.
        if (sps.PicOrderCntType == 0)
        {
            int lsbBits = (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4;
            w.WriteBits(0, lsbBits);
        }
        // pic_order_cnt_type==2: no extra fields.
        if (pps.RedundantPicCntPresentFlag)
        {
            ExpGolombWriter.WriteUe(w, 0);
        }
        // P-slice ref-list management.
        w.WriteBit(0); // num_ref_idx_active_override_flag = 0
        w.WriteBit(0); // ref_pic_list_modification_flag_l0 = 0
        // pred_weight_table absent (PPS WeightedPredFlag=0).
        // dec_ref_pic_marking for non-IDR ref slice: adaptive_ref_pic_marking_mode_flag = 0 (sliding window).
        w.WriteBit(0);
        // CABAC P/SP-slice: emit cabac_init_idc when entropy_coding_mode_flag=1 (spec §7.3.3).
        if (pps.EntropyCodingModeFlag)
        {
            ExpGolombWriter.WriteUe(w, cabacInitIdc);
        }
        ExpGolombWriter.WriteSe(w, sliceQpDelta);
        if (pps.DeblockingFilterControlPresentFlag)
        {
            ExpGolombWriter.WriteUe(w, disableDeblockingFilterIdc);
            if (disableDeblockingFilterIdc != 1)
            {
                ExpGolombWriter.WriteSe(w, 0);
                ExpGolombWriter.WriteSe(w, 0);
            }
        }
    }
}
