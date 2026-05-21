using H264Decoder.Bitstream;

namespace H264Decoder.Syntax;

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
    public required bool Direct8x8InferenceFlag { get; init; }

    public required bool FrameCroppingFlag { get; init; }
    public required uint FrameCropLeftOffset { get; init; }
    public required uint FrameCropRightOffset { get; init; }
    public required uint FrameCropTopOffset { get; init; }
    public required uint FrameCropBottomOffset { get; init; }

    public required bool VuiParametersPresentFlag { get; init; }

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
        if (profileIdc != 66)
        {
            throw new NotSupportedException($"SPS profile_idc {profileIdc} is not Baseline (66)");
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

        // Baseline (profile_idc == 66) skips the chroma_format_idc / bit_depth fields.

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
        if (!frameMbsOnly)
        {
            throw new NotSupportedException("SPS frame_mbs_only_flag=0 (interlaced) is not supported");
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
        // We deliberately do not parse VUI — not needed for sample reconstruction.

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
            Direct8x8InferenceFlag = direct8x8,
            FrameCroppingFlag = cropFlag,
            FrameCropLeftOffset = cropL,
            FrameCropRightOffset = cropR,
            FrameCropTopOffset = cropT,
            FrameCropBottomOffset = cropB,
            VuiParametersPresentFlag = vui,
        };
    }
}
