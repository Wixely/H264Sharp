namespace H264Decoder.Cabac;

/// <summary>
/// Inverse of <see cref="CabacDecoder"/> — used only by unit tests to synthesize
/// known CABAC bitstreams for round-trip verification. Implements spec §9.3.4
/// (CABAC encoding, informative annex) using the standard "bits-outstanding"
/// renormalization so emitted bits are produced without carry propagation.
///
/// Not used by the runtime decoder; kept internal and test-only.
/// </summary>
internal sealed class CabacEncoder
{
    private uint _codILow;
    private uint _codIRange;
    private int _bitsOutstanding;
    private bool _firstBitFlag;
    private readonly List<byte> _bytes = new();
    private int _byteBitPos; // 0..7 — bit position inside the next byte being built
    private byte _currentByte;

    public CabacContexts Contexts { get; }

    public CabacEncoder(CabacContexts contexts)
    {
        Contexts = contexts;
        _codILow = 0;
        _codIRange = 510;
        _bitsOutstanding = 0;
        _firstBitFlag = true;
    }

    /// <summary>Spec §9.3.4.2 — context-modelled encode.</summary>
    public void EncodeBin(int ctxIdx, int binVal)
    {
        ref var ctx = ref Contexts.Get(ctxIdx);
        uint rangeLPS = CabacTables.RangeTabLPS[ctx.StateIdx, (_codIRange >> 6) & 3];
        _codIRange -= rangeLPS;
        if (binVal == ctx.ValMPS)
        {
            ctx.StateIdx = CabacTables.TransIdxMPS[ctx.StateIdx];
        }
        else
        {
            _codILow += _codIRange;
            _codIRange = rangeLPS;
            if (ctx.StateIdx == 0) ctx.ValMPS = (byte)(1 - ctx.ValMPS);
            ctx.StateIdx = CabacTables.TransIdxLPS[ctx.StateIdx];
        }
        RenormE();
    }

    /// <summary>Spec §9.3.4.4 — bypass encode (equal-probability).</summary>
    public void EncodeBypass(int binVal)
    {
        _codILow <<= 1;
        if (binVal != 0) _codILow += _codIRange;
        if (_codILow >= 1024)
        {
            PutBit(1);
            _codILow -= 1024;
        }
        else if (_codILow < 512)
        {
            PutBit(0);
        }
        else
        {
            _bitsOutstanding++;
            _codILow -= 512;
        }
    }

    /// <summary>Spec §9.3.4.5 — terminating bin encode.</summary>
    public void EncodeTerminate(int binVal)
    {
        _codIRange -= 2;
        if (binVal != 0)
        {
            _codILow += _codIRange;
            // No renorm — finalize via Finish().
        }
        else
        {
            RenormE();
        }
    }

    /// <summary>Spec §9.3.4.6 — flush encoder and return the byte-aligned bitstream.</summary>
    public byte[] Finish()
    {
        // Spec EncodeFlush: codIRange = 2; RenormE(); then emit final bits.
        _codIRange = 2;
        RenormE();
        PutBit((int)((_codILow >> 9) & 1));
        WriteBitDirect((int)((_codILow >> 8) & 1));
        WriteBitDirect(1);
        // Byte-align by padding with zeros.
        if (_byteBitPos != 0)
        {
            // Left-shift to put the written bits into the MSB side of the byte.
            _currentByte = (byte)(_currentByte << (8 - _byteBitPos));
            _bytes.Add(_currentByte);
            _currentByte = 0;
            _byteBitPos = 0;
        }
        return _bytes.ToArray();
    }

    private void RenormE()
    {
        while (_codIRange < 256)
        {
            if (_codILow < 256)
            {
                PutBit(0);
            }
            else if (_codILow >= 512)
            {
                _codILow -= 512;
                PutBit(1);
            }
            else
            {
                _codILow -= 256;
                _bitsOutstanding++;
            }
            _codIRange <<= 1;
            _codILow <<= 1;
        }
    }

    private void PutBit(int b)
    {
        // The deferred (outstanding) bits occupy earlier stream positions than `b`;
        // they resolve to (1 - b). Write them first so stream order matches.
        if (_firstBitFlag)
        {
            // Discard the first emitted bit: it is implicit in the decoder's initial codIOffset.
            _firstBitFlag = false;
        }
        else
        {
            WriteBitDirect(b);
        }
        while (_bitsOutstanding > 0)
        {
            WriteBitDirect(1 - b);
            _bitsOutstanding--;
        }
    }

    private void WriteBitDirect(int b)
    {
        _currentByte = (byte)((_currentByte << 1) | (b & 1));
        _byteBitPos++;
        if (_byteBitPos == 8)
        {
            _bytes.Add(_currentByte);
            _currentByte = 0;
            _byteBitPos = 0;
        }
    }
}
