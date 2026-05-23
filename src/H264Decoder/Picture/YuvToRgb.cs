using H264Decoder.Syntax;

namespace H264Decoder.Picture;

/// <summary>
/// YUV 4:2:0 limited-range to 24-bit RGB conversion.
/// Picks the matrix from the SPS VUI matrix_coefficients field; defaults to
/// BT.601 (or BT.709 for HD-sized inputs) when unspecified, following the
/// usual "small picture = BT.601, big picture = BT.709" convention.
/// Chroma upsampling is nearest-neighbor (each chroma sample maps to a 2x2
/// luma block).
/// </summary>
public static class YuvToRgb
{
    public enum Matrix { Bt601, Bt709 }

    public static byte[] Convert(DecodedPicture pic, VuiParameters? vui = null)
    {
        Matrix matrix = PickMatrix(pic.Height, vui);
        byte[] rgb = new byte[pic.Width * pic.Height * 3];
        ConvertCore(pic, matrix, rgb);
        return rgb;
    }

    private static Matrix PickMatrix(int height, VuiParameters? vui)
    {
        if (vui is not null && vui.ColourDescriptionPresentFlag)
        {
            // matrix_coefficients: 1=BT.709, 5/6=BT.601, others fall back to height-based heuristic
            return vui.MatrixCoefficients switch
            {
                1 => Matrix.Bt709,
                5 or 6 => Matrix.Bt601,
                _ => height >= 720 ? Matrix.Bt709 : Matrix.Bt601,
            };
        }
        return height >= 720 ? Matrix.Bt709 : Matrix.Bt601;
    }

    private static void ConvertCore(DecodedPicture pic, Matrix matrix, byte[] dst)
    {
        // Integer-fixed-point coefficients (limited range).
        // BT.601: Y range [16, 235], Cb/Cr range [16, 240], offset 128 for chroma.
        // R = (298*(Y-16) + 409*(Cr-128) + 128) >> 8
        // G = (298*(Y-16) - 100*(Cb-128) - 208*(Cr-128) + 128) >> 8
        // B = (298*(Y-16) + 516*(Cb-128) + 128) >> 8
        // BT.709 uses different coefficients.
        int kY, kRCr, kGCb, kGCr, kBCb;
        if (matrix == Matrix.Bt601)
        {
            kY = 298; kRCr = 409; kGCb = -100; kGCr = -208; kBCb = 516;
        }
        else // BT.709
        {
            kY = 298; kRCr = 459; kGCb = -55; kGCr = -136; kBCb = 541;
        }

        int W = pic.Width, H = pic.Height;
        int bw = pic.BufferWidth;
        int cbw = pic.ChromaBufferWidth;
        int cropL = pic.CropLeft, cropT = pic.CropTop;
        int cCropL = cropL / 2, cCropT = cropT / 2;
        for (int y = 0; y < H; y++)
        {
            int srcY = cropT + y;
            int cy = (cCropT + (y >> 1));
            for (int x = 0; x < W; x++)
            {
                int srcX = cropL + x;
                int cx = cCropL + (x >> 1);
                int yv = pic.Y[srcY * bw + srcX] - 16;
                int cb = pic.U[cy * cbw + cx] - 128;
                int cr = pic.V[cy * cbw + cx] - 128;
                int r = (kY * yv + kRCr * cr + 128) >> 8;
                int g = (kY * yv + kGCb * cb + kGCr * cr + 128) >> 8;
                int b = (kY * yv + kBCb * cb + 128) >> 8;
                int o = (y * W + x) * 3;
                dst[o]     = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                dst[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                dst[o + 2] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
            }
        }
    }
}
