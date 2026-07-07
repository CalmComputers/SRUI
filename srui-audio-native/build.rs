use std::env;
use std::path::PathBuf;

// Builds cosmos.dll: miniaudio + the cosmos DSP/HRTF nodes (vendored from
// the cosmos rust project's csrc) with every MA_API symbol dllexported,
// linked against the bundled Steam Audio (phonon). The C# side (Srui.Audio)
// P/Invokes this DLL; no Rust code is involved beyond src/lib.rs's linker
// anchors.

fn main() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());

    cc::Build::new()
        .file("csrc/miniaudio_impl.c")
        .file("csrc/cosmos_extra.c")
        .file("csrc/ma_convreverb.c")
        .file("csrc/ma_delay.c")
        .file("csrc/ma_disperser.c")
        .file("csrc/ma_distortion.c")
        .file("csrc/ma_eq.c")
        .file("csrc/ma_filter.c")
        .file("csrc/ma_vocoder.c")
        .file("csrc/miniaudio_phonon.c")
        .include("csrc")
        .define("MA_NO_WEBAUDIO", None)
        .define("MA_NO_NULL", None)
        // Export every MA_API function from the cdylib.
        .define("MA_API", "__declspec(dllexport)")
        .opt_level(2)
        .compile("cosmos_c");

    // Steam Audio import library; phonon.dll ships beside cosmos.dll.
    println!(
        "cargo:rustc-link-search=native={}",
        manifest_dir.join("phonon").display()
    );
    println!("cargo:rustc-link-lib=dylib=phonon");

    // Copy phonon.dll next to the built binaries (OUT_DIR is
    // target/{profile}/build/{pkg}-{hash}/out).
    if let Some(profile_dir) = out_dir.ancestors().nth(3) {
        let _ = std::fs::copy(
            manifest_dir.join("phonon").join("phonon.dll"),
            profile_dir.join("phonon.dll"),
        );
    }

    println!("cargo:rerun-if-changed=csrc");
}
