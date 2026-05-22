using H264Decoder.Bitstream;
using H264Decoder.Cabac;
using H264Decoder.Loop;
using H264Decoder.Picture;
using H264Decoder.Syntax;

namespace H264Decoder;

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

    /// <summary>Detects Annex-B framing by looking for a leading zero byte (start code).</summary>
    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < Math.Min(4, bytes.Length); i++)
        {
            if (bytes[i] == 0) continue;
            return bytes[i] == 1;
        }
        return false;
    }

    public DecodedPicture DecodeFirstIFrame(List<NalUnit> nals) =>
        DecodeAllFrames(nals).First();

    public List<DecodedPicture> DecodeAllFrames(List<NalUnit> nals)
    {
        SequenceParameterSet? sps = null;
        PictureParameterSet? pps = null;
        // DPB: short-term reference pictures, newest first (index 0 = most recent).
        var dpb = new List<DecodedPicture>();
        var outputs = new List<DecodedPicture>();
        // POC type-0 running state (spec §8.2.1.1).
        int prevPicOrderCntMsb = 0;
        int prevPicOrderCntLsb = 0;

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
                    if (n.NalUnitType == NalUnitType.SliceIdr)
                    {
                        // IDR clears the DPB (per spec §8.2.5.1) and resets POC state.
                        dpb.Clear();
                        prevPicOrderCntMsb = 0;
                        prevPicOrderCntLsb = 0;
                    }
                    DecodedPicture pic = DecodeSlice(n, sps, pps, dpb,
                        ref prevPicOrderCntMsb, ref prevPicOrderCntLsb);
                    outputs.Add(pic);
                    if (n.NalRefIdc != 0)
                    {
                        // Sliding window: insert at front, evict oldest if over capacity.
                        dpb.Insert(0, pic);
                        int maxRefs = (int)Math.Max(1u, sps.MaxNumRefFrames);
                        while (dpb.Count > maxRefs) dpb.RemoveAt(dpb.Count - 1);
                    }
                    break;
            }
        }

        if (outputs.Count == 0) throw new InvalidDataException("no slices in bitstream");
        // Stage 1: sort by POC for display order. TODO: replace with proper §C.2.4 bumping
        // process once B-frame decoding lands and we need real-time output.
        outputs.Sort((a, b) => a.PicOrderCnt.CompareTo(b.PicOrderCnt));
        return outputs;
    }

    private DecodedPicture DecodeSlice(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps,
        List<DecodedPicture> dpb,
        ref int prevPicOrderCntMsb, ref int prevPicOrderCntLsb)
    {
        var header = SliceHeader.Parse(nal.Rbsp.Span, nal, sps, pps);

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

        // Compute POC (spec §8.2.1) for this picture. We update prev* state below
        // after computing so reference-list construction can use this picture's POC.
        int picOrderCnt = ComputePicOrderCnt(header, sps,
            ref prevPicOrderCntMsb, ref prevPicOrderCntLsb, nal.NalRefIdc != 0);

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
            // P-slice L0: DPB newest-first (already maintained that way).
            refPicListL0 = dpb.Take(Math.Min(numActiveL0, dpb.Count)).ToList();
        }
        else
        {
            refPicListL0 = new List<DecodedPicture>();
        }
        int width = (int)sps.CroppedWidth;
        int height = (int)sps.CroppedHeight;
        var picture = new DecodedPicture(width, height)
        {
            FrameNum = (int)header.FrameNum,
            PicOrderCnt = picOrderCnt,
            MbsPerRow = (int)sps.PicWidthInMbs,
        };

        var reader = new BitReader(nal.Rbsp.Span);
        SkipSliceHeader(ref reader, nal, sps, pps);

        int mbsPerRow = (int)sps.PicWidthInMbs;
        int totalMbs = mbsPerRow * (int)sps.PicHeightInMbs;
        int qpY = header.SliceQpY(pps);
        Macroblock[] mbs = new Macroblock[totalMbs];
        bool implicitBipred = isBSlice && pps.WeightedBipredIdc == 2;

        int addr = (int)header.FirstMbInSlice;

        // ---- Branch on entropy coding mode ----
        if (pps.EntropyCodingModeFlag)
        {
            DecodeSliceCabac(nal, sps, pps, header, ref reader, mbs, picture, refPicListL0, refPicListL1,
                mbsPerRow, totalMbs, ref qpY, addr, implicitBipred);
            if (header.DisableDeblockingFilterIdc != 1)
            {
                bool filterMbEdges = header.DisableDeblockingFilterIdc != 2;
                DeblockingFilter.Apply(picture, mbs, mbsPerRow,
                    pps.ChromaQpIndexOffset,
                    header.SliceAlphaC0OffsetDiv2 * 2,
                    header.SliceBetaOffsetDiv2 * 2,
                    filterMbEdges);
            }
            LastMacroblocks = mbs;
            picture.Macroblocks = mbs;
            return picture;
        }

        int mbSkipRun = 0;
        if (isPSlice || isBSlice)
        {
            mbSkipRun = (int)ExpGolomb.ReadUe(ref reader);
        }

        while (addr < totalMbs)
        {
            int mbX = addr % mbsPerRow;
            int mbY = addr / mbsPerRow;

            Macroblock? leftMb = mbX > 0 ? mbs[addr - 1] : null;
            Macroblock? topMb = mbY > 0 ? mbs[addr - mbsPerRow] : null;
            Macroblock? topRightMb = (mbY > 0 && mbX + 1 < mbsPerRow)
                ? mbs[addr - mbsPerRow + 1]
                : null;
            Macroblock? topLeftMb = (mbY > 0 && mbX > 0)
                ? mbs[addr - mbsPerRow - 1]
                : null;

            if (isPSlice && mbSkipRun > 0)
            {
                // P_Skip: derive MV per spec §8.4.1.1 from neighbors, then treat as
                // P_L0_16x16 with refIdx=0 + that MV + zero residual.
                (int skipMvX, int skipMvY) = MacroblockParser.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
                Macroblock skipMb = SkipPlaceholder(addr, skipMvX, skipMvY);
                mbs[addr] = skipMb;
                MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, null, header.PredWeights);
                mbSkipRun--;
                addr++;
                continue;
            }

            if (isBSlice && mbSkipRun > 0)
            {
                Macroblock? colMb = isBSlice ? GetColocatedMb(refPicListL1, addr) : null;
                Macroblock skipMb = BSkipPlaceholder(addr, header, leftMb, topMb, topRightMb, topLeftMb, colMb);
                mbs[addr] = skipMb;
                MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                    implicitBipred);
                mbSkipRun--;
                addr++;
                continue;
            }

            Macroblock? colMbInter = isBSlice ? GetColocatedMb(refPicListL1, addr) : null;
            Macroblock mb = MacroblockParser.Parse(
                ref reader, sps, pps, header,
                leftMb, topMb, topRightMb, topLeftMb, addr, ref qpY, colMbInter);
            mbs[addr] = mb;

            MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                implicitBipred);

            addr++;

            // In CAVLC P-slices, after each non-skipped MB we read another mb_skip_run.
            if ((isPSlice || isBSlice) && addr < totalMbs)
            {
                mbSkipRun = (int)ExpGolomb.ReadUe(ref reader);
            }
        }

        if (header.DisableDeblockingFilterIdc != 1)
        {
            bool filterMbEdges = header.DisableDeblockingFilterIdc != 2;
            DeblockingFilter.Apply(picture, mbs, mbsPerRow,
                pps.ChromaQpIndexOffset,
                header.SliceAlphaC0OffsetDiv2 * 2,
                header.SliceBetaOffsetDiv2 * 2,
                filterMbEdges);
        }

        LastMacroblocks = mbs;
        picture.Macroblocks = mbs;
        return picture;
    }

    /// <summary>Placeholder Macroblock for a P_Skip — treated as PredL0 with refIdx=0 and MV derived per §8.4.1.1.</summary>
    private static Macroblock SkipPlaceholder(int addr, int mvX, int mvY)
    {
        var mb = new Macroblock
        {
            MbAddress = addr,
            Type = new IntraMbType(0, MbPartPredMode.PredL0, default, 0, 0),
            IsSkipped = true,
            RefIdxL0 = 0,
            MvL0X = mvX,
            MvL0Y = mvY,
        };
        for (int i = 0; i < 16; i++) { mb.MvL0XBlock[i] = mvX; mb.MvL0YBlock[i] = mvY; }
        // RefIdxL08x8 left as zeros (correct for P_Skip).
        mb.InterPartitions.Add(new MvPartition(0, 0, 16, 16, 0, mvX, mvY));
        return mb;
    }

    /// <summary>Placeholder Macroblock for a B_Skip — uses B_Direct_16x16 spatial direct derivation
    /// to fill MVs; no residual.</summary>
    private static Macroblock BSkipPlaceholder(int addr, SliceHeader header,
        Macroblock? leftMb, Macroblock? topMb, Macroblock? topRightMb, Macroblock? topLeftMb,
        Macroblock? colocatedMb)
    {
        var mb = new Macroblock
        {
            MbAddress = addr,
            Type = new IntraMbType(0, MbPartPredMode.PredL0, default, 0, 0),
            IsSkipped = true,
            IsBSkip = true,
            IsBInter = true,
        };
        BDirectMode.ApplyDirect16x16(mb, header, leftMb, topMb, topRightMb, topLeftMb, colocatedMb);
        return mb;
    }

    /// <summary>CABAC slice_data() loop (spec §7.3.4). Currently handles all-P_Skip P-slices.</summary>
    private static void DecodeSliceCabac(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps, SliceHeader header,
        ref BitReader reader, Macroblock[] mbs, DecodedPicture picture,
        List<DecodedPicture> refPicListL0, List<DecodedPicture> refPicListL1,
        int mbsPerRow, int totalMbs, ref int qpY, int addr, bool implicitBipred)
    {
        bool isPSlice = header.SliceType == SliceType.P;
        bool isBSlice = header.SliceType == SliceType.B;

        // CABAC alignment: consume one-bits up to byte boundary (spec §7.3.4 — cabac_alignment_one_bit).
        while ((reader.BitPosition & 7) != 0) reader.ReadBit();

        // Initialize contexts.
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

        int prevMbQpDeltaState = 0; // CABAC state for mb_qp_delta binIdx 0 ctxIdxInc

        while (addr < totalMbs)
        {
            int mbX = addr % mbsPerRow;
            int mbY = addr / mbsPerRow;
            Macroblock? leftMb = mbX > 0 ? mbs[addr - 1] : null;
            Macroblock? topMb = mbY > 0 ? mbs[addr - mbsPerRow] : null;
            Macroblock? topRightMb = (mbY > 0 && mbX + 1 < mbsPerRow) ? mbs[addr - mbsPerRow + 1] : null;
            Macroblock? topLeftMb = (mbY > 0 && mbX > 0) ? mbs[addr - mbsPerRow - 1] : null;

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
                    Macroblock skipMb = BSkipPlaceholder(addr, header, leftMb, topMb, topRightMb, topLeftMb, colMbSkip);
                    mbs[addr] = skipMb;
                    MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                        pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                        implicitBipred);
                }
                else
                {
                    (int skipMvX, int skipMvY) = MacroblockParser.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
                    Macroblock skipMb = SkipPlaceholder(addr, skipMvX, skipMvY);
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
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag, colMbB);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0, refPicListL1, header.PredWeights,
                    implicitBipred);
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
            if (addr < totalMbs)
            {
                int endOfSlice = cabac.DecodeTerminate();
                if (endOfSlice == 1) break;
            }
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
        if (idrPicFlag) _ = ExpGolomb.ReadUe(ref r);
        if (sps.PicOrderCntType == 0)
        {
            _ = r.ReadBits((int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag) _ = ExpGolomb.ReadSe(ref r);
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
        ref int prevPicOrderCntMsb, ref int prevPicOrderCntLsb, bool isReference)
    {
        if (header.IdrPicFlag)
        {
            prevPicOrderCntMsb = 0;
            prevPicOrderCntLsb = 0;
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

        // pic_order_cnt_type == 2: decode-order = display-order. Use frame_num*2 as POC.
        return (int)header.FrameNum * 2;
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

    /// <summary>B-slice reference list construction per spec §8.2.4.2.3. Short-term only;
    /// long-term refs are not yet supported.</summary>
    private static (List<DecodedPicture> l0, List<DecodedPicture> l1) BuildBSliceRefLists(
        List<DecodedPicture> dpb, int currentPoc, int numActiveL0, int numActiveL1)
    {
        // L0: past (POC < current, descending) followed by future (POC > current, ascending).
        var past = dpb.Where(p => p.PicOrderCnt < currentPoc).OrderByDescending(p => p.PicOrderCnt).ToList();
        var future = dpb.Where(p => p.PicOrderCnt > currentPoc).OrderBy(p => p.PicOrderCnt).ToList();
        var l0 = past.Concat(future).Take(Math.Min(numActiveL0, dpb.Count)).ToList();
        // L1: future (ascending) followed by past (descending).
        var l1 = future.Concat(past).Take(Math.Min(numActiveL1, dpb.Count)).ToList();
        return (l0, l1);
    }
}
