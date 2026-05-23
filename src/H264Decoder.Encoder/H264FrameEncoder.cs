using H264Decoder.Bitstream;
using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Mode;
using H264Decoder.Encoder.Syntax;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder;

/// <summary>Top-level H.264 encoder: takes YUV 4:2:0 frames and produces Baseline-profile
/// I-frame-only Annex-B byte streams. CAVLC entropy, 4x4 transform only, fixed QP, no
/// deblocking, no inter prediction. Output is decodable by our existing H264FrameDecoder.</summary>
public static class H264FrameEncoder
{
    /// <summary>Encode a sequence of raw YUV 4:2:0 frames (planar Y then U then V, 8-bit)
    /// into an Annex-B H.264 byte stream.</summary>
    /// <param name="yuv">All frames concatenated: each frame is W*H Y, then (W/2)*(H/2) U, then V.</param>
    /// <param name="width">Display width.</param>
    /// <param name="height">Display height.</param>
    /// <param name="qp">Fixed quantization parameter (0..51).</param>
    /// <param name="frames">Number of frames in <paramref name="yuv"/>.</param>
    public static byte[] EncodeAnnexB(ReadOnlySpan<byte> yuv, int width, int height, int qp, int frames = 1)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("invalid frame size");
        if (qp < 0 || qp > 51) throw new ArgumentException("qp must be in [0, 51]");
        if (frames <= 0) throw new ArgumentException("frames must be > 0");

        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline();

        byte[] spsRbsp = SpsWriter.Serialize(sps);
        byte[] ppsRbsp = PpsWriter.Serialize(pps);

        var output = new MemoryStream();
        // SPS NAL (nal_ref_idc = 3, type = 7)
        byte[] spsNal = AnnexBWriter.BuildNalUnit(NalUnitType.Sps, nalRefIdc: 3, spsRbsp);
        byte[] ppsNal = AnnexBWriter.BuildNalUnit(NalUnitType.Pps, nalRefIdc: 3, ppsRbsp);
        AnnexBWriter.WriteAnnexB(output, new[] { spsNal, ppsNal });

        int picWidthInMbs = (int)(sps.PicWidthInMbsMinus1 + 1);
        int picHeightInMbs = (int)(sps.PicHeightInMapUnitsMinus1 + 1);
        int bufferWidth = picWidthInMbs * 16;
        int bufferHeight = picHeightInMbs * 16;
        int bufferChromaWidth = bufferWidth / 2;
        int bufferChromaHeight = bufferHeight / 2;

        int frameBytes = width * height + 2 * (width / 2) * (height / 2);
        if (yuv.Length < frameBytes * frames)
            throw new ArgumentException(
                $"yuv buffer too small: expected {frameBytes * frames}, got {yuv.Length}");

        for (int frameIdx = 0; frameIdx < frames; frameIdx++)
        {
            ReadOnlySpan<byte> frame = yuv.Slice(frameIdx * frameBytes, frameBytes);
            byte[] sliceNal = EncodeIFrame(frame, width, height, qp,
                picWidthInMbs, picHeightInMbs, bufferWidth, bufferHeight,
                bufferChromaWidth, bufferChromaHeight,
                frameNum: (uint)(frameIdx & 0xF), idrPicId: (uint)frameIdx);
            AnnexBWriter.WriteAnnexB(output, new[] { sliceNal });
        }
        return output.ToArray();
    }

    private static byte[] EncodeIFrame(
        ReadOnlySpan<byte> yuv, int width, int height, int qp,
        int picWidthInMbs, int picHeightInMbs,
        int bufferWidth, int bufferHeight, int bufferChromaWidth, int bufferChromaHeight,
        uint frameNum, uint idrPicId)
    {
        // Allocate MB-aligned planes with edge padding by replicating the last row/column.
        var picY = new byte[bufferWidth * bufferHeight];
        var picU = new byte[bufferChromaWidth * bufferChromaHeight];
        var picV = new byte[bufferChromaWidth * bufferChromaHeight];
        // Source planes: just for reading (we don't write back).
        var srcY = new byte[bufferWidth * bufferHeight];
        var srcU = new byte[bufferChromaWidth * bufferChromaHeight];
        var srcV = new byte[bufferChromaWidth * bufferChromaHeight];
        // Copy with edge padding.
        int yOffset = 0;
        int uOffset = width * height;
        int vOffset = uOffset + (width / 2) * (height / 2);
        CopyPlaneWithEdgePad(yuv.Slice(yOffset, width * height), width, height, srcY, bufferWidth, bufferHeight);
        CopyPlaneWithEdgePad(yuv.Slice(uOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcU, bufferChromaWidth, bufferChromaHeight);
        CopyPlaneWithEdgePad(yuv.Slice(vOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcV, bufferChromaWidth, bufferChromaHeight);

        // Build slice header into the same BitWriter the MB layer will append to.
        var sliceWriter = new BitWriter(4096);
        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline();
        // slice_qp_delta = qp - 26 - pps.PicInitQpMinus26.
        int sliceQpDelta = qp - 26 - pps.PicInitQpMinus26;
        // disable_deblocking_filter_idc = 1 (no deblocking).
        SliceHeaderWriter.Write(sliceWriter, sps, pps,
            frameNum: frameNum, idrPicId: idrPicId,
            sliceQpDelta: sliceQpDelta, disableDeblockingFilterIdc: 1);

        int totalMbs = picWidthInMbs * picHeightInMbs;
        var mbStates = new MacroblockEncoderState?[totalMbs];

        // Encode each MB in raster order.
        for (int addr = 0; addr < totalMbs; addr++)
        {
            int mbX = addr % picWidthInMbs;
            int mbY = addr / picWidthInMbs;
            int y0 = mbY * 16;
            int x0 = mbX * 16;
            ReadOnlySpan<byte> mbSrcY = srcY.AsSpan(y0 * bufferWidth + x0);
            ReadOnlySpan<byte> mbSrcU = srcU.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            ReadOnlySpan<byte> mbSrcV = srcV.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            MacroblockEncoder.EncodeIntra16x16(
                sliceWriter,
                mbSrcY, mbSrcU, mbSrcV,
                srcStrideY: bufferWidth, srcStrideC: bufferChromaWidth,
                picY, picU, picV,
                picStrideY: bufferWidth, picStrideC: bufferChromaWidth,
                mbX, mbY, mbsPerRow: picWidthInMbs,
                qpY: qp,
                mbStates, mbAddress: addr);
        }

        // rbsp_trailing_bits + flush to bytes.
        sliceWriter.WriteRbspTrailingBits();
        byte[] sliceRbsp = sliceWriter.ToByteArray();
        return AnnexBWriter.BuildNalUnit(NalUnitType.SliceIdr, nalRefIdc: 3, sliceRbsp);
    }

    private static void CopyPlaneWithEdgePad(
        ReadOnlySpan<byte> src, int srcWidth, int srcHeight,
        byte[] dst, int dstWidth, int dstHeight)
    {
        for (int y = 0; y < dstHeight; y++)
        {
            int srcY = y < srcHeight ? y : srcHeight - 1;
            for (int x = 0; x < dstWidth; x++)
            {
                int srcX = x < srcWidth ? x : srcWidth - 1;
                dst[y * dstWidth + x] = src[srcY * srcWidth + srcX];
            }
        }
    }
}
