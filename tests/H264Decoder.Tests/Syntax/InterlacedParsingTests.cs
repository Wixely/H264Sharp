using H264Decoder.Bitstream;
using H264Decoder.Syntax;

namespace H264Decoder.Tests.Syntax;

/// <summary>Stage 1 of interlaced support: SPS / slice-header parsing accepts frame_mbs_only_flag=0
/// streams (PAFF + MBAFF) and exposes the relevant fields. The decoder itself rejects them at
/// dispatch time with a parameterized error. These tests use hand-crafted bytes so we can target
/// the exact bit patterns without depending on a real interlaced encoder.</summary>
public sealed class InterlacedParsingTests
{
    /// <summary>Hand-built minimal Baseline SPS RBSP with frame_mbs_only_flag set per the
    /// <paramref name="frameMbsOnly"/> parameter; when frame_mbs_only_flag=0,
    /// mb_adaptive_frame_field_flag follows from <paramref name="mbAdaptive"/>. The picture is
    /// 1 MB wide × 1 MB tall, no cropping, no VUI. Bit layout:
    ///   byte 0: profile_idc=66 (Baseline)
    ///   byte 1: constraint flags + reserved = 0
    ///   byte 2: level_idc=30
    ///   bits 24..31: spsId=0 (1), log2_max_frame_num_minus4=0 (1), pic_order_cnt_type=0 (1),
    ///                log2_max_pic_order_cnt_lsb_minus4=0 (1), max_num_ref_frames=0 (1),
    ///                gaps=0, pic_width_in_mbs_minus1=0 (1), pic_height_in_map_units_minus1=0 (1)
    ///   bit 32: frame_mbs_only_flag
    ///   bit 33: mb_adaptive_frame_field_flag (only if !frame_mbs_only)
    ///   then direct_8x8_inference_flag=0, frame_cropping_flag=0, vui_parameters_present_flag=0,
    ///   trailing "1" + zero pad.</summary>
    private static byte[] BuildMinimalSps(bool frameMbsOnly, bool mbAdaptive = false)
    {
        // Top 3 bytes are fixed.
        // Byte 3 = 0xFB = 11111011 (spsId=1, l2MaxFN=1, pocType=1, l2MaxPocLsb=1,
        //                            maxRefs=1, gaps=0, pwidth=1, pheight=1).
        // Byte 4 layout depends on frame_mbs_only_flag.
        // If frame_mbs_only=1: bits = 1 (FMO) 0 (direct8x8) 0 (crop) 0 (vui) 1 (trailing) 000 = 0x88
        //   Actually that's 1 0 0 0 1 0 0 0 = 0x88.
        // If frame_mbs_only=0, mb_adaptive=0: 0 0 0 0 0 1 0 0 = 0x04
        // If frame_mbs_only=0, mb_adaptive=1: 0 1 0 0 0 1 0 0 = 0x44
        byte byte4;
        if (frameMbsOnly) byte4 = 0x88;
        else byte4 = mbAdaptive ? (byte)0x44 : (byte)0x04;
        return new byte[] { 0x42, 0x00, 0x1E, 0xFB, byte4 };
    }

    [Fact]
    public void Sps_FrameMbsOnly_True_ParsesSuccessfully()
    {
        // Sanity: baseline test that our hand-built SPS round-trips the trivial case.
        var sps = SequenceParameterSet.Parse(BuildMinimalSps(frameMbsOnly: true));
        Assert.True(sps.FrameMbsOnlyFlag);
        Assert.False(sps.MbAdaptiveFrameFieldFlag);
        Assert.Equal(1u, sps.PicHeightInMbs); // FrameMbsOnly=true → same as pic_height_in_map_units
    }

    [Fact]
    public void Sps_PaffNoMbaff_ParsesFrameMbsOnlyFalse()
    {
        var sps = SequenceParameterSet.Parse(BuildMinimalSps(frameMbsOnly: false, mbAdaptive: false));
        Assert.False(sps.FrameMbsOnlyFlag);
        Assert.False(sps.MbAdaptiveFrameFieldFlag);
        // PicHeightInMbs doubles when frame_mbs_only is false (interlaced container).
        Assert.Equal(2u, sps.PicHeightInMbs);
    }

    [Fact]
    public void Sps_Mbaff_ParsesMbAdaptiveTrue()
    {
        var sps = SequenceParameterSet.Parse(BuildMinimalSps(frameMbsOnly: false, mbAdaptive: true));
        Assert.False(sps.FrameMbsOnlyFlag);
        Assert.True(sps.MbAdaptiveFrameFieldFlag);
    }

