using H264Sharp.Decoder;

namespace H264Sharp.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void DecoderConstructs()
    {
        _ = new H264FrameDecoder();
    }
}
