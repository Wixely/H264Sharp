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

    /// <summary>Two-frame 128x96 testsrc clip with horizontal shift: forces x264 to use
    /// P_L0_16x16 with non-zero integer-pel motion vectors plus inter residuals.</summary>
    public static Sample TwoFramesShifted128x96()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_shifted_128x96.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_shifted_128x96.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:d=0.5:r=2[a];testsrc=size={W}x{H}:d=0.5:r=2,crop=120:96:8:0,pad={W}:{H}:0:0[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            $"-pix_fmt yuv420p -c:v libx264 -profile:v baseline -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 0 -an -qp 5 -x264-params \"no-deblock=1\" -f h264 \"{h264}\"",
            // Note: -vsync passthrough is critical — without it ffmpeg dup/drops frames to a default rate
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 128x96 testsrc with frame-2 light blur — forces x264 (qp=18,
    /// subme=7, partitions=none) to pick fractional-pel MVs across many of its
    /// P_L0_16x16 macroblocks. Exercises all 16 luma sub-pel positions and chroma
    /// 1/8-pel bilinear.</summary>
    public static Sample TwoFramesFractionalMv128x96()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_subpel_128x96.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_subpel_128x96.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:d=0.5:r=2[a];testsrc=size={W}x{H}:d=0.5:r=2,smartblur=lr=1.5[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            $"-pix_fmt yuv420p -c:v libx264 -profile:v baseline -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 0 -an -qp 18 -x264-params \"no-deblock=1:partitions=none:subme=7\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 128x96 clip exercising every P-slice partition shape:
    /// P_L0_16x16, P_L0_L0_16x8, P_L0_L0_8x16, P_8x8 and P_8x8ref0 plus the
    /// 4 sub_mb_types within P_8x8 (8x8, 8x4, 4x8, 4x4).</summary>
    public static Sample TwoFramesAllPartitions128x96()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:d=0.5:r=2[a];testsrc=size={W}x{H}:d=0.5:r=2,gblur=sigma=0.5[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            $"-pix_fmt yuv420p -c:v libx264 -profile:v baseline -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 0 -an -qp 8 -x264-params \"no-deblock=1:subme=8:partitions=all\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Same content as TwoFramesAllPartitions128x96 but muxed into an MP4 container.</summary>
    public static Sample TwoFramesAllPartitionsMp4()
    {
        const int W = 128, H = 96;
        string mp4 = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96.mp4");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96_mp4.yuv");
        EnsureGenerated(mp4, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:d=0.5:r=2[a];testsrc=size={W}x{H}:d=0.5:r=2,gblur=sigma=0.5[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            $"-pix_fmt yuv420p -c:v libx264 -profile:v baseline -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 0 -an -qp 8 -x264-params \"no-deblock=1:subme=8:partitions=all\" -movflags +faststart \"{mp4}\"",
            $"-y -i \"{mp4}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(mp4, yuv, W, H);
    }

    /// <summary>Three-frame 64x48 testsrc clip encoded with --refs 3 so the
    /// last P-frame's slice header signals num_ref_idx_l0_active_minus1=1 and
    /// the decoder must select between two reference pictures via ref_idx_l0.</summary>
    public static Sample ThreeFramesMultiRef64x48()
    {
        const int W = 64, H = 48;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_multiref_64x48.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_multiref_64x48.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i testsrc=size={W}x{H}:d=1.5:r=2 -frames:v 3 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v baseline -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 0 -an -qp 18 -x264-params \"no-deblock=1:ref=3\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 3 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 32x16 vertical-stripe pattern with a 4-pixel horizontal shift.
    /// Encoded CABAC (Main profile). Forces x264 to emit a 100% I16x16 IDR and 100%
    /// P_L0_16x16 P-frame with non-zero integer-pel motion vectors and inter residual.
    /// Stays away from Intra_4x4 (which CABAC I-slice parser doesn't yet support).</summary>
    public static Sample TwoFramesShifted128x96Cabac()
    {
        const int W = 32, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_shifted_cabac_min.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_shifted_cabac_min.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"color=s=48x16:d=0.5:r=2,geq='if(lt(mod(X\\,8)\\,4)\\,100\\,180)':128:128\" " +
            $"-filter_complex \"[0:v]crop={W}:{H}:0:0[a];[0:v]crop={W}:{H}:4:0[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            "-pix_fmt yuv420p -c:v libx264 -profile:v main -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 1 -an -qp 18 " +
            $"-x264-params \"no-deblock=1\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 16x16 textured testsrc clip, Main/CABAC, partitions=none and 8x8dct=0.
    /// Forces x264 to use only P_L0_16x16 inter MBs with no sub-MB partitions and 4x4 transform.
    /// Minimum-complexity textured P-slice CABAC reproducer for the orchestration desync.</summary>
    public static Sample TextureTestsrc16x16Cabac()
    {
        const int W = 16, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "texture_testsrc_16x16_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "texture_testsrc_16x16_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=s={W}x{H}:r=2:d=1,format=yuv420p\" -frames:v 2 " +
            "-c:v libx264 -profile:v main -bf 0 -keyint_min 99 -g 99 -coder 1 -an -qp 18 " +
            $"-x264-params \"no-deblock=1:partitions=none:8x8dct=0\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 32x32 textured testsrc clip (multi-row P-slice). Forces a mix of P_Skip
    /// and P_L0_16x16 MBs so that a P_L0_16x16 has a P_Skip top neighbor — the exact case that
    /// triggers the CABAC CBP-luma condTermFlag-from-skipped-neighbor desync.</summary>
    public static Sample TextureTestsrc32x32Cabac()
    {
        const int W = 32, H = 32;
        string h264 = Path.Combine(SamplesDirectory, "texture_testsrc_32x32_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "texture_testsrc_32x32_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=s={W}x{H}:r=2:d=1,format=yuv420p\" -frames:v 2 " +
            "-c:v libx264 -profile:v main -bf 0 -keyint_min 99 -g 99 -coder 1 -an -qp 18 " +
            $"-x264-params \"no-deblock=1:partitions=none:8x8dct=0\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Two-frame 128x96 CABAC clip exercising every P-slice partition shape (subme=8, partitions=all).</summary>
    public static Sample TwoFramesAllPartitions128x96Cabac()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_all_parts_128x96_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:d=0.5:r=2[a];testsrc=size={W}x{H}:d=0.5:r=2,gblur=sigma=0.5[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p\" " +
            $"-pix_fmt yuv420p -c:v libx264 -profile:v main -bf 0 -keyint_min 100 -g 100 -sc_threshold 0 -coder 1 -an -qp 8 -x264-params \"no-deblock=1:subme=8:partitions=p8x8:8x8dct=0\" -f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 2 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

/// <summary>Two-frame 16x16 red clip encoded with CABAC (Main profile): IDR + P_Skip.
    /// Exercises the CABAC arithmetic engine, I-slice CABAC mb_type/residual, mb_skip_flag.</summary>
    public static Sample TwoFramesIdentical16x16Cabac()
    {
        const int W = 16, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "two_frames_red_16x16_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "two_frames_red_16x16_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1:r=2 -frames:v 2 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v main -bf 0 -keyint_min 99 -g 99 -coder 1 -an " +
            $"-x264-params \"no-deblock=1\" -f h264 \"{h264}\"",
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

    /// <summary>32x32 ffmpeg testsrc encoded CABAC (Main profile, coder=1) — Intra_4x4-dominated IDR.</summary>
    public static Sample Testsrc32x32Cabac()
    {
        const int W = 32, H = 32;
        string h264 = Path.Combine(SamplesDirectory, "testsrc_32x32_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "testsrc_32x32_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i testsrc=size={W}x{H}:d=1 -frames:v 1 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v main -bf 0 -g 1 -coder 1 -an -x264-params \"no-deblock=1\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>128x96 mandelbrot encoded with High profile + 8x8dct=1 + partitions=i8x8 with CAVLC (coder=0).
    /// PPS carries transform_8x8_mode_flag=1 and most I-MBs select Intra_8x8 prediction.</summary>
    public static Sample Mandelbrot128x96HighCavlc8x8()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_cavlc_8x8dct.h264");
        string yuv = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_cavlc_8x8dct.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i mandelbrot=s={W}x{H},format=yuv420p -frames:v 1 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v high -bf 0 -g 1 -coder 0 -an -qp 10 -x264-params \"no-deblock=1:8x8dct=1:partitions=i8x8\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>128x96 mandelbrot encoded High profile + 8x8dct=1 + partitions=i8x8 with CABAC (coder=1).
    /// Mirror of Mandelbrot128x96HighCavlc8x8 with CABAC entropy coding.</summary>
    public static Sample Mandelbrot128x96HighCabac8x8()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_cabac_8x8dct.h264");
        string yuv = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_cabac_8x8dct.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i mandelbrot=s={W}x{H},format=yuv420p -frames:v 1 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v high -bf 0 -g 1 -coder 1 -an -qp 10 -x264-params \"no-deblock=1:8x8dct=1:partitions=i8x8\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>128x96 mandelbrot encoded with High profile + 8x8dct=1 + partitions=i8x8.
    /// PPS carries transform_8x8_mode_flag=1 and most I-MBs select Intra_8x8 prediction.</summary>
    public static Sample Mandelbrot128x96High8x8Dct()
    {
        const int W = 128, H = 96;
        string h264 = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_8x8dct.h264");
        string yuv = Path.Combine(SamplesDirectory, "mandelbrot_128x96_high_8x8dct.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i mandelbrot=s={W}x{H},format=yuv420p -frames:v 1 -pix_fmt yuv420p " +
            "-c:v libx264 -profile:v high -bf 0 -g 1 -coder 1 -an -qp 10 -x264-params \"no-deblock=1:8x8dct=1:partitions=i8x8\" " +
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

    /// <summary>Three-frame 64x48 clip with B-frames enabled (-bf 1), CAVLC. Display order I + B + P.
    /// Encoded with high QP so most B-MBs are B_Skip or B_Direct (small or zero residual).</summary>
    public static Sample ThreeFramesBFrames64x48Cavlc()
    {
        const int W = 64, H = 48;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_bframes_64x48_cavlc.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_bframes_64x48_cavlc.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:r=2:d=1.5,format=yuv420p\" -frames:v 3 " +
            "-c:v libx264 -profile:v main -bf 1 -keyint_min 99 -g 99 -coder 0 -an " +
            "-x264-params \"no-deblock=1\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 3 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>4-frame 64x48 CAVLC B-frame clip with -bf 2 (IBBP pattern). Exercises B-frames
    /// with multiple references including the future P-frame.</summary>
    public static Sample FourFramesBFrames64x48Cavlc()
    {
        const int W = 64, H = 48;
        string h264 = Path.Combine(SamplesDirectory, "four_frames_bframes_64x48_cavlc.h264");
        string yuv = Path.Combine(SamplesDirectory, "four_frames_bframes_64x48_cavlc.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:r=4:d=1,format=yuv420p\" -frames:v 4 " +
            "-c:v libx264 -profile:v main -bf 2 -keyint_min 99 -g 99 -coder 0 -an " +
            "-x264-params \"no-deblock=1\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 4 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Three-frame 16x16 red clip with B-frames enabled (-bf 2): IDR + P + B reorder.
    /// Used to exercise the B-slice header parser, POC computation, and L0/L1 list construction.</summary>
    public static Sample ThreeFramesBFrames16x16()
    {
        const int W = 16, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_bframes_16x16.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_bframes_16x16.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1.5:r=2,format=yuv420p -frames:v 3 " +
            "-c:v libx264 -profile:v main -bf 2 -keyint_min 99 -g 99 -coder 1 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Three-frame 32x16 CABAC B-frame clip (-bf 1). Single B between I and P.
    /// Small synthetic content keeps it within the CABAC P-parser's currently supported scope
    /// while still exercising the CABAC B mb_type / mvd / residual parsing paths.</summary>
    public static Sample ThreeFramesBFrames32x16Cabac()
    {
        const int W = 32, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_bframes_32x16_cabac.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_bframes_32x16_cabac.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1.5:r=2,format=yuv420p -frames:v 3 " +
            "-c:v libx264 -profile:v main -bf 1 -keyint_min 99 -g 99 -coder 1 -an " +
            "-x264-params \"no-deblock=1:8x8dct=0\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 3 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Same as ThreeFramesBFrames64x48Cavlc but with deblocking ENABLED — exercises
    /// the deblocking filter for P and B slices end-to-end (luma byte-exact check).</summary>
    public static Sample ThreeFramesBFrames64x48CavlcDeblock()
    {
        const int W = 64, H = 48;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_bframes_64x48_cavlc_deblock.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_bframes_64x48_cavlc_deblock.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i \"testsrc=size={W}x{H}:r=2:d=1.5,format=yuv420p\" -frames:v 3 " +
            "-c:v libx264 -profile:v main -bf 1 -keyint_min 99 -g 99 -coder 0 -an " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 3 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
        return new Sample(h264, yuv, W, H);
    }

    /// <summary>Same as ThreeFramesBFrames32x16Cabac but with deblocking ENABLED — exercises
    /// the deblocking filter for CABAC P + B slices.</summary>
    public static Sample ThreeFramesBFrames32x16CabacDeblock()
    {
        const int W = 32, H = 16;
        string h264 = Path.Combine(SamplesDirectory, "three_frames_bframes_32x16_cabac_deblock.h264");
        string yuv = Path.Combine(SamplesDirectory, "three_frames_bframes_32x16_cabac_deblock.yuv");
        EnsureGenerated(h264, yuv,
            $"-y -f lavfi -i color=c=red:s={W}x{H}:d=1.5:r=2,format=yuv420p -frames:v 3 " +
            "-c:v libx264 -profile:v main -bf 1 -keyint_min 99 -g 99 -coder 1 -an " +
            "-x264-params \"8x8dct=0\" " +
            $"-f h264 \"{h264}\"",
            $"-y -i \"{h264}\" -frames:v 3 -vsync passthrough -f rawvideo -pix_fmt yuv420p \"{yuv}\"");
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
