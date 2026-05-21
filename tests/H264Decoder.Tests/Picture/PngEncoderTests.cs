using System.Buffers.Binary;
using System.IO.Compression;
using H264Decoder.Picture;

namespace H264Decoder.Tests.Picture;

public sealed class PngEncoderTests
{
    [Fact]
    public void EncodedFile_StartsWithPngSignature()
    {
        byte[] rgb = new byte[3 * 2 * 2];
        byte[] png = PngEncoder.EncodeRgb(2, 2, rgb);
        byte[] expected = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < 8; i++) Assert.Equal(expected[i], png[i]);
    }

    [Fact]
    public void Ihdr_HasCorrectDimensionsAndColorType()
    {
        byte[] rgb = new byte[3 * 7 * 5];
        byte[] png = PngEncoder.EncodeRgb(7, 5, rgb);

        // IHDR follows the 8-byte signature: 4B length (=13) + "IHDR" + 13B data + 4B CRC
        int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4));
        Assert.Equal(13, len);
        Assert.Equal((byte)'I', png[12]);
        Assert.Equal((byte)'H', png[13]);
        Assert.Equal((byte)'D', png[14]);
        Assert.Equal((byte)'R', png[15]);

        int w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.Equal(7, w);
        Assert.Equal(5, h);
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(2, png[25]); // color type = RGB
    }

    [Fact]
    public void Idat_RoundTripsBackToOriginalPixels()
    {
        // Build a small known RGB image; encode; manually inflate; verify pixels.
        const int W = 4, H = 3;
        byte[] rgb = new byte[W * H * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 17 + 5);
        byte[] png = PngEncoder.EncodeRgb(W, H, rgb);

        // Find IDAT chunk
        int idx = 8; // skip signature
        byte[]? idatData = null;
        while (idx < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idx, 4));
            string type = System.Text.Encoding.ASCII.GetString(png, idx + 4, 4);
            if (type == "IDAT") { idatData = png[(idx + 8)..(idx + 8 + len)]; break; }
            idx += 8 + len + 4;
        }
        Assert.NotNull(idatData);

        using var msIn = new MemoryStream(idatData!);
        using var zlib = new ZLibStream(msIn, CompressionMode.Decompress);
        using var msOut = new MemoryStream();
        zlib.CopyTo(msOut);
        byte[] decoded = msOut.ToArray();

        // Per scanline: 1 byte filter type + W*3 bytes pixel data
        int row = W * 3 + 1;
        Assert.Equal(H * row, decoded.Length);
        for (int y = 0; y < H; y++)
        {
            Assert.Equal(0, decoded[y * row]); // filter = none
            for (int x = 0; x < W * 3; x++)
                Assert.Equal(rgb[y * W * 3 + x], decoded[y * row + 1 + x]);
        }
    }
}
