using H264Decoder.Syntax;

namespace H264Decoder.Prediction;

/// <summary>
/// H.264 intra-sample prediction (spec §8.3). All outputs are unclipped byte values
/// in [0, 255]. Inputs are pre-clipped reconstructed neighbor samples.
/// </summary>
public static class IntraPrediction
{
    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ---------------------------------------------------------------------
    // Intra_16x16 (spec §8.3.3)
    // ---------------------------------------------------------------------
    /// <param name="top">16 samples directly above the macroblock (top row).</param>
    /// <param name="left">16 samples directly left of the macroblock (left column).</param>
    /// <param name="topLeft">Sample at (x=-1, y=-1) — only used by Plane mode.</param>
    /// <param name="output">256 output samples in raster scan order (row 0 first).</param>
    public static void PredictIntra16x16(
        Intra16x16PredMode mode,
        ReadOnlySpan<byte> top, bool topAvail,
        ReadOnlySpan<byte> left, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        Span<byte> output)
    {
        switch (mode)
        {
            case Intra16x16PredMode.Vertical:
                if (!topAvail) throw new InvalidDataException("Intra_16x16 Vertical: top not available");
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        output[y * 16 + x] = top[x];
                break;

            case Intra16x16PredMode.Horizontal:
                if (!leftAvail) throw new InvalidDataException("Intra_16x16 Horizontal: left not available");
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        output[y * 16 + x] = left[y];
                break;

            case Intra16x16PredMode.Dc:
                {
                    int sum = 0, count = 0;
                    if (topAvail) { for (int i = 0; i < 16; i++) sum += top[i]; count += 16; }
                    if (leftAvail) { for (int i = 0; i < 16; i++) sum += left[i]; count += 16; }
                    byte dc;
                    if (count == 0) dc = 128;
                    else if (count == 32) dc = (byte)((sum + 16) >> 5);
                    else dc = (byte)((sum + 8) >> 4);
                    output.Fill(dc);
                    break;
                }

            case Intra16x16PredMode.Plane:
                if (!topAvail || !leftAvail || !topLeftAvail)
                    throw new InvalidDataException("Intra_16x16 Plane: neighbors not available");
                PlanePredict16x16(top, left, topLeft, output);
                break;

            default:
                throw new InvalidDataException($"Intra_16x16 mode {mode} invalid");
        }
    }

    private static void PlanePredict16x16(ReadOnlySpan<byte> top, ReadOnlySpan<byte> left, byte topLeft, Span<byte> output)
    {
        int h = 0;
        for (int i = 0; i < 8; i++)
            h += (i + 1) * (top[8 + i] - (i == 7 ? topLeft : top[6 - i]));

        int v = 0;
        for (int j = 0; j < 8; j++)
            v += (j + 1) * (left[8 + j] - (j == 7 ? topLeft : left[6 - j]));

        int b = (5 * h + 32) >> 6;
        int c = (5 * v + 32) >> 6;
        int a = 16 * (left[15] + top[15]);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                output[y * 16 + x] = Clip((a + b * (x - 7) + c * (y - 7) + 16) >> 5);
    }

    // ---------------------------------------------------------------------
    // IntraChroma 8x8 (spec §8.3.4)
    // ---------------------------------------------------------------------
    /// <param name="top">8 samples above the chroma block.</param>
    /// <param name="left">8 samples left of the chroma block.</param>
    /// <param name="topLeft">Sample at (-1,-1).</param>
    /// <param name="output">64 samples in raster order.</param>
    public static void PredictChroma8x8(
        IntraChromaPredMode mode,
        ReadOnlySpan<byte> top, bool topAvail,
        ReadOnlySpan<byte> left, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        Span<byte> output)
    {
        switch (mode)
        {
            case IntraChromaPredMode.Dc:
                ChromaDcPredict(top, topAvail, left, leftAvail, output);
                break;
            case IntraChromaPredMode.Horizontal:
                if (!leftAvail) throw new InvalidDataException("Chroma Horizontal: left not available");
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        output[y * 8 + x] = left[y];
                break;
            case IntraChromaPredMode.Vertical:
                if (!topAvail) throw new InvalidDataException("Chroma Vertical: top not available");
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        output[y * 8 + x] = top[x];
                break;
            case IntraChromaPredMode.Plane:
                if (!topAvail || !leftAvail || !topLeftAvail)
                    throw new InvalidDataException("Chroma Plane: neighbors not available");
                ChromaPlanePredict(top, left, topLeft, output);
                break;
        }
    }

