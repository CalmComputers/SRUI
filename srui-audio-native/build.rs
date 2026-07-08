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

    // Vendored xiph stack for the opus decoding backend (versions and
    // pruning in PATCHES.md). Scalar builds, no SIMD dispatch: decode is
    // cheap and portability beats the last few percent here.
    cc::Build::new()
        .file("csrc/ogg/bitwise.c")
        .file("csrc/ogg/framing.c")
        .include("csrc/ogg")
        .warnings(false)
        .opt_level(2)
        .compile("ogg");

    let mut opus = cc::Build::new();
    for sub in ["celt", "silk", "silk/float", "src"] {
        let dir = manifest_dir.join("csrc/opus").join(sub);
        let mut files: Vec<PathBuf> = std::fs::read_dir(&dir)
            .unwrap_or_else(|e| panic!("reading {}: {e}", dir.display()))
            .map(|entry| entry.unwrap().path())
            .filter(|p| p.extension().is_some_and(|ext| ext == "c"))
            .collect();
        files.sort();
        for file in files {
            opus.file(file);
        }
    }
    opus.include("csrc/opus/include")
        .include("csrc/opus/celt")
        .include("csrc/opus/silk")
        .include("csrc/opus/silk/float")
        .define("OPUS_BUILD", None)
        // Float decode with alloca-based scratch (MSVC has no VLAs);
        // WIN32 steers stack_alloc.h to <malloc.h>.
        .define("USE_ALLOCA", None)
        .define("WIN32", None)
        .warnings(false)
        .opt_level(2)
        .compile("opus");

    cc::Build::new()
        .file("csrc/opusfile/opusfile.c")
        .file("csrc/opusfile/info.c")
        .file("csrc/opusfile/internal.c")
        .file("csrc/opusfile/stream.c")
        // http.c/wincerts.c compile to stubs without OP_ENABLE_HTTP.
        .file("csrc/opusfile/http.c")
        .file("csrc/opusfile/wincerts.c")
        .include("csrc/opusfile")
        .include("csrc/ogg")
        .include("csrc/opus/include")
        .warnings(false)
        .opt_level(2)
        .compile("opusfile");

    cc::Build::new()
        .file("csrc/miniaudio_impl.c")
        .file("csrc/cosmos_extra.c")
        .file("csrc/miniaudio_libopus.c")
        .file("csrc/ma_convreverb.c")
        .file("csrc/ma_delay.c")
        .file("csrc/ma_disperser.c")
        .file("csrc/ma_distortion.c")
        .file("csrc/ma_eq.c")
        .file("csrc/ma_filter.c")
        .file("csrc/ma_vocoder.c")
        .file("csrc/miniaudio_phonon.c")
        .include("csrc")
        .include("csrc/opusfile")
        .include("csrc/opus/include")
        .include("csrc/ogg")
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
