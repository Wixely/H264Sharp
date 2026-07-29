using H264Sharp.Decoder;
using H264Sharp.Decoder.Picture;
using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Tests.Syntax;

/// <summary>Reference-picture list construction and DPB marking (spec §8.2.4 / §8.2.5), exercised
/// directly through <see cref="H264FrameDecoder"/>'s internal helpers with synthesized pictures —
/// no ffmpeg dependency, so these run in CI. Guards the round of fixes to sliding-window eviction,
/// B-list ordering, ref-list modification, and MMCO PicNum matching.</summary>
public sealed class RefListManagementTests
{
    private static DecodedPicture Pic(int frameNum, int poc, bool longTerm = false, int ltIdx = 0) =>
        new DecodedPicture(16, 16)
        {
            FrameNum = frameNum,
            PicOrderCnt = poc,
            IsLongTerm = longTerm,
            LongTermFrameIdx = ltIdx,
            LongTermPicNum = ltIdx,
        };

    private static SequenceParameterSet MakeSps(uint maxRefs) => new()
    {
        ProfileIdc = 66, LevelIdc = 30,
        ConstraintSet0Flag = false, ConstraintSet1Flag = false,
        ConstraintSet2Flag = false, ConstraintSet3Flag = false,
        SeqParameterSetId = 0,
        Log2MaxFrameNumMinus4 = 0,          // MaxFrameNum = 16
        PicOrderCntType = 0, Log2MaxPicOrderCntLsbMinus4 = 4,
        MaxNumRefFrames = maxRefs, GapsInFrameNumValueAllowedFlag = false,
        PicWidthInMbsMinus1 = 0, PicHeightInMapUnitsMinus1 = 0,
        FrameMbsOnlyFlag = true, Direct8x8InferenceFlag = true,
        FrameCroppingFlag = false,
        FrameCropLeftOffset = 0, FrameCropRightOffset = 0,
        FrameCropTopOffset = 0, FrameCropBottomOffset = 0,
        VuiParametersPresentFlag = false,
    };

    private static SliceHeader PHeader(uint frameNum, bool adaptive = false, MmcoOperation[]? mmco = null) => new()
    {
        FirstMbInSlice = 0, SliceTypeRaw = 5, SliceType = SliceType.P, AllSlicesSameType = true,
        PicParameterSetId = 0, FrameNum = frameNum,
        IdrPicFlag = false, IdrPicId = 0, PicOrderCntLsb = frameNum * 2, DeltaPicOrderCntBottom = 0,
        NoOutputOfPriorPicsFlag = false, LongTermReferenceFlag = false,
        SliceQpDelta = 0, DisableDeblockingFilterIdc = 1,
        SliceAlphaC0OffsetDiv2 = 0, SliceBetaOffsetDiv2 = 0,
        AdaptiveRefPicMarkingMode = adaptive,
        MmcoOps = mmco ?? Array.Empty<MmcoOperation>(),
    };

    // --- Sliding window (§8.2.5.3): the cap counts short-term + long-term together. ---

    [Fact]
    public void SlidingWindow_LongTermCountsTowardCap()
    {
        var sps = MakeSps(maxRefs: 2);
        var dpb = new List<DecodedPicture> { Pic(3, 6), Pic(0, 0, longTerm: true, ltIdx: 0) };
        int maxLt = 0;

        // Add a new short-term reference. With one long-term pinned and cap 2, exactly one
        // short-term survives (total = 2) — not two.
        H264FrameDecoder.ApplyDecRefPicMarking(Pic(4, 8), PHeader(4), dpb, sps, ref maxLt);

        Assert.Equal(2, dpb.Count);
        Assert.Equal(1, dpb.Count(p => !p.IsLongTerm));
        Assert.Contains(dpb, p => p.IsLongTerm && p.LongTermFrameIdx == 0);
    }

    [Fact]
    public void SlidingWindow_EvictsOldestShortTermFirst()
    {
        var sps = MakeSps(maxRefs: 2);
        var dpb = new List<DecodedPicture> { Pic(2, 4), Pic(1, 2) };
        int maxLt = -1;

        H264FrameDecoder.ApplyDecRefPicMarking(Pic(3, 6), PHeader(3), dpb, sps, ref maxLt);

        // frame_num 1 (oldest, smallest FrameNumWrap) is evicted; 2 and 3 remain.
        Assert.Equal(2, dpb.Count);
        Assert.DoesNotContain(dpb, p => p.FrameNum == 1);
        Assert.Contains(dpb, p => p.FrameNum == 2);
        Assert.Contains(dpb, p => p.FrameNum == 3);
    }

    // --- B ref lists (§8.2.4.2.3): swap L1[0]/L1[1] when L1 == L0 and len > 1. ---

    [Fact]
    public void BuildBSliceRefLists_L1EqualsL0_SwapsFirstTwo()
    {
        // Two past refs, no future ref -> L0 and L1 both order as [poc4, poc2] before the swap.
        var dpb = new List<DecodedPicture> { Pic(2, 4), Pic(1, 2) };
        var (l0, l1) = H264FrameDecoder.BuildBSliceRefLists(dpb, currentPoc: 6, numActiveL0: 2, numActiveL1: 2);

        Assert.Equal(4, l0[0].PicOrderCnt);
        Assert.Equal(2, l0[1].PicOrderCnt);
        // L1 identical to L0 -> first two swapped.
        Assert.Equal(2, l1[0].PicOrderCnt);
        Assert.Equal(4, l1[1].PicOrderCnt);
    }

    [Fact]
    public void BuildBSliceRefLists_PastAndFuture_NoSwap()
    {
        // One past (poc2) and one future (poc8) around current poc6: L0 and L1 differ, no swap.
        var dpb = new List<DecodedPicture> { Pic(1, 2), Pic(2, 8) };
        var (l0, l1) = H264FrameDecoder.BuildBSliceRefLists(dpb, currentPoc: 6, numActiveL0: 2, numActiveL1: 2);

        Assert.Equal(2, l0[0].PicOrderCnt);  // past first for L0
        Assert.Equal(8, l0[1].PicOrderCnt);
        Assert.Equal(8, l1[0].PicOrderCnt);  // future first for L1
        Assert.Equal(2, l1[1].PicOrderCnt);
    }

    // --- MMCO 1 (§8.2.5.4.1): match on FrameNumWrap, so refs coded before a frame_num wrap resolve. ---

    [Fact]
    public void Mmco1_RemovesRefCodedBeforeFrameNumWrap()
    {
        // MaxFrameNum = 16. Current frame_num 1 (just wrapped past 15); a still-referenced pic has
        // frame_num 15 (FrameNumWrap = 15 - 16 = -1). MMCO 1 with diff+1 = 2 targets picNumX = 1-2 = -1.
        var sps = MakeSps(maxRefs: 8);
        var dpb = new List<DecodedPicture> { Pic(15, 30), Pic(0, 32) };
        int maxLt = -1;
        var mmco = new[] { new MmcoOperation(1, DifferenceOfPicNumsMinus1: 1, 0, 0, 0) };

        H264FrameDecoder.ApplyDecRefPicMarking(Pic(1, 34), PHeader(1, adaptive: true, mmco: mmco), dpb, sps, ref maxLt);

        Assert.DoesNotContain(dpb, p => p.FrameNum == 15); // the pre-wrap ref was found and removed
        Assert.Contains(dpb, p => p.FrameNum == 0);
    }
}