    private static void ChromaDcPredict(ReadOnlySpan<byte> top, bool topAvail, ReadOnlySpan<byte> left, bool leftAvail, Span<byte> output)
    {
        // Chroma 8x8 DC is computed per 4x4 quadrant (spec §8.3.4.2):
        //   block (0,0)    block (1,0)
        //   block (0,1)    block (1,1)
        // Quadrant DC depends on which of {top half, left half} are available.
        Span<byte> dc = stackalloc byte[4];

        int topSum0 = 0, topSum4 = 0, leftSum0 = 0, leftSum4 = 0;
        if (topAvail) { for (int i = 0; i < 4; i++) { topSum0 += top[i]; topSum4 += top[i + 4]; } }
        if (leftAvail) { for (int i = 0; i < 4; i++) { leftSum0 += left[i]; leftSum4 += left[i + 4]; } }

        // (0,0): uses top[0..3] and left[0..3] if available
        if (topAvail && leftAvail) dc[0] = (byte)((topSum0 + leftSum0 + 4) >> 3);
        else if (topAvail) dc[0] = (byte)((topSum0 + 2) >> 2);
        else if (leftAvail) dc[0] = (byte)((leftSum0 + 2) >> 2);
        else dc[0] = 128;

        // (1,0): uses top[4..7] if available; else left[0..3]
        if (topAvail) dc[1] = (byte)((topSum4 + 2) >> 2);
        else if (leftAvail) dc[1] = (byte)((leftSum0 + 2) >> 2);
        else dc[1] = 128;

        // (0,1): uses left[4..7] if available; else top[0..3]
        if (leftAvail) dc[2] = (byte)((leftSum4 + 2) >> 2);
        else if (topAvail) dc[2] = (byte)((topSum0 + 2) >> 2);
        else dc[2] = 128;

        // (1,1): uses top[4..7] and left[4..7] if available
        if (topAvail && leftAvail) dc[3] = (byte)((topSum4 + leftSum4 + 4) >> 3);
        else if (topAvail) dc[3] = (byte)((topSum4 + 2) >> 2);
        else if (leftAvail) dc[3] = (byte)((leftSum4 + 2) >> 2);
        else dc[3] = 128;

        for (int y = 0; y < 8; y++)
        {
            int qy = y >= 4 ? 2 : 0;
            for (int x = 0; x < 8; x++)
            {
                int qx = x >= 4 ? 1 : 0;
                output[y * 8 + x] = dc[qy + qx];
            }
        }
    }

    private static void ChromaPlanePredict(ReadOnlySpan<byte> top, ReadOnlySpan<byte> left, byte topLeft, Span<byte> output)
    {
        int h = 0;
        for (int i = 0; i < 4; i++)
            h += (i + 1) * (top[4 + i] - (i == 3 ? topLeft : top[2 - i]));
        int v = 0;
        for (int j = 0; j < 4; j++)
            v += (j + 1) * (left[4 + j] - (j == 3 ? topLeft : left[2 - j]));
        int b = (34 * h + 32) >> 6;
        int c = (34 * v + 32) >> 6;
        int a = 16 * (left[7] + top[7]);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                output[y * 8 + x] = Clip((a + b * (x - 3) + c * (y - 3) + 16) >> 5);
    }

    // ---------------------------------------------------------------------
    // Intra_4x4 (spec §8.3.1) — 9 modes
    // ---------------------------------------------------------------------
    public enum Intra4x4Mode
    {
        Vertical = 0,
        Horizontal = 1,
        Dc = 2,
        DiagDownLeft = 3,
        DiagDownRight = 4,
        VerticalRight = 5,
        HorizontalDown = 6,
        VerticalLeft = 7,
        HorizontalUp = 8,
    }

