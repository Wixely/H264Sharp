using H264Decoder.Encoder.Bitstream;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder.Syntax;

/// <summary>Serialize a baseline-profile SPS to RBSP bytes (spec §7.3.2.1).</summary>
public static class SpsWriter
{
    /// <summary>Build a minimal Baseline SPS for an I-frame-only stream.</summary>
    public static SequenceParameterSet BuildBaseline(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("width/height must be positive");
        int picWidthInMbs = (width + 15) / 16;
        int picHeightInMbs = (height + 15) / 16;
        int paddedWidth = picWidthInMbs * 16;
        int paddedHeight = picHeightInMbs * 16;
        // Use frame cropping to convey the requested (possibly non-MB-aligned) display size.
        // Crop is in chroma samples for SubHeightC/SubWidthC == 2 -> 2-sample units of luma.
        int cropRight = (paddedWidth - width) / 2;
        int cropBottom = (paddedHeight - height) / 2;
        bool cropFlag = (cropRight | cropBottom) != 0;
        return new SequenceParameterSet
        {
            ProfileIdc = 66,
            ConstraintSet0Flag = true,
            ConstraintSet1Flag = true,
            ConstraintSet2Flag = true,
            ConstraintSet3Flag = false,
            LevelIdc = 30,
            SeqParameterSetId = 0,
            Log2MaxFrameNumMinus4 = 0,
            PicOrderCntType = 2,
            Log2MaxPicOrderCntLsbMinus4 = 0,
            MaxNumRefFrames = 1,
            GapsInFrameNumValueAllowedFlag = false,
            PicWidthInMbsMinus1 = (uint)(picWidthInMbs - 1),
            PicHeightInMapUnitsMinus1 = (uint)(picHeightInMbs - 1),
            FrameMbsOnlyFlag = true,
            Direct8x8InferenceFlag = false,
            FrameCroppingFlag = cropFlag,
            FrameCropLeftOffset = 0,
            FrameCropRightOffset = (uint)cropRight,
            FrameCropTopOffset = 0,
            FrameCropBottomOffset = (uint)cropBottom,
            VuiParametersPresentFlag = false,
        };
    }

    /// <summary>Build a Main-profile SPS for streams that include B-frames. Switches to
    /// pic_order_cnt_type=0 (explicit POC LSB) so the decoder can recover display order from the
    /// out-of-order decode sequence (IPBP...). num_ref_frames=2 lets us address one past and one
    /// future reference. Log2MaxPicOrderCntLsbMinus4=4 → 8-bit LSB (range 0..255), enough headroom
    /// for any reasonable GOP at <see cref="Log2MaxFrameNumMinus4"/>=0 (4-bit frame_num wrap).</summary>
    public static SequenceParameterSet BuildMain(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("width/height must be positive");
        int picWidthInMbs = (width + 15) / 16;
        int picHeightInMbs = (height + 15) / 16;
        int paddedWidth = picWidthInMbs * 16;
        int paddedHeight = picHeightInMbs * 16;
        int cropRight = (paddedWidth - width) / 2;
        int cropBottom = (paddedHeight - height) / 2;
        bool cropFlag = (cropRight | cropBottom) != 0;
        return new SequenceParameterSet
        {
            ProfileIdc = 77, // Main profile — supports B-slices + CABAC.
            ConstraintSet0Flag = false,
            ConstraintSet1Flag = true,  // Main-conformant
            ConstraintSet2Flag = false,
            ConstraintSet3Flag = false,
            LevelIdc = 30,
            SeqParameterSetId = 0,
            Log2MaxFrameNumMinus4 = 0,
            PicOrderCntType = 0,
            Log2MaxPicOrderCntLsbMinus4 = 4, // MaxPicOrderCntLsb = 256.
            MaxNumRefFrames = 2,
            GapsInFrameNumValueAllowedFlag = false,
            PicWidthInMbsMinus1 = (uint)(picWidthInMbs - 1),
            PicHeightInMapUnitsMinus1 = (uint)(picHeightInMbs - 1),
            FrameMbsOnlyFlag = true,
            Direct8x8InferenceFlag = true, // Required by spec for B-slices with frame_mbs_only=1.
            FrameCroppingFlag = cropFlag,
            FrameCropLeftOffset = 0,
            FrameCropRightOffset = (uint)cropRight,
            FrameCropTopOffset = 0,
            FrameCropBottomOffset = (uint)cropBottom,
            VuiParametersPresentFlag = false,
        };
    }

    public static byte[] Serialize(SequenceParameterSet sps)
    {
        var w = new BitWriter(64);
        w.WriteBits(sps.ProfileIdc, 8);
        w.WriteBit(sps.ConstraintSet0Flag ? 1u : 0u);
        w.WriteBit(sps.ConstraintSet1Flag ? 1u : 0u);
        w.WriteBit(sps.ConstraintSet2Flag ? 1u : 0u);
        w.WriteBit(sps.ConstraintSet3Flag ? 1u : 0u);
        w.WriteBits(0, 4); // reserved_zero_4bits
        w.WriteBits(sps.LevelIdc, 8);
        ExpGolombWriter.WriteUe(w, sps.SeqParameterSetId);
        // Baseline (66) -> no chroma_format / bit_depth fields here.
        ExpGolombWriter.WriteUe(w, sps.Log2MaxFrameNumMinus4);
        ExpGolombWriter.WriteUe(w, sps.PicOrderCntType);
        if (sps.PicOrderCntType == 0)
        {
            ExpGolombWriter.WriteUe(w, sps.Log2MaxPicOrderCntLsbMinus4);
        }
        ExpGolombWriter.WriteUe(w, sps.MaxNumRefFrames);
        w.WriteBit(sps.GapsInFrameNumValueAllowedFlag ? 1u : 0u);
        ExpGolombWriter.WriteUe(w, sps.PicWidthInMbsMinus1);
        ExpGolombWriter.WriteUe(w, sps.PicHeightInMapUnitsMinus1);
        w.WriteBit(sps.FrameMbsOnlyFlag ? 1u : 0u);
        w.WriteBit(sps.Direct8x8InferenceFlag ? 1u : 0u);
        w.WriteBit(sps.FrameCroppingFlag ? 1u : 0u);
        if (sps.FrameCroppingFlag)
        {
            ExpGolombWriter.WriteUe(w, sps.FrameCropLeftOffset);
            ExpGolombWriter.WriteUe(w, sps.FrameCropRightOffset);
            ExpGolombWriter.WriteUe(w, sps.FrameCropTopOffset);
            ExpGolombWriter.WriteUe(w, sps.FrameCropBottomOffset);
        }
        w.WriteBit(sps.VuiParametersPresentFlag ? 1u : 0u);
        w.WriteRbspTrailingBits();
        return w.ToByteArray();
    }
}
