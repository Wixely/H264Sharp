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

    private static void Intra4x4Mixed(
        ReadOnlySpan<byte> t, ReadOnlySpan<byte> left, byte topLeft,
        Span<byte> output, Intra4x4Mode mode)
    {
        // P[x,y] inlined: y==-1 -> top row (t[x]), x==-1 -> left col (left[y]),
        // (-1,-1) -> topLeft. Negative indices into top mean topLeft.
        switch (mode)
        {
            case Intra4x4Mode.VerticalRight:
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int zVR = 2 * x - y;
                            int xt = x - (y >> 1); // index into top row
                            int v;
                            if (zVR == 0 || zVR == 2 || zVR == 4 || zVR == 6)
                            {
                                int p1 = xt - 1 == -1 ? topLeft : t[xt - 1];
                                v = (p1 + t[xt] + 1) >> 1;
                            }
                            else if (zVR == -1)
                            {
                                v = (left[0] + 2 * topLeft + t[0] + 2) >> 2;
                            }
                            else if (zVR == 1 || zVR == 3 || zVR == 5)
                            {
                                int p0 = xt - 2 == -1 ? topLeft : t[xt - 2];
                                int p1 = xt - 1 == -1 ? topLeft : t[xt - 1];
                                v = (p0 + 2 * p1 + t[xt] + 2) >> 2;
                            }
                            else // zVR == -2 or -3
                            {
                                int p0 = y - 2 == -1 ? topLeft : left[y - 2];
                                int p1 = y - 1 == -1 ? topLeft : left[y - 1];
                                v = (left[y] + 2 * p1 + p0 + 2) >> 2;
                            }
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }
            case Intra4x4Mode.HorizontalDown:
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int zHD = 2 * y - x;
                            int yl = y - (x >> 1);
                            int v;
                            if (zHD == 0 || zHD == 2 || zHD == 4 || zHD == 6)
                            {
                                int p1 = yl - 1 == -1 ? topLeft : left[yl - 1];
                                v = (p1 + left[yl] + 1) >> 1;
                            }
                            else if (zHD == -1)
                            {
                                v = (left[0] + 2 * topLeft + t[0] + 2) >> 2;
                            }
                            else if (zHD == 1 || zHD == 3 || zHD == 5)
                            {
                                int p0 = yl - 2 == -1 ? topLeft : left[yl - 2];
                                int p1 = yl - 1 == -1 ? topLeft : left[yl - 1];
                                v = (p0 + 2 * p1 + left[yl] + 2) >> 2;
                            }
                            else // zHD == -2 or -3
                            {
                                int p0 = x - 2 == -1 ? topLeft : t[x - 2];
                                int p1 = x - 1 == -1 ? topLeft : t[x - 1];
                                v = (t[x] + 2 * p1 + p0 + 2) >> 2;
                            }
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }
            case Intra4x4Mode.VerticalLeft:
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int i = x + (y >> 1);
                            int v = ((y & 1) == 0)
                                ? (t[i] + t[i + 1] + 1) >> 1
                                : (t[i] + 2 * t[i + 1] + t[i + 2] + 2) >> 2;
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }
            case Intra4x4Mode.HorizontalUp:
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int zHU = x + 2 * y;
                            int i = y + (x >> 1);
                            int v;
                            if (zHU == 0 || zHU == 2 || zHU == 4)
                                v = (left[i] + left[i + 1] + 1) >> 1;
                            else if (zHU == 1 || zHU == 3)
                                v = (left[i] + 2 * left[i + 1] + left[Math.Min(i + 2, 3)] + 2) >> 2;
                            else if (zHU == 5)
                                v = (left[2] + 3 * left[3] + 2) >> 2;
                            else
                                v = left[3];
                            output[y * 4 + x] = (byte)v;
                        }
                    break;
                }
        }
    }
}
