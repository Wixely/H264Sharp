# Third-Party Software Attribution

This project contains data tables derived from third-party open-source software.
The terms of the third-party license below apply to those derived portions and
to any reproduction of the table values in this repository.

The original third-party source files are not compiled into the built artifacts
of this project; they are present in the repository only as reference inputs to
the table-generation tooling (`tools/CavlcGen`). The generated tables (e.g.,
`src/H264Sharp.Decoder/Cabac/CabacInitTable.cs`) include attribution headers pointing
back to the third-party origin.

---

## OpenH264 (Cisco Systems)

**Project**: https://github.com/cisco/openh264
**License**: BSD-2-Clause

The following data tables in this project are derived from OpenH264:

- `src/H264Sharp.Decoder/Cabac/CabacInitTable.cs` — generated from OpenH264's
  `g_kiCabacGlobalContextIdx` table (CABAC context initialization values per
  ITU-T H.264 Tables 9-12 through 9-24).

The original OpenH264 source file used by the table-generation tool is included
at `tools/CavlcGen/reference/openh264_common_tables.cpp` with its original
copyright header preserved.

### License text

```
Copyright (c) 2013, Cisco Systems
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

   * Redistributions of source code must retain the above copyright
     notice, this list of conditions and the following disclaimer.

   * Redistributions in binary form must reproduce the above copyright
     notice, this list of conditions and the following disclaimer in
     the documentation and/or other materials provided with the
     distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

---

## Test-time-only dependencies (not redistributed)

The following tools are invoked as external subprocesses during testing and
development. They are not linked, statically or dynamically, into this
project's compiled artifacts. License obligations of these tools therefore do
not propagate to consumers of this project:

- **ffmpeg** — used by the test suite (`tests/H264Sharp.Tests/Fixtures/`)
  to generate H.264 bitstream test fixtures and reference YUV outputs.
  Invoked as `ffmpeg.exe` via `System.Diagnostics.Process`.

- **OpenH264 decoder binary (patched)** — used during CABAC bin-trace
  debugging only. Not part of the test suite, not shipped, not referenced
  by repository code.

Standard ITU-T H.264 specification (Rec. ITU-T H.264 | ISO/IEC 14496-10)
formulas, syntax structures, and lookup tables defined directly by the spec
are not subject to third-party copyright; they are implemented from the
public specification.
