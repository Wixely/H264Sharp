using System.Diagnostics;

namespace H264Decoder.Tests.Fixtures;

/// <summary>
/// Lazily generates ffmpeg-produced .h264 + reference .yuv files into a Samples/
/// subdirectory of the test assembly. Generated files are cached on disk and
/// re-used across runs.
/// </summary>
public static class FfmpegFixture
{
    private static readonly object _lock = new();

    public static string SamplesDirectory { get; } = Path.Combine(
        AppContext.BaseDirectory, "Samples");

    public static string FfmpegPath { get; } =
        Environment.GetEnvironmentVariable("FFMPEG") ?? @"C:\FFMPEG\bin\ffmpeg.exe";

    public sealed record Sample(string H264Path, string YuvPath, int Width, int Height);

    /// <summary>Single 16x16 red frame, Baseline profile, CAVLC, no B-frames, GOP=1.</summary>
    public static Sample SingleRed16x16()
    {
        const int W = 16, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "single_red_16x16.h264");
        string yuv = Path.Combine(SamplesDirectory, "single_red_16x16.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1 -frames:v 1 " +
            "-c:v libx264 -profile:v baseline -bf 0 -g 1 -coder 0 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 16x16 red clip: I-frame + identical P-frame (all P_Skip).</summary>
    public static Sample TwoFramesIdentical16x16()
    {
        const int W = 16, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_red_16x16.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_red_16x16.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1:r=2 -frames:v 2 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v baseline -bf 0 -keyint_min 99 -g 99 -coder 0 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>32x32 ffmpeg testsrc — forces x264 to pick mostly Intra_4x4 (textured).</summary>
    public static Sample Testsrc32x32()
    {
        const int W = 32, H = 32;
        string h264 = Path.Combine(SamplesDirectory, "testsrc_32x32.h264");
        string yuv = Path.Combine(SamplesDirectory, "testsrc_32x32.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i testsrc=size={W}x{H}:d=1 -frames:v 1 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v baseline -bf 0 -g 1 -coder 0 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>4-color 32x32 (2x2 MBs) — exercises multi-MB decoding with intra prediction.</summary>
    public static Sample FourQuadrants32x32()
    {
        const int W = 32, H = 32;
        string h264 = Path.Combine(SamplesDirectory, "four_quadrants_32x32.h264");
        string yuv = Path.Combine(SamplesDirectory, "four_quadrants_32x32.yuv");
        // testsrc is too detailed for an Intra_16x16-only encoder pass; use a 2x2 color grid.
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s=16x16:d=1 -f lavfi -i color=c=green:s=16x16:d=1 " +
            "-f lavfi -i color=c=blue:s=16x16:d=1 -f lavfi -i color=c=yellow:s=16x16:d=1 " +
            "-filter_complex \"[0][1]hstack=inputs=2[t];[2][3]hstack=inputs=2[b];[t][b]vstack=inputs=2\" " +
            "-frames:v 1 -c:v libx264 -profile:v baseline -bf 0 -g 1 -coder 0 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    private static void EnsureGenerated(string h264, string yuv, string h264Args, string yuvArgs)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(SamplesDirectory);
            if (!File.Exists(h264))
            {
                RunFfmpeg(h264Args);
            }
            if (!File.Exists(yuv))
            {
                RunFfmpeg(yuvArgs);
            }
        }
    }

    private static void RunFfmpeg(string args)
    {
        if (!File.Exists(FfmpegPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg not found at '{FfmpegPath}'. Set the FFMPEG env var to override.");
        }

        var psi = new ProcessStartInfo(FfmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg");
        string stderr = p.StandardError.ReadToEnd();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited {p.ExitCode}\nargs: {args}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
    }
}
