//! Raw FFI bindings to Prism — the Platform-agnostic Reader Interface
//! for Speech and Messages (https://github.com/ethindp/prism), vendored
//! under `prism/` and built by build.rs.
//!
//! Hand-written against `prism/include/prism.h`. The custom-backend
//! registration surface (`PrismBackendVTable`, `prism_registry_builder_*`)
//! is deliberately omitted; use the safe `srui-prism` wrapper for
//! everything else.

#![allow(non_camel_case_types)]

use std::os::raw::{c_char, c_float, c_int, c_void};

// Opaque handles.
#[repr(C)]
pub struct PrismContext {
    _private: [u8; 0],
}
#[repr(C)]
pub struct PrismBackend {
    _private: [u8; 0],
}
#[repr(C)]
pub struct PrismRegistry {
    _private: [u8; 0],
}

pub type PrismBackendId = u64;

/// `PrismError` — C enum, `int`-sized on all supported targets.
pub type PrismError = c_int;

pub const PRISM_OK: PrismError = 0;
pub const PRISM_ERROR_NOT_INITIALIZED: PrismError = 1;
pub const PRISM_ERROR_INVALID_PARAM: PrismError = 2;
pub const PRISM_ERROR_NOT_IMPLEMENTED: PrismError = 3;
pub const PRISM_ERROR_NO_VOICES: PrismError = 4;
pub const PRISM_ERROR_VOICE_NOT_FOUND: PrismError = 5;
pub const PRISM_ERROR_SPEAK_FAILURE: PrismError = 6;
pub const PRISM_ERROR_MEMORY_FAILURE: PrismError = 7;
pub const PRISM_ERROR_RANGE_OUT_OF_BOUNDS: PrismError = 8;
pub const PRISM_ERROR_INTERNAL: PrismError = 9;
pub const PRISM_ERROR_NOT_SPEAKING: PrismError = 10;
pub const PRISM_ERROR_NOT_PAUSED: PrismError = 11;
pub const PRISM_ERROR_ALREADY_PAUSED: PrismError = 12;
pub const PRISM_ERROR_INVALID_UTF8: PrismError = 13;
pub const PRISM_ERROR_INVALID_OPERATION: PrismError = 14;
pub const PRISM_ERROR_ALREADY_INITIALIZED: PrismError = 15;
pub const PRISM_ERROR_BACKEND_NOT_AVAILABLE: PrismError = 16;
pub const PRISM_ERROR_UNKNOWN: PrismError = 17;
pub const PRISM_ERROR_INVALID_AUDIO_FORMAT: PrismError = 18;
pub const PRISM_ERROR_INTERNAL_BACKEND_LIMIT_EXCEEDED: PrismError = 19;
pub const PRISM_ERROR_BACKEND_ENTERED_UNDEFINED_STATE: PrismError = 20;

pub type PrismLogLevel = c_int;

pub const PRISM_LOG_LEVEL_TRACE: PrismLogLevel = 0;
pub const PRISM_LOG_LEVEL_DEBUG: PrismLogLevel = 1;
pub const PRISM_LOG_LEVEL_INFO: PrismLogLevel = 2;
pub const PRISM_LOG_LEVEL_WARN: PrismLogLevel = 3;
pub const PRISM_LOG_LEVEL_ERROR: PrismLogLevel = 4;
pub const PRISM_LOG_LEVEL_NONE: PrismLogLevel = 5;

pub type PrismAvailabilityCallback = Option<
    unsafe extern "C" fn(
        userdata: *mut c_void,
        backend: PrismBackendId,
        name: *const c_char,
        available: bool,
    ),
>;

pub type PrismAudioCallback = Option<
    unsafe extern "C" fn(
        userdata: *mut c_void,
        samples: *const c_float,
        sample_count: usize,
        channels: usize,
        sample_rate: usize,
    ),
>;

pub const PRISM_CONFIG_VERSION: u8 = 3;

