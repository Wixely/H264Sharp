# H264Sharp

A pure-C# / .NET H.264 (AVC) **decoder** and **encoder** with a small command-line
front end. No native dependencies — the core libraries are AOT- and trim-compatible,
and the CLI ships as a single self-contained native binary.

## Projects

| Path | Assembly | Description |
| --- | --- | --- |
| `src/H264Sharp.Decoder` | `H264Sharp.Decoder` | H.264 bitstream decoder (CAVLC/CABAC, intra/inter, deblocking, MP4/Annex-B). |
| `src/H264Sharp.Encoder` | `H264Sharp.Encoder` | H.264 encoder. |
| `src/H264Sharp.Cli`     | `h264sharp`         | Command-line front end (encode / decode / frame extraction). |
| `tests/H264Sharp.Tests` | —                   | xUnit test suite. |
| `tools/*`               | —                   | Internal development/diagnostic tools. |

## Build & test

Requires the .NET SDK pinned in [`global.json`](global.json).

```sh
dotnet build H264Sharp.slnx -c Release
dotnet test  H264Sharp.slnx -c Release
```

## CLI usage

```
h264sharp encode <in.yuv> <out.h264> --size <W>x<H> [--frames N] [--qp 18]
h264sharp <in.h264|in.mp4> <out.yuv|out.png>
h264sharp <in.mp4> <out.png> --at <seconds>
h264sharp <in.mp4> <out.png> --at-pct <0..1>
h264sharp <in.mp4> <out_dir> --frames <spec>      # spec: 'all', 'N', 'N-M', or '5,10-15,20'
h264sharp --info <in.mp4>
```

## Releases

Pre-built standalone binaries for Linux (`linux-x64`) and Windows (`win-x64`) are
attached to each [GitHub Release](../../releases). To cut one, push a version tag:

```sh
git tag v1.0.0
git push origin v1.0.0
```

The [Release workflow](.github/workflows/release.yml) runs the test suite, publishes
native AOT binaries for both platforms, and uploads them to the release. Every push
and pull request is also built and tested on Linux and Windows by the
[CI workflow](.github/workflows/ci.yml).

## Licensing

See [LICENSE-3RDPARTY.md](LICENSE-3RDPARTY.md) for third-party attributions.
