namespace H264Sharp.Decoder.Picture;

/// <summary>
/// Single decoded YUV 4:2:0 picture. Y plane is full resolution; U/V are half in both dimensions.
/// All planes are stored row-major, contiguous, no padding.
/// </summary>
public sealed class DecodedPicture
{
    /// <summary>Displayable (cropped) luma width — what consumers should treat as the image size.</summary>
    public int Width { get; }
    /// <summary>Displayable (cropped) luma height.</summary>
    public int Height { get; }
    public int ChromaWidth => Width / 2;
    public int ChromaHeight => Height / 2;

    /// <summary>Buffer-allocated luma width (MB-aligned encoded width). Equals Width when no crop is needed.
    /// Y plane is stored at this stride; arithmetic that walks rows must use BufferWidth, not Width.</summary>
    public int BufferWidth { get; }
    /// <summary>Buffer-allocated luma height (MB-aligned encoded height).</summary>
    public int BufferHeight { get; }
    public int ChromaBufferWidth => BufferWidth / 2;
    public int ChromaBufferHeight => BufferHeight / 2;

    /// <summary>Top-left luma offset of the visible region within the encoded buffer (cropping offsets).</summary>
    public int CropLeft { get; }
    public int CropTop { get; }

    public byte[] Y { get; }
    public byte[] U { get; }
    public byte[] V { get; }

    /// <summary>frame_num of the slice that produced this picture (for DPB ordering).</summary>
    public int FrameNum { get; set; }

    /// <summary>Picture Order Count (spec §8.2.1) — display order key. Lower POC = earlier display.</summary>
    public int PicOrderCnt { get; set; }

    /// <summary>Coded-video-sequence index: incremented at each IDR (and MMCO5 reset). POC is only
    /// defined within a CVS and restarts at 0 across IDRs, so display-order sorting must group by
    /// this first, then PicOrderCnt. Otherwise frames from different GOPs interleave.</summary>
    public int CvsIndex { get; set; }

    /// <summary>0-based index of this picture in the bitstream's decode order (assigned per
    /// <see cref="H264Sharp.Decoder.H264FrameDecoder.DecodeAllFrames(System.Collections.Generic.List{H264Sharp.Decoder.Bitstream.NalUnit})"/> call).
    /// Lets callers map an MP4 sample-table index to the right entry of the POC-sorted output list.</summary>
    public int DecodeOrderIndex { get; set; }

    /// <summary>Per-MB decoded syntax/state, indexed by mbAddress. Used by spatial-direct
    /// derivation (spec §8.4.1.2.2) to access the colocated MB in refPicListL1[0].</summary>
    public Syntax.Macroblock[]? Macroblocks { get; set; }

    /// <summary>Number of MBs per row (PicWidthInMbs). Needed to map (mbX, mbY) ↔ mbAddress
    /// when this picture is queried as a colocated reference.</summary>
    public int MbsPerRow { get; set; }

    /// <summary>True when this DPB entry has been marked "used for long-term reference"
    /// (spec §8.2.5). Long-term refs survive sliding-window eviction and are addressed
    /// by <see cref="LongTermFrameIdx"/> / <see cref="LongTermPicNum"/>.</summary>
    public bool IsLongTerm { get; set; }

    /// <summary>LongTermFrameIdx assigned via MMCO op 3/6 (spec §8.2.5.4.3 / §8.2.5.4.6).</summary>
    public int LongTermFrameIdx { get; set; }

    /// <summary>LongTermPicNum used for ref-list construction (spec §8.2.4.1). For frame
    /// pictures, LongTermPicNum == LongTermFrameIdx.</summary>
    public int LongTermPicNum { get; set; }

    /// <summary>VUI parameters from the active SPS, if present. Needed by YuvToRgb to honour
    /// video_full_range_flag (yuvj420p / Apple-encoded content uses Y in [0,255])
    /// and matrix_coefficients.</summary>
    public Syntax.VuiParameters? Vui { get; set; }

    /// <summary>POCs of this picture's L0 / L1 reference lists, in list order, captured when the
    /// picture was decoded. Used by temporal direct (§8.4.1.2.3) when this picture is the colocated
    /// (L1[0]) reference: the colocated block's refIdxCol indexes these to find the referenced
    /// picture's POC, which is then matched into the current picture's L0. Null for I pictures.</summary>
    public int[]? RefListL0Pocs { get; set; }
    /// <summary>See <see cref="RefListL0Pocs"/>.</summary>
    public int[]? RefListL1Pocs { get; set; }

    public DecodedPicture(int width, int height)
        : this(width, height, width, height, 0, 0) { }

    public DecodedPicture(int croppedWidth, int croppedHeight, int bufferWidth, int bufferHeight, int cropLeft, int cropTop)
    {
        if (croppedWidth <= 0 || croppedHeight <= 0 || (croppedWidth & 1) != 0 || (croppedHeight & 1) != 0)
            throw new ArgumentException("width and height must be positive and even");
        if (bufferWidth < croppedWidth || bufferHeight < croppedHeight)
            throw new ArgumentException("buffer dimensions must be >= cropped dimensions");
        if ((bufferWidth & 1) != 0 || (bufferHeight & 1) != 0)
            throw new ArgumentException("buffer dimensions must be even (4:2:0 requires even)");
        Width = croppedWidth;
        Height = croppedHeight;
        BufferWidth = bufferWidth;
        BufferHeight = bufferHeight;
        CropLeft = cropLeft;
        CropTop = cropTop;
        Y = new byte[bufferWidth * bufferHeight];
        U = new byte[(bufferWidth / 2) * (bufferHeight / 2)];
        V = new byte[(bufferWidth / 2) * (bufferHeight / 2)];
    }
}
