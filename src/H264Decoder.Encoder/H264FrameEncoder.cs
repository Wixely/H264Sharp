using H264Decoder.Bitstream;
using H264Decoder.Encoder.Bitstream;
using H264Decoder.Encoder.Cabac;
using H264Decoder.Encoder.Mode;
using H264Decoder.Encoder.Syntax;
using H264Decoder.Syntax;

namespace H264Decoder.Encoder;

/// <summary>Top-level H.264 encoder: takes YUV 4:2:0 frames and produces Baseline-profile
/// Annex-B byte streams. Phase 1: I-only. Phase 2 adds P_L0_16x16 with single L0 reference
/// (previous decoded frame), integer-pel diamond ME, and P_Skip detection. Output is
/// decodable by our existing H264FrameDecoder and by ffmpeg.</summary>
public static class H264FrameEncoder
{
    /// <summary>Encoder tuning options. Defaults give phase-2 behaviour. Tests can disable
    /// individual optimizations to exercise lower-level paths in isolation.</summary>
    public sealed class Options
    {
        /// <summary>When false, every P-frame MB is emitted as an Intra-only refresh (legacy phase-1).</summary>
        public bool EnableInterPrediction { get; init; } = true;
        /// <summary>When false, the encoder won't fold inter MBs into P_Skip even when eligible — useful
        /// for tests that want to see the explicit P_L0_16x16 mb_type emitted for zero residuals.</summary>
        public bool EnablePSkip { get; init; } = true;
        /// <summary>When false, ME uses a fixed starting MV (predicted median) without any search refinement.</summary>
        public bool EnableMotionSearch { get; init; } = true;
        /// <summary>Max integer-pel search radius for ME.</summary>
        public int SearchRangePel { get; init; } = 16;
        /// <summary>Hard cap on SAD evaluations per MB.</summary>
        public int MaxSadEvalsPerMb { get; init; } = 64;
        /// <summary>When false, ME stops at integer-pel (no half/quarter-pel refinement).</summary>
        public bool EnableSubpelMe { get; init; } = true;
        /// <summary>When false, only P_L0_16x16 / P_Skip / Intra are considered for P MBs
        /// (skips P_L0_L0_16x8 / P_L0_L0_8x16 / P_8x8 mode decision — legacy phase-2 behavior).</summary>
        public bool EnableSubMbPartitions { get; init; } = true;
        /// <summary>Lambda value for SAD + λ*bits cost weighting in partition mode decision.
        /// When 0, decision is by raw SAD (phase 2 behavior). x264-style λ ≈ pow(2,(QP-12)/3).</summary>
        public int ModeDecisionLambda { get; init; } = -1;
        /// <summary>When true (default), Intra-MB mode decision considers Intra_4x4 in addition to
        /// Intra_16x16 and picks the lower-SAD option. Disable to force Intra_16x16 (phase-3 behavior).</summary>
        public bool EnableIntra4x4 { get; init; } = true;
        /// <summary>When true, use CABAC entropy coding instead of CAVLC. Phase 4a enabled CABAC for
        /// I-slice Intra_16x16. Phase 4b extends CABAC to P-slices (P_Skip, P_L0_16x16, P_L0_L0_16x8,
        /// P_L0_L0_8x16, P_8x8 with sub_mb_type 0..3). Intra_4x4 in P-slices and inter MB
        /// transform_8x8 are not yet supported under CABAC.</summary>
        public bool EnableCabac { get; init; } = false;
    }

    /// <summary>Encode a sequence of raw YUV 4:2:0 frames into an Annex-B H.264 byte stream.</summary>
    public static byte[] EncodeAnnexB(ReadOnlySpan<byte> yuv, int width, int height, int qp, int frames = 1)
        => EncodeAnnexB(yuv, width, height, qp, frames, new Options());

    /// <summary>Encode with explicit options (used by tests to disable inter/skip features).</summary>
    public static byte[] EncodeAnnexB(ReadOnlySpan<byte> yuv, int width, int height, int qp, int frames, Options options)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("invalid frame size");
        if (qp < 0 || qp > 51) throw new ArgumentException("qp must be in [0, 51]");
        if (frames <= 0) throw new ArgumentException("frames must be > 0");
        ArgumentNullException.ThrowIfNull(options);

        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline(entropyCodingModeFlag: options.EnableCabac);

