using H264Sharp.Decoder.Bitstream;
using H264Sharp.Decoder.Syntax;
using H264Sharp.Tests.Fixtures;

using H264Sharp.Decoder;
namespace H264Sharp.Tests.EndToEnd;

/// <summary>
/// I_PCM macroblock end-to-end tests. We synthesise a one-MB IDR slice whose only
/// MB is I_PCM, paired with the SPS/PPS extracted from the existing 16x16 single-MB
/// CAVLC fixture (Baseline) or CABAC fixture (Main).
/// </summary>
public sealed class IPcmTests
{
    [Fact]
    public void DecodeSyntheticCavlcIPcmFrame()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        byte[] container = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(container);

        NalUnit sps = nals.First(n => n.NalUnitType == NalUnitType.Sps);
        NalUnit pps = nals.First(n => n.NalUnitType == NalUnitType.Pps);

        // Distinctive luma/chroma fill so we can verify byte-exact pass-through.
        byte[] pcmLuma = new byte[256];
        byte[] pcmCb   = new byte[64];
        byte[] pcmCr   = new byte[64];
        for (int i = 0; i < 256; i++) pcmLuma[i] = (byte)(i & 0xFF);
        for (int i = 0; i < 64; i++)  pcmCb[i]   = (byte)((i * 4 + 1) & 0xFF);
        for (int i = 0; i < 64; i++)  pcmCr[i]   = (byte)((i * 4 + 2) & 0xFF);

        var spsParsed = SequenceParameterSet.Parse(sps.Rbsp.Span);
        var ppsParsed = PictureParameterSet.Parse(pps.Rbsp.Span);
        byte[] sliceRbsp = BuildIPcmSliceRbspCavlc(spsParsed, ppsParsed, pcmLuma, pcmCb, pcmCr);
        byte[] annexB = BuildAnnexB(sps, pps, sliceRbsp, isCabac: false);

        var decoder = new H264FrameDecoder();
        var pic = decoder.DecodeFirstIFrame(annexB);

        Assert.Equal(16, pic.Width);
        Assert.Equal(16, pic.Height);
        for (int i = 0; i < 256; i++) Assert.Equal(pcmLuma[i], pic.Y[i]);
        for (int i = 0; i < 64; i++)  Assert.Equal(pcmCb[i],   pic.U[i]);
        for (int i = 0; i < 64; i++)  Assert.Equal(pcmCr[i],   pic.V[i]);

