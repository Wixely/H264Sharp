using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Cabac;
using H264Sharp.Decoder.Loop;
using H264Sharp.Decoder.Picture;
using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Decoder;

public sealed class H264FrameDecoder
{
    /// <summary>For testing/debugging: the per-MB parse state from the most recent decode.</summary>
    public Macroblock[]? LastMacroblocks { get; private set; }

    /// <summary>
    /// Decode the first I-frame from a byte stream. Auto-detects the framing:
    /// MP4 container, AVCC (length-prefixed), or Annex-B (start-code framed).
    /// </summary>
    public DecodedPicture DecodeFirstIFrame(ReadOnlySpan<byte> bytes) =>
        DecodeFirstIFrame(SplitToNalUnits(bytes));

    /// <summary>Decode all frames in the stream in decode order.</summary>
    public List<DecodedPicture> DecodeAllFrames(ReadOnlySpan<byte> bytes) =>
        DecodeAllFrames(SplitToNalUnits(bytes));

    private static List<NalUnit> SplitToNalUnits(ReadOnlySpan<byte> bytes)
    {
        if (LooksLikeMp4(bytes)) return Mp4Reader.ExtractH264NalUnits(bytes);
        if (LooksLikeAnnexB(bytes)) return AnnexBReader.SplitNalUnits(bytes);
        return AvccReader.SplitNalUnits(bytes);
    }

    /// <summary>Detects MP4: bytes 4..7 are a well-known top-level box type.</summary>
    private static bool LooksLikeMp4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        // First 4 bytes are a size. Bytes 4-7 are the type fourcc.
        ReadOnlySpan<byte> ty = bytes.Slice(4, 4);
        // Common top-level types: ftyp, moov, mdat, free, skip, wide
        return Match(ty, "ftyp") || Match(ty, "moov") || Match(ty, "mdat")
            || Match(ty, "free") || Match(ty, "skip") || Match(ty, "wide");