#[repr(C)]
pub struct PrismConfig {
    pub version: u8,
    pub registry: *mut PrismRegistry,
    pub availability_callback: PrismAvailabilityCallback,
    pub availability_userdata: *mut c_void,
    pub availability_poll_interval_ms: u32,
    pub availability_debounce_samples: u32,
    pub availability_backoff_max_ms: u32,
    pub availability_auto_power_manage: bool,
}

// Well-known backend ids.
pub const PRISM_BACKEND_INVALID: PrismBackendId = 0;
pub const PRISM_BACKEND_SAPI: PrismBackendId = 0x1D6DF72422CEEE66;
pub const PRISM_BACKEND_AV_SPEECH: PrismBackendId = 0x28E3429577805C24;
pub const PRISM_BACKEND_VOICE_OVER: PrismBackendId = 0xCB4897961A754BCB;
pub const PRISM_BACKEND_SPEECH_DISPATCHER: PrismBackendId = 0xE3D6F895D949EBFE;
pub const PRISM_BACKEND_NVDA: PrismBackendId = 0x89CC19C5C4AC1A56;
pub const PRISM_BACKEND_JAWS: PrismBackendId = 0xAC3D60E9BD84B53E;
pub const PRISM_BACKEND_ONE_CORE: PrismBackendId = 0x6797D32F0D994CB4;
pub const PRISM_BACKEND_ORCA: PrismBackendId = 0x10AA1FC05A17F96C;
pub const PRISM_BACKEND_ANDROID_SCREEN_READER: PrismBackendId = 0xD199C175AEEC494B;
pub const PRISM_BACKEND_ANDROID_TTS: PrismBackendId = 0xBC175831BFE4E5CC;
pub const PRISM_BACKEND_WEB_SPEECH: PrismBackendId = 0x3572538D44D44A8F;
pub const PRISM_BACKEND_UIA: PrismBackendId = 0x6238F019DB678F8E;
pub const PRISM_BACKEND_ZDSR: PrismBackendId = 0x3D93C56C9E7F2A2E;
pub const PRISM_BACKEND_ZOOM_TEXT: PrismBackendId = 0xAE439D62DC7B1479;
pub const PRISM_BACKEND_BOY_PC_READER: PrismBackendId = 0x285ABA1C16F3300F;
pub const PRISM_BACKEND_PC_TALKER: PrismBackendId = 0x344B951962E3B835;
pub const PRISM_BACKEND_SENSE_READER: PrismBackendId = 0xED4760890B55C2F2;
pub const PRISM_BACKEND_SYSTEM_ACCESS: PrismBackendId = 0x8380F2A37B2C3EB6;
pub const PRISM_BACKEND_WINDOW_EYES: PrismBackendId = 0x9120D89908785C13;
pub const PRISM_BACKEND_SPIEL: PrismBackendId = 0x478B44F14AD3D89C;

