# Vendored Sources and Local Patches

`cosmos/` is vendored from the cosmos rust project (miniaudio, its impl
helpers, the cosmos DSP nodes, and the Steam Audio glue); `phonon/`
carries the Steam Audio 4 binaries. Local changes to re-apply when
updating the snapshot:

# 1. miniaudio_impl.c: MA_API on the helper functions

The seven helpers (ma_engine_alloc/free, ma_sound_alloc/free,
ma_engine_init/uninit_with_caching, ma_sound_get_node_ptr) gained MA_API
so they export from cosmos.dll.

# 2. miniaudio_phonon.h: declare the alloc/free helpers

ma_phonon_binaural_node_alloc/free are defined in the .c but were not
declared in the header; callers hit C's implicit-int declaration and the
returned pointer was truncated to 32 bits (access violation on x64).
Declared with MA_API.

# 3. cosmos_extra.c (new file)

SRUI-added convenience wrappers so the C# side never marshals structs:
whole-file decode to malloc'd f32 PCM, audio-buffer-ref create/destroy,
binaural node create/destroy with the global phonon context baked in,
and create/destroy pairs for the seven effect nodes.

# 4. Vendored opus stack (new: ogg/, opus/, opusfile/, miniaudio_libopus.*)

Opus decoding via the xiph reference libraries, pruned to the sources
the decoder needs (no docs, tests, build systems, or arch-specific
SIMD trees; scalar float build):

- ogg/ — libogg 1.3.5: bitwise.c, framing.c, crctable.h, headers.
  ogg/ogg/config_types.h is hand-written (normally autoconf-generated).
- opus/ — opus 1.4: celt/, silk/ (+ silk/float/), src/ minus the
  demo/compare tools. Built with OPUS_BUILD, USE_ALLOCA, WIN32.
- opusfile/ — opusfile 0.12: all of src/ (http.c/wincerts.c compile to
  stubs without OP_ENABLE_HTTP) plus opusfile.h.
- miniaudio_libopus.h/.c — the decoding-backend shim from the miniaudio
  repo (extras/decoders/libopus at tag 0.11.23). Two local edits: the
  header's `#include "../../../miniaudio.h"` became `"miniaudio.h"` to
  match this layout, and the vtable's onInitFile is NULL — it reached
  op_open_file, whose ANSI fopen fails on non-ASCII paths on Windows;
  with it disabled every consumer takes the callbacks path through
  miniaudio's UTF-8-aware VFS.

All three libraries are BSD-licensed; each directory keeps its COPYING.

# 5. Opus backend registration (edits to vendored files)

`ma_decoding_backend_libopus` is registered wherever a decoder is
configured, so every decode path accepts .opus:

- miniaudio_impl.c: ma_engine_init_with_caching sets
  ppCustomDecodingBackendVTables on the resource manager config
  (covers ma_sound_init_from_file / Sound.Load).
- cosmos_extra.c: cosmos_decode_file sets ppCustomBackendVTables
  (covers LoadStretched / LoadReversed).
- ma_convreverb.c: ma_convreverb_node_load_ir_file sets
  ppCustomBackendVTables (covers impulse responses).

# 6. miniaudio_impl.c: caller-chosen period, granted-period getter, shared decode cache

ma_engine_init_with_caching takes a periodSizeInFrames parameter (0
selects the 128-frame default — low trigger-to-ear latency; larger
values buy mixing headroom for heavy scenes), and a new exported helper
ma_engine_get_actual_period_frames returns the period the device
actually granted (WASAPI aligns the request; IAudioClient3 clamps it to
the driver's range). The C# side sizes the Steam Audio frame from the
granted value, so the phonon block and the device period cannot
disagree regardless of what the driver decides.

The resource manager (the decode cache) is process-global and
refcounted rather than per-engine: init reuses a live cache, and
ma_engine_uninit_with_caching takes a keepCache flag that preserves it
past the last engine — the engine-rebuild path (a period change via
SoundManager.Reconfigure) sets it so the rebuilt engine reuses every
decoded file instead of re-decoding.

# 7. miniaudio.h: UTF-8 paths open wide on Windows

The managed side passes file paths as UTF-8, but miniaudio's file
opens read them in the ANSI codepage — any non-ASCII filename failed
to open, for every format. Two sites patched, both converting UTF-8
to UTF-16 first and falling back to the stock behavior only if
conversion fails:

- ma_default_vfs_open__win32: CreateFileA → CreateFileW. This is the
  open behind the resource manager and the decoders (the one that
  matters at runtime on Windows).
- ma_fopen (MSVC branch): fopen_s → _wfopen_s. The stdio VFS fallback,
  patched for consistency.

This is also why the libopus vtable keeps onInitFile disabled in
entry 4: one consistent, VFS-mediated open path for everything.

# 8. Media additions: cosmos_media.c (new file) and the binauralizer

cosmos_media.c carries the media-playback surface: a device-free
duration probe over the same decoder stack as cosmos_decode_file
(cosmos_probe_duration_ms), a pull data source whose read/seek
callbacks let managed decoders feed the engine (cosmos_pull_ds_*),
struct-free wrappers over miniaudio's order-based filter nodes
(lpf/hpf/bpf/loshelf/hishelf/peak: create/reinit/destroy, stereo f32),
and cosmos_binauralizer_node_create/destroy with the global phonon
context baked in.

miniaudio_phonon.c/h additionally regained the binauralizer node
(ma_phonon_binauralizer_node_*: stereo in, stereo out, two parallel
HRTFs summed) — ported back from the Lightspeed rust-era glue this
file descends from.

# 9. miniaudio_impl.c: callback deadline counters

ma_engine_init_with_caching substitutes a timing wrapper for the
engine's internal device data callback (via engineConfig.dataCallback;
the engine still sets the device's pUserData to itself, so the wrapper
forwards straight to ma_engine_data_callback_internal). The wrapper
counts callbacks and deadline misses — a callback exceeding
frameCount / sampleRate is a buffer underrun — and the exported
ma_engine_get_callback_stats reads (and optionally resets) the
counters. Surfaced in C# as SoundManager.GetCallbackStats.
