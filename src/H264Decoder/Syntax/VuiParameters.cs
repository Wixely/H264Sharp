using H264Decoder.Bitstream;

namespace H264Decoder.Syntax;

/// <summary>
/// VUI parameters — display/timing/colour metadata appended to the SPS
/// (spec §E.1.1). We parse the prefix relevant to YUV→RGB conversion and
/// stop after timing_info; HRD parameters are not consumed.
/// </summary>
public sealed class VuiParameters
{
    public bool AspectRatioInfoPresentFlag { get; init; }
    public byte AspectRatioIdc { get; init; }
    public ushort SarWidth { get; init; }
    public ushort SarHeight { get; init; }

    public bool VideoSignalTypePresentFlag { get; init; }
    public byte VideoFormat { get; init; }
    public bool VideoFullRangeFlag { get; init; }
    public bool ColourDescriptionPresentFlag { get; init; }

    /// <summary>colour_primaries (spec Table E-3). Default = 2 (unspecified) when not signalled.</summary>
    public byte ColourPrimaries { get; init; } = 2;

    /// <summary>transfer_characteristics (Table E-4). Default = 2 (unspecified).</summary>
    public byte TransferCharacteristics { get; init; } = 2;

    /// <summary>matrix_coefficients (Table E-5). Default = 2 (unspecified).
    /// Common values: 1=BT.709, 5=BT.601 (625-line/PAL), 6=BT.601 (525-line/NTSC), 9=BT.2020.</summary>
    public byte MatrixCoefficients { get; init; } = 2;

    public bool TimingInfoPresentFlag { get; init; }
    public uint NumUnitsInTick { get; init; }
    public uint TimeScale { get; init; }
    public bool FixedFrameRateFlag { get; init; }

    public static VuiParameters Parse(ref BitReader r)
    {
        var v = new VuiParameters();

        bool aspectRatio = r.ReadBit() == 1;
        byte arIdc = 0;
        ushort sarW = 0, sarH = 0;
        if (aspectRatio)
        {
            arIdc = (byte)r.ReadBits(8);
            if (arIdc == 255) // Extended_SAR
            {
                sarW = (ushort)r.ReadBits(16);
                sarH = (ushort)r.ReadBits(16);
            }
        }

        bool overscanPresent = r.ReadBit() == 1;
        if (overscanPresent) _ = r.ReadBit();

        bool videoSignalTypePresent = r.ReadBit() == 1;
        byte videoFormat = 5;
        bool videoFullRange = false;
        bool colourDescPresent = false;
        byte colourPrim = 2, transChar = 2, matrix = 2;
        if (videoSignalTypePresent)
        {
            videoFormat = (byte)r.ReadBits(3);
            videoFullRange = r.ReadBit() == 1;
            colourDescPresent = r.ReadBit() == 1;
            if (colourDescPresent)
            {
                colourPrim = (byte)r.ReadBits(8);
                transChar = (byte)r.ReadBits(8);
                matrix = (byte)r.ReadBits(8);
            }
        }

        bool chromaLocPresent = r.ReadBit() == 1;
        if (chromaLocPresent)
        {
            _ = ExpGolomb.ReadUe(ref r);
            _ = ExpGolomb.ReadUe(ref r);
        }

        bool timingPresent = r.ReadBit() == 1;
        uint numUnits = 0, timeScale = 0;
        bool fixedFrameRate = false;
        if (timingPresent)
        {
            numUnits = r.ReadBits(32);
            timeScale = r.ReadBits(32);
            fixedFrameRate = r.ReadBit() == 1;
        }

        // HRD and bitstream restrictions follow — we don't consume them.

        return new VuiParameters
        {
            AspectRatioInfoPresentFlag = aspectRatio,
            AspectRatioIdc = arIdc,
            SarWidth = sarW,
            SarHeight = sarH,
            VideoSignalTypePresentFlag = videoSignalTypePresent,
            VideoFormat = videoFormat,
            VideoFullRangeFlag = videoFullRange,
            ColourDescriptionPresentFlag = colourDescPresent,
            ColourPrimaries = colourPrim,
            TransferCharacteristics = transChar,
            MatrixCoefficients = matrix,
            TimingInfoPresentFlag = timingPresent,
            NumUnitsInTick = numUnits,
            TimeScale = timeScale,
            FixedFrameRateFlag = fixedFrameRate,
        };
    }
}