// Backend feature flags (u64 bitmask from prism_backend_get_features).
pub const PRISM_BACKEND_IS_SUPPORTED_AT_RUNTIME: u64 = 1 << 0;
pub const PRISM_BACKEND_SUPPORTS_SPEAK: u64 = 1 << 2;
pub const PRISM_BACKEND_SUPPORTS_SPEAK_TO_MEMORY: u64 = 1 << 3;
pub const PRISM_BACKEND_SUPPORTS_BRAILLE: u64 = 1 << 4;
pub const PRISM_BACKEND_SUPPORTS_OUTPUT: u64 = 1 << 5;
pub const PRISM_BACKEND_SUPPORTS_IS_SPEAKING: u64 = 1 << 6;
pub const PRISM_BACKEND_SUPPORTS_STOP: u64 = 1 << 7;
pub const PRISM_BACKEND_SUPPORTS_PAUSE: u64 = 1 << 8;
pub const PRISM_BACKEND_SUPPORTS_RESUME: u64 = 1 << 9;
pub const PRISM_BACKEND_SUPPORTS_SET_VOLUME: u64 = 1 << 10;
pub const PRISM_BACKEND_SUPPORTS_GET_VOLUME: u64 = 1 << 11;
pub const PRISM_BACKEND_SUPPORTS_SET_RATE: u64 = 1 << 12;
pub const PRISM_BACKEND_SUPPORTS_GET_RATE: u64 = 1 << 13;
pub const PRISM_BACKEND_SUPPORTS_SET_PITCH: u64 = 1 << 14;
pub const PRISM_BACKEND_SUPPORTS_GET_PITCH: u64 = 1 << 15;
pub const PRISM_BACKEND_SUPPORTS_REFRESH_VOICES: u64 = 1 << 16;
pub const PRISM_BACKEND_SUPPORTS_COUNT_VOICES: u64 = 1 << 17;
pub const PRISM_BACKEND_SUPPORTS_GET_VOICE_NAME: u64 = 1 << 18;
pub const PRISM_BACKEND_SUPPORTS_GET_VOICE_LANGUAGE: u64 = 1 << 19;
pub const PRISM_BACKEND_SUPPORTS_GET_VOICE: u64 = 1 << 20;
pub const PRISM_BACKEND_SUPPORTS_SET_VOICE: u64 = 1 << 21;
pub const PRISM_BACKEND_SUPPORTS_GET_CHANNELS: u64 = 1 << 22;
pub const PRISM_BACKEND_SUPPORTS_GET_SAMPLE_RATE: u64 = 1 << 23;
pub const PRISM_BACKEND_SUPPORTS_GET_BIT_DEPTH: u64 = 1 << 24;
pub const PRISM_BACKEND_PERFORMS_SILENCE_TRIMMING_ON_SPEAK: u64 = 1 << 25;
pub const PRISM_BACKEND_PERFORMS_SILENCE_TRIMMING_ON_SPEAK_TO_MEMORY: u64 = 1 << 26;
pub const PRISM_BACKEND_SUPPORTS_SPEAK_SSML: u64 = 1 << 27;
pub const PRISM_BACKEND_SUPPORTS_SPEAK_TO_MEMORY_SSML: u64 = 1 << 28;

