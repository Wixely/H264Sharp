namespace H264Decoder.Transform;

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
}
