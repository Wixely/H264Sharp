namespace H264Sharp.Decoder.Picture;

public static class Yuv420Frame
{
    /// <summary>Write a decoded picture to a stream as planar YUV 4:2:0 (Y then U then V).
    /// Outputs the cropped (displayable) region from the encoded buffer.</summary>
    public static void Write(DecodedPicture pic, Stream output)
    {
        WritePlane(output, pic.Y, pic.BufferWidth, pic.CropLeft, pic.CropTop, pic.Width, pic.Height);
        WritePlane(output, pic.U, pic.ChromaBufferWidth, pic.CropLeft / 2, pic.CropTop / 2, pic.ChromaWidth, pic.ChromaHeight);
        WritePlane(output, pic.V, pic.ChromaBufferWidth, pic.CropLeft / 2, pic.CropTop / 2, pic.ChromaWidth, pic.ChromaHeight);
    }

    private static void WritePlane(Stream output, byte[] plane, int stride, int x0, int y0, int w, int h)
    {
        if (stride == w && x0 == 0 && y0 == 0)
        {
            output.Write(plane, 0, plane.Length);
            return;
        }
        for (int y = 0; y < h; y++)
        {
            output.Write(plane, (y0 + y) * stride + x0, w);
        }
    }
}
