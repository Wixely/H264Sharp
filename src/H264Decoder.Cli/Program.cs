using H264Decoder;
using H264Decoder.Picture;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: H264Decoder.Cli <in.h264> <out.yuv|out.png>");
    return 1;
}

string inPath = args[0];
string outPath = args[1];

if (!File.Exists(inPath))
{
    Console.Error.WriteLine($"input not found: {inPath}");
    return 2;
}

byte[] stream = File.ReadAllBytes(inPath);
var decoder = new H264FrameDecoder();
DecodedPicture pic;
try
{
    pic = decoder.DecodeFirstIFrame(stream);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"decode failed: {ex.Message}");
    return 3;
}

if (outPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
{
    byte[] rgb = YuvToRgb.Convert(pic);
    byte[] png = PngEncoder.EncodeRgb(pic.Width, pic.Height, rgb);
    File.WriteAllBytes(outPath, png);
    Console.Error.WriteLine($"decoded {pic.Width}x{pic.Height} -> {outPath} ({png.Length} bytes PNG)");
}
else
{
    using (var fs = File.Create(outPath))
    {
        Yuv420Frame.Write(pic, fs);
    }
    Console.Error.WriteLine($"decoded {pic.Width}x{pic.Height} YUV 4:2:0 -> {outPath} ({new FileInfo(outPath).Length} bytes)");
}

return 0;
