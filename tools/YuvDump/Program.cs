using H264Decoder;
using H264Decoder.Picture;

// Decode an MP4/AnnexB file and dump all (or selected) decoded frames as
// concatenated planar YUV 4:2:0 (Y then U then V) cropped to display size.
//
// Usage:
//   dotnet run --project tools/YuvDump -- <input> <output.yuv> [--max N]
//
// Output frames are written in decode order (matches OpenH264 h264dec output).

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: YuvDump <input> <output.yuv> [--max N]");
    return 1;
}

string inPath = args[0];
string outPath = args[1];
int max = int.MaxValue;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--max") max = int.Parse(args[i + 1]);
}

byte[] data = File.ReadAllBytes(inPath);
var decoder = new H264FrameDecoder();
List<DecodedPicture> frames = decoder.DecodeAllFrames(data);

// Sort to decode order so byte offsets match OpenH264 raw YUV output.
var ordered = frames.OrderBy(f => f.DecodeOrderIndex).ToList();
int n = Math.Min(ordered.Count, max);

using var fs = File.Create(outPath);
for (int i = 0; i < n; i++)
{
    Yuv420Frame.Write(ordered[i], fs);
}
fs.Flush();
Console.Error.WriteLine($"wrote {n} frame(s) ({ordered[0].Width}x{ordered[0].Height}) to {outPath}");
return 0;
