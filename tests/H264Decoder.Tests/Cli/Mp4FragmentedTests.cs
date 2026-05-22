using H264Decoder.Bitstream;
using H264Decoder.Cli;
using H264Decoder.Tests.Fixtures;

namespace H264Decoder.Tests.Cli;

/// <summary>End-to-end coverage for fragmented MP4 (moof/traf/trun) parsing.</summary>
public class Mp4FragmentedTests
{
    [Fact]
    public void Mp4Reader_HandlesFragmentedMp4()
    {
        var sample = FfmpegFixture.FragmentedMp4Red64x48();
        using var fs = File.OpenRead(sample.H264Path);
        var stream = Mp4Reader.ExtractH264WithTiming(fs);

        // 2 frames at r=2, d=1.
        Assert.Equal(2, stream.Samples.Count);
        Assert.Equal(sample.Width, stream.Width);
        Assert.Equal(sample.Height, stream.Height);
        Assert.True(stream.Timescale > 0);
        // First sample is the IDR keyframe.
        Assert.True(stream.Samples[0].IsSyncSample);
        // Decode times monotonically non-decreasing.
        Assert.True(stream.Samples[1].DecodeTimeSeconds >= stream.Samples[0].DecodeTimeSeconds);
        // SPS+PPS recovered from avcC.
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Sps);
        Assert.Contains(stream.AvcCConfigNalUnits, n => n.NalUnitType == NalUnitType.Pps);
        // Each sample resolves to at least one NAL unit at its declared offset.
        for (int i = 0; i < stream.Samples.Count; i++)
        {
            var nals = stream.ResolveNalUnits(i);
            Assert.NotEmpty(nals);
        }
    }

    [Fact]
    public void Commands_Info_OnFragmentedMp4_PrintsCorrectMetadata()
    {
        var sample = FfmpegFixture.FragmentedMp4Red64x48();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int rc = Commands.Info(sample.H264Path, stdout, stderr);

        Assert.Equal(0, rc);
        string s = stdout.ToString();
        Assert.Contains($"resolution: {sample.Width}x{sample.Height}", s);
        Assert.Contains("frames: 2", s);
        Assert.Contains("profile:", s);
    }

    [Fact]
    public void Commands_ThumbnailAt_OnFragmentedMp4_DecodesFrame()
    {
        var sample = FfmpegFixture.FragmentedMp4Red64x48();
        string outPng = Path.Combine(Path.GetTempPath(), $"frag_thumb_{Guid.NewGuid():N}.png");
        try
        {
            var stderr = new StringWriter();
            int rc = Commands.ThumbnailAt(sample.H264Path, outPng, "0", stderr);
            Assert.Equal(0, rc);
            Assert.True(File.Exists(outPng));
            byte[] png = File.ReadAllBytes(outPng);
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
}
