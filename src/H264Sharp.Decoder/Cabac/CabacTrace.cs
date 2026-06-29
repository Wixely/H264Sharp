namespace H264Sharp.Decoder.Cabac;

/// <summary>
/// Env-gated CABAC bin-by-bin trace facility for diagnosing decoder desyncs.
///
/// Enable by setting the H264_CABAC_TRACE environment variable to a file path
/// (or "-" for stderr) before constructing a decoder. When unset (the default),
/// every method here is a no-op fast-path (one volatile read on an int flag).
///
/// The decoder calls <see cref="Bin"/>/<see cref="Bypass"/>/<see cref="Terminate"/>
/// after each engine primitive; higher-level slice parsers call <see cref="Mark"/>
/// to delimit macroblock / syntax-element boundaries.
///
/// Output format (one line each):
///   #&lt;global-bin&gt; B ctx=&lt;ctxIdx&gt; state=&lt;preState&gt; mps=&lt;preMps&gt; r=&lt;preRange&gt; o=&lt;preOff&gt; → &lt;bin&gt;
///   #&lt;global-bin&gt; Y r=&lt;preRange&gt; o=&lt;preOff&gt; → &lt;bin&gt;             (bypass)
///   #&lt;global-bin&gt; T r=&lt;preRange&gt; o=&lt;preOff&gt; → &lt;bin&gt;             (terminate)
///   # &lt;label&gt;                                                       (marker)
/// </summary>
public static class CabacTrace
{
    private static TextWriter? _writer;
    private static bool _enabled;
    private static long _binCount;
    private static readonly object _lock = new();

    /// <summary>Initialize from H264_CABAC_TRACE env var. Idempotent.</summary>
    public static void EnsureInitialized()
    {
        if (_writer != null) return;
        lock (_lock)
        {
            if (_writer != null) return;
            string? path = Environment.GetEnvironmentVariable("H264_CABAC_TRACE");
            if (string.IsNullOrEmpty(path))
            {
                _writer = TextWriter.Null;
                _enabled = false;
                return;
            }
            _writer = path == "-"
                ? Console.Error
                : new StreamWriter(File.Create(path)) { AutoFlush = false };
            _enabled = true;
        }
    }

    public static bool Enabled
    {
        get { EnsureInitialized(); return _enabled; }
    }

    public static long BinCount => _binCount;

    /// <summary>Flush + close the trace file (if any).</summary>
    public static void Flush()
    {
        if (_writer != null && _writer != TextWriter.Null && _writer != Console.Error)
        {
            lock (_lock) { _writer.Flush(); }
        }
    }

    public static void Mark(string label)
    {
        if (!_enabled) return;
        lock (_lock) { _writer!.Write("# "); _writer.WriteLine(label); }
    }

    internal static void Bin(int ctxIdx, byte preState, byte preMps, uint preRange, uint preOff, int binVal)
    {
        long n = System.Threading.Interlocked.Increment(ref _binCount);
        if (!_enabled) return;
        lock (_lock)
        {
            _writer!.Write('#'); _writer.Write(n);
            _writer.Write(" B ctx="); _writer.Write(ctxIdx);
            _writer.Write(" state="); _writer.Write(preState);
            _writer.Write(" mps="); _writer.Write(preMps);
            _writer.Write(" r="); _writer.Write(preRange);
            _writer.Write(" o="); _writer.Write(preOff);
            _writer.Write(" -> "); _writer.WriteLine(binVal);
        }
    }

    internal static void Bypass(uint preRange, uint preOff, int binVal)
    {
        long n = System.Threading.Interlocked.Increment(ref _binCount);
        if (!_enabled) return;
        lock (_lock)
        {
            _writer!.Write('#'); _writer.Write(n);
            _writer.Write(" Y r="); _writer.Write(preRange);
            _writer.Write(" o="); _writer.Write(preOff);
            _writer.Write(" -> "); _writer.WriteLine(binVal);
        }
    }

    internal static void Terminate(uint preRange, uint preOff, int binVal)
    {
        long n = System.Threading.Interlocked.Increment(ref _binCount);
        if (!_enabled) return;
        lock (_lock)
        {
            _writer!.Write('#'); _writer.Write(n);
            _writer.Write(" T r="); _writer.Write(preRange);
            _writer.Write(" o="); _writer.Write(preOff);
            _writer.Write(" -> "); _writer.WriteLine(binVal);
        }
    }
}
