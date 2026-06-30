# H264Sharp

A pure-C# / .NET implementation of an H.264 (AVC) **decoder** and **encoder**, with a
small command-line front end. There are **no native dependencies** — the core libraries
are AOT- and trim-compatible, and every executable publishes as a single self-contained
native binary.

- **Decoder** — parses Annex-B, length-prefixed AVCC, and MP4 (incl. fragmented MP4)
  inputs; decodes I/P/B slices with CAVLC and CABAC; outputs planar YUV 4:2:0 or PNG.
- **Encoder** — encodes raw YUV 4:2:0 into Annex-B H.264 (Baseline/Main), CAVLC or CABAC,
  with intra/inter prediction, motion estimation, and optional B-frames.
- **CLI (`h264sharp`)** — encode, decode, probe (`--info`), grab thumbnails, and batch
  extract frames to PNG.

> H264Sharp is a from-scratch, readable implementation built for learning and
> experimentation, not a drop-in replacement for libavcodec. See
> [Feature support](#feature-support) for exactly what is and isn't implemented.

## Projects

| Path | Assembly / binary | Description |
| --- | --- | --- |
| `src/H264Sharp.Decoder` | `H264Sharp.Decoder` | H.264 bitstream decoder (CAVLC/CABAC, intra/inter, deblocking, MP4/Annex-B/AVCC). |
| `src/H264Sharp.Encoder` | `H264Sharp.Encoder` | H.264 encoder (depends on the decoder for shared transform/syntax types). |
| `src/H264Sharp.Cli`     | `h264sharp`         | Command-line front end. |
| `tests/H264Sharp.Tests` | —                   | xUnit test suite (433 tests). |
| `tools/*`               | `BinTrace`, `YuvDump`, `CavlcGen` | Development/diagnostic tools (also AOT-publishable). |

## Install

Grab a standalone binary for your platform from the
[latest release](../../releases/latest):

- Linux x64 — `h264sharp-linux-x64`
- Windows x64 — `h264sharp-win-x64.exe`

```sh
# Linux: make it executable and (optionally) put it on your PATH
chmod +x h264sharp-linux-x64
./h264sharp-linux-x64 --info clip.mp4
```

No .NET runtime install is required — the binaries are fully self-contained.

## CLI usage

```
h264sharp encode <in.yuv> <out.h264> --size <W>x<H> [--frames N] [--qp Q] [--cabac] [--intra4x4|--no-intra4x4]
h264sharp <in.h264|in.mp4> <out.yuv|out.png>          # decode the first I-frame
h264sharp <in.mp4> <out.png> --at <seconds|mm:ss>     # thumbnail at a timestamp
h264sharp <in.mp4> <out.png> --at-pct <0..1>          # thumbnail at % of duration
h264sharp <in.mp4> <out_dir> --frames <spec>          # batch-extract frames to PNG
h264sharp --info <in.mp4|in.h264>                      # probe resolution/fps/profile
```

`--frames <spec>` accepts `all`, a single index `89`, a range `12-39`, or a
comma-separated mix like `5,10-15,20`. Frames are written display-ordered as
`frame_NNNNN.png`. Extraction is parallelized across GOPs.

### Examples

```sh
# Probe a file (works on MP4, Annex-B, or AVCC)
h264sharp --info clip.mp4
#   duration: 12.480 s
#   resolution: 1280x720
#   frames: 312 (25.00 fps)
#   profile: High

# Decode the first keyframe to a PNG...
h264sharp clip.mp4 poster.png
# ...or to raw planar YUV 4:2:0
h264sharp clip.h264 frame0.yuv

# Thumbnail 5 seconds in (or 44% of the way through)
h264sharp clip.mp4 thumb.png --at 5
h264sharp clip.mp4 thumb.png --at 1:23.5
h264sharp clip.mp4 thumb.png --at-pct 0.44

# Extract a range of frames as PNGs into ./out
h264sharp clip.mp4 out --frames 0-50
h264sharp clip.mp4 out --frames all

# Encode a raw 320x240 YUV 4:2:0 clip (3 frames) at QP 22 with CABAC
h264sharp encode raw.yuv out.h264 --size 320x240 --frames 3 --qp 22 --cabac
```

The encoder's input is **headerless planar YUV 4:2:0** (full Y plane, then U, then V;
8-bit). One frame is `W*H + 2*(W/2)*(H/2)` bytes. Use macroblock-aligned dimensions
(multiples of 16). You can produce a test clip with ffmpeg:

```sh
ffmpeg -i clip.mp4 -pix_fmt yuv420p -s 320x240 raw.yuv
```

