namespace H264Sharp.Decoder.Transform;

internal static class ScanOrder
{
    /// <summary>
    /// 4x4 zig-zag scan: scanPos → raster position within the 4x4 block.
    /// Spec Figure 8-9 (frame mode).
    /// </summary>
    public static readonly int[] ZigZag4x4 =
    [
        0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15,
    ];

    /// <summary>
    /// Restore a zigzag-scanned coefficient array back to raster order in place.
    /// </summary>
    public static void Unzigzag4x4(ReadOnlySpan<int> scan, Span<int> raster)
    {
        raster.Clear();
        for (int i = 0; i < 16; i++)
        {
            raster[ZigZag4x4[i]] = scan[i];
        }
    }

    /// <summary>
    /// 8x8 zig-zag scan: scanPos → raster position within the 8x8 block (spec Figure 8-10 frame).
    /// </summary>
    public static readonly int[] ZigZag8x8 =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Restore an 8x8 zigzag-scanned coefficient array to raster order.</summary>
    public static void Unzigzag8x8(ReadOnlySpan<int> scan, Span<int> raster)
    {
        raster.Clear();
        for (int i = 0; i < 64; i++)
        {
            raster[ZigZag8x8[i]] = scan[i];
        }
    }
}