        byte[] spsRbsp = SpsWriter.Serialize(sps);
        byte[] ppsRbsp = PpsWriter.Serialize(pps);

        var output = new MemoryStream();
        byte[] spsNal = AnnexBWriter.BuildNalUnit(NalUnitType.Sps, nalRefIdc: 3, spsRbsp);
        byte[] ppsNal = AnnexBWriter.BuildNalUnit(NalUnitType.Pps, nalRefIdc: 3, ppsRbsp);
        AnnexBWriter.WriteAnnexB(output, new[] { spsNal, ppsNal });

        int picWidthInMbs = (int)(sps.PicWidthInMbsMinus1 + 1);
        int picHeightInMbs = (int)(sps.PicHeightInMapUnitsMinus1 + 1);
        int bufferWidth = picWidthInMbs * 16;
        int bufferHeight = picHeightInMbs * 16;
        int bufferChromaWidth = bufferWidth / 2;
        int bufferChromaHeight = bufferHeight / 2;

        int frameBytes = width * height + 2 * (width / 2) * (height / 2);
        if (yuv.Length < frameBytes * frames)
            throw new ArgumentException(
                $"yuv buffer too small: expected {frameBytes * frames}, got {yuv.Length}");

        // Reconstructed reference for inter prediction (previous decoded frame).
        byte[]? refY = null, refU = null, refV = null;
        for (int frameIdx = 0; frameIdx < frames; frameIdx++)
        {
            ReadOnlySpan<byte> frame = yuv.Slice(frameIdx * frameBytes, frameBytes);
            bool isFirst = frameIdx == 0;
            // Phase 4b: CABAC supports P-slices too. Earlier phases gated P off when CABAC was on.
            bool asP = !isFirst && options.EnableInterPrediction;
            byte[] sliceNal;
            byte[] reconY = new byte[bufferWidth * bufferHeight];
            byte[] reconU = new byte[bufferChromaWidth * bufferChromaHeight];
            byte[] reconV = new byte[bufferChromaWidth * bufferChromaHeight];
            if (!asP)
            {
                sliceNal = EncodeIFrame(frame, width, height, qp,
                    picWidthInMbs, picHeightInMbs, bufferWidth, bufferHeight,
                    bufferChromaWidth, bufferChromaHeight,
                    frameNum: (uint)(frameIdx & 0xF), idrPicId: (uint)frameIdx,
                    reconY, reconU, reconV, options);
            }
            else
            {
                sliceNal = EncodePFrame(frame, width, height, qp,
                    picWidthInMbs, picHeightInMbs, bufferWidth, bufferHeight,
                    bufferChromaWidth, bufferChromaHeight,
                    frameNum: (uint)(frameIdx & 0xF),
                    reconY, reconU, reconV,
                    refY!, refU!, refV!,
                    options);
            }
            AnnexBWriter.WriteAnnexB(output, new[] { sliceNal });
            refY = reconY; refU = reconU; refV = reconV;
        }
        return output.ToArray();
    }

    private static byte[] EncodeIFrame(
        ReadOnlySpan<byte> yuv, int width, int height, int qp,
        int picWidthInMbs, int picHeightInMbs,
        int bufferWidth, int bufferHeight, int bufferChromaWidth, int bufferChromaHeight,
        uint frameNum, uint idrPicId,
        byte[] reconYOut, byte[] reconUOut, byte[] reconVOut,
        Options options)
    {
        // Allocate MB-aligned planes with edge padding by replicating the last row/column.
        var srcY = new byte[bufferWidth * bufferHeight];
        var srcU = new byte[bufferChromaWidth * bufferChromaHeight];
        var srcV = new byte[bufferChromaWidth * bufferChromaHeight];
        int yOffset = 0;
        int uOffset = width * height;
        int vOffset = uOffset + (width / 2) * (height / 2);
        CopyPlaneWithEdgePad(yuv.Slice(yOffset, width * height), width, height, srcY, bufferWidth, bufferHeight);
        CopyPlaneWithEdgePad(yuv.Slice(uOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcU, bufferChromaWidth, bufferChromaHeight);
        CopyPlaneWithEdgePad(yuv.Slice(vOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcV, bufferChromaWidth, bufferChromaHeight);

        if (options.EnableCabac)
        {
            return EncodeIFrameCabac(srcY, srcU, srcV, qp,
                picWidthInMbs, picHeightInMbs, bufferWidth, bufferChromaWidth,
                frameNum, idrPicId, reconYOut, reconUOut, reconVOut, width, height);
        }

        var sliceWriter = new BitWriter(4096);
        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline();
        int sliceQpDelta = qp - 26 - pps.PicInitQpMinus26;
        SliceHeaderWriter.Write(sliceWriter, sps, pps,
            frameNum: frameNum, idrPicId: idrPicId,
            sliceQpDelta: sliceQpDelta, disableDeblockingFilterIdc: 1);

        int totalMbs = picWidthInMbs * picHeightInMbs;
        var mbStates = new MacroblockEncoderState?[totalMbs];

        for (int addr = 0; addr < totalMbs; addr++)
        {
            int mbX = addr % picWidthInMbs;
            int mbY = addr / picWidthInMbs;
            int y0 = mbY * 16;
            int x0 = mbX * 16;
            ReadOnlySpan<byte> mbSrcY = srcY.AsSpan(y0 * bufferWidth + x0);
            ReadOnlySpan<byte> mbSrcU = srcU.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            ReadOnlySpan<byte> mbSrcV = srcV.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));

            bool useIntra4x4 = false;
            if (options.EnableIntra4x4)
            {
                // Compare per-MB SAD: Intra_16x16 best-mode SAD vs Intra_4x4 summed-block SAD.
                int sad16 = MacroblockEncoder.EstimateBestIntra16x16Sad(
                    mbSrcY, bufferWidth,
                    reconYOut, bufferWidth, mbX, mbY, mbsPerRow: picWidthInMbs,
                    mbStates, addr);
                int sad4 = IntraEncoder4x4.EstimateMbSad(
                    mbSrcY, bufferWidth,
                    reconYOut, bufferWidth, mbX, mbY, mbsPerRow: picWidthInMbs,
                    mbStates, addr);
                // x264-style bias: prefer Intra_16x16 by λ × (extra mode-bits per MB ≈ 50).
                // Intra_4x4 adds 16 prev-flag bits + ~24 expected rem bits, plus the per-block
                // residual is split into 16 separate CAVLC blocks vs 1 DC+16 AC structure.
                int lambda = DefaultLambda(qp);
                int bias = lambda * 32;
                useIntra4x4 = sad4 + bias < sad16;
            }

            if (useIntra4x4)
            {
                IntraEncoder4x4.EncodeIntra4x4(
                    sliceWriter,
                    mbSrcY, mbSrcU, mbSrcV,
                    srcStrideY: bufferWidth, srcStrideC: bufferChromaWidth,
                    reconYOut, reconUOut, reconVOut,
                    picStrideY: bufferWidth, picStrideC: bufferChromaWidth,
                    mbX, mbY, mbsPerRow: picWidthInMbs,
                    qpY: qp,
                    mbStates, mbAddress: addr);
            }
            else
            {
                MacroblockEncoder.EncodeIntra16x16(
                    sliceWriter,
                    mbSrcY, mbSrcU, mbSrcV,
                    srcStrideY: bufferWidth, srcStrideC: bufferChromaWidth,
                    reconYOut, reconUOut, reconVOut,
                    picStrideY: bufferWidth, picStrideC: bufferChromaWidth,
                    mbX, mbY, mbsPerRow: picWidthInMbs,
                    qpY: qp,
                    mbStates, mbAddress: addr);
            }
        }

        sliceWriter.WriteRbspTrailingBits();
        byte[] sliceRbsp = sliceWriter.ToByteArray();
        return AnnexBWriter.BuildNalUnit(NalUnitType.SliceIdr, nalRefIdc: 3, sliceRbsp);
    }

    private static byte[] EncodePFrame(
        ReadOnlySpan<byte> yuv, int width, int height, int qp,
        int picWidthInMbs, int picHeightInMbs,
        int bufferWidth, int bufferHeight, int bufferChromaWidth, int bufferChromaHeight,
        uint frameNum,
        byte[] reconYOut, byte[] reconUOut, byte[] reconVOut,
        byte[] refY, byte[] refU, byte[] refV,
        Options options)
    {
        var srcY = new byte[bufferWidth * bufferHeight];
        var srcU = new byte[bufferChromaWidth * bufferChromaHeight];
        var srcV = new byte[bufferChromaWidth * bufferChromaHeight];
        int yOffset = 0;
        int uOffset = width * height;
        int vOffset = uOffset + (width / 2) * (height / 2);
        CopyPlaneWithEdgePad(yuv.Slice(yOffset, width * height), width, height, srcY, bufferWidth, bufferHeight);
        CopyPlaneWithEdgePad(yuv.Slice(uOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcU, bufferChromaWidth, bufferChromaHeight);
        CopyPlaneWithEdgePad(yuv.Slice(vOffset, (width / 2) * (height / 2)), width / 2, height / 2, srcV, bufferChromaWidth, bufferChromaHeight);

        var sliceWriter = new BitWriter(4096);
        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline(entropyCodingModeFlag: options.EnableCabac);
        int sliceQpDelta = qp - 26 - pps.PicInitQpMinus26;
        SliceHeaderWriter.WritePSlice(sliceWriter, sps, pps,
            frameNum: frameNum,
            sliceQpDelta: sliceQpDelta, disableDeblockingFilterIdc: 1,
            cabacInitIdc: 0);

        int totalMbs = picWidthInMbs * picHeightInMbs;
        var mbStates = new MacroblockEncoderState?[totalMbs];

        // ---- CABAC state (only used when EnableCabac is true) ----
        H264Decoder.Cabac.CabacEncoder? cabac = null;
        int prevMbQpDeltaState = 0;
        if (options.EnableCabac)
        {
            // Byte-align before CABAC bytes (cabac_alignment_one_bit padding per §7.3.4).
            while ((sliceWriter.BitPosition & 7) != 0) sliceWriter.WriteBit(1u);
            var contexts = new H264Decoder.Cabac.CabacContexts(H264Decoder.Cabac.CabacInitTable.ContextCount);
            int model = 1; // P-slice with cabac_init_idc=0 maps to model index 1.
            for (int ctxIdx = 0; ctxIdx < H264Decoder.Cabac.CabacInitTable.ContextCount; ctxIdx++)
            {
                sbyte m = H264Decoder.Cabac.CabacInitTable.MN[ctxIdx, model, 0];
                sbyte n = H264Decoder.Cabac.CabacInitTable.MN[ctxIdx, model, 1];
                if (m == H264Decoder.Cabac.CabacInitTable.CtxNA) continue;
                contexts.Initialize(ctxIdx, m, n, qp);
            }
            cabac = new H264Decoder.Cabac.CabacEncoder(contexts);
        }

        // mb_skip_run accumulator (CAVLC only): number of skipped MBs since the last emitted MB.
        int pendingSkipRun = 0;
        Span<byte> srcLuma = stackalloc byte[256];

        for (int addr = 0; addr < totalMbs; addr++)
        {
            int mbX = addr % picWidthInMbs;
            int mbY = addr / picWidthInMbs;
            int y0 = mbY * 16;
            int x0 = mbX * 16;
            ReadOnlySpan<byte> mbSrcY = srcY.AsSpan(y0 * bufferWidth + x0);
            ReadOnlySpan<byte> mbSrcU = srcU.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            ReadOnlySpan<byte> mbSrcV = srcV.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));

            // Neighbors for both MV prediction and CAVLC nC.
            var leftMb = mbX > 0 ? mbStates[addr - 1] : null;
            var topMb = mbY > 0 ? mbStates[addr - picWidthInMbs] : null;
            var topRightMb = (mbY > 0 && mbX + 1 < picWidthInMbs) ? mbStates[addr - picWidthInMbs + 1] : null;
            var topLeftMb = (mbY > 0 && mbX > 0) ? mbStates[addr - picWidthInMbs - 1] : null;

            // Compute the median-predicted MV for THIS MB (caller's reference for mvd computation
            // and the start point of ME).
            (int predX, int predY) = MacroblockEncoderInter.PredictMvMedian(leftMb, topMb, topRightMb, topLeftMb);

            // Read 16x16 luma source into a contiguous span.
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    srcLuma[y * 16 + x] = mbSrcY[y * bufferWidth + x];

            // Partition mode decision. If sub-MB partitions are disabled, this always returns a 16x16 candidate.
            int lambda = options.ModeDecisionLambda >= 0 ? options.ModeDecisionLambda : DefaultLambda(qp);
            MacroblockEncoderPartition.PartitionCandidate cand;
            if (options.EnableMotionSearch)
            {
                cand = MacroblockEncoderPartition.ChooseBestPartition(
                    srcLuma,
                    refY, bufferWidth, bufferHeight,
                    mbX, mbY,
                    predX, predY,
                    options.SearchRangePel,
                    options.MaxSadEvalsPerMb,
                    options.EnableSubpelMe,
                    options.EnableSubMbPartitions,
                    lambda);
            }
            else
            {
                cand = new MacroblockEncoderPartition.PartitionCandidate { RawMbType = 0 };
                cand.Partitions.Add(new MacroblockEncoderPartition.Partition(0, 0, 4, 4, predX, predY));
            }

            // Build the inter candidate (predict + residual + reconstruct). For RawMbType=0 we can
            // call the legacy single-MV path; for partitioned shapes we predict per partition first.
            MacroblockEncoderInter.InterEncodeBundle bundle;
            int mvX, mvY;
            if (cand.RawMbType == 0)
            {
                mvX = cand.Partitions[0].MvX;
                mvY = cand.Partitions[0].MvY;
                bundle = MacroblockEncoderInter.BuildInterCandidate(
                    mbSrcY, mbSrcU, mbSrcV,
                    bufferWidth, bufferChromaWidth,
                    refY, refU, refV,
                    bufferWidth, bufferHeight, bufferChromaWidth, bufferChromaHeight,
                    mbX, mbY, qp, mvX, mvY);
            }
            else
            {
                bundle = new MacroblockEncoderInter.InterEncodeBundle();
                MacroblockEncoderPartition.BuildPrediction(
                    cand,
                    refY, bufferWidth, bufferHeight,
                    refU, refV, bufferChromaWidth, bufferChromaHeight,
                    mbX, mbY,
                    bundle.PredY, bundle.PredU, bundle.PredV);
                MacroblockEncoderInter.BuildInterCandidateFromPrediction(bundle, mbSrcY, bufferWidth, qp);
                int qPc = MacroblockEncoderInter.ChromaQpFromLuma(qp);
                MacroblockEncoderInter.EncodeChromaFromPrediction(mbSrcU, mbSrcV, bufferChromaWidth, qPc, bundle);
                mvX = cand.Partitions[0].MvX;
                mvY = cand.Partitions[0].MvY;
            }

            // P_Skip eligibility: only applies to 16x16 partition with single MV matching P_Skip MV
            // AND zero residual AND refIdx=0.
            (int skipMvX, int skipMvY) = MacroblockEncoderInter.DerivePSkipMv(leftMb, topMb, topRightMb, topLeftMb);
            bool eligibleSkip = options.EnablePSkip
                && cand.RawMbType == 0
                && mvX == skipMvX && mvY == skipMvY
                && bundle.CbpLuma == 0 && bundle.CbpChroma == 0;

            if (eligibleSkip)
            {
                var skipState = new MacroblockEncoderState
                {
                    MbAddress = addr,
                    IsSkipped = true,
                    IsInter = true,
                    IsInterP16x16 = false,
                    RawMbType = -1,
                    MvL0X = skipMvX,
                    MvL0Y = skipMvY,
                    RefIdxL0 = 0,
                    QpY = qp,
                };
                // P_Skip implies the whole MB MV is (skipMvX, skipMvY), set per-block.
                for (int i = 0; i < 16; i++)
                {
                    skipState.MvL0XBlock[i] = skipMvX;
                    skipState.MvL0YBlock[i] = skipMvY;
                }
                for (int q = 0; q < 4; q++) skipState.RefIdxL08x8[q] = 0;
                if (options.EnableCabac)
                {
                    // CABAC P_Skip emits mb_skip_flag=1 explicitly, then end_of_slice_flag if last MB.
                    CabacEncSliceP.EncodeMbSkipFlag(cabac!, isSkip: true, leftMb, topMb);
                    bool lastMb = addr == totalMbs - 1;
                    CabacEncSlice.EncodeEndOfSliceFlag(cabac!, lastMb);
                }
                else
                {
                    // CAVLC: accumulate into mb_skip_run.
                    pendingSkipRun++;
                }
                MacroblockEncoderInter.StoreReconToPicture(bundle,
                    reconYOut, reconUOut, reconVOut,
                    bufferWidth, bufferChromaWidth, mbX, mbY);
                bundle.ReconY.CopyTo(skipState.ReconY, 0);
                bundle.ReconU.CopyTo(skipState.ReconU, 0);
                bundle.ReconV.CopyTo(skipState.ReconV, 0);
                mbStates[addr] = skipState;
                continue;
            }

            var state = new MacroblockEncoderState
            {
                MbAddress = addr,
                QpY = qp,
            };

            if (options.EnableCabac)
            {
                // CABAC: emit mb_skip_flag=0 + mb_type + sub_mb_type + mvds + cbp + qp_delta + residual.
                CabacMbEncoderP.EncodeNonSkip(
                    cabac!, cand, bundle, state,
                    addr, qp,
                    leftMb, topMb, topRightMb, topLeftMb,
                    ref prevMbQpDeltaState);
                bool lastMb = addr == totalMbs - 1;
                CabacEncSlice.EncodeEndOfSliceFlag(cabac!, lastMb);
            }
            else
            {
                // CAVLC: flush any pending skip run before this coded MB.
                ExpGolombWriter.WriteUe(sliceWriter, (uint)pendingSkipRun);
                pendingSkipRun = 0;
                if (cand.RawMbType == 0)
                {
                    int mvdX = mvX - predX;
                    int mvdY = mvY - predY;
                    state.IsInter = true;
                    MacroblockEncoderInter.EmitP_L0_16x16(
                        sliceWriter, bundle, qp,
                        mvdX, mvdY, mvX, mvY,
                        refIdxBits: 0,
                        leftMb, topMb, state);
                    for (int i = 0; i < 16; i++)
                    {
                        state.MvL0XBlock[i] = mvX;
                        state.MvL0YBlock[i] = mvY;
                    }
                    for (int qq = 0; qq < 4; qq++) state.RefIdxL08x8[qq] = 0;
                    state.RawMbType = 0;
                }
                else
                {
                    MacroblockEncoderPartition.EmitPartitionMb(
                        sliceWriter, cand, bundle, qp,
                        leftMb, topMb, topRightMb, topLeftMb,
                        state);
                }
            }
            MacroblockEncoderInter.StoreReconToPicture(bundle,
                reconYOut, reconUOut, reconVOut,
                bufferWidth, bufferChromaWidth, mbX, mbY);
            mbStates[addr] = state;
        }

        if (options.EnableCabac)
        {
            // Append CABAC bytes after the byte-aligned slice header. No rbsp_trailing_bits — CABAC
            // ends with end_of_slice_flag, and Finish() produces byte-aligned output.
            byte[] cabacBytes = cabac!.Finish();
            foreach (byte b in cabacBytes) sliceWriter.WriteBits(b, 8);
        }
        else
        {
            // Trailing mb_skip_run for CAVLC P-slice ending in a skip-run.
            if (pendingSkipRun > 0)
            {
                ExpGolombWriter.WriteUe(sliceWriter, (uint)pendingSkipRun);
            }
            sliceWriter.WriteRbspTrailingBits();
        }
        byte[] sliceRbsp = sliceWriter.ToByteArray();
        return AnnexBWriter.BuildNalUnit(NalUnitType.SliceNonIdr, nalRefIdc: 2, sliceRbsp);
    }

    /// <summary>CABAC-mode I-frame encoder. Currently Intra_16x16 only — when EnableCabac is on,
    /// the encoder forces Intra_16x16 selection for all MBs (the CABAC residual encoder for
    /// the Intra_4x4 / I_NxN ctxBlockCat=2 path is not yet wired through). Emits one slice with
    /// CABAC byte-aligned macroblock data ending in end_of_slice_flag.</summary>
    private static byte[] EncodeIFrameCabac(
        byte[] srcY, byte[] srcU, byte[] srcV, int qp,
        int picWidthInMbs, int picHeightInMbs,
        int bufferWidth, int bufferChromaWidth,
        uint frameNum, uint idrPicId,
        byte[] reconYOut, byte[] reconUOut, byte[] reconVOut,
        int width, int height)
    {
        var sliceWriter = new BitWriter(4096);
        var sps = SpsWriter.BuildBaseline(width, height);
        var pps = PpsWriter.BuildBaseline(entropyCodingModeFlag: true);
        int sliceQpDelta = qp - 26 - pps.PicInitQpMinus26;
        SliceHeaderWriter.Write(sliceWriter, sps, pps,
            frameNum: frameNum, idrPicId: idrPicId,
            sliceQpDelta: sliceQpDelta, disableDeblockingFilterIdc: 1);

        // CABAC byte alignment per spec §7.3.4: cabac_alignment_one_bit (one-bit fillers up to byte boundary).
        while ((sliceWriter.BitPosition & 7) != 0) sliceWriter.WriteBit(1u);

        // Build CABAC context state for an I-slice.
        var contexts = new H264Decoder.Cabac.CabacContexts(H264Decoder.Cabac.CabacInitTable.ContextCount);
        int sliceQp = qp;
        int model = 0; // I-slice
        for (int ctxIdx = 0; ctxIdx < H264Decoder.Cabac.CabacInitTable.ContextCount; ctxIdx++)
        {
            sbyte m = H264Decoder.Cabac.CabacInitTable.MN[ctxIdx, model, 0];
            sbyte n = H264Decoder.Cabac.CabacInitTable.MN[ctxIdx, model, 1];
            if (m == H264Decoder.Cabac.CabacInitTable.CtxNA) continue;
            contexts.Initialize(ctxIdx, m, n, sliceQp);
        }
        var cabac = new H264Decoder.Cabac.CabacEncoder(contexts);

        int totalMbs = picWidthInMbs * picHeightInMbs;
        var mbStates = new MacroblockEncoderState?[totalMbs];
        int prevMbQpDeltaState = 0;

        for (int addr = 0; addr < totalMbs; addr++)
        {
            int mbX = addr % picWidthInMbs;
            int mbY = addr / picWidthInMbs;
            int y0 = mbY * 16;
            int x0 = mbX * 16;
            ReadOnlySpan<byte> mbSrcY = srcY.AsSpan(y0 * bufferWidth + x0);
            ReadOnlySpan<byte> mbSrcU = srcU.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            ReadOnlySpan<byte> mbSrcV = srcV.AsSpan((y0 / 2) * bufferChromaWidth + (x0 / 2));
            H264Decoder.Encoder.Cabac.CabacMbEncoder.EncodeIntra16x16(
                cabac,
                mbSrcY, mbSrcU, mbSrcV,
                srcStrideY: bufferWidth, srcStrideC: bufferChromaWidth,
                reconYOut, reconUOut, reconVOut,
                picStrideY: bufferWidth, picStrideC: bufferChromaWidth,
                mbX, mbY, mbsPerRow: picWidthInMbs,
                qpY: qp,
                mbStates, mbAddress: addr,
                ref prevMbQpDeltaState);
            // end_of_slice_flag: 1 only on the final MB.
            bool last = addr == totalMbs - 1;
            H264Decoder.Encoder.Cabac.CabacEncSlice.EncodeEndOfSliceFlag(cabac, last);
        }

        byte[] cabacBytes = cabac.Finish();
        // Append CABAC bytes to the slice RBSP (after byte-aligned slice header).
        foreach (byte b in cabacBytes) sliceWriter.WriteBits(b, 8);
        // No rbsp_trailing_bits for CABAC slices — the bytes are already byte-aligned and
        // the cabac_zero_word handling is done at the NAL level by the spec convention; for
        // our framing the slice NAL just contains the slice header + CABAC bytes.

        byte[] sliceRbsp = sliceWriter.ToByteArray();
        return AnnexBWriter.BuildNalUnit(NalUnitType.SliceIdr, nalRefIdc: 3, sliceRbsp);
    }

    /// <summary>x264-style default λ for SAD+λ*bits mode decision.</summary>
    private static int DefaultLambda(int qp)
    {
        // λ ≈ pow(2, (QP-12)/3), rounded; clamped to a safe range.
        double lam = Math.Pow(2.0, (qp - 12) / 3.0);
        if (lam < 1) lam = 1;
        if (lam > 256) lam = 256;
        return (int)Math.Round(lam);
    }

    private static void CopyPlaneWithEdgePad(
        ReadOnlySpan<byte> src, int srcWidth, int srcHeight,
        byte[] dst, int dstWidth, int dstHeight)
    {
        for (int y = 0; y < dstHeight; y++)
        {
            int srcY = y < srcHeight ? y : srcHeight - 1;
            for (int x = 0; x < dstWidth; x++)
            {
                int srcX = x < srcWidth ? x : srcWidth - 1;
                dst[y * dstWidth + x] = src[srcY * srcWidth + srcX];
            }
        }
    }
}
