namespace H264Decoder.Cabac;

/// <summary>
/// H.264 arithmetic decoder (CABAC — Context-Adaptive Binary Arithmetic Coding).
/// Implements the three spec-mandated decode primitives directly per ITU-T H.264 §9.3.3:
///   - DecodeBin(ctxIdx)        — context-modelled bin, updates the context state
///   - DecodeBypass()            — equal-probability bypass bin, no context
///   - DecodeTerminate()         — terminating bin (used for end_of_slice_flag etc.)
///
/// Spec-direct implementation: reads bits one at a time during renormalization,
/// rather than OpenH264's 32-bit-at-a-time fast path. Slower, simpler.
/// </summary>
internal sealed class CabacDecoder
{
    private readonly byte[] _data;
    private int _bitPos;
    private uint _codIRange;
    private uint _codIOffset;
    public CabacContexts Contexts { get; }

    public CabacDecoder(byte[] rbsp, int startBitPos, CabacContexts contexts)
    {
        _data = rbsp;
        _bitPos = startBitPos;
        Contexts = contexts;

        // Spec §9.3.1.2: codIRange = 510, codIOffset = read 9 bits.
        _codIRange = 510;
        _codIOffset = ReadBits(9);
    }

    public int CurrentBitPos => _bitPos;

    /// <summary>Manually advance the underlying bit position (used to skip raw I_PCM bytes).</summary>
    public void AdvanceBits(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        _bitPos += n;
    }

    /// <summary>Spec §9.3.1.2: re-initialize the arithmetic engine after an I_PCM macroblock.
    /// Sets codIRange=510 and reads 9 bits for codIOffset from the current (byte-aligned) position.</summary>
    public void Reinitialize()
    {
        _codIRange = 510;
        _codIOffset = ReadBits(9);
    }

    /// <summary>Byte-align <c>_bitPos</c> by skipping bits (used before raw PCM samples).</summary>
    public void ByteAlignBits()
    {
        int rem = _bitPos & 7;
        if (rem != 0) _bitPos += 8 - rem;
    }

    /// <summary>Read a single 8-bit byte directly from the underlying bit position (no arithmetic decoding).</summary>
    public byte ReadAlignedByte()
    {
        return (byte)ReadBits(8);
    }

    private uint ReadBit()
    {
        if (_bitPos >= _data.Length * 8)
        {
            // Per spec the bitstream can be exhausted in the middle of a context update;
            // the engine treats subsequent reads as zero bits.
            return 0;
        }
        int byteIdx = _bitPos >> 3;
        int bitInByte = 7 - (_bitPos & 7);
        _bitPos++;
        return (uint)((_data[byteIdx] >> bitInByte) & 1);
    }

    private uint ReadBits(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
        return v;
    }

    /// <summary>Decode one context-modelled bin.</summary>
    public int DecodeBin(int ctxIdx)
    {
        ref var ctx = ref Contexts.Get(ctxIdx);
        byte preState = ctx.StateIdx;
        byte preMps = ctx.ValMPS;
        uint preRange = _codIRange;
        uint preOff = _codIOffset;

        uint rangeLPS = CabacTables.RangeTabLPS[ctx.StateIdx, (_codIRange >> 6) & 3];
        _codIRange -= rangeLPS;

        int binVal;
        if (_codIOffset >= _codIRange)
        {
            binVal = 1 - ctx.ValMPS;
            _codIOffset -= _codIRange;
            _codIRange = rangeLPS;
            // LPS state transition
            if (ctx.StateIdx == 0) ctx.ValMPS = (byte)(1 - ctx.ValMPS);
            ctx.StateIdx = CabacTables.TransIdxLPS[ctx.StateIdx];
        }
        else
        {
            binVal = ctx.ValMPS;
            // MPS state transition
            ctx.StateIdx = CabacTables.TransIdxMPS[ctx.StateIdx];
        }

        // Renormalize
        while (_codIRange < 256)
        {
            _codIRange <<= 1;
            _codIOffset = (_codIOffset << 1) | ReadBit();
        }
        CabacTrace.Bin(ctxIdx, preState, preMps, preRange, preOff, binVal);
        return binVal;
    }

    /// <summary>Decode one bypass (equal-probability) bin — no context update.</summary>
    public int DecodeBypass()
    {
        uint preRange = _codIRange;
        uint preOff = _codIOffset;
        _codIOffset = (_codIOffset << 1) | ReadBit();
        int binVal;
        if (_codIOffset >= _codIRange)
        {
            _codIOffset -= _codIRange;
            binVal = 1;
        }
        else
        {
            binVal = 0;
        }
        CabacTrace.Bypass(preRange, preOff, binVal);
        return binVal;
    }

    /// <summary>Decode the terminating bin. Returns 1 at end of slice (range collapses).</summary>
    public int DecodeTerminate()
    {
        uint preRange = _codIRange;
        uint preOff = _codIOffset;
        _codIRange -= 2;
        int binVal;
        if (_codIOffset >= _codIRange)
        {
            binVal = 1;
        }
        else
        {
            binVal = 0;
            // Renormalize and continue
            while (_codIRange < 256)
            {
                _codIRange <<= 1;
                _codIOffset = (_codIOffset << 1) | ReadBit();
            }
        }
        CabacTrace.Terminate(preRange, preOff, binVal);
        return binVal;
    }
}
