using H264Decoder.Picture;

namespace H264Decoder.Tests.Picture;

public sealed class YuvToRgbTests
{
    [Fact]
    public void Black_YuvToRgb_IsBlack()
    {
        var pic = new DecodedPicture(2, 2);
        Array.Fill<byte>(pic.Y, 16);   // black luma in limited range
        Array.Fill<byte>(pic.U, 128);
        Array.Fill<byte>(pic.V, 128);

        byte[] rgb = YuvToRgb.Convert(pic);
        // BT.601 limited: R=G=B = 0 for Y=16, Cb=Cr=128
        Assert.All(rgb, b => Assert.Equal(0, b));
    }

    [Fact]
    public void White_YuvToRgb_IsWhite()
    {
        var pic = new DecodedPicture(2, 2);
        Array.Fill<byte>(pic.Y, 235);  // peak luma in limited range
        Array.Fill<byte>(pic.U, 128);
        Array.Fill<byte>(pic.V, 128);

        byte[] rgb = YuvToRgb.Convert(pic);
        // BT.601 limited: R = (298*(235-16) + 128) >> 8 = (298*219+128) >> 8 = (65262+128) >> 8 = 255
        foreach (byte b in rgb) Assert.True(b >= 254, $"expected ~255, got {b}");
    }

    [Fact]
    public void Red_YuvToRgb_IsRed()
    {
        // BT.601 limited-range "red" approximate components: Y≈81, Cb≈90, Cr≈240
        var pic = new DecodedPicture(2, 2);
        Array.Fill<byte>(pic.Y, 81);
        Array.Fill<byte>(pic.U, 90);
        Array.Fill<byte>(pic.V, 240);
        byte[] rgb = YuvToRgb.Convert(pic);
        // Expect R high (~255), G and B low (<30).
        Assert.True(rgb[0] >= 230, $"red R should be ~255, got {rgb[0]}");
        Assert.True(rgb[1] <= 30,  $"red G should be ~0, got {rgb[1]}");
        Assert.True(rgb[2] <= 30,  $"red B should be ~0, got {rgb[2]}");
    }
}