        Assert.NotNull(decoder.LastMacroblocks);
        Assert.True(decoder.LastMacroblocks![0].IsPcm);
    }

    [Fact]
    public void DecodeSyntheticCabacIPcmFrame()
    {
        var sample = FfmpegFixture.TwoFramesIdentical16x16Cabac();
        byte[] container = File.ReadAllBytes(sample.H264Path);
        List<NalUnit> nals = AnnexBReader.SplitNalUnits(container);

        NalUnit sps = nals.First(n => n.NalUnitType == NalUnitType.Sps);
        NalUnit pps = nals.First(n => n.NalUnitType == NalUnitType.Pps);

        byte[] pcmLuma = new byte[256];
        byte[] pcmCb   = new byte[64];
        byte[] pcmCr   = new byte[64];
        for (int i = 0; i < 256; i++) pcmLuma[i] = (byte)((i * 7 + 5) & 0xFF);
        for (int i = 0; i < 64; i++)  pcmCb[i]   = (byte)((i * 3 + 11) & 0xFF);
        for (int i = 0; i < 64; i++)  pcmCr[i]   = (byte)((i * 5 + 17) & 0xFF);

        var spsParsed = SequenceParameterSet.Parse(sps.Rbsp.Span);
        var ppsParsed = PictureParameterSet.Parse(pps.Rbsp.Span);
        byte[] sliceRbsp = BuildIPcmSliceRbspCabac(spsParsed, ppsParsed, pcmLuma, pcmCb, pcmCr);
        byte[] annexB = BuildAnnexB(sps, pps, sliceRbsp, isCabac: true);

        var decoder = new H264FrameDecoder();
        var pic = decoder.DecodeFirstIFrame(annexB);

        Assert.Equal(16, pic.Width);
        Assert.Equal(16, pic.Height);
        for (int i = 0; i < 256; i++) Assert.Equal(pcmLuma[i], pic.Y[i]);
        for (int i = 0; i < 64; i++)  Assert.Equal(pcmCb[i],   pic.U[i]);
        for (int i = 0; i < 64; i++)  Assert.Equal(pcmCr[i],   pic.V[i]);

        Assert.NotNull(decoder.LastMacroblocks);
        Assert.True(decoder.LastMacroblocks![0].IsPcm);
    }

    // -----------------------------------------------------------------
    // Bitstream construction helpers
    // -----------------------------------------------------------------

    /// <summary>Build IDR slice RBSP for a 1-MB CAVLC I-slice whose sole MB is I_PCM.</summary>
    private static byte[] BuildIPcmSliceRbspCavlc(SequenceParameterSet sps, PictureParameterSet pps,
        byte[] pcmLuma, byte[] pcmCb, byte[] pcmCr)
    {
        var bw = new BitWriter();
        WriteSliceHeader(bw, sps, pps, sliceTypeRaw: 7 /* I-slice "all same type" */, isIdr: true);
        // mb_type ue(v) = 25 (I_PCM in I-slice).
        WriteUe(bw, 25);
        // pcm_alignment_zero_bit loop — already byte-aligned after a freshly written ue(25) here?
        // Not necessarily — align to byte boundary now.
        bw.ByteAlign();
        bw.WriteBytes(pcmLuma);
        bw.WriteBytes(pcmCb);
        bw.WriteBytes(pcmCr);
        // rbsp_trailing_bits: one 1-bit then zero-pad to byte boundary.
        bw.WriteBit(1);
        bw.ByteAlign();
        return bw.ToArray();
    }

    /// <summary>Build IDR slice RBSP for a 1-MB CABAC I-slice whose sole MB is I_PCM.</summary>
    private static byte[] BuildIPcmSliceRbspCabac(SequenceParameterSet sps, PictureParameterSet pps,
        byte[] pcmLuma, byte[] pcmCb, byte[] pcmCr)
    {
        var bw = new BitWriter();
        WriteSliceHeader(bw, sps, pps, sliceTypeRaw: 7, isIdr: true);
        // cabac_alignment_one_bit: pad with 1-bits to byte boundary.
        while ((bw.BitPos & 7) != 0) bw.WriteBit(1);

        // CABAC payload for one I_PCM MB. Easiest path: drive the CABAC encoder by reproducing
        // the bins the spec mandates for I_PCM mb_type, then append raw PCM bytes plus a
        // 9-bit re-init "cabac_zero_word" followed by end-of-slice terminate.
        //
        // We instead directly synthesise the arithmetic-coded prefix by hand. The decode path
        // we care about: DecodeMbTypeI reads bin0 (ctx 3+condA+condB) — must be 1, then
        // DecodeTerminate — must be 1 (I_PCM signal). The minimal bit-string that produces
        // (bin0=1, terminate=1) given the initial state (codIRange=510, codIOffset=initial 9 bits)
        // is non-trivial to construct without a real CABAC encoder, so we cheat: use a
        // helper CabacEncoder that mirrors the decoder.
        var enc = new TestCabacEncoder();
        // Initial CABAC contexts are configured by the decoder; here we just emit:
        //   bin0=1, terminate=1
        // The encoder mirrors DecodeMbTypeI's first two operations exactly.
        enc.EncodeBinNoCtx(1);  // bin0 — context state is initialised to a known MPS=0 in default
        enc.EncodeTerminateBin(1);
        // After terminate=1, decoder calls ByteAlignBits + reads raw bytes + Reinitialize.
        enc.FlushBeforePcm(bw); // writes the arithmetic prefix into bw
        bw.ByteAlign();
        bw.WriteBytes(pcmLuma);
        bw.WriteBytes(pcmCb);
        bw.WriteBytes(pcmCr);
        // After PCM, decoder calls Reinitialize which reads 9 bits to fill codIOffset.
        // It then expects DecodeTerminate() on the slice-end check. Provide 9 bits whose
        // value makes codIOffset >= codIRange-2 (i.e., terminate=1 → end-of-slice).
        // codIRange after Reinitialize = 510, so DecodeTerminate subtracts 2 → range=508.
        // We need codIOffset >= 508. codIOffset is the top 9 bits read.
        // 9-bit value 511 (0b111111111) gives codIOffset=511 → >= 508 → terminate=1.
        // We additionally need bytes available for any peek-ahead; provide extra padding.
        bw.WriteBits(0b111111111, 9);
        bw.WriteBit(1); // rbsp_stop_one_bit
        bw.ByteAlign();
        // Pad with a few extra zero bytes for safety (CABAC engine may peek past end).
        bw.WriteBytes(new byte[] { 0, 0 });
        return bw.ToArray();
    }

    /// <summary>Slice header for an IDR I-slice (CAVLC or CABAC indifferently — entropy mode
    /// flag is signalled in PPS). Layout follows spec §7.3.3 / matches what SliceHeader.Parse
    /// expects under the SPS/PPS produced by the SingleRed fixture (frame_num=4 bits,
    /// pic_order_cnt_lsb=4 bits, no FMO, no weighted_pred, deblock=disabled).</summary>
    private static void WriteSliceHeader(BitWriter bw, SequenceParameterSet sps, PictureParameterSet pps,
        uint sliceTypeRaw, bool isIdr)
    {
        WriteUe(bw, 0);                       // first_mb_in_slice
        WriteUe(bw, sliceTypeRaw);            // slice_type (7 = I, all same)
        WriteUe(bw, 0);                       // pic_parameter_set_id
        int frameNumBits = (int)sps.Log2MaxFrameNumMinus4 + 4;
        bw.WriteBits(0, frameNumBits);        // frame_num
        if (isIdr) WriteUe(bw, 0);            // idr_pic_id
        if (sps.PicOrderCntType == 0)
        {
            int pocBits = (int)sps.Log2MaxPicOrderCntLsbMinus4 + 4;
            bw.WriteBits(0, pocBits);
            // pps.BottomFieldPicOrderInFramePresentFlag: skip (typically false for baseline).
            if (pps.BottomFieldPicOrderInFramePresentFlag) WriteSe(bw, 0);
        }
        if (pps.RedundantPicCntPresentFlag) WriteUe(bw, 0);
        // dec_ref_pic_marking — IDR variant: 2 bits (nal_ref_idc != 0).
        bw.WriteBit(0); // no_output_of_prior_pics_flag
        bw.WriteBit(0); // long_term_reference_flag
        WriteSe(bw, 0);                       // slice_qp_delta = 0 → SliceQpY = 26 + PicInitQpMinus26
        if (pps.DeblockingFilterControlPresentFlag)
        {
            WriteUe(bw, 1);                   // disable_deblocking_filter_idc = 1 (no deblocking)
        }
    }

    private static byte[] BuildAnnexB(NalUnit sps, NalUnit pps, byte[] sliceRbsp, bool isCabac)
    {
        // Annex-B: [00 00 00 01] [nalU header byte] [rbsp]
        // sps/pps come in as parsed NAL units (header already stripped). Rewrap them.
        using var ms = new MemoryStream();
        WriteAnnexBNal(ms, MakeNalHeader(3, NalUnitType.Sps), sps.Rbsp.Span);
        WriteAnnexBNal(ms, MakeNalHeader(3, NalUnitType.Pps), pps.Rbsp.Span);
        WriteAnnexBNal(ms, MakeNalHeader(3, NalUnitType.SliceIdr), sliceRbsp);
        _ = isCabac;
        return ms.ToArray();
    }

    private static byte MakeNalHeader(byte refIdc, NalUnitType type)
        => (byte)((refIdc << 5) | (int)type);

    private static void WriteAnnexBNal(MemoryStream ms, byte header, ReadOnlySpan<byte> rbsp)
    {
        ms.Write(new byte[] { 0, 0, 0, 1 });
        ms.WriteByte(header);
        // Apply emulation prevention: insert 0x03 wherever we'd otherwise have 0x000000/01/02/03.
        int zeroRun = 0;
        for (int i = 0; i < rbsp.Length; i++)
        {
            byte b = rbsp[i];
            if (zeroRun == 2 && b <= 0x03)
            {
                ms.WriteByte(0x03);
                zeroRun = 0;
            }
            ms.WriteByte(b);
            zeroRun = b == 0 ? zeroRun + 1 : 0;
        }
    }

    // -----------------------------------------------------------------
    // Minimal bit/ue/se writer
    // -----------------------------------------------------------------
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _curByte;
        private int _bitInByte; // 0..7, 0 = MSB
        public int BitPos => _bytes.Count * 8 + _bitInByte;

        public void WriteBit(int v)
        {
            _curByte |= (v & 1) << (7 - _bitInByte);
            _bitInByte++;
            if (_bitInByte == 8)
            {
                _bytes.Add((byte)_curByte);
                _curByte = 0;
                _bitInByte = 0;
            }
        }
        public void WriteBits(uint v, int n)
        {
            for (int i = n - 1; i >= 0; i--) WriteBit((int)((v >> i) & 1));
        }
        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            // Requires byte alignment.
            if (_bitInByte != 0) throw new InvalidOperationException("WriteBytes requires byte alignment");
            foreach (byte b in data) _bytes.Add(b);
        }
        public void ByteAlign()
        {
            while (_bitInByte != 0) WriteBit(0);
        }
        public byte[] ToArray()
        {
            if (_bitInByte != 0) _bytes.Add((byte)_curByte);
            return _bytes.ToArray();
        }
    }

    private static void WriteUe(BitWriter bw, uint codeNum)
    {
        // Compute leading zero count L such that 2^L <= codeNum+1 < 2^(L+1).
        uint v = codeNum + 1;
        int L = 0;
        while ((1u << (L + 1)) <= v) L++;
        for (int i = 0; i < L; i++) bw.WriteBit(0);
        bw.WriteBit(1);
        if (L > 0) bw.WriteBits(v - (1u << L), L);
    }

    private static void WriteSe(BitWriter bw, int v)
    {
        uint codeNum = v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1);
        WriteUe(bw, codeNum);
    }

    // -----------------------------------------------------------------
    // Minimal CABAC encoder helper (used only for the 2-bin I_PCM prefix:
    // mb_type bin0=1 and terminate=1). Implementing a full CABAC encoder
    // is overkill for this; we leverage a known fact:
    //
    // For the very first MB of an I-slice with no left/top neighbours, the
    // mb_type context (ctxIdx=3) is initialized from the m,n table. With
    // SliceQp=26 and the standard init formula, ctxIdx=3 starts with
    // (state=0..63, MPS=0 or 1) depending on the init table — but we can
    // sidestep all of that by emitting a long-enough bypass-like prefix that
    // produces the desired bins regardless of the specific state.
    //
    // The cleanest trick: we drive a *real* CabacDecoder instance with a
    // mock bit-stream, recording each bit the decoder peeks until both bins
    // (DecodeBin(3+0+0)=1 and DecodeTerminate()=1) have been produced.
    // This guarantees the prefix is exactly what the decoder expects.
    // -----------------------------------------------------------------
    private sealed class TestCabacEncoder
    {
        // Bits we will append to the bitstream after this prefix, in order.
        public void EncodeBinNoCtx(int desired) { _binsToProduce.Add(("ctx3", desired)); }
        public void EncodeTerminateBin(int desired) { _binsToProduce.Add(("term", desired)); }

        private readonly List<(string kind, int val)> _binsToProduce = new();

        public void FlushBeforePcm(BitWriter bw)
        {
            // Search over short bit-strings until we find one that, when fed to a
            // fresh CABAC decoder, produces the requested bin sequence. The first
            // I-slice MB uses ctxIdx = 3 + 0 + 0 = 3 (no neighbours).
            //
            // Brute-force search bounded to 64-bit prefixes is more than enough.
            for (int nbits = 9; nbits <= 64; nbits++)
            {
                for (ulong candidate = 0; candidate < (1ul << Math.Min(nbits, 24)); candidate++)
                {
                    if (TryProduce(candidate, nbits, out _))
                    {
                        for (int i = nbits - 1; i >= 0; i--)
                            bw.WriteBit((int)((candidate >> i) & 1));
                        return;
                    }
                }
            }
            throw new InvalidOperationException("CABAC prefix search failed");
        }

        /// <summary>Feeds <paramref name="candidate"/> (left-aligned to <paramref name="nbits"/>)
        /// to a fresh CABAC decoder configured with the I-slice contexts for slice QP=26.</summary>
        private bool TryProduce(ulong candidate, int nbits, out int usedBits)
        {
            usedBits = nbits;
            // Build a byte buffer with candidate's bits in MSB-first order.
            int byteLen = (nbits + 7) / 8 + 4;
            byte[] buf = new byte[byteLen];
            for (int i = 0; i < nbits; i++)
            {
                int bit = (int)((candidate >> (nbits - 1 - i)) & 1);
                buf[i >> 3] |= (byte)(bit << (7 - (i & 7)));
            }

            var contexts = new H264Sharp.Decoder.Cabac.CabacContexts(H264Sharp.Decoder.Cabac.CabacInitTable.ContextCount);
            int sliceQp = 26;
            for (int ctxIdx = 0; ctxIdx < H264Sharp.Decoder.Cabac.CabacInitTable.ContextCount; ctxIdx++)
            {
                sbyte m = H264Sharp.Decoder.Cabac.CabacInitTable.MN[ctxIdx, 0, 0];
                sbyte n = H264Sharp.Decoder.Cabac.CabacInitTable.MN[ctxIdx, 0, 1];
                if (m == H264Sharp.Decoder.Cabac.CabacInitTable.CtxNA) continue;
                contexts.Initialize(ctxIdx, m, n, sliceQp);
            }
            var dec = new H264Sharp.Decoder.Cabac.CabacDecoder(buf, 0, contexts);
            foreach (var (kind, want) in _binsToProduce)
            {
                int got = kind == "ctx3" ? dec.DecodeBin(3) : dec.DecodeTerminate();
                if (got != want) return false;
            }
            // The decoder must not have looked past the nbits we wrote — otherwise the
            // outcome depends on the trailing zero padding (which won't be there in the
            // real bitstream where PCM bytes follow).
            return dec.CurrentBitPos <= nbits;
        }
    }
}