    /// <summary>Build a minimal slice header RBSP for an IDR I-slice on top of the given SPS/PPS.
    /// When <paramref name="fieldPic"/> is true the field_pic_flag bit is set; bottom_field_flag
    /// reflects <paramref name="bottomField"/>. Only fields read by SliceHeader.Parse before / up
    /// to slice_qp_delta are emitted; the rest is padded with rbsp_trailing_bits.</summary>
    private static byte[] BuildMinimalIdrSliceRbsp(SequenceParameterSet sps, PictureParameterSet pps,
        bool fieldPic, bool bottomField)
    {
        // We assemble the bits into a buffer using a helper writer.
        var bw = new BitWriter();
        bw.WriteUe(0);              // first_mb_in_slice = 0
        bw.WriteUe(7);              // slice_type = 7 → all I (raw 7 = I-slice + AllSlicesSameType)
        bw.WriteUe(0);              // pic_parameter_set_id = 0
        int frameNumBits = (int)sps.Log2MaxFrameNumMinus4 + 4;
        bw.WriteUBits(0, frameNumBits); // frame_num = 0
        if (!sps.FrameMbsOnlyFlag)
        {
            bw.WriteBit(fieldPic ? 1 : 0);
            if (fieldPic) bw.WriteBit(bottomField ? 1 : 0);
        }
        bw.WriteUe(0);              // idr_pic_id (IDR only)
        if (sps.PicOrderCntType == 0)
        {
            int lsbBits = (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4;
            bw.WriteUBits(0, lsbBits); // pic_order_cnt_lsb = 0
            // delta_pic_order_cnt_bottom omitted when field_pic_flag=1, and we built our PPS with
            // BottomFieldPicOrderInFramePresentFlag = false anyway.
        }
        bw.WriteBit(0);             // no_output_of_prior_pics_flag (NAL ref idc != 0 path: we use IDR)
        bw.WriteBit(0);             // long_term_reference_flag
        bw.WriteSe(0);              // slice_qp_delta
        // No deblocking filter control (PPS.DeblockingFilterControlPresentFlag=false).
        bw.WriteTrailing();
        return bw.ToArray();
    }

    [Fact]
    public void SliceHeader_FrameMbsOnlyFalse_FieldPicTrue_BottomField_Parses()
    {
        var sps = SequenceParameterSet.Parse(BuildMinimalSps(frameMbsOnly: false));
        var pps = BuildMinimalPps();
        byte[] rbsp = BuildMinimalIdrSliceRbsp(sps, pps, fieldPic: true, bottomField: true);

        var nal = new NalUnit(nalRefIdc: 3, NalUnitType.SliceIdr, rbsp);
        var header = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.True(header.FieldPicFlag);
        Assert.True(header.BottomFieldFlag);
    }

    [Fact]
    public void SliceHeader_FrameMbsOnlyFalse_FieldPicFalse_Parses()
    {
        var sps = SequenceParameterSet.Parse(BuildMinimalSps(frameMbsOnly: false));
        var pps = BuildMinimalPps();
        byte[] rbsp = BuildMinimalIdrSliceRbsp(sps, pps, fieldPic: false, bottomField: false);

        var nal = new NalUnit(nalRefIdc: 3, NalUnitType.SliceIdr, rbsp);
        var header = SliceHeader.Parse(rbsp, nal, sps, pps);

        Assert.False(header.FieldPicFlag);
        Assert.False(header.BottomFieldFlag);
    }

    [Fact]
    public void Decoder_RejectsMbaffWithParameterizedError()
    {
        byte[] bitstream = BuildInterlacedAnnexBStream(mbAdaptive: true, fieldPic: false, bottomField: false);
        var dec = new H264FrameDecoder();
        var ex = Assert.Throws<NotSupportedException>(() => dec.DecodeAllFrames(bitstream));
        Assert.Contains("MBAFF", ex.Message);
    }

    [Fact]
    public void Decoder_RejectsPaffFieldPictureWithParameterizedError()
    {
        byte[] bitstream = BuildInterlacedAnnexBStream(mbAdaptive: false, fieldPic: true, bottomField: false);
        var dec = new H264FrameDecoder();
        var ex = Assert.Throws<NotSupportedException>(() => dec.DecodeAllFrames(bitstream));
        Assert.Contains("field picture", ex.Message);
        Assert.Contains("bottom_field_flag=False", ex.Message);
    }

    [Fact]
    public void Decoder_RejectsPaffFramePictureWithParameterizedError()
    {
        byte[] bitstream = BuildInterlacedAnnexBStream(mbAdaptive: false, fieldPic: false, bottomField: false);
        var dec = new H264FrameDecoder();
        var ex = Assert.Throws<NotSupportedException>(() => dec.DecodeAllFrames(bitstream));
        Assert.Contains("PAFF frame picture", ex.Message);
    }

    // ---------- helpers ----------

    private static PictureParameterSet BuildMinimalPps() => new PictureParameterSet
    {
        PicParameterSetId = 0,
        SeqParameterSetId = 0,
        EntropyCodingModeFlag = false,
        BottomFieldPicOrderInFramePresentFlag = false,
        NumSliceGroupsMinus1 = 0,
        NumRefIdxL0DefaultActiveMinus1 = 0,
        NumRefIdxL1DefaultActiveMinus1 = 0,
        WeightedPredFlag = false,
        WeightedBipredIdc = 0,
        PicInitQpMinus26 = 0,
        PicInitQsMinus26 = 0,
        ChromaQpIndexOffset = 0,
        DeblockingFilterControlPresentFlag = false,
        ConstrainedIntraPredFlag = false,
        RedundantPicCntPresentFlag = false,
        Transform8x8ModeFlag = false,
        SecondChromaQpIndexOffset = 0,
    };

    /// <summary>Build an Annex-B bitstream carrying just enough NAL units (SPS, PPS, SliceIdr) to
    /// drive the decoder past slice-header parse and into the interlaced rejection gate.</summary>
    private static byte[] BuildInterlacedAnnexBStream(bool mbAdaptive, bool fieldPic, bool bottomField)
    {
        byte[] spsRbsp = BuildMinimalSps(frameMbsOnly: false, mbAdaptive: mbAdaptive);
        byte[] ppsRbsp = BuildMinimalPpsRbsp();
        var sps = SequenceParameterSet.Parse(spsRbsp);
        // Parse PPS bytes once to get the actual PictureParameterSet that the slice header expects.
        var pps = PictureParameterSet.Parse(ppsRbsp);
        byte[] sliceRbsp = BuildMinimalIdrSliceRbsp(sps, pps, fieldPic, bottomField);

        var ms = new MemoryStream();
        WriteAnnexB(ms, NalUnitType.Sps, nalRefIdc: 3, spsRbsp);
        WriteAnnexB(ms, NalUnitType.Pps, nalRefIdc: 3, ppsRbsp);
        WriteAnnexB(ms, NalUnitType.SliceIdr, nalRefIdc: 3, sliceRbsp);
        return ms.ToArray();
    }

    private static byte[] BuildMinimalPpsRbsp()
    {
        // pps_id=0 (ue "1"), sps_id=0 (ue "1"), entropy_coding=0, bottom_field_poc=0,
        // num_slice_groups_minus1=0 (ue "1"), num_ref_l0=0 (ue "1"), num_ref_l1=0 (ue "1"),
        // weighted_pred=0, weighted_bipred=00, pic_init_qp_minus26=0 (se "1"),
        // pic_init_qs_minus26=0 (se "1"), chroma_qp_offset=0 (se "1"),
        // deblocking_filter_control=0, constrained_intra=0, redundant_pic_cnt=0, trailing "1" + pad.
        var bw = new BitWriter();
        bw.WriteUe(0); bw.WriteUe(0);          // ids
        bw.WriteBit(0); bw.WriteBit(0);         // entropy, bottom_field_poc
        bw.WriteUe(0);                          // num_slice_groups_minus1
        bw.WriteUe(0); bw.WriteUe(0);           // num_ref l0/l1
        bw.WriteBit(0); bw.WriteUBits(0, 2);    // weighted_pred, weighted_bipred_idc
        bw.WriteSe(0); bw.WriteSe(0); bw.WriteSe(0); // pic_init qp/qs/chroma_qp_offset
        bw.WriteBit(0); bw.WriteBit(0); bw.WriteBit(0); // deblocking control, constrained, redundant
        bw.WriteTrailing();
        return bw.ToArray();
    }

    private static void WriteAnnexB(Stream s, NalUnitType type, byte nalRefIdc, byte[] rbsp)
    {
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(1);
        s.WriteByte((byte)(((nalRefIdc & 3) << 5) | (byte)type));
        // Naive emulation-prevention insertion: any 00 00 00|01|02|03 in rbsp needs a 03 inserted.
        int zeros = 0;
        for (int i = 0; i < rbsp.Length; i++)
        {
            byte b = rbsp[i];
            if (zeros >= 2 && b <= 3) { s.WriteByte(3); zeros = 0; }
            s.WriteByte(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
    }

    /// <summary>Minimal MSB-first bit writer for RBSP construction. Test-internal.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private byte _cur;
        private int _bitsInCur;

        public void WriteBit(int b)
        {
            _cur = (byte)((_cur << 1) | (b & 1));
            _bitsInCur++;
            if (_bitsInCur == 8) { _bytes.Add(_cur); _cur = 0; _bitsInCur = 0; }
        }

        public void WriteUBits(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--) WriteBit((int)((value >> i) & 1));
        }

        public void WriteUe(uint value)
        {
            // Exp-Golomb unsigned: encode (value+1) with leading zeros.
            uint v = value + 1;
            int leadingZeros = 0;
            uint t = v;
            while (t > 1) { t >>= 1; leadingZeros++; }
            for (int i = 0; i < leadingZeros; i++) WriteBit(0);
            for (int i = leadingZeros; i >= 0; i--) WriteBit((int)((v >> i) & 1));
        }

        public void WriteSe(int value)
        {
            uint mapped = value <= 0 ? (uint)(-2 * value) : (uint)(2 * value - 1);
            WriteUe(mapped);
        }

        /// <summary>Append rbsp_trailing_bits: a '1' bit followed by zero bits to the next byte boundary.</summary>
        public void WriteTrailing()
        {
            WriteBit(1);
            while (_bitsInCur != 0) WriteBit(0);
        }

        public byte[] ToArray()
        {
            if (_bitsInCur != 0)
            {
                // Pad with zeros to byte boundary (caller should have invoked WriteTrailing).
                while (_bitsInCur != 0) WriteBit(0);
            }
            return _bytes.ToArray();
        }
    }
}
