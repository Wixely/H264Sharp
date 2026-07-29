namespace H264Sharp.Decoder.Bitstream;

/// <summary>
/// Bit-level reader over an RBSP. Position is measured in bits from the start of the buffer.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public BitReader(ReadOnlySpan<byte> data)
    {
        // Bit positions are tracked in int; data.Length * 8 must not overflow. No real
        // H.264 NAL approaches 256 MiB, so reject rather than track positions in long.
        if (data.Length > int.MaxValue / 8)
        {
            throw new ArgumentException("RBSP too large for BitReader (>= 256 MiB)", nameof(data));
        }
        _data = data;
        _bitPos = 0;
    }

    public int BitPosition => _bitPos;
    public int TotalBits => _data.Length * 8;
    public int BitsRemaining => TotalBits - _bitPos;
    public bool EndOfStream => _bitPos >= TotalBits;

    public uint ReadBit()
    {
        if (_bitPos >= TotalBits)
        {
            throw new InvalidDataException("BitReader: read past end of RBSP");
        }
        int byteIndex = _bitPos >> 3;
        int bitInByte = 7 - (_bitPos & 7);
        _bitPos++;
        return (uint)((_data[byteIndex] >> bitInByte) & 1);
    }

    public uint ReadBits(int n)
    {
        if ((uint)n > 32u)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "ReadBits supports up to 32 bits");
        }
        uint result = 0;
        for (int i = 0; i < n; i++)
        {
            result = (result << 1) | ReadBit();
        }
        return result;
    }

    /// <summary>Peek the next <paramref name="n"/> bits MSB-first without advancing the cursor.
    /// If fewer than n bits remain, the missing trailing bits are padded as zero —
    /// matching the behavior of openh264's 32-bit read cache at end-of-buffer.</summary>
    public uint PeekBits(int n)
    {
        if ((uint)n > 32u)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }
        uint result = 0;
        int pos = _bitPos;
        int total = TotalBits;
        for (int i = 0; i < n; i++)
        {
            if (pos < total)
            {
                int byteIndex = pos >> 3;
                int bitInByte = 7 - (pos & 7);
                result = (result << 1) | (uint)((_data[byteIndex] >> bitInByte) & 1);
            }
            else
            {
                result <<= 1;
            }
            pos++;
        }
        return result;
    }

    public void Skip(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (_bitPos + n > TotalBits)
        {
            throw new InvalidDataException("BitReader: skip past end of RBSP");
        }
        _bitPos += n;
    }

    public void ByteAlign()
    {
        int rem = _bitPos & 7;
        if (rem != 0)
        {
            _bitPos += 8 - rem;
        }
    }

    /// <summary>
    /// Implements the normative more_rbsp_data() check (spec §7.2).
    /// Returns false if the only remaining bits are the rbsp_trailing_bits
    /// (a single 1 bit followed by 0-7 zero bits to byte-align).
    /// </summary>
    public bool MoreRbspData()
    {
        if (EndOfStream)
        {
            return false;
        }

        // Save state, peek forward, restore.
        int saved = _bitPos;
        try
        {
            // The next set bit must be the rbsp_stop_one_bit and it must lie within
            // the current byte's remaining bits — otherwise there is more data.
            int bitInByte = saved & 7;
            int bitsLeftInByte = 8 - bitInByte;
            // If the next bit is 0, there is definitely more data (stop bit would be 1).
            if (ReadBit() == 0)
            {
                return true;
            }
            // The next bit was 1. If all remaining bits in the buffer are 0, that
            // 1 was the stop bit. Otherwise there is more data.
            while (!EndOfStream)
            {
                if (ReadBit() != 0)
                {
                    return true;
                }
            }
            _ = bitsLeftInByte;
            return false;
        }
        finally
        {
            _bitPos = saved;
        }
    }
}
