using H264Decoder.Bitstream;
using H264Decoder.Cli;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.Cli;

public class CliCommandsTests
{
    [Fact]
    public void Info_FromMp4_PrintsDurationResolutionFramesProfile()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int rc = Commands.Info(sample.H264Path, stdout, stderr);

        Assert.Equal(0, rc);
        string s = stdout.ToString();
        Assert.Contains("duration:", s);
        Assert.Contains("resolution: 128x96", s);
        Assert.Contains("frames: 2", s);
        Assert.Contains("profile:", s);
    }

    [Fact]
    public void Info_FromAnnexB_PrintsResolutionAndFrameCount()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int rc = Commands.Info(sample.H264Path, stdout, stderr);

        Assert.Equal(0, rc);
        string s = stdout.ToString();
        Assert.Contains("resolution: 16x16", s);
        Assert.Contains("frames: 1", s);
    }

    [Fact]
    public void ThumbnailAt_FromMp4_WritesPngOfCorrectSize()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            byte[] png = File.ReadAllBytes(outPng);
            // PNG magic: 89 50 4E 47 0D 0A 1A 0A
            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("0.5")]
    [InlineData("1.0")]
    public void ThumbnailAtPercent_FromMp4_WritesPng(string pct)
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, pct, stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            byte[] png = File.ReadAllBytes(outPng);
            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);
        }
        finally
        {
            if (File.Exists(outPng)) File.Delete(outPng);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("2")]
    public void ThumbnailAtPercent_RejectsBadInput(string pct)
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_bad_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, pct, stderr);
        Assert.NotEqual(0, rc);
        Assert.False(File.Exists(outPng));
    }

    [Fact]
    public void ThumbnailAtPercent_OnAnnexB_FailsWithMessage()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_pct_annexb_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAtPercent(sample.H264Path, outPng, "0.5", stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("MP4", stderr.ToString());
        Assert.False(File.Exists(outPng));
    }

    [Fact]
    public void ThumbnailAt_OnAnnexB_FailsWithMessage()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outPng = Path.Combine(Path.GetTempPath(), $"thumb_fail_{Guid.NewGuid():N}.png");
        var stderr = new StringWriter();
        int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0.5", stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("MP4", stderr.ToString());
        Assert.False(File.Exists(outPng));
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("5", 5.0)]
    [InlineData("12.345", 12.345)]
    [InlineData("1:23.5", 83.5)]
    [InlineData("0:00.001", 0.001)]
    public void TryParseTimestamp_AcceptsSecondsAndMmSs(string input, double expected)
    {
        Assert.True(Commands.TryParseTimestamp(input, out double v));
        Assert.Equal(expected, v, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("-1")]
    public void TryParseTimestamp_RejectsBadInput(string input)
    {
        Assert.False(Commands.TryParseTimestamp(input, out _));
    }

    [Fact]
    public void Run_NoArgs_PrintsUsageAndReturnsNonZero()
    {
        var stderr = new StringWriter();
        int rc = Commands.Run(Array.Empty<string>(), new StringWriter(), stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("Usage", stderr.ToString());
    }

    [Fact]
    public void Mp4Reader_ExtractWithTiming_ReturnsSamplesAndDimensions()
    {
        var sample = FfmpegFixture.TwoFramesAllPartitionsMp4();
        byte[] bytes = File.ReadAllBytes(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(bytes);

        Assert.Equal(2, stream.Samples.Count);
        Assert.Equal(128, stream.Width);
        Assert.Equal(96, stream.Height);
        Assert.True(stream.Timescale > 0);
        // First sample must be a sync sample (IDR).
        Assert.True(stream.Samples[0].IsSyncSample);
        // Composition times monotonically non-decreasing for this no-B-frame fixture.
        Assert.True(stream.Samples[1].CompositionTimeSeconds >= stream.Samples[0].CompositionTimeSeconds);
        // avcC carries at least one SPS and one PPS.
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Sps);
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Pps);
    }

    [Fact]
    public void DecodeFirstIFrameToFile_PreservesLegacyBehavior()
    {
        var sample = FfmpegFixture.SingleRed16x16();
        string outYuv = Path.Combine(Path.GetTempPath(), $"frame_{Guid.NewGuid():N}.yuv");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.DecodeFirstIFrameToFile(sample.H264Path, outYuv, stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outYuv));
            // YUV 4:2:0: W*H*1.5 bytes
            long expected = 16 * 16 * 3 / 2;
            Assert.Equal(expected, new FileInfo(outYuv).Length);
        }
        finally
        {
            if (File.Exists(outYuv)) File.Delete(outYuv);
        }
    }
}
