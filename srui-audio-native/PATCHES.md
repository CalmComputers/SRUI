# Vendored Sources and Local Patches

`csrc/` is vendored from the cosmos rust project (miniaudio, its impl
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
