//! Linker anchors only — the real library is the C code in csrc/, and the
//! consumers are C# (Srui.Audio). Taking one function pointer per object
//! file forces the MSVC linker to include that object (and therefore all
//! of its dllexports) in cosmos.dll; without a reference, objects from the
//! static archive would be discarded.

use std::ffi::c_void;

extern "C" {
    fn ma_engine_alloc() -> *mut c_void;
    fn cosmos_decode_file(
        path: *const i8,
        sr: u32,
        ch: *mut u32,
        osr: *mut u32,
        frames: *mut u64,
    ) -> *mut f32;
    fn ma_convreverb_node_alloc() -> *mut c_void;
    fn ma_delay_fx_alloc() -> *mut c_void;
    fn ma_disperser_node_alloc() -> *mut c_void;
    fn ma_distortion_node_alloc() -> *mut c_void;
    fn ma_eq_node_alloc() -> *mut c_void;
    fn ma_filter_node_alloc() -> *mut c_void;
    fn ma_vocoder_node_alloc() -> *mut c_void;
    fn ma_phonon_is_initialized() -> u32;
}

/// Never called; exists to reference one symbol from each C object file.
#[no_mangle]
pub extern "C" fn cosmos_linker_anchor() -> usize {
    ma_engine_alloc as usize
        ^ cosmos_decode_file as usize
        ^ ma_convreverb_node_alloc as usize
        ^ ma_delay_fx_alloc as usize
        ^ ma_disperser_node_alloc as usize
        ^ ma_distortion_node_alloc as usize
        ^ ma_eq_node_alloc as usize
        ^ ma_filter_node_alloc as usize
        ^ ma_vocoder_node_alloc as usize
        ^ ma_phonon_is_initialized as usize
}
