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
