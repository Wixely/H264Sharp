using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Picture;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Tests.Fixtures;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.Syntax;

/// <summary>Long-term reference (LTR) handling — spec §7.3.3.3, §8.2.4, §8.2.5. Tests cover
/// MMCO op parsing on the slice header, ref_pic_list_modification capture, DPB marking via
/// <see cref="H264FrameDecoder"/>'s internal helpers, sliding-window pinning, and ref-list
/// construction with mixed short/long-term entries.</summary>
public sealed class LongTermReferenceTests
{
    // -----------------------------------------------------------------
    // Tiny bit/ue writer (mirrors IPcmTests' helpers, kept local so this
    // file stands alone).
    // -----------------------------------------------------------------
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _curByte;
        private int _bitInByte;
        public void WriteBit(int v)
        {
            _curByte |= (v & 1) << (7 - _bitInByte);
            _bitInByte++;
            if (_bitInByte == 8) { _bytes.Add((byte)_curByte); _curByte = 0; _bitInByte = 0; }
        }
        public void WriteBits(uint v, int n) { for (int i = n - 1; i >= 0; i--) WriteBit((int)((v >> i) & 1)); }
        public byte[] ToArray() { if (_bitInByte != 0) _bytes.Add((byte)_curByte); return _bytes.ToArray(); }
    }
    private static void WriteUe(BitWriter bw, uint codeNum)
    {
        uint v = codeNum + 1;
        int L = 0;
        while ((1u << (L + 1)) <= v) L++;
        for (int i = 0; i < L; i++) bw.WriteBit(0);
        bw.WriteBit(1);
        if (L > 0) bw.WriteBits(v - (1u << L), L);
    }
    private static void WriteSe(BitWriter bw, int v) =>
        WriteUe(bw, v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));

    // Build a minimal non-IDR P slice header (RBSP body) whose adaptive_ref_pic_marking
    // contains the supplied MMCO ops. Borrows the SPS/PPS from the standard 16x16 fixture
    // (Baseline, CAVLC, frame_num bits = 4, POC type 0).
    private static (byte[] rbsp, NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps)
        BuildNonIdrPSliceWithMmco(uint frameNum, params (uint op, uint a, uint b)[] mmco)
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var nals = AnnexBReader.SplitNalUnits(stream);
        var sps = SequenceParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        var pps = PictureParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Pps).Rbsp.Span);

        var bw = new BitWriter();
        WriteUe(bw, 0);                                  // first_mb_in_slice
        WriteUe(bw, 5);                                  // slice_type = 5 (P, all same)
        WriteUe(bw, 0);                                  // pic_parameter_set_id
        bw.WriteBits(frameNum, (int)sps.Log2MaxFrameNumMinus4 + 4);
        if (sps.PicOrderCntType == 0)
        {
            bw.WriteBits(frameNum * 2, (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag) WriteSe(bw, 0); // delta_pic_order_cnt_bottom
        }
        if (pps.RedundantPicCntPresentFlag) WriteUe(bw, 0);
        // P-slice: num_ref_idx_active_override_flag = 0
        bw.WriteBit(0);
        // ref_pic_list_modification_flag_l0 = 0
        bw.WriteBit(0);
        // pred_weight_table only when weighted_pred_flag is set for P-slices.
        if (pps.WeightedPredFlag)
        {
            WriteUe(bw, 0); WriteUe(bw, 0);  // log2 denoms
            // num_ref_idx_l0_default_active_minus1+1 entries; default minus1 used since override=0.
            for (uint i = 0; i <= pps.NumRefIdxL0DefaultActiveMinus1; i++)
            {
                bw.WriteBit(0); // luma_weight_l0_flag
                bw.WriteBit(0); // chroma_weight_l0_flag
            }
        }
        // dec_ref_pic_marking — non-IDR: adaptive_ref_pic_marking_mode_flag
        bw.WriteBit(1);
        foreach (var (op, a, b) in mmco)
        {
            WriteUe(bw, op);
            if (op == 1 || op == 3) WriteUe(bw, a);             // difference_of_pic_nums_minus1
            if (op == 2) WriteUe(bw, a);                        // long_term_pic_num
            if (op == 3 || op == 6) WriteUe(bw, b);             // long_term_frame_idx
            if (op == 4) WriteUe(bw, a);                        // max_long_term_frame_idx_plus1
        }
        WriteUe(bw, 0);                                  // mmco terminator
        if (pps.EntropyCodingModeFlag) WriteUe(bw, 0);   // cabac_init_idc
        WriteSe(bw, 0);                                  // slice_qp_delta
        if (pps.DeblockingFilterControlPresentFlag)
            WriteUe(bw, 1);                              // disable_deblocking_filter_idc = 1
        bw.WriteBit(1);                                  // rbsp_stop_one_bit

        byte[] rbsp = bw.ToArray();
        var nal = new NalUnit(nalRefIdc: 2, NalUnitType.SliceNonIdr, rbsp);
        return (rbsp, nal, sps, pps);
    }

    [Fact]
    public void SliceHeader_Parses_Mmco_Op6_AsLongTermAssignment()
    {
        var (rbsp, nal, sps, pps) = BuildNonIdrPSliceWithMmco(frameNum: 1, (6u, 0u, 3u));

        var hdr = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.True(hdr.AdaptiveRefPicMarkingMode);
        Assert.Single(hdr.MmcoOps);
        Assert.Equal(6u, hdr.MmcoOps[0].Op);
        Assert.Equal(3u, hdr.MmcoOps[0].LongTermFrameIdx);
    }

    [Fact]
    public void SliceHeader_Parses_Mmco_Op5_MemoryReset()
    {
        var (rbsp, nal, sps, pps) = BuildNonIdrPSliceWithMmco(frameNum: 2, (5u, 0u, 0u));

        var hdr = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.True(hdr.AdaptiveRefPicMarkingMode);
        Assert.Single(hdr.MmcoOps);
        Assert.Equal(5u, hdr.MmcoOps[0].Op);
    }

    [Fact]
    public void SliceHeader_Parses_Mmco_Op4_MaxLongTermIdx()
    {
        // op 4: max_long_term_frame_idx_plus1 = 3 -> MaxLongTermFrameIdx = 2.
        var (rbsp, nal, sps, pps) = BuildNonIdrPSliceWithMmco(frameNum: 3, (4u, 3u, 0u));

        var hdr = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.Single(hdr.MmcoOps);
        Assert.Equal(4u, hdr.MmcoOps[0].Op);
        Assert.Equal(3u, hdr.MmcoOps[0].MaxLongTermFrameIdxPlus1);
    }

    [Fact]
    public void SliceHeader_Parses_RefPicListModification_Op2()
    {
        // Build a P slice with ref_pic_list_modification_flag_l0 = 1, one op (long-term ref).
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var nals = AnnexBReader.SplitNalUnits(stream);
        var sps = SequenceParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        var pps = PictureParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Pps).Rbsp.Span);

        var bw = new BitWriter();
        WriteUe(bw, 0); WriteUe(bw, 5); WriteUe(bw, 0);
        bw.WriteBits(1, (int)sps.Log2MaxFrameNumMinus4 + 4);
        if (sps.PicOrderCntType == 0)
        {
            bw.WriteBits(2, (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag) WriteSe(bw, 0);
        }
        if (pps.RedundantPicCntPresentFlag) WriteUe(bw, 0);
        bw.WriteBit(0); // num_ref_idx_active_override_flag
        bw.WriteBit(1); // ref_pic_list_modification_flag_l0 = 1
        WriteUe(bw, 2); WriteUe(bw, 7); // op 2, long_term_pic_num = 7
        WriteUe(bw, 3);                 // loop terminator
        if (pps.WeightedPredFlag)
        {
            WriteUe(bw, 0); WriteUe(bw, 0);
            for (uint i = 0; i <= pps.NumRefIdxL0DefaultActiveMinus1; i++)
            { bw.WriteBit(0); bw.WriteBit(0); }
        }
        bw.WriteBit(0);                 // adaptive_ref_pic_marking_mode_flag = 0
        if (pps.EntropyCodingModeFlag) WriteUe(bw, 0);
        WriteSe(bw, 0);
        if (pps.DeblockingFilterControlPresentFlag) WriteUe(bw, 1);
        bw.WriteBit(1);

        var rbsp = bw.ToArray();
        var nal = new NalUnit(2, NalUnitType.SliceNonIdr, rbsp);
        var hdr = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.Single(hdr.RefPicListModificationL0);
        Assert.Equal(2u, hdr.RefPicListModificationL0[0].ModificationOfPicNumsIdc);
        Assert.Equal(7u, hdr.RefPicListModificationL0[0].Value);
    }

    // -----------------------------------------------------------------
    // DPB-marking behavior — exercise H264FrameDecoder helpers directly.
    // -----------------------------------------------------------------

    private static DecodedPicture MakeRefPic(int frameNum, int poc)
    {
        return new DecodedPicture(16, 16) { FrameNum = frameNum, PicOrderCnt = poc };
    }

    private static SliceHeader MakeHeader(uint frameNum, bool idr, bool ltrFlag,
        bool adaptive = false, MmcoOperation[]? mmco = null)
    {
        return new SliceHeader
        {
            FirstMbInSlice = 0, SliceTypeRaw = 5, SliceType = SliceType.P, AllSlicesSameType = true,
            PicParameterSetId = 0, FrameNum = frameNum,
            IdrPicFlag = idr, IdrPicId = 0, PicOrderCntLsb = frameNum * 2, DeltaPicOrderCntBottom = 0,
            NoOutputOfPriorPicsFlag = false, LongTermReferenceFlag = ltrFlag,
            SliceQpDelta = 0, DisableDeblockingFilterIdc = 1,
            SliceAlphaC0OffsetDiv2 = 0, SliceBetaOffsetDiv2 = 0,
            AdaptiveRefPicMarkingMode = adaptive,
            MmcoOps = mmco ?? Array.Empty<MmcoOperation>(),
        };
    }

    private static SequenceParameterSet MakeSps(uint maxRefs)
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        var nals = AnnexBReader.SplitNalUnits(stream);
        var baseSps = SequenceParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        // Clone with overridden MaxNumRefFrames — use required-init via object initializer.
        return new SequenceParameterSet
        {
            ProfileIdc = baseSps.ProfileIdc, LevelIdc = baseSps.LevelIdc,
            ConstraintSet0Flag = baseSps.ConstraintSet0Flag,
            ConstraintSet1Flag = baseSps.ConstraintSet1Flag,
            ConstraintSet2Flag = baseSps.ConstraintSet2Flag,
            ConstraintSet3Flag = baseSps.ConstraintSet3Flag,
            SeqParameterSetId = baseSps.SeqParameterSetId,
            Log2MaxFrameNumMinus4 = baseSps.Log2MaxFrameNumMinus4,
            PicOrderCntType = baseSps.PicOrderCntType,
            Log2MaxPicOrderCntLsbMinus4 = baseSps.Log2MaxPicOrderCntLsbMinus4,
            MaxNumRefFrames = maxRefs,
            GapsInFrameNumValueAllowedFlag = baseSps.GapsInFrameNumValueAllowedFlag,
            PicWidthInMbsMinus1 = baseSps.PicWidthInMbsMinus1,
            PicHeightInMapUnitsMinus1 = baseSps.PicHeightInMapUnitsMinus1,
            FrameMbsOnlyFlag = baseSps.FrameMbsOnlyFlag,
            Direct8x8InferenceFlag = baseSps.Direct8x8InferenceFlag,
            FrameCroppingFlag = baseSps.FrameCroppingFlag,
            FrameCropLeftOffset = baseSps.FrameCropLeftOffset,
            FrameCropRightOffset = baseSps.FrameCropRightOffset,
            FrameCropTopOffset = baseSps.FrameCropTopOffset,
            FrameCropBottomOffset = baseSps.FrameCropBottomOffset,
            VuiParametersPresentFlag = baseSps.VuiParametersPresentFlag,
            Vui = baseSps.Vui,
        };
    }

    [Fact]
    public void ApplyDecRefPicMarking_IdrLongTermFlag_MarksAsLongTerm()
    {
        var sps = MakeSps(maxRefs: 4);
        var pic = MakeRefPic(0, 0);
        var hdr = MakeHeader(frameNum: 0, idr: true, ltrFlag: true);
        var dpb = new List<DecodedPicture>();
        int maxLt = -1;

        H264FrameDecoder.ApplyDecRefPicMarking(pic, hdr, dpb, sps, ref maxLt);

        Assert.Single(dpb);
        Assert.True(dpb[0].IsLongTerm);
        Assert.Equal(0, dpb[0].LongTermFrameIdx);
        Assert.Equal(0, maxLt);
    }

    [Fact]
    public void ApplyDecRefPicMarking_Op6_MarksCurrentPicAsLongTerm()
    {
        var sps = MakeSps(maxRefs: 4);
        var pic = MakeRefPic(1, 2);
        var mmco = new[] { new MmcoOperation(6, 0, 0, LongTermFrameIdx: 2, 0) };
        var hdr = MakeHeader(frameNum: 1, idr: false, ltrFlag: false, adaptive: true, mmco: mmco);
        var dpb = new List<DecodedPicture> { MakeRefPic(0, 0) }; // prior IDR short-term
        int maxLt = -1;

        H264FrameDecoder.ApplyDecRefPicMarking(pic, hdr, dpb, sps, ref maxLt);

        Assert.Equal(2, dpb.Count);
        Assert.Same(pic, dpb[0]);
        Assert.True(pic.IsLongTerm);
        Assert.Equal(2, pic.LongTermFrameIdx);
    }

    [Fact]
    public void ApplyDecRefPicMarking_LongTermRef_SurvivesSlidingWindowEviction()
    {
        // MaxNumRefFrames = 2. Insert an IDR LT, then 3 short-term refs. The LT survives.
        var sps = MakeSps(maxRefs: 2);
        var dpb = new List<DecodedPicture>();
        int maxLt = -1;
        // IDR with long_term_reference_flag=1.
        var idr = MakeRefPic(0, 0);
        H264FrameDecoder.ApplyDecRefPicMarking(idr, MakeHeader(0, idr: true, ltrFlag: true), dpb, sps, ref maxLt);
        // Three subsequent short-term ref slices.
        for (int i = 1; i <= 3; i++)
        {
            var p = MakeRefPic(i, i * 2);
            H264FrameDecoder.ApplyDecRefPicMarking(p, MakeHeader((uint)i, idr: false, ltrFlag: false), dpb, sps, ref maxLt);
        }

        Assert.Contains(dpb, p => p.IsLongTerm && p.LongTermFrameIdx == 0);
        Assert.Equal(2, dpb.Count(p => !p.IsLongTerm));
    }

    [Fact]
    public void ApplyDecRefPicMarking_Op1_RemovesShortTermRef()
    {
        var sps = MakeSps(maxRefs: 4);
        var dpb = new List<DecodedPicture>
        {
            MakeRefPic(3, 6), MakeRefPic(2, 4), MakeRefPic(1, 2), MakeRefPic(0, 0),
        };
        int maxLt = -1;
        // Current frame_num=4; difference_of_pic_nums_minus1=1 => target picNum = 4 - 2 = 2.
        var mmco = new[] { new MmcoOperation(1, DifferenceOfPicNumsMinus1: 1, 0, 0, 0) };
        var pic = MakeRefPic(4, 8);
        H264FrameDecoder.ApplyDecRefPicMarking(pic, MakeHeader(4, idr: false, ltrFlag: false, adaptive: true, mmco: mmco), dpb, sps, ref maxLt);

        Assert.DoesNotContain(dpb, p => !p.IsLongTerm && p.FrameNum == 2);
    }

    [Fact]
    public void ApplyDecRefPicMarking_Op5_ClearsDpb()
    {
        var sps = MakeSps(maxRefs: 4);
        var dpb = new List<DecodedPicture>
        {
            MakeRefPic(2, 4), MakeRefPic(1, 2), MakeRefPic(0, 0),
        };
        int maxLt = 2;
        var mmco = new[] { new MmcoOperation(5, 0, 0, 0, 0) };
        var pic = MakeRefPic(3, 6);
        H264FrameDecoder.ApplyDecRefPicMarking(pic, MakeHeader(3, idr: false, ltrFlag: false, adaptive: true, mmco: mmco), dpb, sps, ref maxLt);

        Assert.Single(dpb);
        Assert.Same(pic, dpb[0]);
        Assert.Equal(-1, maxLt);
    }

    [Fact]
    public void BuildPSliceRefListL0_OrdersShortTermDescThenLongTermAsc()
    {
        // Short-term: picNums 5, 3 (newest first). Long-term: idx 0, idx 2.
        var st1 = MakeRefPic(5, 10); st1.LongTermPicNum = 5;
        var st2 = MakeRefPic(3, 6);  st2.LongTermPicNum = 3;
        var lt0 = MakeRefPic(0, 0);  lt0.IsLongTerm = true; lt0.LongTermFrameIdx = 0; lt0.LongTermPicNum = 0;
        var lt2 = MakeRefPic(2, 4);  lt2.IsLongTerm = true; lt2.LongTermFrameIdx = 2; lt2.LongTermPicNum = 2;
        var dpb = new List<DecodedPicture> { st1, lt2, st2, lt0 };

        var l0 = H264FrameDecoder.BuildPSliceRefListL0(dpb, numActiveL0: 4);

        Assert.Equal(4, l0.Count);
        Assert.Same(st1, l0[0]);
        Assert.Same(st2, l0[1]);
        Assert.Same(lt0, l0[2]);
        Assert.Same(lt2, l0[3]);
    }

    [Fact]
    public void BuildBSliceRefLists_AppendsLongTermAfterShortTerm()
    {
        // currentPoc = 4. Short-term past poc=2, future poc=6. One long-term.
        var past = MakeRefPic(1, 2);
        var future = MakeRefPic(3, 6);
        var lt = MakeRefPic(0, 0); lt.IsLongTerm = true; lt.LongTermFrameIdx = 0; lt.LongTermPicNum = 0;
        var dpb = new List<DecodedPicture> { future, lt, past };

        var (l0, l1) = H264FrameDecoder.BuildBSliceRefLists(dpb, currentPoc: 4, numActiveL0: 3, numActiveL1: 3);

        Assert.Same(past, l0[0]);
        Assert.Same(future, l0[1]);
        Assert.Same(lt, l0[2]);
        Assert.Same(future, l1[0]);
        Assert.Same(past, l1[1]);
        Assert.Same(lt, l1[2]);
    }

    [Fact]
    public void RefPicListModification_Op2_PullsLongTermToFront()
    {
        // Build a starting L0 of short-term refs; apply op-2 to insert a long-term at index 0.
        var st1 = MakeRefPic(5, 10); st1.LongTermPicNum = 5;
        var st2 = MakeRefPic(3, 6);  st2.LongTermPicNum = 3;
        var lt = MakeRefPic(0, 0);   lt.IsLongTerm = true; lt.LongTermFrameIdx = 7; lt.LongTermPicNum = 7;
        var dpb = new List<DecodedPicture> { st1, st2, lt };
        var refList = new List<DecodedPicture> { st1, st2 };
        var ops = new[] { new RefPicListModification(2, 7) };

        H264FrameDecoder.ApplyRefPicListModification(refList, ops, dpb,
            curFrameNum: 6, maxFrameNum: 16, numActive: 2);

        Assert.Equal(2, refList.Count);
        Assert.Same(lt, refList[0]);
        Assert.Same(st1, refList[1]);
    }

    [Fact]
    public void RefPicListModification_Op0_ShortTermDifference()
    {
        // current frame_num = 5. Initial picNumPred = 5. op 0 with abs_diff_pic_num_minus1=1
        // -> target picNum = 5 - 2 = 3. Should pull frameNum=3 ref to index 0.
        var st5 = MakeRefPic(5, 10); st5.LongTermPicNum = 5;
        var st3 = MakeRefPic(3, 6);  st3.LongTermPicNum = 3;
        var st1 = MakeRefPic(1, 2);  st1.LongTermPicNum = 1;
        var dpb = new List<DecodedPicture> { st5, st3, st1 };
        var refList = new List<DecodedPicture> { st5, st3, st1 };
        var ops = new[] { new RefPicListModification(0, 1) }; // abs_diff_pic_num_minus1 = 1

        H264FrameDecoder.ApplyRefPicListModification(refList, ops, dpb,
            curFrameNum: 5, maxFrameNum: 16, numActive: 3);

        Assert.Same(st3, refList[0]);
    }
}
