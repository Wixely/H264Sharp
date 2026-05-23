using H264Decoder.Cli;

namespace H264Decoder.Tests.Encoder;

public class CliEncodeTests
{
    [Fact]
    public void Encode_Smoke_WritesAnnexBFile()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "h264enc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string yuvPath = Path.Combine(tmpDir, "in.yuv");
            string outPath = Path.Combine(tmpDir, "out.h264");
            // 16x16 solid yuv: 256 Y + 64 U + 64 V = 384 bytes.
            byte[] yuv = new byte[16 * 16 + 2 * 8 * 8];
            Array.Fill(yuv, (byte)80, 0, 256);
            Array.Fill(yuv, (byte)100, 256, 64);
            Array.Fill(yuv, (byte)140, 320, 64);
            File.WriteAllBytes(yuvPath, yuv);

            using var sw = new StringWriter();
            using var stdout = new StringWriter();
            int code = Commands.Run(new[] { "encode", yuvPath, outPath, "--size", "16x16", "--qp", "18" }, stdout, sw);
            Assert.Equal(0, code);
            Assert.True(File.Exists(outPath));
            Assert.True(new FileInfo(outPath).Length > 16);

            // Round-trip: decode it.
            byte[] bytes = File.ReadAllBytes(outPath);
            var dec = new H264FrameDecoder();
            var pic = dec.DecodeFirstIFrame(bytes);
            Assert.Equal(16, pic.Width);
            Assert.Equal(16, pic.Height);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
