using H264Sharp.Decoder.Syntax;

namespace H264Sharp.Tests.Syntax;

/// <summary>Parameter-set fields that must be rejected rather than silently accepted-and-ignored.
/// Built from hand-written RBSPs (no ffmpeg), so these run in CI.</summary>
public sealed class ParserRejectionTests
{
    private sealed class Bits
    {
        private readonly List<byte> _bytes = new();
        private int _cur, _n;
        public void Bit(int v) { _cur |= (v & 1) << (7 - _n); if (++_n == 8) { _bytes.Add((byte)_cur); _cur = 0; _n = 0; } }
        public void U(uint v, int n) { for (int i = n - 1; i >= 0; i--) Bit((int)((v >> i) & 1)); }
        public void Ue(uint codeNum) { uint v = codeNum + 1; int L = 0; while ((1u << (L + 1)) <= v) L++; for (int i = 0; i < L; i++) Bit(0); Bit(1); if (L > 0) U(v - (1u << L), L); }
        public void Se(int v) => Ue(v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));
        public byte[] ToArray() { if (_n != 0) _bytes.Add((byte)_cur); return _bytes.ToArray(); }
    }

    [Fact]
    public void Sps_Log2MaxFrameNumOutOfRange_Throws()
    {
        // Baseline SPS up to log2_max_frame_num_minus4 = 13 (spec range is [0, 12]).
        var b = new Bits();
        b.U(66, 8);                 // profile_idc = Baseline (skips the High-profile field block)
        b.U(0, 8);                  // constraint flags + reserved
        b.U(30, 8);                 // level_idc
        b.Ue(0);                    // seq_parameter_set_id
        b.Ue(13);                   // log2_max_frame_num_minus4 -> out of range
        Assert.Throws<InvalidDataException>(() => SequenceParameterSet.Parse(b.ToArray()));
    }

    [Fact]
    public void Sps_Log2MaxPocLsbOutOfRange_Throws()
    {
        var b = new Bits();
        b.U(66, 8); b.U(0, 8); b.U(30, 8);
        b.Ue(0);                    // seq_parameter_set_id
        b.Ue(0);                    // log2_max_frame_num_minus4 (valid)
        b.Ue(0);                    // pic_order_cnt_type = 0
        b.Ue(13);                   // log2_max_pic_order_cnt_lsb_minus4 -> out of range
        Assert.Throws<InvalidDataException>(() => SequenceParameterSet.Parse(b.ToArray()));
    }

    [Fact]
    public void Pps_SecondChromaQpOffsetDiffers_Throws()
    {
        // Minimal CAVLC PPS with a High-profile extension whose second_chroma_qp_index_offset
        // differs from chroma_qp_index_offset — the decoder applies one offset to both planes,
        // so a differing Cr offset must be rejected, not silently ignored.
        var b = new Bits();
        b.Ue(0);            // pic_parameter_set_id
        b.Ue(0);            // seq_parameter_set_id
        b.Bit(0);           // entropy_coding_mode_flag = CAVLC
        b.Bit(0);           // bottom_field_pic_order_in_frame_present_flag
        b.Ue(0);            // num_slice_groups_minus1
        b.Ue(0);            // num_ref_idx_l0_default_active_minus1
        b.Ue(0);            // num_ref_idx_l1_default_active_minus1
        b.Bit(0);           // weighted_pred_flag
        b.U(0, 2);          // weighted_bipred_idc
        b.Se(0);            // pic_init_qp_minus26
        b.Se(0);            // pic_init_qs_minus26
        b.Se(0);            // chroma_qp_index_offset = 0
        b.Bit(0);           // deblocking_filter_control_present_flag
        b.Bit(0);           // constrained_intra_pred_flag
        b.Bit(0);           // redundant_pic_cnt_present_flag
        // High-profile extension (present because more_rbsp_data follows):
        b.Bit(0);           // transform_8x8_mode_flag
        b.Bit(0);           // pic_scaling_matrix_present_flag
        b.Se(2);            // second_chroma_qp_index_offset = 2 (!= 0)
        b.Bit(1);           // rbsp_stop_one_bit

        Assert.Throws<NotSupportedException>(() => PictureParameterSet.Parse(b.ToArray()));
    }
}
