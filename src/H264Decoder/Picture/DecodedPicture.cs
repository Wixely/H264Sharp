namespace H264Decoder.Picture;

/// <summary>
/// Single decoded YUV 4:2:0 picture. Y plane is full resolution; U/V are half in both dimensions.
/// All planes are stored row-major, contiguous, no padding.
/// </summary>
public sealed class DecodedPicture
{
    public int Width { get; }
    public int Height { get; }
    public int ChromaWidth => Width / 2;
    public int ChromaHeight => Height / 2;
    public byte[] Y { get; }
    public byte[] U { get; }
    public byte[] V { get; }

    /// <summary>frame_num of the slice that produced this picture (for DPB ordering).</summary>
    public int FrameNum { get; set; }

    /// <summary>Picture Order Count (spec §8.2.1) — display order key. Lower POC = earlier display.</summary>
    public int PicOrderCnt { get; set; }

    /// <summary>0-based index of this picture in the bitstream's decode order (assigned per
    /// <see cref="H264Decoder.H264FrameDecoder.DecodeAllFrames(System.Collections.Generic.List{H264Decoder.Bitstream.NalUnit})"/> call).
    /// Lets callers map an MP4 sample-table index to the right entry of the POC-sorted output list.</summary>
    public int DecodeOrderIndex { get; set; }

    /// <summary>Per-MB decoded syntax/state, indexed by mbAddress. Used by spatial-direct
    /// derivation (spec §8.4.1.2.2) to access the colocated MB in refPicListL1[0].</summary>
    public Syntax.Macroblock[]? Macroblocks { get; set; }

    /// <summary>Number of MBs per row (PicWidthInMbs). Needed to map (mbX, mbY) ↔ mbAddress
    /// when this picture is queried as a colocated reference.</summary>
    public int MbsPerRow { get; set; }

    public DecodedPicture(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException("width and height must be positive and even");
        Width = width;
        Height = height;
        Y = new byte[width * height];
        U = new byte[ChromaWidth * ChromaHeight];
        V = new byte[ChromaWidth * ChromaHeight];
    }
}
