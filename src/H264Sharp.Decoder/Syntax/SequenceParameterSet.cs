using H264Sharp.Decoder.Bitstream;

namespace H264Sharp.Decoder.Syntax;

/// <summary>
/// H.264 Sequence Parameter Set, Baseline-profile subset (spec §7.3.2.1.1).
/// </summary>
public sealed class SequenceParameterSet
{
    public required byte ProfileIdc { get; init; }
    public required byte LevelIdc { get; init; }
    public required bool ConstraintSet0Flag { get; init; }
    public required bool ConstraintSet1Flag { get; init; }
    public required bool ConstraintSet2Flag { get; init; }
    public required bool ConstraintSet3Flag { get; init; }
    public required uint SeqParameterSetId { get; init; }

    public required uint Log2MaxFrameNumMinus4 { get; init; }
    public required uint PicOrderCntType { get; init; }
    public required uint Log2MaxPicOrderCntLsbMinus4 { get; init; }
    public required uint MaxNumRefFrames { get; init; }
    public required bool GapsInFrameNumValueAllowedFlag { get; init; }

    public required uint PicWidthInMbsMinus1 { get; init; }
    public required uint PicHeightInMapUnitsMinus1 { get; init; }
    public required bool FrameMbsOnlyFlag { get; init; }
    /// <summary>Only present when <see cref="FrameMbsOnlyFlag"/> is false. When true, the bitstream
    /// uses MBAFF (mb_adaptive_frame_field_flag = 1) — per-MB-pair frame/field coding.</summary>
    public bool MbAdaptiveFrameFieldFlag { get; init; }
    public required bool Direct8x8InferenceFlag { get; init; }

    public required bool FrameCroppingFlag { get; init; }
    public required uint FrameCropLeftOffset { get; init; }
    public required uint FrameCropRightOffset { get; init; }
    public required uint FrameCropTopOffset { get; init; }
    public required uint FrameCropBottomOffset { get; init; }

    public required bool VuiParametersPresentFlag { get; init; }

    /// <summary>VUI subset that affects display / colour conversion (spec §E.1.1). Null when VuiParametersPresentFlag is false.</summary>
    public VuiParameters? Vui { get; init; }

    // Derived (chroma_format_idc defaults to 1 for Baseline -> 4:2:0; bit depth defaults to 8)
    public uint ChromaFormatIdc => 1;
    public uint BitDepthY => 8;
    public uint BitDepthC => 8;
    public uint SubWidthC => 2;
    public uint SubHeightC => 2;

    public uint PicWidthInMbs => PicWidthInMbsMinus1 + 1;
    public uint PicHeightInMapUnits => PicHeightInMapUnitsMinus1 + 1;
    public uint PicWidthInSamplesL => PicWidthInMbs * 16;
    public uint PicHeightInMbs => FrameMbsOnlyFlag ? PicHeightInMapUnits : PicHeightInMapUnits * 2;
    public uint PicHeightInSamplesL => PicHeightInMbs * 16;

    public uint CroppedWidth => FrameCroppingFlag
        ? PicWidthInSamplesL - SubWidthC * (FrameCropLeftOffset + FrameCropRightOffset)
        : PicWidthInSamplesL;

    public uint CroppedHeight => FrameCroppingFlag
        ? PicHeightInSamplesL - SubHeightC * (uint)(FrameMbsOnlyFlag ? 1 : 2) * (FrameCropTopOffset + FrameCropBottomOffset)
        : PicHeightInSamplesL;

