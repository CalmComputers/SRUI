// Miniaudio implementation file
// This is compiled by build.rs

// Include stb_vorbis for OGG support
#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

// Now include the stb_vorbis implementation
#undef STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

// SRUI: opus decoding backend over the vendored xiph stack (PATCHES.md).
#include "miniaudio_libopus.h"

#include <stdlib.h>

// Helper functions for Rust bindings - allocate opaque structs
MA_API ma_engine* ma_engine_alloc(void) {
    return (ma_engine*)malloc(sizeof(ma_engine));
}

MA_API void ma_engine_free(ma_engine* engine) {
    free(engine);
}

MA_API ma_sound* ma_sound_alloc(void) {
    return (ma_sound*)malloc(sizeof(ma_sound));
}

MA_API void ma_sound_free(ma_sound* sound) {
    free(sound);
}

// SRUI: callback deadline instrumentation. The device data callback has
// frameCount / sampleRate seconds to produce its buffer; exceeding that
// budget is a buffer underrun (an audible gap or click) regardless of
// what the mix sounds like. The wrapper times every callback so hosts
// can tell real underruns apart from other click sources (resampler
// pitch steps, source starts). Single writer (the audio thread); the
// reader takes benign races — these are diagnostics, not control flow.
static ma_uint64 g_cb_callbacks   = 0;   // callbacks observed
static ma_uint64 g_cb_overruns    = 0;   // callbacks that missed the deadline
static ma_uint64 g_cb_max_ns      = 0;   // slowest callback seen
static ma_uint64 g_cb_budget_ns   = 0;   // most recent per-callback budget

static void cosmos_timed_data_callback(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount) {
    ma_timer timer;
    ma_uint64 elapsedNs;
    ma_uint64 budgetNs;

    ma_timer_init(&timer);
    ma_engine_data_callback_internal(pDevice, pOutput, pInput, frameCount);
    elapsedNs = (ma_uint64)(ma_timer_get_time_in_seconds(&timer) * 1.0e9);

    budgetNs = ((ma_uint64)frameCount * 1000000000ULL) / pDevice->sampleRate;
    g_cb_budget_ns = budgetNs;
    g_cb_callbacks += 1;
    if (elapsedNs > g_cb_max_ns) {
        g_cb_max_ns = elapsedNs;
    }
    if (elapsedNs > budgetNs) {
        g_cb_overruns += 1;
    }
}

// SRUI: read (and optionally reset) the callback timing counters. Any
// out-pointer may be NULL. Reset clears the counters but not the budget.
MA_API void ma_engine_get_callback_stats(ma_uint64* pCallbacks, ma_uint64* pOverruns, ma_uint64* pMaxNs, ma_uint64* pBudgetNs, ma_uint32 reset) {
    if (pCallbacks != NULL) *pCallbacks = g_cb_callbacks;
    if (pOverruns  != NULL) *pOverruns  = g_cb_overruns;
    if (pMaxNs     != NULL) *pMaxNs     = g_cb_max_ns;
    if (pBudgetNs  != NULL) *pBudgetNs  = g_cb_budget_ns;
    if (reset) {
        g_cb_callbacks = 0;
        g_cb_overruns  = 0;
        g_cb_max_ns    = 0;
    }
}

// SRUI: the resource manager (the decode cache) is process-global and
// refcounted so it can outlive any single engine: an engine rebuild (a
// period change) or a second engine reuses already-decoded files
// instead of re-decoding them.
static ma_resource_manager* g_resource_manager = NULL;
static int g_resource_manager_refs = 0;

