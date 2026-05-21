namespace H264Decoder.Picture;

public static class Yuv420Frame
{
    /// <summary>Write a decoded picture to a stream as planar YUV 4:2:0 (Y then U then V).</summary>
    public static void Write(DecodedPicture pic, Stream output)
    {
        output.Write(pic.Y, 0, pic.Y.Length);
        output.Write(pic.U, 0, pic.U.Length);
        output.Write(pic.V, 0, pic.V.Length);
    }
}
