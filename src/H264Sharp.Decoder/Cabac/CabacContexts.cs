namespace H264Sharp.Decoder.Cabac;

/// <summary>One CABAC context: (pStateIdx, valMPS) pair. Updated per decoded bin.</summary>
internal struct CabacContext
{
    public byte StateIdx;
    public byte ValMPS;
}

/// <summary>
/// Holds the CABAC context state for one slice. Initialized at slice start from
/// the spec init tables (using SliceQP, cabac_init_idc, slice type), then mutated
/// in place by the arithmetic decoder.
/// </summary>
internal sealed class CabacContexts
{
    private readonly CabacContext[] _ctx;
    public int Count => _ctx.Length;

    public CabacContexts(int contextCount)
    {
        _ctx = new CabacContext[contextCount];
    }

    public ref CabacContext Get(int ctxIdx) => ref _ctx[ctxIdx];

    /// <summary>
    /// Initialize one context from the spec (m, n) values per §9.3.1.1:
    ///   preCtxState = Clip3(1, 126, ((m * SliceQpY) >> 4) + n)
    ///   if preCtxState <= 63: StateIdx = 63 - preCtxState; ValMPS = 0
    ///   else: StateIdx = preCtxState - 64; ValMPS = 1
    /// </summary>
    public void Initialize(int ctxIdx, int m, int n, int sliceQp)
    {
        int pre = ((m * sliceQp) >> 4) + n;
        if (pre < 1) pre = 1;
        else if (pre > 126) pre = 126;
        if (pre <= 63)
        {
            _ctx[ctxIdx].StateIdx = (byte)(63 - pre);
            _ctx[ctxIdx].ValMPS = 0;
        }
        else
        {
            _ctx[ctxIdx].StateIdx = (byte)(pre - 64);
            _ctx[ctxIdx].ValMPS = 1;
        }
    }
}
