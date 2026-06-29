using System.Buffers.Binary;
using System.IO.Compression;

namespace H264Sharp.Decoder.Picture;

/// <summary>
/// Minimal pure-managed PNG encoder for 24-bit RGB images.
/// Uses System.IO.Compression.ZLibStream for the DEFLATE/zlib wrapper and a
/// table-based CRC32 for chunk integrity. No external dependencies; AOT-safe.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] _signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Encode a 24-bit RGB image as a PNG byte stream. <paramref name="rgb"/>
    /// must be exactly width * height * 3 bytes in interleaved R, G, B order
    /// (top-left first, row-major).
    /// </summary>
    public static byte[] EncodeRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
        if (rgb.Length != width * height * 3) throw new ArgumentException("rgb length does not match width*height*3");

        using var ms = new MemoryStream();
        ms.Write(_signature, 0, _signature.Length);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type = truecolor (RGB)
        ihdr[10] = 0; // compression method = deflate
        ihdr[11] = 0; // filter method = adaptive (5 filter types, but we use 0 only)
        ihdr[12] = 0; // interlace = none
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT: scanlines prefixed by filter type 0 (none), zlib-compressed.
        int rowBytes = width * 3;
        byte[] filtered = new byte[height * (rowBytes + 1)];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            filtered[dst++] = 0;
            rgb.Slice(y * rowBytes, rowBytes).CopyTo(filtered.AsSpan(dst, rowBytes));
            dst += rowBytes;
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(filtered, 0, filtered.Length);
        }
        WriteChunk(ms, "IDAT", compressed.ToArray());

        // IEND
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);

        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        // Copy 4 ASCII chars in-place rather than allocating an Encoding result.
        header[4] = (byte)type[0];
        header[5] = (byte)type[1];
        header[6] = (byte)type[2];
        header[7] = (byte)type[3];
        stream.Write(header[..4]);
        stream.Write(header.Slice(4, 4));
        stream.Write(data);

        uint crc = Crc32Update(0xFFFFFFFFu, header.Slice(4, 4));
        crc = Crc32Update(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static readonly uint[] _crcTable = MakeCrcTable();
    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            }
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32Update(uint c, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c;
    }

}