// raw-dylib: imports resolve against prism.dll at load time with no
// import library, which lets the DLL come from a different (C23-capable)
// toolchain than the Rust build.
#[link(name = "prism", kind = "raw-dylib")]
extern "C" {
    // Context lifecycle.
    pub fn prism_config_init() -> PrismConfig;
    pub fn prism_init(cfg: *mut PrismConfig) -> *mut PrismContext;
    pub fn prism_shutdown(ctx: *mut PrismContext);
    pub fn prism_availability_poll_pause(ctx: *mut PrismContext);
    pub fn prism_availability_poll_resume(ctx: *mut PrismContext);
    pub fn prism_availability_auto_power_supported() -> bool;

    // Registry queries.
    pub fn prism_registry_count(ctx: *mut PrismContext) -> usize;
    pub fn prism_registry_id_at(ctx: *mut PrismContext, index: usize) -> PrismBackendId;
    pub fn prism_registry_id(ctx: *mut PrismContext, name: *const c_char) -> PrismBackendId;
    pub fn prism_registry_name(ctx: *mut PrismContext, id: PrismBackendId) -> *const c_char;
    pub fn prism_registry_priority(ctx: *mut PrismContext, id: PrismBackendId) -> c_int;
    pub fn prism_registry_exists(ctx: *mut PrismContext, id: PrismBackendId) -> bool;
    pub fn prism_registry_get(ctx: *mut PrismContext, id: PrismBackendId) -> *mut PrismBackend;
    pub fn prism_registry_create(ctx: *mut PrismContext, id: PrismBackendId)
        -> *mut PrismBackend;
    pub fn prism_registry_create_best(ctx: *mut PrismContext) -> *mut PrismBackend;
    pub fn prism_registry_acquire(ctx: *mut PrismContext, id: PrismBackendId)
        -> *mut PrismBackend;
    pub fn prism_registry_acquire_best(ctx: *mut PrismContext) -> *mut PrismBackend;

    // Backend lifecycle and info.
    pub fn prism_backend_free(backend: *mut PrismBackend);
    pub fn prism_backend_name(backend: *mut PrismBackend) -> *const c_char;
    pub fn prism_backend_get_features(backend: *mut PrismBackend) -> u64;
    pub fn prism_backend_initialize(backend: *mut PrismBackend) -> PrismError;

    // Output.
    pub fn prism_backend_speak(
        backend: *mut PrismBackend,
        text: *const c_char,
        interrupt: bool,
    ) -> PrismError;
    pub fn prism_backend_speak_to_memory(
        backend: *mut PrismBackend,
        text: *const c_char,
        callback: PrismAudioCallback,
        userdata: *mut c_void,
    ) -> PrismError;
    pub fn prism_backend_braille(backend: *mut PrismBackend, text: *const c_char) -> PrismError;
    pub fn prism_backend_output(
        backend: *mut PrismBackend,
        text: *const c_char,
        interrupt: bool,
    ) -> PrismError;
    pub fn prism_backend_stop(backend: *mut PrismBackend) -> PrismError;
    pub fn prism_backend_pause(backend: *mut PrismBackend) -> PrismError;
    pub fn prism_backend_resume(backend: *mut PrismBackend) -> PrismError;
    pub fn prism_backend_is_speaking(
        backend: *mut PrismBackend,
        out_speaking: *mut bool,
    ) -> PrismError;

    // Parameters.
    pub fn prism_backend_set_volume(backend: *mut PrismBackend, volume: c_float) -> PrismError;
    pub fn prism_backend_get_volume(
        backend: *mut PrismBackend,
        out_volume: *mut c_float,
    ) -> PrismError;
    pub fn prism_backend_set_rate(backend: *mut PrismBackend, rate: c_float) -> PrismError;
    pub fn prism_backend_get_rate(
        backend: *mut PrismBackend,
        out_rate: *mut c_float,
    ) -> PrismError;
    pub fn prism_backend_set_pitch(backend: *mut PrismBackend, pitch: c_float) -> PrismError;
    pub fn prism_backend_get_pitch(
        backend: *mut PrismBackend,
        out_pitch: *mut c_float,
    ) -> PrismError;

    // Voices.
    pub fn prism_backend_refresh_voices(backend: *mut PrismBackend) -> PrismError;
    pub fn prism_backend_count_voices(
        backend: *mut PrismBackend,
        out_count: *mut usize,
    ) -> PrismError;
    pub fn prism_backend_get_voice_name(
        backend: *mut PrismBackend,
        voice_id: usize,
        out_name: *mut *const c_char,
    ) -> PrismError;
    pub fn prism_backend_get_voice_language(
        backend: *mut PrismBackend,
        voice_id: usize,
        out_language: *mut *const c_char,
    ) -> PrismError;
    pub fn prism_backend_set_voice(backend: *mut PrismBackend, voice_id: usize) -> PrismError;
    pub fn prism_backend_get_voice(
        backend: *mut PrismBackend,
        out_voice_id: *mut usize,
    ) -> PrismError;

    // Audio format queries.
    pub fn prism_backend_get_channels(
        backend: *mut PrismBackend,
        out_channels: *mut usize,
    ) -> PrismError;
    pub fn prism_backend_get_sample_rate(
        backend: *mut PrismBackend,
        out_sample_rate: *mut usize,
    ) -> PrismError;
    pub fn prism_backend_get_bit_depth(
        backend: *mut PrismBackend,
        out_bit_depth: *mut usize,
    ) -> PrismError;

    // Diagnostics.
    pub fn prism_error_string(error: PrismError) -> *const c_char;
    pub fn prism_set_log_level(level: PrismLogLevel) -> PrismLogLevel;
}
