using H264Decoder.Bitstream;
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
        DecodedPicture? referencePicture = null;
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
                    DecodedPicture pic = DecodeSlice(n, sps, pps, referencePicture);
                    outputs.Add(pic);
                    if (n.NalRefIdc != 0)
                    {
                        referencePicture = pic;
                    }
                    break;
            }
        }

        if (outputs.Count == 0) throw new InvalidDataException("no slices in bitstream");
        return outputs;
    }

    private DecodedPicture DecodeSlice(
        NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps,
        DecodedPicture? referencePicture)
    {
        var header = SliceHeader.Parse(nal.Rbsp.Span, nal, sps, pps);

        bool isPSlice = header.SliceType == SliceType.P;
        if (isPSlice && referencePicture is null)
        {
            throw new InvalidDataException("P-slice with no reference picture");
        }

        int width = (int)sps.CroppedWidth;
        int height = (int)sps.CroppedHeight;
        var picture = new DecodedPicture(width, height);

        var reader = new BitReader(nal.Rbsp.Span);
        SkipSliceHeader(ref reader, nal, sps, pps);

        int mbsPerRow = (int)sps.PicWidthInMbs;
        int totalMbs = mbsPerRow * (int)sps.PicHeightInMbs;
        int qpY = header.SliceQpY(pps);
        Macroblock[] mbs = new Macroblock[totalMbs];

        int addr = (int)header.FirstMbInSlice;
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
                    pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, referencePicture);
                mbSkipRun--;
                addr++;
                continue;
            }

            Macroblock mb = MacroblockParser.Parse(
                ref reader, sps, pps, header,
                leftMb, topMb, topRightMb, topLeftMb, addr, ref qpY);
            mbs[addr] = mb;

            MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb, referencePicture);

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

    /// <summary>Copy 16x16 luma + 8x8 chroma block from reference picture into output picture at MB position.</summary>
    private static void CopySkipMacroblockFromReference(
        DecodedPicture dst, DecodedPicture src, int mbX, int mbY)
    {
        int yStride = dst.Width;
        int yX = mbX * 16, yY = mbY * 16;
        for (int row = 0; row < 16; row++)
        {
            Array.Copy(src.Y, (yY + row) * yStride + yX,
                       dst.Y, (yY + row) * yStride + yX, 16);
        }
        int cStride = dst.ChromaWidth;
        int cX = mbX * 8, cY = mbY * 8;
        for (int row = 0; row < 8; row++)
        {
            Array.Copy(src.U, (cY + row) * cStride + cX,
                       dst.U, (cY + row) * cStride + cX, 8);
            Array.Copy(src.V, (cY + row) * cStride + cX,
                       dst.V, (cY + row) * cStride + cX, 8);
        }
    }

    /// <summary>Placeholder Macroblock for a P_Skip — treated as PredL0 with refIdx=0 and MV derived per §8.4.1.1.</summary>
    private static Macroblock SkipPlaceholder(int addr, int mvX, int mvY)
    {
        var mb = new Macroblock
        {
            MbAddress = addr,
            Type = new IntraMbType(0, MbPartPredMode.PredL0, default, 0, 0),
            RefIdxL0 = 0,
            MvL0X = mvX,
            MvL0Y = mvY,
        };
        for (int i = 0; i < 16; i++) { mb.MvL0XBlock[i] = mvX; mb.MvL0YBlock[i] = mvY; }
        // RefIdxL08x8 left as zeros (correct for P_Skip).
        mb.InterPartitions.Add(new MvPartition(0, 0, 16, 16, 0, mvX, mvY));
        return mb;
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
        if (sliceType == SliceType.P)
        {
            bool overrideFlag = r.ReadBit() == 1;
            if (overrideFlag) _ = ExpGolomb.ReadUe(ref r);
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
        if (nal.NalRefIdc != 0)
        {
            if (idrPicFlag) { _ = r.ReadBit(); _ = r.ReadBit(); }
            else _ = r.ReadBit();
        }
        _ = ExpGolomb.ReadSe(ref r);                              // slice_qp_delta
        if (pps.DeblockingFilterControlPresentFlag)
        {
            uint idc = ExpGolomb.ReadUe(ref r);
            if (idc != 1) { _ = ExpGolomb.ReadSe(ref r); _ = ExpGolomb.ReadSe(ref r); }
        }
    }
}
