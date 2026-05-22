using H264Decoder.Syntax;

namespace H264Decoder.Cabac;

/// <summary>
/// CABAC parser for B-slice non-skip macroblocks (spec §7.3.5.1 + §9.3.3.1).
/// Stage-2 stub: currently only the B_Skip pathway is supported via the
/// outer slice loop (mb_skip_flag-handled before this is called). All
/// non-skip B-MBs throw NotSupported.
/// </summary>
internal static class CabacSliceB
{
    public static Macroblock ParseMb(
        CabacDecoder cabac,
        SliceHeader sliceHeader,
        Macroblock? leftMb,
        Macroblock? topMb,
        Macroblock? topRightMb,
        Macroblock? topLeftMb,
        int mbAddress,
        ref int qpYRunning,
        ref int prevMbQpDeltaState,
        bool transform8x8ModeFlag = false)
    {
        _ = cabac; _ = sliceHeader; _ = leftMb; _ = topMb; _ = topRightMb; _ = topLeftMb;
        _ = mbAddress; _ = qpYRunning; _ = prevMbQpDeltaState; _ = transform8x8ModeFlag;
        throw new NotSupportedException(
            "CABAC B-slice non-skip macroblock decoding not yet implemented (CAVLC works; CABAC handles B_Skip only).");
    }
}
