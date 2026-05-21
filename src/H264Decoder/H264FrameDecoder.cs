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
    /// Decode the first I-frame from a byte stream. Accepts either Annex-B
    /// (start-code framed) or AVCC (4-byte length prefixed). The framing is
    /// auto-detected: a leading zero byte indicates Annex-B; anything else is
    /// treated as AVCC.
    /// </summary>
    public DecodedPicture DecodeFirstIFrame(ReadOnlySpan<byte> bytes)
    {
        List<NalUnit> nals = LooksLikeAnnexB(bytes)
            ? AnnexBReader.SplitNalUnits(bytes)
            : AvccReader.SplitNalUnits(bytes);
        return DecodeFirstIFrame(nals);
    }

    /// <summary>Detects Annex-B framing by looking for a leading zero byte (start code).</summary>
    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> bytes)
    {
        // Annex-B streams always start with 0x000001 or 0x00000001 (or padding zeros).
        // AVCC streams start with a non-zero length-prefix high byte for any NAL of
        // length >= 256 bytes; for tiny streams length might begin with 0x00 too, but
        // that's a 3-byte length 0x0000nn — impossible in standard AVCC (length must
        // be ≥ 1). So we look at the *first non-zero byte*: if it's 0x01 within the
        // first 4 bytes, this is Annex-B.
        for (int i = 0; i < Math.Min(4, bytes.Length); i++)
        {
            if (bytes[i] == 0) continue;
            return bytes[i] == 1;
        }
        return false;
    }

    /// <summary>Decode from pre-parsed NAL units. Use this if you already have a List&lt;NalUnit&gt;
    /// (e.g. extracted from an MP4 avcC + mdat).</summary>
    public DecodedPicture DecodeFirstIFrame(List<NalUnit> nals)
    {
        SequenceParameterSet? sps = null;
        PictureParameterSet? pps = null;
        NalUnit? idr = null;

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
                    idr ??= n;
                    break;
            }
        }

        if (sps is null) throw new InvalidDataException("no SPS in bitstream");
        if (pps is null) throw new InvalidDataException("no PPS in bitstream");
        if (idr is null) throw new InvalidDataException("no IDR slice in bitstream");

        int width = (int)sps.CroppedWidth;
        int height = (int)sps.CroppedHeight;
        var picture = new DecodedPicture(width, height);

        // Parse slice header, then walk macroblocks.
        var reader = new BitReader(idr.Value.Rbsp.Span);
        var header = SliceHeader.Parse(idr.Value.Rbsp.Span, idr.Value, sps, pps);
        SkipSliceHeader(ref reader, idr.Value, sps, pps);

        int mbsPerRow = (int)sps.PicWidthInMbs;
        int totalMbs = mbsPerRow * (int)sps.PicHeightInMbs;
        int qpY = header.SliceQpY(pps);

        Macroblock[] mbs = new Macroblock[totalMbs];

        for (int addr = (int)header.FirstMbInSlice; addr < totalMbs; addr++)
        {
            int mbX = addr % mbsPerRow;
            int mbY = addr / mbsPerRow;

            Macroblock? leftMb = mbX > 0 ? mbs[addr - 1] : null;
            Macroblock? topMb = mbY > 0 ? mbs[addr - mbsPerRow] : null;
            Macroblock? topRightMb = (mbY > 0 && mbX + 1 < mbsPerRow)
                ? mbs[addr - mbsPerRow + 1]
                : null;

            Macroblock mb = MacroblockParser.Parse(
                ref reader, sps, pps, header,
                leftMb, topMb, addr, ref qpY);
            mbs[addr] = mb;

            MacroblockReconstructor.Reconstruct(mb, picture, mbX, mbY,
                pps.ChromaQpIndexOffset, leftMb, topMb, topRightMb);
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
        return picture;
    }

    /// <summary>Advance the bit reader past the slice header (mirrors SliceHeader.Parse).</summary>
    private static void SkipSliceHeader(
        ref BitReader r, NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps)
    {
        bool idrPicFlag = nal.NalUnitType == NalUnitType.SliceIdr;
        _ = ExpGolomb.ReadUe(ref r);                              // first_mb_in_slice
        _ = ExpGolomb.ReadUe(ref r);                              // slice_type
        _ = ExpGolomb.ReadUe(ref r);                              // pic_parameter_set_id
        _ = r.ReadBits((int)sps.Log2MaxFrameNumMinus4 + 4);       // frame_num
        if (idrPicFlag) _ = ExpGolomb.ReadUe(ref r);
        if (sps.PicOrderCntType == 0)
        {
            _ = r.ReadBits((int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag) _ = ExpGolomb.ReadSe(ref r);
        }
        if (pps.RedundantPicCntPresentFlag) _ = ExpGolomb.ReadUe(ref r);
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