        static bool Match(ReadOnlySpan<byte> a, string b) =>
            a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
    }

    /// <summary>Detects Annex-B framing: a 3- or 4-byte start code at offset 0 followed by a
    /// plausible NAL header (forbidden_zero_bit == 0, nal_unit_type in 1..23). The header check
    /// disambiguates from AVCC, whose first bytes are a length prefix that can alias a start code
    /// (e.g. a first NAL of 256-511 bytes begins 00 00 01 xx). On mismatch the caller uses AVCC.</summary>
    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> bytes)
    {
        int scLen;
        if (bytes.Length >= 3 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1) scLen = 3;
        else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 1) scLen = 4;
        else return false;
        if (bytes.Length <= scLen) return false;
        byte nalHeader = bytes[scLen];
        if ((nalHeader & 0x80) != 0) return false; // forbidden_zero_bit must be 0
        int nalType = nalHeader & 0x1F;
        return nalType >= 1 && nalType <= 23;
    }

    public DecodedPicture DecodeFirstIFrame(List<NalUnit> nals) =>
        DecodeAllFrames(nals).First();

    /// <summary>Picture-scope state shared across all slices of a single coded frame
    /// (access unit). Built by <see cref="BeginPicture"/> on the slice whose
    /// first_mb_in_slice == 0 and reused by every continuation slice of the same
    /// frame. Per spec §7.4.1.2: an access unit (coded frame) can contain multiple
    /// slices; picture-level params (POC, ref lists) are constant within an AU.</summary>
    private sealed class PictureContext
    {
        public required DecodedPicture Picture { get; init; }
        public required Macroblock[] Mbs { get; init; }
        public required SliceHeader FirstSliceHeader { get; init; }
        public required List<DecodedPicture> RefPicListL0 { get; init; }
        public required List<DecodedPicture> RefPicListL1 { get; init; }
        public required int MbsPerRow { get; init; }
        public required int TotalMbs { get; init; }
        public required bool IsReference { get; init; }
        /// <summary>The SPS/PPS active for this picture, captured when it began. Finalization
        /// (deblocking) and marking must use these, not the decoder's current sps/pps variables,
        /// which a parameter set re-sent before the next AU may already have replaced.</summary>
        public required SequenceParameterSet Sps { get; init; }
        public required PictureParameterSet Pps { get; init; }
        /// <summary>True once a continuation slice (first_mb_in_slice > 0) has been added. Lets
        /// finalize distinguish single- from multi-slice pictures for disable_deblocking_filter_idc==2.</summary>
        public bool IsMultiSlice { get; set; }
    }

    public List<DecodedPicture> DecodeAllFrames(List<NalUnit> nals)
    {
        SequenceParameterSet? sps = null;
        PictureParameterSet? pps = null;
        // DPB: reference pictures, newest first (index 0 = most recently inserted).
        // Both short-term and long-term entries live here; long-term entries are pinned
        // (immune to sliding-window eviction) until cleared by MMCO ops.
        var dpb = new List<DecodedPicture>();
        var outputs = new List<DecodedPicture>();
        // POC type-0 running state (spec §8.2.1.1).
        int prevPicOrderCntMsb = 0;
        int prevPicOrderCntLsb = 0;
        // POC type-2 running state (spec §8.2.1.3): FrameNumOffset accumulates across frame_num wraps.
        int prevFrameNum = 0;
        int prevFrameNumOffset = 0;
        // Coded-video-sequence index: bumped at each IDR (and MMCO5). POC restarts per CVS,
        // so output ordering must group by this before PicOrderCnt.
        int cvsIndex = 0;
        // MaxLongTermFrameIdx (spec §8.2.5). -1 == "no long-term frame indices" (initial state
        // and after IDR with long_term_reference_flag=0). Raised by MMCO op 4 / IDR LT=1.
        int maxLongTermFrameIdx = -1;
        // Monotonic counter assigned to each decoded picture; used by callers to map
        // an MP4-sample-table index back into the POC-sorted output list.
        int decodeOrderCounter = 0;
        // The picture currently being built up. Multiple slices may write into it
        // before it is finalized; finalize on the next first_mb_in_slice==0 slice
        // or after the final NAL.
        PictureContext? currentPicture = null;

        foreach (var n in nals)
        {
            switch (n.NalUnitType)
            {
                case NalUnitType.Sps:
                    sps = SequenceParameterSet.Parse(n.Rbsp.Span);
                    break;
                case NalUnitType.Pps:
                    pps = PictureParameterSet.Parse(n.Rbsp.Span);
                    break;
                case NalUnitType.SliceIdr:
                case NalUnitType.SliceNonIdr:
                    if (sps is null || pps is null)
                    {
                        throw new InvalidDataException("slice encountered before SPS/PPS");
                    }
                    // Parse the full slice header. Cheap and re-used for both AU
                    // boundary detection (first_mb_in_slice) and the actual MB decode.
                    SliceHeader header = SliceHeader.Parse(n.Rbsp.Span, n, sps, pps);
                    // Redundant coded pictures (§7.4.3) duplicate primary slice data at coarser
                    // quality; decoding them would overwrite the correct primary output. Skip.
                    if (header.RedundantPicCnt > 0) break;
                    // Interlaced gate: MBAFF I-slice CAVLC with all-frame-coded MB pairs is
                    // supported (Stage 3a). PAFF (field_pic_flag=1, or PAFF frame pictures) and
                    // MBAFF for P/B slices / CABAC / field-coded pairs are not yet implemented
                    // — those are rejected with parameterized errors at the slice_data layer
                    // or here at dispatch.
                    if (!sps.FrameMbsOnlyFlag)
                    {
                        if (header.FieldPicFlag)
                            throw new NotSupportedException(
                                $"PAFF field picture (slice field_pic_flag=1, bottom_field_flag={header.BottomFieldFlag}) decode not yet supported");
                        if (!sps.MbAdaptiveFrameFieldFlag)
                            throw new NotSupportedException(
                                "PAFF frame picture (SPS frame_mbs_only_flag=0, mb_adaptive_frame_field_flag=0) decode not yet supported");
                        // MBAFF (mb_adaptive_frame_field_flag=1, !field_pic_flag): Stage 3a allows
                        // only I-slice + CAVLC; the slice_data loop additionally rejects any
                        // field-coded MB pair (mb_field_decoding_flag=1) it encounters.
                        if (header.SliceType != SliceType.I)
                            throw new NotSupportedException(
                                $"MBAFF {header.SliceType}-slice decode not yet supported (only I-slice in stage 3a)");
                        if (pps.EntropyCodingModeFlag)
                            throw new NotSupportedException(
                                "MBAFF CABAC decode not yet supported (only CAVLC in stage 3a)");
                    }
                    // constrained_intra_pred_flag changes intra-prediction neighbor availability
                    // in P/B slices (inter-coded neighbors become unavailable, §8.3.1.2.1). That
                    // rule is not implemented; decoding anyway would produce silently wrong pixels.
                    // I slices are unaffected (every MB is intra).
                    if (pps.ConstrainedIntraPredFlag && header.SliceType != SliceType.I)
                        throw new NotSupportedException(
                            "PPS constrained_intra_pred_flag=1 not supported for P/B slices");
                    // Access-unit boundary rule (spec §7.4.1.2, simplified): a slice
                    // with first_mb_in_slice == 0 starts a new coded picture; any
                    // other slice is a continuation of the current picture.
                    if (header.FirstMbInSlice == 0)
                    {
                        if (currentPicture is not null)
                        {
                            FinalizePicture(currentPicture, dpb,
                                ref maxLongTermFrameIdx, outputs, ref decodeOrderCounter);
                        }
                        if (n.NalUnitType == NalUnitType.SliceIdr)
                        {
                            // IDR clears the DPB (per spec §8.2.5.1) and resets POC state, and
                            // starts a new coded video sequence.
                            dpb.Clear();
                            prevPicOrderCntMsb = 0;
                            prevPicOrderCntLsb = 0;
                            prevFrameNum = 0;
                            prevFrameNumOffset = 0;
                            maxLongTermFrameIdx = -1;
                            cvsIndex++;
                        }
                        currentPicture = BeginPicture(n, header, sps, pps, dpb,
                            ref prevPicOrderCntMsb, ref prevPicOrderCntLsb,
                            ref prevFrameNum, ref prevFrameNumOffset);
                        currentPicture.Picture.CvsIndex = cvsIndex;
                    }
                    else
                    {
                        if (currentPicture is null)
                        {
                            throw new InvalidDataException(
                                "continuation slice (first_mb_in_slice > 0) without a current picture");
                        }
                        currentPicture.IsMultiSlice = true;
                    }
                    DecodeSliceMacroblocks(n, sps, pps, header, currentPicture);
                    break;
            }
        }

        // Finalize the trailing picture so its slices are emitted.
        if (currentPicture is not null)
        {
            FinalizePicture(currentPicture, dpb,
                ref maxLongTermFrameIdx, outputs, ref decodeOrderCounter);
        }

        if (outputs.Count == 0) throw new InvalidDataException("no slices in bitstream");
        // Display order: POC is only defined within a coded video sequence and restarts at each
        // IDR, so group by CvsIndex first (keeping whole GOPs contiguous), then PicOrderCnt, then
        // decode order to break POC ties deterministically. (Not the full §C.2.4 bumping process.)
        outputs.Sort((a, b) =>
        {
            int c = a.CvsIndex.CompareTo(b.CvsIndex);
            if (c != 0) return c;
            c = a.PicOrderCnt.CompareTo(b.PicOrderCnt);
            return c != 0 ? c : a.DecodeOrderIndex.CompareTo(b.DecodeOrderIndex);
        });
        return outputs;
    }

    /// <summary>Allocate the picture, compute its POC, and build the reference picture lists.
    /// Called once per access unit (coded frame) — on the slice with first_mb_in_slice == 0.
    /// Continuation slices reuse the returned <see cref="PictureContext"/>.</summary>
    private static PictureContext BeginPicture(
        NalUnit nal, SliceHeader header,
        SequenceParameterSet sps, PictureParameterSet pps,
        List<DecodedPicture> dpb,
        ref int prevPicOrderCntMsb, ref int prevPicOrderCntLsb,
        ref int prevFrameNum, ref int prevFrameNumOffset)
    {
        bool isPSlice = header.SliceType == SliceType.P;
        bool isBSlice = header.SliceType == SliceType.B;
        if (isPSlice && dpb.Count == 0)
        {
            throw new InvalidDataException("P-slice with empty DPB");
        }
        if (isBSlice && dpb.Count == 0)
        {
            throw new InvalidDataException("B-slice with empty DPB");
        }

        // Compute POC (spec §8.2.1) for this picture. Only the first slice in an AU
        // computes POC; continuation slices inherit it from the picture context.
        int picOrderCnt = ComputePicOrderCnt(header, sps,
            ref prevPicOrderCntMsb, ref prevPicOrderCntLsb,
            ref prevFrameNum, ref prevFrameNumOffset, nal.NalRefIdc != 0);

        // Update PicNum / LongTermPicNum on each DPB entry relative to the current frame_num
        // (spec §8.2.4.1).
        int maxFrameNum = 1 << ((int)sps.Log2MaxFrameNumMinus4 + 4);
        int curFrameNum = (int)header.FrameNum;
        foreach (var refPic in dpb)
        {
            if (!refPic.IsLongTerm)
            {
                int fnw = refPic.FrameNum;
                if (fnw > curFrameNum) fnw -= maxFrameNum;
                refPic.LongTermPicNum = fnw; // overload: cache short-term PicNum here for list mod
            }
            else
            {
                refPic.LongTermPicNum = refPic.LongTermFrameIdx;
            }
        }

        // Build active reference picture lists per spec §8.2.4.
        int numActiveL0 = (int)(header.NumRefIdxL0ActiveMinus1 + 1);
        int numActiveL1 = (int)(header.NumRefIdxL1ActiveMinus1 + 1);
        List<DecodedPicture> refPicListL0;
        List<DecodedPicture> refPicListL1 = new();
        if (isBSlice)
        {
            (refPicListL0, refPicListL1) = BuildBSliceRefLists(dpb, picOrderCnt, numActiveL0, numActiveL1);
        }
        else if (isPSlice)
        {
            refPicListL0 = BuildPSliceRefListL0(dpb, numActiveL0);
        }
        else
        {
            refPicListL0 = new List<DecodedPicture>();
        }
        // Apply ref_pic_list_modification (spec §8.2.4.3).
        if (isPSlice || isBSlice)
        {
            ApplyRefPicListModification(refPicListL0, header.RefPicListModificationL0,
                dpb, curFrameNum, maxFrameNum, numActiveL0);
        }
        if (isBSlice)
        {
            ApplyRefPicListModification(refPicListL1, header.RefPicListModificationL1,
                dpb, curFrameNum, maxFrameNum, numActiveL1);
        }
        int croppedWidth = (int)sps.CroppedWidth;
        int croppedHeight = (int)sps.CroppedHeight;
        int bufferWidth = (int)sps.PicWidthInSamplesL;
        int bufferHeight = (int)sps.PicHeightInSamplesL;
        int cropLeft = (int)(sps.FrameCroppingFlag ? sps.SubWidthC * sps.FrameCropLeftOffset : 0);
        int cropTop = (int)(sps.FrameCroppingFlag
            ? sps.SubHeightC * (sps.FrameMbsOnlyFlag ? 1u : 2u) * sps.FrameCropTopOffset
            : 0);
        int mbsPerRow = (int)sps.PicWidthInMbs;
        int totalMbs = mbsPerRow * (int)sps.PicHeightInMbs;
        var picture = new DecodedPicture(croppedWidth, croppedHeight, bufferWidth, bufferHeight, cropLeft, cropTop)
        {
            FrameNum = (int)header.FrameNum,
            PicOrderCnt = picOrderCnt,
            MbsPerRow = mbsPerRow,
            Vui = sps.Vui,
            // Record ref-list POCs so this picture, when later used as a temporal-direct colocated
            // reference, can resolve refIdxCol -> referenced POC (§8.4.1.2.3).
            RefListL0Pocs = refPicListL0.Count > 0 ? refPicListL0.Select(p => p.PicOrderCnt).ToArray() : null,
            RefListL1Pocs = refPicListL1.Count > 0 ? refPicListL1.Select(p => p.PicOrderCnt).ToArray() : null,
        };
        return new PictureContext
        {
            Picture = picture,
            Mbs = new Macroblock[totalMbs],
            FirstSliceHeader = header,
            RefPicListL0 = refPicListL0,
            RefPicListL1 = refPicListL1,
            MbsPerRow = mbsPerRow,
            TotalMbs = totalMbs,
            IsReference = nal.NalRefIdc != 0,
            Sps = sps,
            Pps = pps,
        };
    }

    /// <summary>Finalize a picture once all its slices have been decoded: apply the
    /// in-loop deblocking filter across the full MB grid (spec §8.7 — runs once per
    /// picture, not per slice), assign decode-order index, output the picture, and
    /// push it into the DPB if it is a reference. Deblocking parameters come from
    /// the first slice's header — multi-slice frames in our subset share these.</summary>
    private void FinalizePicture(
        PictureContext ctx,
        List<DecodedPicture> dpb,
        ref int maxLongTermFrameIdx,
        List<DecodedPicture> outputs, ref int decodeOrderCounter)
    {
        // Use the parameter sets captured when this picture began, not the decoder's current
        // sps/pps — a set re-sent before the next access unit may already have replaced them.
        SequenceParameterSet sps = ctx.Sps;
        PictureParameterSet pps = ctx.Pps;
        SliceHeader header = ctx.FirstSliceHeader;
        if (header.DisableDeblockingFilterIdc != 1)
        {
            // idc==2 suppresses filtering only across SLICE boundaries (spec §8.7); internal
            // MB edges within one slice must still be filtered. For a single-slice picture that
            // means it behaves exactly like idc==0. We don't track per-MB slice ids, so a
            // multi-slice picture with idc==2 conservatively skips MB edges (documented limit).
            bool filterMbEdges = header.DisableDeblockingFilterIdc != 2 || !ctx.IsMultiSlice;
            bool mbaff = !sps.FrameMbsOnlyFlag && sps.MbAdaptiveFrameFieldFlag && !header.FieldPicFlag;
            DeblockingFilter.Apply(ctx.Picture, ctx.Mbs, ctx.MbsPerRow,
                pps.ChromaQpIndexOffset,
                header.SliceAlphaC0OffsetDiv2 * 2,
                header.SliceBetaOffsetDiv2 * 2,
                filterMbEdges,
                mbaff);
        }
        LastMacroblocks = ctx.Mbs;
        ctx.Picture.Macroblocks = ctx.Mbs;
        ctx.Picture.DecodeOrderIndex = decodeOrderCounter++;
        outputs.Add(ctx.Picture);
        if (ctx.IsReference)
        {
            ApplyDecRefPicMarking(ctx.Picture, header, dpb, sps, ref maxLongTermFrameIdx);
        }
    }

    /// <summary>Decode the MBs carried by a single slice into the picture being built.
    /// The slice's own header drives entropy mode, QP, per-MB parsing path (I/P/B,
    /// CAVLC/CABAC), and end-of-slice termination. MBs are written into the picture's
    /// shared <c>mbs[]</c> starting at <c>first_mb_in_slice</c>.</summary>
    private void DecodeSliceMacroblocks(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps,
        SliceHeader header, PictureContext ctx)
    {
        bool isPSlice = header.SliceType == SliceType.P;
        bool isBSlice = header.SliceType == SliceType.B;

        var reader = new BitReader(nal.Rbsp.Span);
        SkipSliceHeader(ref reader, nal, sps, pps);

        DecodedPicture picture = ctx.Picture;
        Macroblock[] mbs = ctx.Mbs;
        List<DecodedPicture> refPicListL0 = ctx.RefPicListL0;
        List<DecodedPicture> refPicListL1 = ctx.RefPicListL1;
        int mbsPerRow = ctx.MbsPerRow;
        int totalMbs = ctx.TotalMbs;
        // QPY is initialized per spec §7.4.5.1 from the slice's own slice_qp_delta;
        // mb_qp_delta deltas chain only within a slice, not across slices.
        int qpY = header.SliceQpY(pps);
        bool implicitBipred = isBSlice && pps.WeightedBipredIdc == 2;
        bool explicitBipred = isBSlice && pps.WeightedBipredIdc == 1;
        // Temporal direct mode context — built once per B-slice. Only valid for direct_spatial=0.
        TemporalDirectContext? tdCtx = null;
        if (isBSlice && !header.DirectSpatialMvPredFlag && refPicListL1.Count > 0)
        {
            int[] l0Pocs = new int[refPicListL0.Count];
            bool[] l0Lt = new bool[refPicListL0.Count];
            for (int i = 0; i < refPicListL0.Count; i++)
            {
                l0Pocs[i] = refPicListL0[i].PicOrderCnt;
                l0Lt[i] = refPicListL0[i].IsLongTerm;
            }
            tdCtx = new TemporalDirectContext
            {
                CurrentPoc = picture.PicOrderCnt,
                Pic1Poc = refPicListL1[0].PicOrderCnt,
                L0Pocs = l0Pocs,
                L0IsLongTerm = l0Lt,
                // The colocated picture's own ref-list POCs, for refIdxCol -> POC resolution.
                ColRefL0Pocs = refPicListL1[0].RefListL0Pocs,
                ColRefL1Pocs = refPicListL1[0].RefListL1Pocs,
            };
        }

        int addr = (int)header.FirstMbInSlice;
        // Spec §7.4.1.5: MbaffFrameFlag = (mb_adaptive_frame_field_flag && !field_pic_flag).
        // Drives MB-pair iteration order and per-pair mb_field_decoding_flag parsing.
        bool mbaffFrameFlag = !sps.FrameMbsOnlyFlag && sps.MbAdaptiveFrameFieldFlag && !header.FieldPicFlag;

        // ---- Branch on entropy coding mode ----
        if (pps.EntropyCodingModeFlag)
        {
            DecodeSliceCabac(nal, sps, pps, header, ref reader, mbs, picture, refPicListL0, refPicListL1,
                mbsPerRow, totalMbs, ref qpY, addr, implicitBipred, explicitBipred, tdCtx);
            return;
        }

        // CAVLC slice_data loop (spec §7.3.4). End-of-slice is signalled by
        // more_rbsp_data() returning false — only the rbsp_trailing_bits remain —
        // NOT by reaching totalMbs. That distinction matters for multi-slice frames
        // where one slice covers only a subset of the picture's macroblocks. For
        // P/B slices, an mb_skip_run is read before each potential coded MB; the
        // optional coded MB is parsed only when more_rbsp_data() is still true.
        int firstMbInSlice = (int)header.FirstMbInSlice;
        while (addr < totalMbs)
        {
            int mbSkipRun = 0;
            if (isPSlice || isBSlice)
            {
                mbSkipRun = (int)ExpGolomb.ReadUe(ref reader);
            }
            // Process the skip-run portion of this iteration.
            for (int s = 0; s < mbSkipRun && addr < totalMbs; s++)
            {
                int mbX = addr % mbsPerRow;
                int mbY = addr / mbsPerRow;
                Macroblock? leftMb = GetNeighborInSlice(mbs, mbX > 0 ? addr - 1 : -1, firstMbInSlice);
                Macroblock? topMb = GetNeighborInSlice(mbs, mbY > 0 ? addr - mbsPerRow : -1, firstMbInSlice);
                Macroblock? topRightMb = GetNeighborInSlice(mbs,
                    (mbY > 0 && mbX + 1 < mbsPerRow) ? addr - mbsPerRow + 1 : -1, firstMbInSlice);
                Macroblock? topLeftMb = GetNeighborInSlice(mbs,
                    (mbY > 0 && mbX > 0) ? addr - mbsPerRow - 1 : -1, firstMbInSlice);
                if (isPSlice)
                {
                    // P_Skip: derive MV per spec §8.4.1.1 from neighbors, then treat as
                    // P_L0_16x16 with refIdx=0 + that MV + zero residual.
                    (int skipMvX, int skipMvY) = MacroblockParser.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
                    Macroblock skipMb = SkipPlaceholder(addr, skipMvX, skipMvY, qpY);
                    mbs[addr] = skipMb;
                    MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                        pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, null, header.PredWeights);
                }
                else
                {
                    Macroblock? colMb = GetColocatedMb(refPicListL1, addr);
                    Macroblock skipMb = BSkipPlaceholder(addr, header, leftMb, topMb, topRightMb, topLeftMb, colMb, tdCtx, qpY, sps.Direct8x8InferenceFlag);
                    mbs[addr] = skipMb;
                    MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                        pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                        implicitBipred, explicitBipred);
                }
                addr++;
            }
            if (addr >= totalMbs) break;
            // Spec §7.3.4: a coded macroblock_layer() follows the skip-run only when
            // more_rbsp_data() is true. False here means the slice ended right after the
            // skip-run (common at slice tail).
            if ((isPSlice || isBSlice) && !reader.MoreRbspData()) break;

            // Parse one coded MB.
            {
                // MBAFF: parse mb_field_decoding_flag before the top MB of every pair (and
                // before the bottom MB if the top was skipped; for I-slice no skips apply).
                if (mbaffFrameFlag && (addr & 1) == 0)
                {
                    int mbFieldDecodingFlag = (int)reader.ReadBit();
                    if (mbFieldDecodingFlag != 0)
                    {
                        int pairIdx = addr >> 1;
                        throw new NotSupportedException(
                            $"MBAFF field-coded MB pair (mb_field_decoding_flag=1 at pair index {pairIdx}) decode not yet supported");
                    }
                }
                (int mbX, int mbY) = MbAddrToCoords(addr, mbsPerRow, mbaffFrameFlag);
                Macroblock? leftMb = GetNeighborInSlice(mbs,
                    mbX > 0 ? MbAddrFromCoords(mbX - 1, mbY, mbsPerRow, mbaffFrameFlag) : -1, firstMbInSlice);
                Macroblock? topMb = GetNeighborInSlice(mbs,
                    mbY > 0 ? MbAddrFromCoords(mbX, mbY - 1, mbsPerRow, mbaffFrameFlag) : -1, firstMbInSlice);
                Macroblock? topRightMb = GetNeighborInSlice(mbs,
                    (mbY > 0 && mbX + 1 < mbsPerRow) ? MbAddrFromCoords(mbX + 1, mbY - 1, mbsPerRow, mbaffFrameFlag) : -1, firstMbInSlice);
                Macroblock? topLeftMb = GetNeighborInSlice(mbs,
                    (mbY > 0 && mbX > 0) ? MbAddrFromCoords(mbX - 1, mbY - 1, mbsPerRow, mbaffFrameFlag) : -1, firstMbInSlice);
                Macroblock? colMbInter = isBSlice ? GetColocatedMb(refPicListL1, addr) : null;
                Macroblock mb = MacroblockParser.Parse(
                    ref reader, sps, pps, header,
                    leftMb, topMb, topRightMb, topLeftMb, addr, ref qpY, colMbInter, tdCtx);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                    implicitBipred, explicitBipred);
                addr++;
            }
            if (addr >= totalMbs) break;
            // I-slice end-of-slice check (no mb_skip_run on next iter): if no more
            // rbsp_data, the slice ends here. P/B slices re-check at top via the next
            // mb_skip_run + more_rbsp_data check, so this guard suffices for I as well.
            if (!isPSlice && !isBSlice && !reader.MoreRbspData()) break;
        }
    }

    /// <summary>Return the neighbor macroblock at <paramref name="addr"/>, or null when the
    /// neighbor lies in a different slice (spec §6.4.11.1 — neighbouring MB N is unavailable
    /// when N does not belong to the same slice as the current MB). Slices are contiguous in
    /// raster MB order in our subset, so "different slice" means <c>addr &lt; firstMbInSlice</c>.</summary>
    private static Macroblock? GetNeighborInSlice(Macroblock[] mbs, int addr, int firstMbInSlice)
    {
        if (addr < 0 || addr < firstMbInSlice) return null;
        return mbs[addr];
    }

    /// <summary>Map a CurrMbAddr to its (mbX, mbY) spatial coordinates. For MBAFF, MBs are
    /// decoded in pair raster order (pair=top+bottom stacked vertically), so address-to-coords
    /// differs from the simple non-MBAFF mapping.</summary>
    private static (int mbX, int mbY) MbAddrToCoords(int addr, int mbsPerRow, bool mbaff)
    {
        if (!mbaff) return (addr % mbsPerRow, addr / mbsPerRow);
        int pairIdx = addr >> 1;
        int inPair = addr & 1;
        return (pairIdx % mbsPerRow, (pairIdx / mbsPerRow) * 2 + inPair);
    }

    /// <summary>Inverse of <see cref="MbAddrToCoords"/>: spatial (mbX, mbY) → CurrMbAddr.
    /// Used to look up neighbor MBs that were decoded earlier in the slice.</summary>
    private static int MbAddrFromCoords(int mbX, int mbY, int mbsPerRow, bool mbaff)
    {
        if (!mbaff) return mbY * mbsPerRow + mbX;
        int pairIdx = (mbY >> 1) * mbsPerRow + mbX;
        int inPair = mbY & 1;
        return pairIdx * 2 + inPair;
    }

    /// <summary>Placeholder Macroblock for a P_Skip — treated as PredL0 with refIdx=0 and MV derived per §8.4.1.1.
    /// QpY inherits the running QP (skip MBs have implicit mb_qp_delta == 0; this matters for deblocking
    /// since adjacent edges average the two MBs' QpYs to look up alpha/beta).</summary>
    private static Macroblock SkipPlaceholder(int addr, int mvX, int mvY, int qpY)
    {
        var mb = new Macroblock
        {
            MbAddress = addr,
            Type = new IntraMbType(0, MbPartPredMode.PredL0, default, 0, 0),
            IsSkipped = true,
            RefIdxL0 = 0,
            MvL0X = mvX,
            MvL0Y = mvY,
            QpY = qpY,
        };
        for (int i = 0; i < 16; i++) { mb.MvL0XBlock[i] = mvX; mb.MvL0YBlock[i] = mvY; }
        // RefIdxL08x8 left as zeros (correct for P_Skip).
        mb.InterPartitions.Add(new MvPartition(0, 0, 16, 16, 0, mvX, mvY));
        return mb;
    }

    /// <summary>Placeholder Macroblock for a B_Skip — uses B_Direct_16x16 spatial direct derivation
    /// to fill MVs; no residual. QpY inherits the running QP (see SkipPlaceholder).</summary>
    private static Macroblock BSkipPlaceholder(int addr, SliceHeader header,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb, TemporalDirectContext? tdCtx, int qpY, bool direct8x8Inference)
    {
        var mb = new Macroblock
        {
            MbAddress = addr,
            Type = new IntraMbType(0, MbPartPredMode.PredL0, default, 0, 0),
            IsSkipped = true,
            IsBSkip = true,
            IsBInter = true,
            QpY = qpY,
        };
        BDirectMode.ApplyDirect16x16(mb, header, leftMb, topMb, topRightMb, topLeftMb, colocatedMb, tdCtx, direct8x8Inference);
        return mb;
    }

    /// <summary>CABAC slice_data() loop (spec §7.3.4). Currently handles all-P_Skip P-slices.</summary>
    private static void DecodeSliceCabac(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps, SliceHeader header,
        ref BitReader reader, Macroblock[] mbs, DecodedPicture picture,
        List<DecodedPicture> refPicListL0, List<DecodedPicture> refPicListL1,
        int mbsPerRow, int totalMbs, ref int qpY, int addr, bool implicitBipred,
        bool explicitBipred, TemporalDirectContext? tdCtx)
    {
        bool isPSlice = header.SliceType == SliceType.P;
        bool isBSlice = header.SliceType == SliceType.B;

        // CABAC alignment: consume one-bits up to byte boundary (spec §7.3.4 — cabac_alignment_one_bit).
        while ((reader.BitPosition & 7) != 0) reader.ReadBit();

        // Initialize contexts. Per spec §9.3.1.1 each slice re-initializes the CABAC
        // engine from scratch — even within a multi-slice picture, neither contexts
        // nor codIRange/codIOffset carry across slices.
        var contexts = new CabacContexts(CabacInitTable.ContextCount);
        int model = header.SliceType == SliceType.I ? 0 : 1 + (int)header.CabacInitIdc;
        int sliceQp = header.SliceQpY(pps);
        for (int ctxIdx = 0; ctxIdx < CabacInitTable.ContextCount; ctxIdx++)
        {
            sbyte m = CabacInitTable.MN[ctxIdx, model, 0];
            sbyte n = CabacInitTable.MN[ctxIdx, model, 1];
            if (m == CabacInitTable.CtxNA) continue;
            contexts.Initialize(ctxIdx, m, n, sliceQp);
        }

        // Build the CABAC decoder, taking ownership of the rest of the RBSP.
        byte[] rbspBytes = nal.Rbsp.ToArray();
        var cabac = new CabacDecoder(rbspBytes, reader.BitPosition, contexts);

        if (CabacTrace.Enabled)
        {
            CabacTrace.Mark($"SLICE type={header.SliceType} frame_num={header.FrameNum} qp={sliceQp} firstMb={addr} totalMbs={totalMbs}");
        }

        int prevMbQpDeltaState = 0; // CABAC state for mb_qp_delta binIdx 0 ctxIdxInc
        int firstMbInSlice = addr;

        while (addr < totalMbs)
        {
            int mbX = addr % mbsPerRow;
            int mbY = addr / mbsPerRow;
            if (CabacTrace.Enabled) CabacTrace.Mark($"MB {addr} ({mbX},{mbY}) bins-so-far={CabacTrace.BinCount}");
            // Spec §6.4.11.1: neighbouring MBs that lie in an earlier slice are unavailable.
            Macroblock? leftMb = GetNeighborInSlice(mbs, mbX > 0 ? addr - 1 : -1, firstMbInSlice);
            Macroblock? topMb = GetNeighborInSlice(mbs, mbY > 0 ? addr - mbsPerRow : -1, firstMbInSlice);
            Macroblock? topRightMb = GetNeighborInSlice(mbs,
                (mbY > 0 && mbX + 1 < mbsPerRow) ? addr - mbsPerRow + 1 : -1, firstMbInSlice);
            Macroblock? topLeftMb = GetNeighborInSlice(mbs,
                (mbY > 0 && mbX > 0) ? addr - mbsPerRow - 1 : -1, firstMbInSlice);

            int mbSkipFlag = 0;
            if (isPSlice || isBSlice)
            {
                // ctxIdxInc for mb_skip_flag (spec table 9-39).
                int condA = (leftMb != null && !leftMb.IsSkipped) ? 1 : 0;
                int condB = (topMb != null && !topMb.IsSkipped) ? 1 : 0;
                int ctxBase = isBSlice ? 24 : 11;
                mbSkipFlag = cabac.DecodeBin(ctxBase + condA + condB);
            }

            if (mbSkipFlag == 1)
            {
                // Skipped MBs carry no mb_qp_delta — per FFmpeg h264_cabac.c:1952, the
                // "previous mb_qp_delta non-zero" CABAC state must be reset to 0.
                prevMbQpDeltaState = 0;
                if (isBSlice)
                {
                    Macroblock? colMbSkip = GetColocatedMb(refPicListL1, addr);
                    Macroblock skipMb = BSkipPlaceholder(addr, header, leftMb, topMb, topRightMb, topLeftMb, colMbSkip, tdCtx, qpY, sps.Direct8x8InferenceFlag);
                    mbs[addr] = skipMb;
                    MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                        pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                        implicitBipred, explicitBipred);
                }
                else
                {
                    (int skipMvX, int skipMvY) = MacroblockParser.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
                    Macroblock skipMb = SkipPlaceholder(addr, skipMvX, skipMvY, qpY);
                    mbs[addr] = skipMb;
                    MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                        pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, null, header.PredWeights);
                }
            }
            else if (!isPSlice && !isBSlice)
            {
                // I-slice macroblock (no mb_skip_flag exists for I-slices).
                Macroblock mb = CabacSliceI.ParseMb(cabac, leftMb, topMb, addr,
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);
            }
            else if (isBSlice)
            {
                // B-slice non-skip MB.
                Macroblock? colMbB = GetColocatedMb(refPicListL1, addr);
                Macroblock mb = CabacSliceB.ParseMb(cabac, header,
                    leftMb, topMb, topRightMb, topLeftMb, addr,
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag, colMbB, tdCtx,
                    sps.Direct8x8InferenceFlag);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                    implicitBipred, explicitBipred);
            }
            else
            {
                // P-slice non-skip MB.
                Macroblock mb = CabacSliceP.ParseMb(cabac, header,
                    leftMb, topMb, topRightMb, topLeftMb, addr,
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, null, header.PredWeights);
            }

            addr++;
            // Spec §7.3.4: end_of_slice_flag is decoded AFTER every macroblock, including
            // the last one of the slice. We still gate "continue to next MB" on having
            // capacity; reading the terminate even at end-of-stream keeps bin-level traces
            // aligned with OpenH264/FFmpeg.
            int endOfSlice = cabac.DecodeTerminate();
            if (endOfSlice == 1 || addr >= totalMbs) break;
        }
    }

    /// <summary>Advance the bit reader past the slice header (mirrors SliceHeader.Parse).</summary>
    private static void SkipSliceHeader(
        ref BitReader r, NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps)
    {
        bool idrPicFlag = nal.NalUnitType == NalUnitType.SliceIdr;
        _ = ExpGolomb.ReadUe(ref r);                              // first_mb_in_slice
        uint sliceTypeRaw = ExpGolomb.ReadUe(ref r);
        var sliceType = (SliceType)(sliceTypeRaw % 5);
        _ = ExpGolomb.ReadUe(ref r);                              // pic_parameter_set_id
        _ = r.ReadBits((int)sps.Log2MaxFrameNumMinus4 + 4);       // frame_num
        // field_pic_flag / bottom_field_flag — only when frame_mbs_only_flag == 0.
        bool fieldPicFlag = false;
        if (!sps.FrameMbsOnlyFlag)
        {
            fieldPicFlag = r.ReadBit() == 1;
            if (fieldPicFlag) _ = r.ReadBit();                    // bottom_field_flag
        }
        if (idrPicFlag) _ = ExpGolomb.ReadUe(ref r);
        if (sps.PicOrderCntType == 0)
        {
            _ = r.ReadBits((int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag && !fieldPicFlag) _ = ExpGolomb.ReadSe(ref r);
        }
        if (pps.RedundantPicCntPresentFlag) _ = ExpGolomb.ReadUe(ref r);
        uint effectiveNumRefL0 = pps.NumRefIdxL0DefaultActiveMinus1;
        uint effectiveNumRefL1 = pps.NumRefIdxL1DefaultActiveMinus1;
        if (sliceType == SliceType.B)
        {
            _ = r.ReadBit(); // direct_spatial_mv_pred_flag
        }
        if (sliceType == SliceType.P || sliceType == SliceType.SP || sliceType == SliceType.B)
        {
            bool overrideFlag = r.ReadBit() == 1;
            if (overrideFlag)
            {
                effectiveNumRefL0 = ExpGolomb.ReadUe(ref r);
                if (sliceType == SliceType.B) effectiveNumRefL1 = ExpGolomb.ReadUe(ref r);
            }
            bool listModL0 = r.ReadBit() == 1;
            if (listModL0)
            {
                while (true)
                {
                    uint op = ExpGolomb.ReadUe(ref r);
                    if (op == 3) break;
                    _ = ExpGolomb.ReadUe(ref r);
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
        // pred_weight_table (must precede dec_ref_pic_marking).
        bool wForP = pps.WeightedPredFlag && (sliceType == SliceType.P || sliceType == SliceType.SP);
        bool wForB = pps.WeightedBipredIdc == 1 && sliceType == SliceType.B;
        if (wForP || wForB)
        {
            SkipPredWeightTable(ref r, effectiveNumRefL0);
            if (wForB) SkipPredWeightTable(ref r, effectiveNumRefL1);
        }

        if (nal.NalRefIdc != 0)
        {
            if (idrPicFlag) { _ = r.ReadBit(); _ = r.ReadBit(); }
            else
            {
                bool adaptive = r.ReadBit() == 1;
                if (adaptive)
                {
                    // Walk the MMCO loop with full per-op payload widths (spec §7.3.3.3 / Table 7-9).
                    while (true)
                    {
                        uint mmco = ExpGolomb.ReadUe(ref r);
                        if (mmco == 0) break;
                        if (mmco == 1 || mmco == 3) _ = ExpGolomb.ReadUe(ref r);
                        if (mmco == 2) _ = ExpGolomb.ReadUe(ref r);
                        if (mmco == 3 || mmco == 6) _ = ExpGolomb.ReadUe(ref r);
                        if (mmco == 4) _ = ExpGolomb.ReadUe(ref r);
                        if (mmco > 6)
                            throw new InvalidDataException(
                                $"memory_management_control_operation {mmco} out of range");
                    }
                }
            }
        }
        if (pps.EntropyCodingModeFlag && sliceType != SliceType.I)
        {
            _ = ExpGolomb.ReadUe(ref r);                          // cabac_init_idc
        }
        _ = ExpGolomb.ReadSe(ref r);                              // slice_qp_delta
        if (pps.DeblockingFilterControlPresentFlag)
        {
            uint idc = ExpGolomb.ReadUe(ref r);
            if (idc != 1) { _ = ExpGolomb.ReadSe(ref r); _ = ExpGolomb.ReadSe(ref r); }
        }
    }

    /// <summary>Picture Order Count derivation (spec §8.2.1). Frame-coded subset: returns
    /// PicOrderCnt = min(TopFieldOrderCnt, BottomFieldOrderCnt). Updates prev* state for
    /// reference pictures (NalRefIdc != 0).</summary>
    private static int ComputePicOrderCnt(
        SliceHeader header, SequenceParameterSet sps,
        ref int prevPicOrderCntMsb, ref int prevPicOrderCntLsb,
        ref int prevFrameNum, ref int prevFrameNumOffset, bool isReference)
    {
        if (header.IdrPicFlag)
        {
            prevPicOrderCntMsb = 0;
            prevPicOrderCntLsb = 0;
            prevFrameNum = 0;
            prevFrameNumOffset = 0;
            return 0;
        }

        if (sps.PicOrderCntType == 0)
        {
            int maxPicOrderCntLsb = 1 << ((int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            int picOrderCntLsb = (int)header.PicOrderCntLsb;
            int picOrderCntMsb;
            if (picOrderCntLsb < prevPicOrderCntLsb &&
                (prevPicOrderCntLsb - picOrderCntLsb) >= (maxPicOrderCntLsb / 2))
            {
                picOrderCntMsb = prevPicOrderCntMsb + maxPicOrderCntLsb;
            }
            else if (picOrderCntLsb > prevPicOrderCntLsb &&
                     (picOrderCntLsb - prevPicOrderCntLsb) > (maxPicOrderCntLsb / 2))
            {
                picOrderCntMsb = prevPicOrderCntMsb - maxPicOrderCntLsb;
            }
            else
            {
                picOrderCntMsb = prevPicOrderCntMsb;
            }
            int topFieldOrderCnt = picOrderCntMsb + picOrderCntLsb;
            int bottomFieldOrderCnt = topFieldOrderCnt + header.DeltaPicOrderCntBottom;
            int picOrderCnt = Math.Min(topFieldOrderCnt, bottomFieldOrderCnt);
            if (isReference)
            {
                prevPicOrderCntMsb = picOrderCntMsb;
                prevPicOrderCntLsb = picOrderCntLsb;
            }
            return picOrderCnt;
        }

        // pic_order_cnt_type == 2 (spec §8.2.1.3): decode order == display order. FrameNumOffset
        // accumulates MaxFrameNum on each frame_num wrap so POC keeps increasing past the wrap;
        // non-reference pictures subtract 1 so they sort before the same-frame_num ref picture.
        int maxFrameNum = 1 << ((int)sps.Log2MaxFrameNumMinus4 + 4);
        int frameNum = (int)header.FrameNum;
        int frameNumOffset = prevFrameNum > frameNum ? prevFrameNumOffset + maxFrameNum : prevFrameNumOffset;
        int tempPoc = 2 * (frameNumOffset + frameNum) - (isReference ? 0 : 1);
        prevFrameNum = frameNum;
        prevFrameNumOffset = frameNumOffset;
        return tempPoc;
    }

    /// <summary>Walk past a pred_weight_table to keep subsequent slice-header fields aligned
    /// without storing the values (used by SkipSliceHeader's discarded pass).</summary>
    private static void SkipPredWeightTable(ref Bitstream.BitReader r, uint numRefIdxActiveMinus1)
    {
        _ = ExpGolomb.ReadUe(ref r); // luma_log2_weight_denom
        _ = ExpGolomb.ReadUe(ref r); // chroma_log2_weight_denom (4:2:0)
        for (uint i = 0; i <= numRefIdxActiveMinus1; i++)
        {
            bool lumaFlag = r.ReadBit() == 1;
            if (lumaFlag) { _ = ExpGolomb.ReadSe(ref r); _ = ExpGolomb.ReadSe(ref r); }
            bool chromaFlag = r.ReadBit() == 1;
            if (chromaFlag)
            {
                _ = ExpGolomb.ReadSe(ref r); _ = ExpGolomb.ReadSe(ref r);
                _ = ExpGolomb.ReadSe(ref r); _ = ExpGolomb.ReadSe(ref r);
            }
        }
    }

    /// <summary>Returns the colocated MB (same mbAddress) in refPicListL1[0], or null if L1[0]
    /// has no retained MB state (e.g. picture decoded before MB plumbing landed).</summary>
    private static Macroblock? GetColocatedMb(List<DecodedPicture> refPicListL1, int mbAddress)
    {
        if (refPicListL1.Count == 0) return null;
        var p = refPicListL1[0];
        if (p.Macroblocks is null) return null;
        if ((uint)mbAddress >= (uint)p.Macroblocks.Length) return null;
        return p.Macroblocks[mbAddress];
    }

    /// <summary>B-slice reference list construction per spec §8.2.4.2.3. Short-term refs are
    /// ordered by POC relative to the current frame; long-term refs are appended (ordered by
    /// LongTermPicNum ascending) and survive across the standard truncation step.</summary>
    internal static (List<DecodedPicture> l0, List<DecodedPicture> l1) BuildBSliceRefLists(
        List<DecodedPicture> dpb, int currentPoc, int numActiveL0, int numActiveL1)
    {
        var shortTerm = dpb.Where(p => !p.IsLongTerm).ToList();
        var longTerm = dpb.Where(p => p.IsLongTerm).OrderBy(p => p.LongTermPicNum).ToList();
        // L0: past (POC < current, descending) + future (POC > current, ascending) + long-term.
        var past = shortTerm.Where(p => p.PicOrderCnt < currentPoc).OrderByDescending(p => p.PicOrderCnt);
        var future = shortTerm.Where(p => p.PicOrderCnt > currentPoc).OrderBy(p => p.PicOrderCnt);
        var l0 = past.Concat(future).Concat(longTerm).ToList();
        // L1: future (ascending) + past (descending) + long-term.
        var fut1 = shortTerm.Where(p => p.PicOrderCnt > currentPoc).OrderBy(p => p.PicOrderCnt);
        var past1 = shortTerm.Where(p => p.PicOrderCnt < currentPoc).OrderByDescending(p => p.PicOrderCnt);
        var l1 = fut1.Concat(past1).Concat(longTerm).ToList();
        // §8.2.4.2.3: when L1 has >1 entry and is identical to L0 (e.g. no future refs — low-delay
        // B, post-scene-cut, or LT-only), swap L1[0] and L1[1]. Must precede truncation so it
        // survives numActiveL1 == 1, and it fixes the colocated picture (L1[0]) for B_Direct.
        if (l1.Count > 1 && l0.SequenceEqual(l1))
        {
            (l1[0], l1[1]) = (l1[1], l1[0]);
        }
        if (l0.Count > numActiveL0) l0 = l0.Take(numActiveL0).ToList();
        if (l1.Count > numActiveL1) l1 = l1.Take(numActiveL1).ToList();
        return (l0, l1);
    }

    /// <summary>P-slice L0 construction per spec §8.2.4.2.1. Short-term refs ordered by PicNum
    /// descending (newest first), followed by long-term refs ordered by LongTermPicNum ascending.
    /// Truncate to numActiveL0.</summary>
    internal static List<DecodedPicture> BuildPSliceRefListL0(List<DecodedPicture> dpb, int numActiveL0)
    {
        // PicNum has been cached on each short-term entry's LongTermPicNum field by the caller.
        var shortTerm = dpb.Where(p => !p.IsLongTerm).OrderByDescending(p => p.LongTermPicNum);
        var longTerm = dpb.Where(p => p.IsLongTerm).OrderBy(p => p.LongTermPicNum);
        var list = shortTerm.Concat(longTerm).ToList();
        if (list.Count > numActiveL0) list = list.Take(numActiveL0).ToList();
        return list;
    }

    /// <summary>Apply ref_pic_list_modification (spec §8.2.4.3) to an already-built ref list.
    /// op 0/1 adjust a running picNumPred and insert the matching short-term ref at refIdxL;
    /// op 2 inserts a long-term ref by LongTermPicNum. After insertion the list is shifted
    /// (existing entry at the target position moves to the next slot) and truncated to numActive.</summary>
    internal static void ApplyRefPicListModification(
        List<DecodedPicture> refList,
        Syntax.RefPicListModification[] ops,
        List<DecodedPicture> dpb,
        int curFrameNum,
        int maxFrameNum,
        int numActive)
    {
        if (ops.Length == 0) return;
        int picNumPred = curFrameNum;
        int refIdxL = 0;
        foreach (var op in ops)
        {
            if (op.ModificationOfPicNumsIdc == 0 || op.ModificationOfPicNumsIdc == 1)
            {
                // Short-term modification (§8.2.4.3.1).
                int absDiff = (int)op.Value + 1;
                int picNum;
                if (op.ModificationOfPicNumsIdc == 0)
                {
                    picNum = picNumPred - absDiff;
                    if (picNum < 0) picNum += maxFrameNum;
                }
                else
                {
                    picNum = picNumPred + absDiff;
                    if (picNum >= maxFrameNum) picNum -= maxFrameNum;
                }
                picNumPred = picNum;
                // Map picNum to the actual short-term ref (PicNum cached in LongTermPicNum field).
                int picNumNoWrap = picNum > curFrameNum ? picNum - maxFrameNum : picNum;
                DecodedPicture? target = dpb.FirstOrDefault(p => !p.IsLongTerm && p.LongTermPicNum == picNumNoWrap);
                if (target is null) continue;
                InsertAtIndex(refList, target, refIdxL, numActive);
                refIdxL++;
            }
            else if (op.ModificationOfPicNumsIdc == 2)
            {
                // Long-term modification (§8.2.4.3.2).
                DecodedPicture? target = dpb.FirstOrDefault(p => p.IsLongTerm && p.LongTermPicNum == (int)op.Value);
                if (target is null) continue;
                InsertAtIndex(refList, target, refIdxL, numActive);
                refIdxL++;
            }
        }
        if (refList.Count > numActive) refList.RemoveRange(numActive, refList.Count - numActive);
    }

    /// <summary>Insert <paramref name="pic"/> at <paramref name="index"/> per spec §8.2.4.3.1 /
    /// §8.2.4.3.2: shift entries at/after <paramref name="index"/> right, place pic at index, then
    /// remove any duplicate of pic that now sits AFTER the insertion point. Occurrences BEFORE the
    /// insertion point are left intact — the same picture legitimately appears twice (e.g. x264
    /// weightp fades reference one picture at two list positions with different weights). The list
    /// is truncated to numActive at the call site.</summary>
    /// <summary>FrameNumWrap (spec §8.2.4.1): a short-term ref's FrameNum mapped to a signed value
    /// relative to the current picture (negative when it was coded before a frame_num wraparound).</summary>
    private static int FrameNumWrap(int frameNum, int curFrameNum, int maxFrameNum) =>
        frameNum > curFrameNum ? frameNum - maxFrameNum : frameNum;

    private static void InsertAtIndex(List<DecodedPicture> list, DecodedPicture pic, int index, int numActive)
    {
        if (index > list.Count) index = list.Count;
        list.Insert(index, pic);
        // Remove the (at most one) later duplicate that the shift pushed down.
        for (int i = index + 1; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], pic)) { list.RemoveAt(i); break; }
        }
    }

    /// <summary>Apply dec_ref_pic_marking + sliding-window after decoding a ref slice (spec §8.2.5).
    /// Mutates <paramref name="dpb"/> in place: inserts <paramref name="pic"/> at front, processes
    /// MMCO ops (or sliding-window when no adaptive marking), and updates
    /// <paramref name="maxLongTermFrameIdx"/>. For IDR with long_term_reference_flag=1, the IDR
    /// enters as long-term idx 0 with MaxLongTermFrameIdx=0.</summary>
    internal static void ApplyDecRefPicMarking(
        DecodedPicture pic,
        SliceHeader header,
        List<DecodedPicture> dpb,
        SequenceParameterSet sps,
        ref int maxLongTermFrameIdx)
    {
        if (header.IdrPicFlag)
        {
            // IDR — DPB was cleared by caller. Insert and set long-term state per LT-flag.
            if (header.LongTermReferenceFlag)
            {
                pic.IsLongTerm = true;
                pic.LongTermFrameIdx = 0;
                pic.LongTermPicNum = 0;
                maxLongTermFrameIdx = 0;
            }
            else
            {
                pic.IsLongTerm = false;
                maxLongTermFrameIdx = -1;
            }
            dpb.Insert(0, pic);
            return;
        }

        // §8.2.5.1: the marking process is selected by adaptive_ref_pic_marking_mode_flag alone.
        // With the flag set but an empty MMCO list (immediate op 0), no sliding-window eviction
        // happens — the encoder keeps all existing refs. Branch on the flag, not the op count.
        if (header.AdaptiveRefPicMarkingMode)
        {
            // Apply MMCO ops (spec §8.2.5.4). Some ops affect existing DPB entries; op 6 marks
            // the current picture as long-term and replaces sliding-window for this slice.
            int maxFrameNum = 1 << ((int)sps.Log2MaxFrameNumMinus4 + 4);
            int curPicNum = (int)header.FrameNum;
            bool op5Seen = false;
            bool op6Seen = false;
            int op6Idx = 0;
            foreach (var op in header.MmcoOps)
            {
                switch (op.Op)
                {
                    case 1:
                    {
                        // Mark short-term ref as unused (§8.2.5.4.1). Match on FrameNumWrap, not the
                        // raw FrameNum: picNumX is negative for refs coded before a frame_num wrap.
                        int picNumX = curPicNum - (int)(op.DifferenceOfPicNumsMinus1 + 1);
                        int idx = dpb.FindIndex(p => !p.IsLongTerm && FrameNumWrap(p.FrameNum, curPicNum, maxFrameNum) == picNumX);
                        if (idx >= 0) dpb.RemoveAt(idx);
                        break;
                    }
                    case 2:
                    {
                        // Mark long-term ref as unused (§8.2.5.4.2).
                        int ltPicNum = (int)op.LongTermPicNum;
                        int idx = dpb.FindIndex(p => p.IsLongTerm && p.LongTermFrameIdx == ltPicNum);
                        if (idx >= 0) dpb.RemoveAt(idx);
                        break;
                    }
                    case 3:
                    {
                        // Mark a short-term ref as long-term (§8.2.5.4.3). Any existing long-term
                        // with the same idx is first marked unused. Match on FrameNumWrap (see op 1).
                        int picNumX = curPicNum - (int)(op.DifferenceOfPicNumsMinus1 + 1);
                        int ltIdx = (int)op.LongTermFrameIdx;
                        int existing = dpb.FindIndex(p => p.IsLongTerm && p.LongTermFrameIdx == ltIdx);
                        if (existing >= 0) dpb.RemoveAt(existing);
                        var st = dpb.FirstOrDefault(p => !p.IsLongTerm && FrameNumWrap(p.FrameNum, curPicNum, maxFrameNum) == picNumX);
                        if (st is not null)
                        {
                            st.IsLongTerm = true;
                            st.LongTermFrameIdx = ltIdx;
                            st.LongTermPicNum = ltIdx;
                        }
                        break;
                    }
                    case 4:
                    {
                        // Update MaxLongTermFrameIdx (§8.2.5.4.4). Any long-term with idx >= new max
                        // is marked unused. max_long_term_frame_idx_plus1==0 means "no LT idx".
                        int newMaxPlus1 = (int)op.MaxLongTermFrameIdxPlus1;
                        maxLongTermFrameIdx = newMaxPlus1 - 1;
                        if (maxLongTermFrameIdx < 0)
                        {
                            dpb.RemoveAll(p => p.IsLongTerm);
                        }
                        else
                        {
                            int max = maxLongTermFrameIdx;
                            dpb.RemoveAll(p => p.IsLongTerm && p.LongTermFrameIdx > max);
                        }
                        break;
                    }
                    case 5:
                    {
                        // Mark all as unused (§8.2.5.4.5). Effectively an IDR-style reset; the
                        // current picture goes in fresh below.
                        dpb.Clear();
                        maxLongTermFrameIdx = -1;
                        op5Seen = true;
                        break;
                    }
                    case 6:
                    {
                        // Mark current pic as long-term (§8.2.5.4.6). Defer the actual insert until
                        // after the loop so op-4 ordering doesn't matter.
                        op6Seen = true;
                        op6Idx = (int)op.LongTermFrameIdx;
                        // Also evict any pre-existing LT with the same idx.
                        int ex = dpb.FindIndex(p => p.IsLongTerm && p.LongTermFrameIdx == op6Idx);
                        if (ex >= 0) dpb.RemoveAt(ex);
                        break;
                    }
                }
            }
            if (op6Seen)
            {
                pic.IsLongTerm = true;
                pic.LongTermFrameIdx = op6Idx;
                pic.LongTermPicNum = op6Idx;
            }
            else if (op5Seen)
            {
                pic.IsLongTerm = false;
            }
            dpb.Insert(0, pic);
            // After op5, only the current pic should remain.
            return;
        }

        // No adaptive marking: sliding window (§8.2.5.3). The cap applies to the TOTAL of short-
        // and long-term entries (Max(max_num_ref_frames, 1)); only short-term refs are evicted,
        // oldest first (smallest FrameNumWrap == last in dpb, since newest is inserted at front).
        dpb.Insert(0, pic);
        int maxRefs = (int)Math.Max(1u, sps.MaxNumRefFrames);
        while (dpb.Count > maxRefs)
        {
            int victim = -1;
            for (int i = dpb.Count - 1; i >= 0; i--)
            {
                if (!dpb[i].IsLongTerm) { victim = i; break; }
            }
            if (victim < 0) break; // only long-term refs remain — nothing to evict (avoid infinite loop)
            dpb.RemoveAt(victim);
        }
    }
}
