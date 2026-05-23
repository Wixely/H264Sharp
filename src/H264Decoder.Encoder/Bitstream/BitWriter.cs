namespace H264Decoder.Encoder.Bitstream;

/// <summary>MSB-first bit writer over a growable byte buffer. Mirror of BitReader.</summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private int _bitPos;

    public BitWriter(int initialCapacity = 256)
    {
        if (initialCapacity < 16) initialCapacity = 16;
        _buffer = new byte[initialCapacity];
        _bitPos = 0;
    }

    public int BitPosition => _bitPos;
    public int ByteLength => (_bitPos + 7) >> 3;
    public bool IsByteAligned => (_bitPos & 7) == 0;

    public void WriteBit(uint bit)
    {
        EnsureCapacityForBits(1);
        int byteIndex = _bitPos >> 3;
        int bitInByte = 7 - (_bitPos & 7);
        if ((bit & 1) != 0)
        {
            _buffer[byteIndex] |= (byte)(1 << bitInByte);
        }
        _bitPos++;
    }

    public void WriteBits(uint value, int n)
    {
        if ((uint)n > 32u) throw new ArgumentOutOfRangeException(nameof(n));
        for (int i = n - 1; i >= 0; i--)
        {
            WriteBit((value >> i) & 1u);
        }
    }

    public void WriteBitsLong(ulong value, int n)
    {
        if ((uint)n > 64u) throw new ArgumentOutOfRangeException(nameof(n));
        for (int i = n - 1; i >= 0; i--)
        {
            WriteBit((uint)((value >> i) & 1u));
        }
    }

    /// <summary>Append rbsp_trailing_bits: a single 1 bit then zero-fill to the next byte boundary.</summary>
    public void WriteRbspTrailingBits()
    {
        WriteBit(1);
        while (!IsByteAligned) WriteBit(0);
    }

    /// <summary>Return the bit-stream as a byte array, padded to the next byte boundary with zeros.</summary>
    public byte[] ToByteArray()
    {
        int len = ByteLength;
        var result = new byte[len];
        if (len > 0) Array.Copy(_buffer, result, len);
        return result;
    }

    private void EnsureCapacityForBits(int extraBits)
    {
        int neededBytes = (_bitPos + extraBits + 7) >> 3;
        if (neededBytes <= _buffer.Length) return;
        int newCap = _buffer.Length;
        while (newCap < neededBytes) newCap *= 2;
        var newBuf = new byte[newCap];
        Array.Copy(_buffer, newBuf, _buffer.Length);
        _buffer = newBuf;
    }
}