MA_API ma_result ma_engine_init_with_caching(ma_engine* pEngine, ma_uint32 periodSizeInFrames) {
    ma_result result;
    int createdCacheHere = 0;

    if (g_resource_manager == NULL) {
        g_resource_manager = (ma_resource_manager*)malloc(sizeof(ma_resource_manager));
        if (g_resource_manager == NULL) {
            return MA_OUT_OF_MEMORY;
        }

        ma_resource_manager_config rmConfig = ma_resource_manager_config_init();
        // SRUI: register the opus decoding backend (stock decoders handle
        // wav/flac/mp3, stb_vorbis handles ogg vorbis).
        static ma_decoding_backend_vtable* customBackends[1];
        customBackends[0] = ma_decoding_backend_libopus;
        rmConfig.ppCustomDecodingBackendVTables = customBackends;
        rmConfig.customDecodingBackendCount = 1;
        result = ma_resource_manager_init(&rmConfig, g_resource_manager);
        if (result != MA_SUCCESS) {
            free(g_resource_manager);
            g_resource_manager = NULL;
            return result;
        }
        createdCacheHere = 1;
    }

    // Create engine with resource manager
    ma_engine_config engineConfig = ma_engine_config_init();
    engineConfig.pResourceManager = g_resource_manager;
    // SRUI: caller-chosen period; 0 selects the 128-frame default —
    // small for low trigger-to-ear latency (~2.7ms at 48kHz), larger
    // for headroom under heavy mixing loads. WASAPI may grant a
    // different size (IAudioClient3 clamps to the driver's supported
    // range); callers read the granted value via
    // ma_engine_get_actual_period_frames and size the phonon frame to
    // match, so the request and the Steam Audio block never disagree.
    engineConfig.periodSizeInFrames = (periodSizeInFrames == 0) ? 128 : periodSizeInFrames;
    // SRUI: substitute the timed wrapper for the engine's internal data
    // callback (the engine still sets the device's pUserData to itself,
    // so the wrapper forwards straight to the internal callback).
    engineConfig.dataCallback = cosmos_timed_data_callback;
    // SRUI: ramp volume changes instead of stepping them. Hosts drive
    // per-tick distance attenuation through ma_sound_set_volume; with no
    // smoothing each write is a mid-waveform gain jump — an audible
    // click on every moving source. ~5ms at 48kHz: long enough to kill
    // the click, short enough that attenuation still tracks motion.
    engineConfig.defaultVolumeSmoothTimeInPCMFrames = 240;

    result = ma_engine_init(&engineConfig, pEngine);
    if (result != MA_SUCCESS) {
        if (createdCacheHere) {
            ma_resource_manager_uninit(g_resource_manager);
            free(g_resource_manager);
            g_resource_manager = NULL;
        }
        return result;
    }

    g_resource_manager_refs++;
    return MA_SUCCESS;
}

// SRUI: the period size the device actually granted (frames), which can
// differ from the request — WASAPI aligns it, and IAudioClient3 clamps
// it to the driver's supported range. The C# side reads this to size
// the phonon frame and to report latency diagnostics.
MA_API ma_uint32 ma_engine_get_actual_period_frames(ma_engine* pEngine) {
    ma_device* pDevice = ma_engine_get_device(pEngine);
    if (pDevice == NULL) {
        return 0;
    }
    return pDevice->playback.internalPeriodSizeInFrames;
}

// SRUI: keepCache nonzero preserves the decode cache past the last
// engine — the engine-rebuild path (period change) sets it so the
// immediately following init reuses every decoded file.
MA_API void ma_engine_uninit_with_caching(ma_engine* pEngine, ma_uint32 keepCache) {
    ma_engine_uninit(pEngine);

    if (g_resource_manager_refs > 0) {
        g_resource_manager_refs--;
    }
    if (g_resource_manager != NULL && g_resource_manager_refs == 0 && !keepCache) {
        ma_resource_manager_uninit(g_resource_manager);
        free(g_resource_manager);
        g_resource_manager = NULL;
    }
}

// Node graph functions for HRTF integration
MA_API ma_node* ma_sound_get_node_ptr(ma_sound* pSound) {
    return &pSound->engineNode.baseNode;
}