Encoder flags: `--qp 0..51` (lower = higher quality, default 18), `--cabac` (CABAC entropy
coding instead of the CAVLC default), `--intra4x4` / `--no-intra4x4` (default on).
Set `H264_VERBOSE=1` to print full stack traces on decode/parse errors.

## Library usage

Reference `H264Sharp.Decoder` (and `H264Sharp.Encoder`) and call the static entry points.

```csharp
using H264Sharp.Decoder;
using H264Sharp.Encoder;

// --- Decode ---
byte[] stream = File.ReadAllBytes("clip.h264");        // Annex-B, AVCC, or MP4 bytes
var decoder = new H264FrameDecoder();

DecodedPicture first = decoder.DecodeFirstIFrame(stream);
Console.WriteLine($"{first.Width}x{first.Height}");
// first.Y / first.U / first.V are planar 8-bit buffers (stride = BufferWidth).

List<DecodedPicture> all = decoder.DecodeAllFrames(stream);  // display (POC) order

// --- Encode ---
byte[] yuv = File.ReadAllBytes("raw.yuv");              // planar YUV 4:2:0
byte[] annexB = H264FrameEncoder.EncodeAnnexB(yuv, width: 320, height: 240, qp: 22, frames: 3);

byte[] withCabac = H264FrameEncoder.EncodeAnnexB(
    yuv, 320, 240, qp: 22, frames: 3,
    new H264FrameEncoder.Options { EnableCabac = true, EnableBFrames = true });
```

## Feature support

**Decoder**

- Containers: Annex-B start-code streams, length-prefixed AVCC, MP4 / fragmented MP4
  (with stream-walked `moov` and sample timing for accurate seeks).
- Entropy: CAVLC and CABAC.
- Slices: I, P, B (with POC-based display ordering and B-pyramid handling).
- Prediction: Intra 4x4 / 16x16 / I_PCM; inter P/B with sub-pel motion compensation.
- Reference management incl. long-term references; multi-slice frames; in-loop deblocking.
- Interlaced: partial MBAFF (frame-coded I-slice macroblock pairs); PAFF and full
  field decoding are not implemented.
- Profiles parsed/reported: Baseline, Main, Extended, High, High10/4:2:2/4:4:4
  (8-bit 4:2:0 decode path).
- Output: planar YUV 4:2:0, or RGB/PNG via the built-in PNG encoder (honoring VUI color info).

**Encoder**

- Output: Annex-B H.264, QP 0–51. Default streams are Baseline-style CAVLC; enabling
  CABAC or B-frames raises the stream to Main-profile features.
- Entropy: CAVLC, or CABAC for I- and P-slices.
- Intra: Intra_16x16 and Intra_4x4 mode decision.
- Inter (P): P_Skip, P_L0_16x16, 16x8, 8x16, and P_8x8 sub-partitions; integer + half/
  quarter-pel motion estimation with configurable search range and λ-based mode decision.
- B-frames: IPBP GOP (CAVLC). See `H264FrameEncoder.Options` for the full toggle list and
  the current limits of the CABAC/B-slice paths.

## Build & test

Requires the .NET SDK pinned in [`global.json`](global.json).

```sh
dotnet build H264Sharp.slnx -c Release
dotnet test  H264Sharp.slnx -c Release
```

Some end-to-end tests shell out to **ffmpeg** to generate reference bitstreams; they are
tagged `Category=Ffmpeg`. The fixture looks for ffmpeg via the `FFMPEG` environment
variable (falling back to `C:\FFMPEG\bin\ffmpeg.exe`). To run only the tests that need no
external tools (this is what CI runs):

```sh
dotnet test H264Sharp.slnx -c Release --filter "Category!=Ffmpeg"
```

### Publishing a standalone binary locally

```sh
dotnet publish src/H264Sharp.Cli/H264Sharp.Cli.csproj -c Release -r linux-x64   # or win-x64
# -> a single native ./publish/h264sharp (or h264sharp.exe)
```

Native AOT publishing needs a platform toolchain: `clang` + `zlib1g-dev` on Linux, the
Visual C++ build tools on Windows.

## Releases

Standalone binaries for Linux (`linux-x64`) and Windows (`win-x64`) are attached to each
[GitHub Release](../../releases). To cut one, push a version tag:

```sh
git tag v1.0.0
git push origin v1.0.0
```

The [Release workflow](.github/workflows/release.yml) runs the test suite, publishes
native AOT binaries for both platforms, and uploads them to the release. Every push and
pull request is also built and tested on Linux and Windows by the
[CI workflow](.github/workflows/ci.yml).

## Licensing

This project is licensed under the [MIT License](LICENSE). See
[LICENSE-3RDPARTY.md](LICENSE-3RDPARTY.md) for third-party attributions.
