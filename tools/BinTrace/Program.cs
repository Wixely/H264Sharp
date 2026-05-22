using H264Decoder;

// Drives the decoder with CABAC bin tracing enabled.
//
// Usage:
//   dotnet run --project tools/BinTrace -- <in.h264|in.mp4> <trace.out> [--max-frames N]
//
// Sets the H264_CABAC_TRACE env var before constructing the decoder so the
// CabacTrace facility opens the trace file. Decodes all frames so that
// multi-frame fixtures (B-pyramid) emit traces for every slice.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: BinTrace <input> <trace-output> [--max-frames N]");
    return 1;
}

string inPath = args[0];
string outPath = args[1];
int maxFrames = int.MaxValue;
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--max-frames") maxFrames = int.Parse(args[i + 1]);
}

if (!File.Exists(inPath))
{
    Console.Error.WriteLine($"input not found: {inPath}");
    return 2;
}

Environment.SetEnvironmentVariable("H264_CABAC_TRACE", outPath);
// Force re-init in case the static was touched.
H264Decoder.Cabac.CabacTrace.EnsureInitialized();

byte[] data = File.ReadAllBytes(inPath);
var decoder = new H264FrameDecoder();
try
{
    var frames = decoder.DecodeAllFrames(data);
    int count = Math.Min(frames.Count, maxFrames);
    Console.Error.WriteLine($"decoded {frames.Count} frame(s); wrote {H264Decoder.Cabac.CabacTrace.BinCount} bins to {outPath}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"decode threw after {H264Decoder.Cabac.CabacTrace.BinCount} bins: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    H264Decoder.Cabac.CabacTrace.Flush();
}
return 0;
