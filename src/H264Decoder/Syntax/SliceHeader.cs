using H264Decoder.Bitstream;

namespace H264Decoder.Syntax;

/// <summary>Explicit pred_weight_table values (§7.3.3.2 / §8.4.2.3.2). Indices: per ref-idx
/// per list, plus chroma component 0=Cb, 1=Cr. Per-ref-idx flags absent in the bitstream are
/// expanded to the no-op default (weight = 1&lt;&lt;denom, offset = 0).</summary>
public sealed class PredWeightTable
{
    public required int LumaLog2WeightDenom { get; init; }
    public required int ChromaLog2WeightDenom { get; init; }

    // Per ref index. Length = num_ref_idx_active for the list.
    public required int[] LumaWeightL0 { get; init; }
    public required int[] LumaOffsetL0 { get; init; }
    public required int[,] ChromaWeightL0 { get; init; }  // [refIdx, c]
    public required int[,] ChromaOffsetL0 { get; init; }

    public int[]? LumaWeightL1 { get; init; }
    public int[]? LumaOffsetL1 { get; init; }
    public int[,]? ChromaWeightL1 { get; init; }
    public int[,]? ChromaOffsetL1 { get; init; }
}

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

    // B-slice fields (stage 1: parsed but MB-level B decoding not yet implemented).
    public bool DirectSpatialMvPredFlag { get; init; }
    public uint NumRefIdxL1ActiveMinus1 { get; init; }

    // CABAC
    public uint CabacInitIdc { get; init; }

    // pred_weight_table (§7.3.3.2). Null when the slice does not carry one (then MC is
    // unweighted "default" 1.0 * sample + 0 with no rounding).
    public PredWeightTable? PredWeights { get; init; }

    public int SliceQpY(PictureParameterSet pps) => 26 + pps.PicInitQpMinus26 + SliceQpDelta;

    /// <summary>Parse pred_weight_table (§7.3.3.2) for one list. Records explicit weight/offset
    /// pairs per ref index (defaults to the no-op weight 1<<denom + offset 0 when the per-ref flag
    /// is absent). Caller consumes the bits sequentially so subsequent slice header fields align.</summary>
    internal static void ParseOneListWeights(
        ref BitReader r, uint numRefIdxActiveMinus1, bool hasChroma,
        int lumaDenom, int chromaDenom,
        int[] lumaWeight, int[] lumaOffset,
        int[,] chromaWeight, int[,] chromaOffset)
    {
        int numActive = (int)(numRefIdxActiveMinus1 + 1);
        int defaultLuma = 1 << lumaDenom;
        int defaultChroma = 1 << chromaDenom;
        for (int i = 0; i < numActive; i++)
        {
            bool lumaFlag = r.ReadBit() == 1;
            if (lumaFlag)
            {
                lumaWeight[i] = ExpGolomb.ReadSe(ref r);
                lumaOffset[i] = ExpGolomb.ReadSe(ref r);
            }
            else
            {
                lumaWeight[i] = defaultLuma;
                lumaOffset[i] = 0;
            }
            if (hasChroma)
            {
                bool chromaFlag = r.ReadBit() == 1;
                if (chromaFlag)
                {
                    chromaWeight[i, 0] = ExpGolomb.ReadSe(ref r);
                    chromaOffset[i, 0] = ExpGolomb.ReadSe(ref r);
                    chromaWeight[i, 1] = ExpGolomb.ReadSe(ref r);
                    chromaOffset[i, 1] = ExpGolomb.ReadSe(ref r);
                }
                else
                {
                    chromaWeight[i, 0] = defaultChroma; chromaOffset[i, 0] = 0;
                    chromaWeight[i, 1] = defaultChroma; chromaOffset[i, 1] = 0;
                }
            }
        }
    }

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
        if (sliceType != SliceType.I && sliceType != SliceType.P && sliceType != SliceType.B)
        {
            throw new NotSupportedException($"slice_type {sliceType} not supported (I/P/B only)");
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

        // B-slice: direct_spatial_mv_pred_flag (spec §7.3.3).
        bool directSpatialMvPred = false;
        if (sliceType == SliceType.B)
        {
            directSpatialMvPred = r.ReadBit() == 1;
        }

        // num_ref_idx_active_override + ref_pic_list_modification — for P, SP and B.
        bool numRefIdxOverride = false;
        uint numRefIdxL0ActiveMinus1 = pps.NumRefIdxL0DefaultActiveMinus1;
        uint numRefIdxL1ActiveMinus1 = pps.NumRefIdxL1DefaultActiveMinus1;
        if (sliceType == SliceType.P || sliceType == SliceType.SP || sliceType == SliceType.B)
        {
            numRefIdxOverride = r.ReadBit() == 1;
            if (numRefIdxOverride)
            {
                numRefIdxL0ActiveMinus1 = ExpGolomb.ReadUe(ref r);
                if (sliceType == SliceType.B)
                {
                    numRefIdxL1ActiveMinus1 = ExpGolomb.ReadUe(ref r);
                }
            }
            // ref_pic_list_modification — list 0 for P/SP/B, plus list 1 for B.
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
            if (sliceType == SliceType.B)
            {
                bool listModL1 = r.ReadBit() == 1;
                if (listModL1)
                {
                    while (true)
                    {
                        uint op = ExpGolomb.ReadUe(ref r);
                        if (op == 3) break;
                        _ = ExpGolomb.ReadUe(ref r);
                    }
                }
            }
        }

        // pred_weight_table() (§7.3.3.2). Emitted for P/SP with weighted_pred_flag=1 or
        // for B with weighted_bipred_idc==1 (explicit). We parse-and-discard since
        // weighted prediction isn't implemented in our reconstructor.
        bool weightedForP = pps.WeightedPredFlag && (sliceType == SliceType.P || sliceType == SliceType.SP);
        bool weightedForB = pps.WeightedBipredIdc == 1 && sliceType == SliceType.B;
        PredWeightTable? predWeights = null;
        if (weightedForP || weightedForB)
        {
            int lumaDenom = (int)ExpGolomb.ReadUe(ref r);
            int chromaDenom = (int)ExpGolomb.ReadUe(ref r); // 4:2:0 always has chroma
            int n0 = (int)(numRefIdxL0ActiveMinus1 + 1);
            var lW0 = new int[n0]; var lO0 = new int[n0];
            var cW0 = new int[n0, 2]; var cO0 = new int[n0, 2];
            ParseOneListWeights(ref r, numRefIdxL0ActiveMinus1, hasChroma: true,
                lumaDenom, chromaDenom, lW0, lO0, cW0, cO0);
            int[]? lW1 = null, lO1 = null; int[,]? cW1 = null, cO1 = null;
            if (weightedForB)
            {
                int n1 = (int)(numRefIdxL1ActiveMinus1 + 1);
                lW1 = new int[n1]; lO1 = new int[n1];
                cW1 = new int[n1, 2]; cO1 = new int[n1, 2];
                ParseOneListWeights(ref r, numRefIdxL1ActiveMinus1, hasChroma: true,
                    lumaDenom, chromaDenom, lW1, lO1, cW1, cO1);
            }
            predWeights = new PredWeightTable
            {
                LumaLog2WeightDenom = lumaDenom,
                ChromaLog2WeightDenom = chromaDenom,
                LumaWeightL0 = lW0, LumaOffsetL0 = lO0,
                ChromaWeightL0 = cW0, ChromaOffsetL0 = cO0,
                LumaWeightL1 = lW1, LumaOffsetL1 = lO1,
                ChromaWeightL1 = cW1, ChromaOffsetL1 = cO1,
            };
        }

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
                    // memory_management_control_operation loop (§7.3.3.3). We parse the loop
                    // structure but reject any actual operations — the common case x264 emits
                    // is adaptive=1 with an empty loop (immediate op=0 terminator).
                    while (true)
                    {
                        uint mmco = ExpGolomb.ReadUe(ref r);
                        if (mmco == 0) break;
                        throw new NotSupportedException(
                            $"memory_management_control_operation {mmco} not supported");
                    }
                }
            }
        }

        uint cabacInitIdc = 0;
        if (pps.EntropyCodingModeFlag && sliceType != SliceType.I)
        {
            cabacInitIdc = ExpGolomb.ReadUe(ref r);
            if (cabacInitIdc > 2) throw new InvalidDataException($"cabac_init_idc {cabacInitIdc} out of range");
        }

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
            NumRefIdxL1ActiveMinus1 = numRefIdxL1ActiveMinus1,
            NumRefIdxActiveOverrideFlag = numRefIdxOverride,
            DirectSpatialMvPredFlag = directSpatialMvPred,
            CabacInitIdc = cabacInitIdc,
            PredWeights = predWeights,
        };
    }
}