    public static SequenceParameterSet Parse(ReadOnlySpan<byte> rbsp)
    {
        var r = new BitReader(rbsp);
        byte profileIdc = (byte)r.ReadBits(8);
        if (profileIdc != 66 && profileIdc != 77 && profileIdc != 88 && profileIdc != 100)
        {
            throw new NotSupportedException($"SPS profile_idc {profileIdc} not supported (Baseline/Main/Extended/High only)");
        }

        bool cs0 = r.ReadBit() == 1;
        bool cs1 = r.ReadBit() == 1;
        bool cs2 = r.ReadBit() == 1;
        bool cs3 = r.ReadBit() == 1;
        _ = r.ReadBit(); // constraint_set4
        _ = r.ReadBit(); // constraint_set5
        _ = r.ReadBits(2); // reserved_zero_2bits

        byte levelIdc = (byte)r.ReadBits(8);
        uint spsId = ExpGolomb.ReadUe(ref r);

        // High profile (100) carries chroma_format_idc / bit_depth fields here.
        // Restrict to 4:2:0, 8-bit, no scaling lists, no separate colour plane.
        if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244 ||
            profileIdc == 44 || profileIdc == 83 || profileIdc == 86 || profileIdc == 118 ||
            profileIdc == 128 || profileIdc == 138 || profileIdc == 139 || profileIdc == 134 || profileIdc == 135)
        {
            uint chromaFormatIdc = ExpGolomb.ReadUe(ref r);
            if (chromaFormatIdc != 1)
            {
                throw new NotSupportedException($"SPS chroma_format_idc {chromaFormatIdc} not supported (4:2:0 only)");
            }
            uint bitDepthLumaMinus8 = ExpGolomb.ReadUe(ref r);
            uint bitDepthChromaMinus8 = ExpGolomb.ReadUe(ref r);
            if (bitDepthLumaMinus8 != 0 || bitDepthChromaMinus8 != 0)
            {
                throw new NotSupportedException("SPS bit_depth != 8 not supported");
            }
            _ = r.ReadBit(); // qpprime_y_zero_transform_bypass_flag
            bool seqScalingMatrixPresent = r.ReadBit() == 1;
            if (seqScalingMatrixPresent)
            {
                throw new NotSupportedException("SPS seq_scaling_matrix_present_flag=1 not supported");
            }
        }

        uint log2MaxFrameNumMinus4 = ExpGolomb.ReadUe(ref r);
        uint picOrderCntType = ExpGolomb.ReadUe(ref r);
        uint log2MaxPicOrderCntLsbMinus4 = 0;
        if (picOrderCntType == 0)
        {
            log2MaxPicOrderCntLsbMinus4 = ExpGolomb.ReadUe(ref r);
        }
        else if (picOrderCntType == 1)
        {
            throw new NotSupportedException("SPS pic_order_cnt_type=1 is not supported");
        }

        uint maxNumRefFrames = ExpGolomb.ReadUe(ref r);
        bool gapsAllowed = r.ReadBit() == 1;
        uint picWidthInMbsMinus1 = ExpGolomb.ReadUe(ref r);
        uint picHeightInMapUnitsMinus1 = ExpGolomb.ReadUe(ref r);
        bool frameMbsOnly = r.ReadBit() == 1;
        // mb_adaptive_frame_field_flag is only present when frame_mbs_only_flag == 0 (spec §7.3.2.1.1).
        // Stage 1 of interlaced support: we parse the field but the decoder still rejects field
        // pictures and MBAFF at decode dispatch with a clear, parameterized error.
        bool mbAdaptiveFrameField = false;
        if (!frameMbsOnly)
        {
            mbAdaptiveFrameField = r.ReadBit() == 1;
        }
        bool direct8x8 = r.ReadBit() == 1;
        bool cropFlag = r.ReadBit() == 1;
        uint cropL = 0, cropR = 0, cropT = 0, cropB = 0;
        if (cropFlag)
        {
            cropL = ExpGolomb.ReadUe(ref r);
            cropR = ExpGolomb.ReadUe(ref r);
            cropT = ExpGolomb.ReadUe(ref r);
            cropB = ExpGolomb.ReadUe(ref r);
        }
        bool vui = r.ReadBit() == 1;
        VuiParameters? vuiParams = vui ? VuiParameters.Parse(ref r) : null;

        return new SequenceParameterSet
        {
            ProfileIdc = profileIdc,
            ConstraintSet0Flag = cs0,
            ConstraintSet1Flag = cs1,
            ConstraintSet2Flag = cs2,
            ConstraintSet3Flag = cs3,
            LevelIdc = levelIdc,
            SeqParameterSetId = spsId,
            Log2MaxFrameNumMinus4 = log2MaxFrameNumMinus4,
            PicOrderCntType = picOrderCntType,
            Log2MaxPicOrderCntLsbMinus4 = log2MaxPicOrderCntLsbMinus4,
            MaxNumRefFrames = maxNumRefFrames,
            GapsInFrameNumValueAllowedFlag = gapsAllowed,
            PicWidthInMbsMinus1 = picWidthInMbsMinus1,
            PicHeightInMapUnitsMinus1 = picHeightInMapUnitsMinus1,
            FrameMbsOnlyFlag = frameMbsOnly,
            MbAdaptiveFrameFieldFlag = mbAdaptiveFrameField,
            Direct8x8InferenceFlag = direct8x8,
            FrameCroppingFlag = cropFlag,
            FrameCropLeftOffset = cropL,
            FrameCropRightOffset = cropR,
            FrameCropTopOffset = cropT,
            FrameCropBottomOffset = cropB,
            VuiParametersPresentFlag = vui,
            Vui = vuiParams,
        };
    }
}