    /// <param name="top">top[0..3] and top[4..7] for top-right. If !topRightAvail, top[4..7] is replaced by top[3] per spec.</param>
    /// <param name="left">left[0..3].</param>
    /// <param name="topLeft">corner sample.</param>
    /// <param name="output">16 samples in raster scan order.</param>
    public static void PredictIntra4x4(
        Intra4x4Mode mode,
        ReadOnlySpan<byte> top, bool topAvail, bool topRightAvail,
        ReadOnlySpan<byte> left, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        Span<byte> output)
    {
        // The spec replaces unavailable top-right samples with a copy of top[3].
        Span<byte> t = stackalloc byte[8];
        if (topAvail)
        {
            top[..4].CopyTo(t[..4]);
            if (topRightAvail) top.Slice(4, 4).CopyTo(t.Slice(4, 4));
            else { byte fill = top[3]; t[4] = fill; t[5] = fill; t[6] = fill; t[7] = fill; }
        }

        switch (mode)
        {
            case Intra4x4Mode.Vertical:
                if (!topAvail) throw new InvalidDataException("4x4 V: top not available");
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        output[y * 4 + x] = t[x];
                break;

            case Intra4x4Mode.Horizontal:
                if (!leftAvail) throw new InvalidDataException("4x4 H: left not available");
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        output[y * 4 + x] = left[y];
                break;

            case Intra4x4Mode.Dc:
                {
                    int sum = 0, count = 0;
                    if (topAvail) { for (int i = 0; i < 4; i++) sum += t[i]; count += 4; }
                    if (leftAvail) { for (int i = 0; i < 4; i++) sum += left[i]; count += 4; }
                    byte dc = count == 0 ? (byte)128 :
                              count == 8 ? (byte)((sum + 4) >> 3) :
                                           (byte)((sum + 2) >> 2);
                    output.Fill(dc);
                    break;
                }

            case Intra4x4Mode.DiagDownLeft:
                {
                    if (!topAvail) throw new InvalidDataException("4x4 DDL: top not available");
                    // out[x,y] = (t[x+y] + 2*t[x+y+1] + t[x+y+2] + 2) >> 2, special-case (x=3,y=3)
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int z = x + y;
                            int v = (x == 3 && y == 3)
                                ? (t[6] + 3 * t[7] + 2) >> 2
                                : (t[z] + 2 * t[z + 1] + t[z + 2] + 2) >> 2;
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }

            case Intra4x4Mode.DiagDownRight:
                {
                    if (!topAvail || !leftAvail || !topLeftAvail)
                        throw new InvalidDataException("4x4 DDR: neighbors not available");
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int v;
                            if (x > y)
                            {
                                int z = x - y;
                                // Sample(z-2,-1): for z>=2 use t[z-2]; for z==1 use topLeft
                                int s0 = z >= 2 ? t[z - 2] : (int)topLeft;
                                int s1 = z >= 1 ? t[z - 1] : (int)topLeft;
                                int s2 = t[z];
                                v = (s0 + 2 * s1 + s2 + 2) >> 2;
                            }
                            else if (x < y)
                            {
                                int z = y - x;
                                int s0 = z >= 2 ? left[z - 2] : (int)topLeft;
                                int s1 = z >= 1 ? left[z - 1] : (int)topLeft;
                                int s2 = left[z];
                                v = (s0 + 2 * s1 + s2 + 2) >> 2;
                            }
                            else // x == y
                            {
                                v = (t[0] + 2 * topLeft + left[0] + 2) >> 2;
                            }
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }

            case Intra4x4Mode.VerticalRight:
                if (!topAvail || !leftAvail || !topLeftAvail)
                    throw new InvalidDataException("4x4 VR: neighbors not available");
                Intra4x4Mixed(t, left, topLeft, output, Intra4x4Mode.VerticalRight);
                break;
            case Intra4x4Mode.HorizontalDown:
                if (!topAvail || !leftAvail || !topLeftAvail)
                    throw new InvalidDataException("4x4 HD: neighbors not available");
                Intra4x4Mixed(t, left, topLeft, output, Intra4x4Mode.HorizontalDown);
                break;
            case Intra4x4Mode.VerticalLeft:
                if (!topAvail) throw new InvalidDataException("4x4 VL: top not available");
                Intra4x4Mixed(t, left, topLeft, output, Intra4x4Mode.VerticalLeft);
                break;
            case Intra4x4Mode.HorizontalUp:
                if (!leftAvail) throw new InvalidDataException("4x4 HU: left not available");
                Intra4x4Mixed(t, left, topLeft, output, Intra4x4Mode.HorizontalUp);
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Intra_8x8 (spec §8.3.2) — 9 modes, with mandatory 3-tap low-pass filter on neighbors.
    // ---------------------------------------------------------------------
    public enum Intra8x8Mode
    {
        Vertical = 0,
        Horizontal = 1,
        Dc = 2,
        DiagDownLeft = 3,
        DiagDownRight = 4,
        VerticalRight = 5,
        HorizontalDown = 6,
        VerticalLeft = 7,
        HorizontalUp = 8,
    }

    /// <summary>
    /// Reference-sample filter for Intra_8x8 prediction (spec §8.3.2.2.1). Applies a 3-tap
    /// [1,2,1]/4 low-pass over the 16 top samples, 8 left samples, and the corner sample,
    /// with edge handling per spec equations 8-78..8-83.
    /// </summary>
    /// <param name="top">Up to 16 unfiltered top samples (top[0..7] required if topAvail; top[8..15] is top-right).</param>
    /// <param name="left">8 unfiltered left samples.</param>
    /// <param name="topLeft">Unfiltered corner sample.</param>
    /// <param name="topAvail">True if top row neighbors exist.</param>
    /// <param name="topRightAvail">True if top-right 8 samples exist (otherwise top[8..15] = top[7]).</param>
    /// <param name="leftAvail">True if left column neighbors exist.</param>
    /// <param name="topLeftAvail">True if corner exists.</param>
    /// <param name="outTop">16 filtered top samples.</param>
    /// <param name="outLeft">8 filtered left samples.</param>
    /// <param name="outTopLeft">Filtered corner sample.</param>
    public static void Intra8x8PredFilter(
        ReadOnlySpan<byte> top, bool topAvail, bool topRightAvail,
        ReadOnlySpan<byte> left, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        Span<byte> outTop, Span<byte> outLeft, out byte outTopLeft)
    {
        // Build unfiltered 16-sample top row, substituting top[7] when top-right is unavailable.
        Span<byte> t = stackalloc byte[16];
        if (topAvail)
        {
            for (int i = 0; i < 8; i++) t[i] = top[i];
            if (topRightAvail) for (int i = 0; i < 8; i++) t[8 + i] = top[8 + i];
            else { byte fill = t[7]; for (int i = 8; i < 16; i++) t[i] = fill; }
        }

        // Filtered top row (16 samples).
        if (topAvail)
        {
            // x = 0
            if (topLeftAvail) outTop[0] = (byte)((topLeft + 2 * t[0] + t[1] + 2) >> 2);
            else outTop[0] = (byte)((3 * t[0] + t[1] + 2) >> 2);
            // x = 1..14
            for (int x = 1; x <= 14; x++)
                outTop[x] = (byte)((t[x - 1] + 2 * t[x] + t[x + 1] + 2) >> 2);
            // x = 15
            outTop[15] = (byte)((t[14] + 3 * t[15] + 2) >> 2);
        }

        // Filtered left column (8 samples).
        if (leftAvail)
        {
            if (topLeftAvail) outLeft[0] = (byte)((topLeft + 2 * left[0] + left[1] + 2) >> 2);
            else outLeft[0] = (byte)((3 * left[0] + left[1] + 2) >> 2);
            for (int y = 1; y <= 6; y++)
                outLeft[y] = (byte)((left[y - 1] + 2 * left[y] + left[y + 1] + 2) >> 2);
            outLeft[7] = (byte)((left[6] + 3 * left[7] + 2) >> 2);
        }

        // Filtered corner.
        if (topLeftAvail)
        {
            if (topAvail && leftAvail)
                outTopLeft = (byte)((t[0] + 2 * topLeft + left[0] + 2) >> 2);
            else if (topAvail)
                outTopLeft = (byte)((t[0] + 3 * topLeft + 2) >> 2);
            else if (leftAvail)
                outTopLeft = (byte)((left[0] + 3 * topLeft + 2) >> 2);
            else
                outTopLeft = topLeft;
        }
        else
        {
            outTopLeft = 0;
        }
    }

    /// <summary>
    /// Intra_8x8 prediction (spec §8.3.2). Neighbors must already be filtered via
    /// <see cref="Intra8x8PredFilter"/>. Output is 64 samples in raster order.
    /// </summary>
    public static void PredictIntra8x8(
        Intra8x8Mode mode,
        ReadOnlySpan<byte> filteredTop, bool topAvail,
        ReadOnlySpan<byte> filteredLeft, bool leftAvail,
        byte filteredTopLeft, bool topLeftAvail,
        Span<byte> output)
    {
        switch (mode)
        {
            case Intra8x8Mode.Vertical:
                if (!topAvail) throw new InvalidDataException("Intra_8x8 V: top not available");
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        output[y * 8 + x] = filteredTop[x];
                break;

            case Intra8x8Mode.Horizontal:
                if (!leftAvail) throw new InvalidDataException("Intra_8x8 H: left not available");
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        output[y * 8 + x] = filteredLeft[y];
                break;

            case Intra8x8Mode.Dc:
                {
                    int sum = 0, count = 0;
                    if (topAvail) { for (int i = 0; i < 8; i++) sum += filteredTop[i]; count += 8; }
                    if (leftAvail) { for (int i = 0; i < 8; i++) sum += filteredLeft[i]; count += 8; }
                    byte dc = count == 0 ? (byte)128 :
                              count == 16 ? (byte)((sum + 8) >> 4) :
                                            (byte)((sum + 4) >> 3);
                    output.Fill(dc);
                    break;
                }

            case Intra8x8Mode.DiagDownLeft:
                {
                    if (!topAvail) throw new InvalidDataException("Intra_8x8 DDL: top not available");
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int v;
                            if (x == 7 && y == 7)
                                v = (filteredTop[14] + 3 * filteredTop[15] + 2) >> 2;
                            else
                                v = (filteredTop[x + y] + 2 * filteredTop[x + y + 1] + filteredTop[x + y + 2] + 2) >> 2;
                            output[y * 8 + x] = (byte)v;
                        }
                    break;
                }

            case Intra8x8Mode.DiagDownRight:
                {
                    if (!topAvail || !leftAvail || !topLeftAvail)
                        throw new InvalidDataException("Intra_8x8 DDR: neighbors not available");
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int v;
                            if (x > y)
                            {
                                int z = x - y;
                                int s0 = z >= 2 ? filteredTop[z - 2] : (int)filteredTopLeft;
                                int s1 = z >= 1 ? filteredTop[z - 1] : (int)filteredTopLeft;
                                v = (s0 + 2 * s1 + filteredTop[z] + 2) >> 2;
                            }
                            else if (x < y)
                            {
                                int z = y - x;
                                int s0 = z >= 2 ? filteredLeft[z - 2] : (int)filteredTopLeft;
                                int s1 = z >= 1 ? filteredLeft[z - 1] : (int)filteredTopLeft;
                                v = (s0 + 2 * s1 + filteredLeft[z] + 2) >> 2;
                            }
                            else
                            {
                                v = (filteredTop[0] + 2 * filteredTopLeft + filteredLeft[0] + 2) >> 2;
                            }
                            output[y * 8 + x] = (byte)v;
                        }
                    break;
                }

            case Intra8x8Mode.VerticalLeft:
                {
                    if (!topAvail) throw new InvalidDataException("Intra_8x8 VL: top not available");
                    // Spec eq 8-115/8-116. y even -> 2-tap, y odd -> 3-tap. Top index 0..14 needed.
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int k = x + (y >> 1);
                            int v = (y & 1) == 0
                                ? (filteredTop[k] + filteredTop[k + 1] + 1) >> 1
                                : (filteredTop[k] + 2 * filteredTop[k + 1] + filteredTop[k + 2] + 2) >> 2;
                            output[y * 8 + x] = (byte)v;
                        }
                    break;
                }

            case Intra8x8Mode.VerticalRight:
                {
                    if (!topAvail || !leftAvail || !topLeftAvail)
                        throw new InvalidDataException("Intra_8x8 VR: neighbors not available");
                    Intra8x8VR(filteredTop, filteredLeft, filteredTopLeft, output);
                    break;
                }

            case Intra8x8Mode.HorizontalDown:
                {
                    if (!topAvail || !leftAvail || !topLeftAvail)
                        throw new InvalidDataException("Intra_8x8 HD: neighbors not available");
                    Intra8x8HD(filteredTop, filteredLeft, filteredTopLeft, output);
                    break;
                }

            case Intra8x8Mode.HorizontalUp:
                {
                    if (!leftAvail) throw new InvalidDataException("Intra_8x8 HU: left not available");
                    Intra8x8HU(filteredLeft, output);
                    break;
                }
        }
    }

    // ---- Intra_8x8 VR/HD/HU table-driven implementations ----
    // Reference samples are named per JM spec figures: Z = topLeft, A..H = filteredTop[0..7],
    // I..P = filteredTop[8..15], Q..X = filteredLeft[0..7].

    private static void Intra8x8VR(ReadOnlySpan<byte> ft, ReadOnlySpan<byte> fl, byte Z, Span<byte> o)
    {
        // Spec §8.3.2.2.6 — VerticalRight.
        // 2-tap top averages (zVR even, 0..12):
        int aa = (Z + ft[0] + 1) >> 1;
        int bb = (ft[0] + ft[1] + 1) >> 1;
        int cc = (ft[1] + ft[2] + 1) >> 1;
        int dd = (ft[2] + ft[3] + 1) >> 1;
        int ee = (ft[3] + ft[4] + 1) >> 1;
        int ff = (ft[4] + ft[5] + 1) >> 1;
        int gg = (ft[5] + ft[6] + 1) >> 1;
        int hh = (ft[6] + ft[7] + 1) >> 1;
        // 3-tap top (zVR odd, -1..13):
        int i = (fl[0] + 2 * Z + ft[0] + 2) >> 2;
        int j = (Z + 2 * ft[0] + ft[1] + 2) >> 2;
        int k = (ft[0] + 2 * ft[1] + ft[2] + 2) >> 2;
        int l = (ft[1] + 2 * ft[2] + ft[3] + 2) >> 2;
        int m = (ft[2] + 2 * ft[3] + ft[4] + 2) >> 2;
        int n = (ft[3] + 2 * ft[4] + ft[5] + 2) >> 2;
        int oo = (ft[4] + 2 * ft[5] + ft[6] + 2) >> 2;
        int p = (ft[5] + 2 * ft[6] + ft[7] + 2) >> 2;
        // 3-tap left (zVR negative even):
        int q = (Z + 2 * fl[0] + fl[1] + 2) >> 2;
        int r = (fl[0] + 2 * fl[1] + fl[2] + 2) >> 2;
        int s = (fl[1] + 2 * fl[2] + fl[3] + 2) >> 2;
        int t = (fl[2] + 2 * fl[3] + fl[4] + 2) >> 2;
        int u = (fl[3] + 2 * fl[4] + fl[5] + 2) >> 2;
        int vv = (fl[4] + 2 * fl[5] + fl[6] + 2) >> 2;

        // Output map. Rows top→bottom.
        // Row 0: aa bb cc dd ee ff gg hh
        // Row 1:  i  j  k  l  m  n  o  p
        // Row 2:  q aa bb cc dd ee ff gg
        // Row 3:  r  i  j  k  l  m  n  o
        // Row 4:  s  q aa bb cc dd ee ff
        // Row 5:  t  r  i  j  k  l  m  n
        // Row 6:  u  s  q aa bb cc dd ee
        // Row 7:  v  t  r  i  j  k  l  m
        int[] row = {
            aa,bb,cc,dd,ee,ff,gg,hh,
            i, j, k, l, m, n, oo, p,
            q, aa,bb,cc,dd,ee,ff,gg,
            r, i, j, k, l, m, n, oo,
            s, q, aa,bb,cc,dd,ee,ff,
            t, r, i, j, k, l, m, n,
            u, s, q, aa,bb,cc,dd,ee,
            vv,t, r, i, j, k, l, m,
        };
        for (int idx = 0; idx < 64; idx++) o[idx] = (byte)row[idx];
    }

    private static void Intra8x8HD(ReadOnlySpan<byte> ft, ReadOnlySpan<byte> fl, byte Z, Span<byte> o)
    {
        // Spec §8.3.2.2.7 — HorizontalDown. Symmetric/transpose of VR.
        // 2-tap left averages (zHD even):
        int aa = (Z + fl[0] + 1) >> 1;
        int bb = (fl[0] + fl[1] + 1) >> 1;
        int cc = (fl[1] + fl[2] + 1) >> 1;
        int dd = (fl[2] + fl[3] + 1) >> 1;
        int ee = (fl[3] + fl[4] + 1) >> 1;
        int ff = (fl[4] + fl[5] + 1) >> 1;
        int gg = (fl[5] + fl[6] + 1) >> 1;
        int hh = (fl[6] + fl[7] + 1) >> 1;
        // 3-tap left (zHD odd):
        int i = (ft[0] + 2 * Z + fl[0] + 2) >> 2;
        int j = (Z + 2 * fl[0] + fl[1] + 2) >> 2;
        int k = (fl[0] + 2 * fl[1] + fl[2] + 2) >> 2;
        int l = (fl[1] + 2 * fl[2] + fl[3] + 2) >> 2;
        int m = (fl[2] + 2 * fl[3] + fl[4] + 2) >> 2;
        int n = (fl[3] + 2 * fl[4] + fl[5] + 2) >> 2;
        int oo = (fl[4] + 2 * fl[5] + fl[6] + 2) >> 2;
        int p = (fl[5] + 2 * fl[6] + fl[7] + 2) >> 2;
        // 3-tap top (zHD negative):
        int q = (Z + 2 * ft[0] + ft[1] + 2) >> 2;
        int r = (ft[0] + 2 * ft[1] + ft[2] + 2) >> 2;
        int s = (ft[1] + 2 * ft[2] + ft[3] + 2) >> 2;
        int t = (ft[2] + 2 * ft[3] + ft[4] + 2) >> 2;
        int u = (ft[3] + 2 * ft[4] + ft[5] + 2) >> 2;
        int vv = (ft[4] + 2 * ft[5] + ft[6] + 2) >> 2;

        // HD is VR transposed (columns become rows). Output layout:
        // Each row y, column x of HD = VR(x=y, y=x), i.e. transpose.
        // Build via direct table:
        // Row 0: aa  i  q  r  s  t  u  v
        // Row 1: bb  j aa  i  q  r  s  t
        // Row 2: cc  k bb  j aa  i  q  r
        // Row 3: dd  l cc  k bb  j aa  i
        // Row 4: ee  m dd  l cc  k bb  j
        // Row 5: ff  n ee  m dd  l cc  k
        // Row 6: gg oo  ff n  ee  m dd  l
        // Row 7: hh  p gg oo  ff  n ee  m
        int[] row = {
            aa, i, q, r, s, t, u, vv,
            bb, j, aa, i, q, r, s, t,
            cc, k, bb, j, aa, i, q, r,
            dd, l, cc, k, bb, j, aa, i,
            ee, m, dd, l, cc, k, bb, j,
            ff, n, ee, m, dd, l, cc, k,
            gg, oo, ff, n, ee, m, dd, l,
            hh, p, gg, oo, ff, n, ee, m,
        };
        for (int idx = 0; idx < 64; idx++) o[idx] = (byte)row[idx];
    }

    private static void Intra8x8HU(ReadOnlySpan<byte> fl, Span<byte> o)
    {
        // Spec §8.3.2.2.9 — HorizontalUp. Uses left column only.
        // Per spec eq 8-122/8-123: zHU = x + 2*y; piecewise classification.
        // Following JM impl pattern:
        // 2-tap (zHU = 0,2,4,6,8,10,12):
        int aa = (fl[0] + fl[1] + 1) >> 1;
        int bb = (fl[1] + fl[2] + 1) >> 1;
        int cc = (fl[2] + fl[3] + 1) >> 1;
        int dd = (fl[3] + fl[4] + 1) >> 1;
        int ee = (fl[4] + fl[5] + 1) >> 1;
        int ff = (fl[5] + fl[6] + 1) >> 1;
        int gg = (fl[6] + fl[7] + 1) >> 1;
        // 3-tap (zHU = 1,3,5,7,9,11,13):
        int i = (fl[0] + 2 * fl[1] + fl[2] + 2) >> 2;
        int j = (fl[1] + 2 * fl[2] + fl[3] + 2) >> 2;
        int k = (fl[2] + 2 * fl[3] + fl[4] + 2) >> 2;
        int l = (fl[3] + 2 * fl[4] + fl[5] + 2) >> 2;
        int m = (fl[4] + 2 * fl[5] + fl[6] + 2) >> 2;
        int n = (fl[5] + 2 * fl[6] + fl[7] + 2) >> 2;
        // zHU = 14:
        int oo = (fl[6] + 3 * fl[7] + 2) >> 2;
        // zHU >= 15:
        int pp = fl[7];

        // Output by zHU = x + 2*y: 0,2,..,12 -> 2-tap; 1,3,..,11 -> 3-tap; 13 -> oo; >=14 -> pp.
        // Row 0: zHU=0,1,2,3,4,5,6,7   -> aa  i bb  j cc  k dd  l
        // Row 1: zHU=2..9              -> bb  j cc  k dd  l ee  m
        // Row 2: zHU=4..11             -> cc  k dd  l ee  m ff  n
        // Row 3: zHU=6..13             -> dd  l ee  m ff  n gg oo
        // Row 4: zHU=8..15             -> ee  m ff  n gg oo pp pp
        // Row 5: zHU=10..17            -> ff  n gg oo pp pp pp pp
        // Row 6: zHU=12..19            -> gg oo pp pp pp pp pp pp
        // Row 7: zHU=14..21            -> pp pp pp pp pp pp pp pp
        int[] row = {
            aa, i, bb, j, cc, k, dd, l,
            bb, j, cc, k, dd, l, ee, m,
            cc, k, dd, l, ee, m, ff, n,
            dd, l, ee, m, ff, n, gg, oo,
            ee, m, ff, n, gg, oo, pp, pp,
            ff, n, gg, oo, pp, pp, pp, pp,
            gg, oo, pp, pp, pp, pp, pp, pp,
            pp, pp, pp, pp, pp, pp, pp, pp,
        };
        for (int idx = 0; idx < 64; idx++) o[idx] = (byte)row[idx];
    }

    /// <summary>
    /// Port of OpenH264's table-driven Intra_4x4 prediction for the
    /// diagonal/oblique modes (VR, HD, VL, HU). Each computes a small list
    /// of intermediate values then emits them at specific output positions
    /// (matches WelsI4x4LumaPredVR_c / HD_c / VL_c / HU_c).
    /// </summary>
    private static void Intra4x4Mixed(
        ReadOnlySpan<byte> t, ReadOnlySpan<byte> left, byte topLeft,
        Span<byte> output, Intra4x4Mode mode)
    {
        // P[x,y] inlined: y==-1 -> top row (t[x]), x==-1 -> left col (left[y]),
        // (-1,-1) -> topLeft. Negative indices into top mean topLeft.
        // Common neighbor samples.
        byte LT = topLeft;
        byte L0 = left[0], L1 = left[1], L2 = left[2], L3 = left[3];
        byte T0 = t[0], T1 = t[1], T2 = t[2], T3 = t[3];
        byte T4 = t[4], T5 = t[5], T6 = t[6], T7 = t[7];

        switch (mode)
        {
            case Intra4x4Mode.VerticalRight:
                {
                    int VR0 = (LT + T0 + 1) >> 1;
                    int VR1 = (T0 + T1 + 1) >> 1;
                    int VR2 = (T1 + T2 + 1) >> 1;
                    int VR3 = (T2 + T3 + 1) >> 1;
                    int VR4 = (L0 + 2 * LT + T0 + 2) >> 2;
                    int VR5 = (LT + 2 * T0 + T1 + 2) >> 2;
                    int VR6 = (T0 + 2 * T1 + T2 + 2) >> 2;
                    int VR7 = (T1 + 2 * T2 + T3 + 2) >> 2;
                    int VR8 = (LT + 2 * L0 + L1 + 2) >> 2;
                    int VR9 = (L0 + 2 * L1 + L2 + 2) >> 2;
                    // Row 0: VR0 VR1 VR2 VR3
                    // Row 1: VR4 VR5 VR6 VR7
                    // Row 2: VR8 VR0 VR1 VR2
                    // Row 3: VR9 VR4 VR5 VR6
                    output[0] = (byte)VR0; output[1] = (byte)VR1; output[2] = (byte)VR2; output[3] = (byte)VR3;
                    output[4] = (byte)VR4; output[5] = (byte)VR5; output[6] = (byte)VR6; output[7] = (byte)VR7;
                    output[8] = (byte)VR8; output[9] = (byte)VR0; output[10] = (byte)VR1; output[11] = (byte)VR2;
                    output[12] = (byte)VR9; output[13] = (byte)VR4; output[14] = (byte)VR5; output[15] = (byte)VR6;
                    break;
                }
            case Intra4x4Mode.HorizontalDown:
                {
                    int HD0 = (LT + L0 + 1) >> 1;
                    int HD1 = (L0 + 2 * LT + T0 + 2) >> 2;
                    int HD2 = (LT + 2 * T0 + T1 + 2) >> 2;
                    int HD3 = (T0 + 2 * T1 + T2 + 2) >> 2;
                    int HD4 = (L0 + L1 + 1) >> 1;
                    int HD5 = (LT + 2 * L0 + L1 + 2) >> 2;
                    int HD6 = (L1 + L2 + 1) >> 1;
                    int HD7 = (L0 + 2 * L1 + L2 + 2) >> 2;
                    int HD8 = (L2 + L3 + 1) >> 1;
                    int HD9 = (L1 + 2 * L2 + L3 + 2) >> 2;
                    // Row 0: HD0 HD1 HD2 HD3
                    // Row 1: HD4 HD5 HD0 HD1
                    // Row 2: HD6 HD7 HD4 HD5
                    // Row 3: HD8 HD9 HD6 HD7
                    output[0] = (byte)HD0; output[1] = (byte)HD1; output[2] = (byte)HD2; output[3] = (byte)HD3;
                    output[4] = (byte)HD4; output[5] = (byte)HD5; output[6] = (byte)HD0; output[7] = (byte)HD1;
                    output[8] = (byte)HD6; output[9] = (byte)HD7; output[10] = (byte)HD4; output[11] = (byte)HD5;
                    output[12] = (byte)HD8; output[13] = (byte)HD9; output[14] = (byte)HD6; output[15] = (byte)HD7;
                    break;
                }
            case Intra4x4Mode.VerticalLeft:
                {
                    int VL0 = (T0 + T1 + 1) >> 1;
                    int VL1 = (T1 + T2 + 1) >> 1;
                    int VL2 = (T2 + T3 + 1) >> 1;
                    int VL3 = (T3 + T4 + 1) >> 1;
                    int VL4 = (T4 + T5 + 1) >> 1;
                    int VL5 = (T0 + 2 * T1 + T2 + 2) >> 2;
                    int VL6 = (T1 + 2 * T2 + T3 + 2) >> 2;
                    int VL7 = (T2 + 2 * T3 + T4 + 2) >> 2;
                    int VL8 = (T3 + 2 * T4 + T5 + 2) >> 2;
                    int VL9 = (T4 + 2 * T5 + T6 + 2) >> 2;
                    // Row 0: VL0 VL1 VL2 VL3
                    // Row 1: VL5 VL6 VL7 VL8
                    // Row 2: VL1 VL2 VL3 VL4
                    // Row 3: VL6 VL7 VL8 VL9
                    output[0] = (byte)VL0; output[1] = (byte)VL1; output[2] = (byte)VL2; output[3] = (byte)VL3;
                    output[4] = (byte)VL5; output[5] = (byte)VL6; output[6] = (byte)VL7; output[7] = (byte)VL8;
                    output[8] = (byte)VL1; output[9] = (byte)VL2; output[10] = (byte)VL3; output[11] = (byte)VL4;
                    output[12] = (byte)VL6; output[13] = (byte)VL7; output[14] = (byte)VL8; output[15] = (byte)VL9;
                    _ = T7; // T7 unused for VL
                    break;
                }
            case Intra4x4Mode.HorizontalUp:
                {
                    int HU0 = (L0 + L1 + 1) >> 1;
                    int HU1 = (L0 + 2 * L1 + L2 + 2) >> 2;
                    int HU2 = (L1 + L2 + 1) >> 1;
                    int HU3 = (L1 + 2 * L2 + L3 + 2) >> 2;
                    int HU4 = (L2 + L3 + 1) >> 1;
                    int HU5 = (L2 + 3 * L3 + 2) >> 2;
                    // Row 0: HU0 HU1 HU2 HU3
                    // Row 1: HU2 HU3 HU4 HU5
                    // Row 2: HU4 HU5 L3  L3
                    // Row 3: L3  L3  L3  L3
                    output[0] = (byte)HU0; output[1] = (byte)HU1; output[2] = (byte)HU2; output[3] = (byte)HU3;
                    output[4] = (byte)HU2; output[5] = (byte)HU3; output[6] = (byte)HU4; output[7] = (byte)HU5;
                    output[8] = (byte)HU4; output[9] = (byte)HU5; output[10] = L3; output[11] = L3;
                    output[12] = L3; output[13] = L3; output[14] = L3; output[15] = L3;
                    break;
                }
        }
    }
}
