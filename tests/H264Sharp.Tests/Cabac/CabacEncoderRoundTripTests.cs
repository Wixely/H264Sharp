using H264Sharp.Decoder.Cabac;

namespace H264Sharp.Tests.Cabac;

/// <summary>
/// Sanity tests for the test-only <see cref="CabacEncoder"/>. If these fail, every
/// other round-trip test in this folder is meaningless — start debugging here.
/// </summary>
public class CabacEncoderRoundTripTests
{
    private const int SliceQp = 18;

    private static CabacContexts MakeContexts()
    {
        var ctx = new CabacContexts(CabacInitTable.ContextCount);
        for (int i = 0; i < CabacInitTable.ContextCount; i++)
        {
            sbyte m = CabacInitTable.MN[i, 0, 0];
            sbyte n = CabacInitTable.MN[i, 0, 1];
            if (m == CabacInitTable.CtxNA) continue;
            ctx.Initialize(i, m, n, SliceQp);
        }
        return ctx;
    }

    [Fact]
    public void RoundTrip_SingleContextBin_Zero()
    {
        var enc = new CabacEncoder(MakeContexts());
        enc.EncodeBin(0, 0);
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        Assert.Equal(0, dec.DecodeBin(0));
    }

    [Fact]
    public void RoundTrip_SingleContextBin_One()
    {
        var enc = new CabacEncoder(MakeContexts());
        enc.EncodeBin(0, 1);
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        Assert.Equal(1, dec.DecodeBin(0));
    }

    [Fact]
    public void RoundTrip_BypassBins()
    {
        var enc = new CabacEncoder(MakeContexts());
        int[] pattern = { 1, 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0 };
        foreach (var b in pattern) enc.EncodeBypass(b);
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        foreach (var b in pattern) Assert.Equal(b, dec.DecodeBypass());
    }

    [Fact]
    public void RoundTrip_MixedContextAndBypass()
    {
        var enc = new CabacEncoder(MakeContexts());
        // mix several contexts + bypass bins
        int[] ctxs = { 0, 1, 2, 3, 0, 1, 2, 3, 3, 2, 1, 0 };
        int[] bins = { 1, 0, 1, 1, 0, 1, 0, 1, 1, 0, 0, 1 };
        for (int i = 0; i < ctxs.Length; i++)
        {
            enc.EncodeBin(ctxs[i], bins[i]);
            enc.EncodeBypass(bins[i] ^ 1);
        }
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        for (int i = 0; i < ctxs.Length; i++)
        {
            Assert.Equal(bins[i], dec.DecodeBin(ctxs[i]));
            Assert.Equal(bins[i] ^ 1, dec.DecodeBypass());
        }
    }

    [Fact]
    public void RoundTrip_LongRunOfSameBin()
    {
        // Many MPS in a row stresses renormalization + bitsOutstanding chain.
        var enc = new CabacEncoder(MakeContexts());
        for (int i = 0; i < 200; i++) enc.EncodeBin(0, 1);
        for (int i = 0; i < 200; i++) enc.EncodeBin(0, 0);
        enc.EncodeTerminate(1);
        byte[] bytes = enc.Finish();

        var dec = new CabacDecoder(bytes, 0, MakeContexts());
        for (int i = 0; i < 200; i++) Assert.Equal(1, dec.DecodeBin(0));
        for (int i = 0; i < 200; i++) Assert.Equal(0, dec.DecodeBin(0));
    }
}
