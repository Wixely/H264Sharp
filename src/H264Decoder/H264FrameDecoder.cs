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
                        // IDR clears the DPB (per spec §8.2.5.1).
                        dpb.Clear();
                    }
                    DecodedPicture pic = DecodeSlice(n, sps, pps, dpb);
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
        return outputs;
    }

    private DecodedPicture DecodeSlice(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps,
        List<DecodedPicture> dpb)
    {
        var header = SliceHeader.Parse(nal.Rbsp.Span, nal, sps, pps);

        bool isPSlice = header.SliceType == SliceType.P;
        if (isPSlice && dpb.Count == 0)
        {
            throw new InvalidDataException("P-slice with empty DPB");
        }

        // Build the active L0 reference picture list for this slice: take the first
        // num_ref_idx_l0_active_minus1+1 entries of the DPB (which is newest-first).
        // We do not honour ref_pic_list_modification — typical x264 default ordering.
        int numActiveRefs = (int)(header.NumRefIdxL0ActiveMinus1 + 1);
        var refPicListL0 = isPSlice
            ? dpb.Take(Math.Min(numActiveRefs, dpb.Count)).ToList()
            : new List<DecodedPicture>();

        int width = (int)sps.CroppedWidth;
        int height = (int)sps.CroppedHeight;
        var picture = new DecodedPicture(width, height) { FrameNum = (int)header.FrameNum };

        var reader = new BitReader(nal.Rbsp.Span);
        SkipSliceHeader(ref reader, nal, sps, pps);

        int mbsPerRow = (int)sps.PicWidthInMbs;
        int totalMbs = mbsPerRow * (int)sps.PicHeightInMbs;
        int qpY = header.SliceQpY(pps);
        Macroblock[] mbs = new Macroblock[totalMbs];

        int addr = (int)header.FirstMbInSlice;

        // ---- Branch on entropy coding mode ----
        if (pps.EntropyCodingModeFlag)
        {
            DecodeSliceCabac(nal, sps, pps, header, ref reader, mbs, picture, refPicListL0,
                mbsPerRow, totalMbs, ref qpY, addr);
            if (header.DisableDeblockingFilterIdc != 1 && !isPSlice)
            {
                bool filterMbEdges = header.DisableDeblockingFilterIdc != 2;
                DeblockingFilter.Apply(picture, mbs, mbsPerRow,
                    pps.ChromaQpIndexOffset,
                    header.SliceAlphaC0OffsetDiv2 * 2,
                    header.SliceBetaOffsetDiv2 * 2,
                    filterMbEdges);
            }
            LastMacroblocks = mbs;
            return picture;
        }

        int mbSkipRun = 0;
        if (isPSlice)
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
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);
                mbSkipRun--;
                addr++;
                continue;
            }

            Macroblock mb = MacroblockParser.Parse(
                ref reader, sps, pps, header,
                leftMb, topMb, topRightMb, topLeftMb, addr, ref qpY);
            mbs[addr] = mb;

            MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);

            addr++;

            // In CAVLC P-slices, after each non-skipped MB we read another mb_skip_run.
            if (isPSlice && addr < totalMbs)
            {
                mbSkipRun = (int)ExpGolomb.ReadUe(ref reader);
            }
        }

        if (header.DisableDeblockingFilterIdc != 1 && !isPSlice)
        {
            // For pure-skip P-slices the reference is already deblocked; the spec
            // would still apply deblocking, but the per-MB filter strengths are
            // all zero (no coded coefs, MVs match, refs match), so it's a no-op
            // for our minimal case.
            bool filterMbEdges = header.DisableDeblockingFilterIdc != 2;
            DeblockingFilter.Apply(picture, mbs, mbsPerRow,
                pps.ChromaQpIndexOffset,
                header.SliceAlphaC0OffsetDiv2 * 2,
                header.SliceBetaOffsetDiv2 * 2,
                filterMbEdges);
        }

        LastMacroblocks = mbs;
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

    /// <summary>CABAC slice_data() loop (spec §7.3.4). Currently handles all-P_Skip P-slices.</summary>
    private static void DecodeSliceCabac(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps, SliceHeader header,
        ref BitReader reader, Macroblock[] mbs, DecodedPicture picture,
        List<DecodedPicture> refPicListL0, int mbsPerRow, int totalMbs, ref int qpY, int addr)
    {
        bool isPSlice = header.SliceType == SliceType.P;

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
            if (isPSlice)
            {
                // ctxIdxInc for mb_skip_flag (spec table 9-39): condTermFlagX = 0 if neighbor
                // is unavailable OR has mb_skip_flag == 1; otherwise 1.
                int condA = (leftMb != null && !leftMb.IsSkipped) ? 1 : 0;
                int condB = (topMb != null && !topMb.IsSkipped) ? 1 : 0;
                mbSkipFlag = cabac.DecodeBin(11 + condA + condB);
            }

            if (mbSkipFlag == 1)
            {
                (int skipMvX, int skipMvY) = MacroblockParser.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
                Macroblock skipMb = SkipPlaceholder(addr, skipMvX, skipMvY);
                mbs[addr] = skipMb;
                MacroblockReconstructor.Reconstruct(skipMb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);
            }
            else if (!isPSlice)
            {
                // I-slice macroblock (no mb_skip_flag exists for I-slices).
                Macroblock mb = CabacSliceI.ParseMb(cabac, leftMb, topMb, addr,
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);
            }
            else
            {
                // P-slice non-skip MB.
                Macroblock mb = CabacSliceP.ParseMb(cabac, header,
                    leftMb, topMb, topRightMb, topLeftMb, addr,
                    ref qpY, ref prevMbQpDeltaState, pps.Transform8x8ModeFlag);
                mbs[addr] = mb;
                MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, refPicListL0);
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
        if (sliceType == SliceType.P)
        {
            bool overrideFlag = r.ReadBit() == 1;
            if (overrideFlag) effectiveNumRefL0 = ExpGolomb.ReadUe(ref r);
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
        }
        // pred_weight_table for P/SP slices when weighted_pred_flag=1 (must precede dec_ref_pic_marking).
        if (pps.WeightedPredFlag && sliceType == SliceType.P)
        {
            SliceHeader.SkipPredWeightTable(ref r, effectiveNumRefL0, hasChroma: true);
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
}
