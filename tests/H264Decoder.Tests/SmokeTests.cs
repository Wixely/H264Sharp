using H264Decoder;

namespace H264Decoder.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void DecoderConstructs()
    {
        _ = new H264FrameDecoder();
    }
}
