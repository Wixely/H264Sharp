using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Tests.Fixtures;

namespace H264Sharp.Tests.Syntax;

[Trait("Category", "Ffmpeg")]
public sealed class MacroblockParserTests
{
    [Fact]
    public void ParsesSingleMacroblockFromFfmpegSample()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] stream = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(stream);

        var sps = SequenceParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Sps).Rbsp.Span);
        var pps = PictureParameterSet.Parse(nals.First(n => n.NalUnitType == NalUnitType.Pps).Rbsp.Span);
        NalUnit idr = nals.First(n => n.NalUnitType == NalUnitType.SliceIdr);
        var hdr = SliceHeader.Parse(idr.Rbsp.Span, idr, sps, pps);

        var slice = new BitReader(idr.Rbsp.Span);
        // Re-parse the header to advance the reader to slice_data().
        // SliceHeader.Parse consumed bytes from a fresh reader — but BitReader is a
        // ref struct so we cannot share its position. Instead we re-run Parse with
        // an instrumented reader. The simplest path: read the header again here.
        ReplayHeader(ref slice, idr, sps, pps);

        int mbAddress = (int)hdr.FirstMbInSlice;
        int qpY = hdr.SliceQpY(pps);

        Macroblock mb = MacroblockParser.Parse(
            ref slice, sps, pps, hdr,
            leftMb: null, topMb: null, topRightMb: null, topLeftMb: null,
            mbAddress: mbAddress, qpYRunning: ref qpY);

        Assert.Equal(MbPartPredMode.Intra16x16, mb.Type.PredMode);
        // Solid-red frame encoded by libx264 typically picks DC prediction with
        // CbpLuma=0 and small DC coefficient. Smoke-assert the parse succeeded
        // without exception and that we landed in the Intra_16x16 path.
        Assert.InRange(mb.QpY, 0, 51);
    }

    private static void ReplayHeader(ref BitReader r, NalUnit nal, SequenceParameterSet sps, PictureParameterSet pps)
    {
        // Walks the same syntax as SliceHeader.Parse but only to advance the reader;
        // assumes I-slice / IDR / non-FMO / non-CABAC (already validated above).
        bool idrPicFlag = nal.NalUnitType == NalUnitType.SliceIdr;

        _ = ExpGolomb.ReadUe(ref r);                              // first_mb_in_slice
        _ = ExpGolomb.ReadUe(ref r);                              // slice_type
        _ = ExpGolomb.ReadUe(ref r);                              // pic_parameter_set_id
        _ = r.ReadBits((int)sps.Log2MaxFrameNumMinus4 + 4);       // frame_num
        if (idrPicFlag) _ = ExpGolomb.ReadUe(ref r);              // idr_pic_id
        if (sps.PicOrderCntType == 0)
        {
            _ = r.ReadBits((int)sps.Log2MaxPicOrderCntLsbMinus4 + 4);
            if (pps.BottomFieldPicOrderInFramePresentFlag)
                _ = ExpGolomb.ReadSe(ref r);
        }
        if (pps.RedundantPicCntPresentFlag) _ = ExpGolomb.ReadUe(ref r);
        if (nal.NalRefIdc != 0)
        {
            if (idrPicFlag)
            {
                _ = r.ReadBit();
                _ = r.ReadBit();
            }
            else
            {
                _ = r.ReadBit();
            }
        }
        _ = ExpGolomb.ReadSe(ref r);                              // slice_qp_delta
        if (pps.DeblockingFilterControlPresentFlag)
        {
            uint idc = ExpGolomb.ReadUe(ref r);
            if (idc != 1)
            {
                _ = ExpGolomb.ReadSe(ref r);
                _ = ExpGolomb.ReadSe(ref r);
            }
        }
    }
}
