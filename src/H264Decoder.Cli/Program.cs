using H264Decoder;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: H264Decoder.Cli <in.h264> <out.yuv>");
    return 1;
}

_ = new H264FrameDecoder();
Console.Error.WriteLine("decoder not yet implemented");
return 2;
