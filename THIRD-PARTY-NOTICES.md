# SRUI Third-Party Notices

# 1. Scope

SRUI's own code — the `Srui` and `Srui.Audio` assemblies, and the DSP
sources under `native/cosmos` written for this project (`cosmos_extra.c`,
`cosmos_media.c`, `miniaudio_libopus.c`, `miniaudio_phonon.c`, and the
`ma_*.c` effect nodes) — is licensed under Apache-2.0, the text of which is
in `LICENSE` at the repository root.

The four native libraries SRUI loads are third-party work, or bundle
third-party work, under their own terms. This document records those terms
for anyone redistributing the binaries. It ships inside both NuGet packages.

Full licence texts live beside the components they cover: `native/prism/LICENSE`
and `native/prism/LICENSES/`, `native/cosmos/ogg/COPYING`,
`native/cosmos/opus/COPYING`, `native/cosmos/opusfile/COPYING`,
`native/phonon/LICENSE.md`, and `native/prebuilt/SDL3-LICENSE.txt`.

# 2. prism.dll — speech output

Prism itself is under the Mozilla Public License 2.0. It bundles the
following:

| Component | Licence |
|---|---|
| Prism | MPL-2.0 |
| concurrentqueue | BSD-2-Clause or BSL-1.0, at your option |
| djinni | Apache-2.0 |
| dr_wav | Unlicense or MIT-0, at your option |
| moderncom | MIT |
| NVGT | Zlib |
| NVDA controller interface | LGPL-2.1 |
| simdutf | Apache-2.0 |

The NVDA entry covers `nvdaController.idl`, the interface definition from
which the build generates an RPC client stub with `midl`. NVDA's own
controller client library is neither linked nor redistributed.

# 3. cosmos.dll — audio engine and DSP

The cosmos-specific sources are SRUI's own (section 1). The library bundles:

| Component | Licence |
|---|---|
| miniaudio | Unlicense or MIT-0, at your option |
| stb_vorbis | Unlicense or MIT, at your option |
| libogg | BSD-3-Clause |
| libopus | BSD-3-Clause |
| opusfile | BSD-3-Clause |

Local modifications to the vendored sources are recorded in
`native/PATCHES-cosmos.md`.

# 4. phonon.dll — Steam Audio

Steam Audio, copyright Valve Corporation, under Apache-2.0. Redistributed as
an unmodified prebuilt binary; the licence text is at
`native/phonon/LICENSE.md`.

# 5. SDL3.dll — window and keyboard input

SDL, copyright Sam Lantinga and contributors, under the Zlib licence.
Redistributed as an unmodified prebuilt binary from an official SDL release;
the licence text is at `native/prebuilt/SDL3-LICENSE.txt`.

# 6. What redistribution requires

Shipping the SRUI binaries carries three obligations beyond attribution.

- **Apache-2.0** (SRUI, Steam Audio, and the Apache-licensed pieces inside
  prism) requires that a copy of the licence and any applicable notices
  travel with the distribution. Including this file and `LICENSE` satisfies
  it.
- **MPL-2.0** (Prism) requires that recipients of the binary can obtain the
  corresponding source, including modifications. SRUI vendors Prism at
  `native/prism` in a public repository, with local changes recorded in
  `native/PATCHES-prism.md`, which satisfies this for anyone receiving SRUI
  from that repository or from NuGet. A closed redistribution that does not
  point back to it must make the source available by some other route.
- **LGPL-2.1** (the NVDA controller interface inside prism) applies to the
  interface definition described in section 2. It does not reach SRUI's own
  code, which neither includes nor links it.

The Zlib, MIT, MIT-0, BSD, Boost, and Unlicense terms in the table above ask
only that the notices are preserved, which this file does.
