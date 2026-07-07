use std::env;
use std::path::{Path, PathBuf};
use std::process::Command;

// Prism requires C23/C++23. MSVC's cl cannot compile C23 (CMake caps it
// at C17), and MinGW lacks the ATL / C++/WinRT headers the Windows
// backends need — so the DLL is built with clang-cl (MSVC ABI, real SDK
// headers) inside a vcvars environment (which also lets midl.exe find
// cl.exe for the NVDA controller stub). The Rust side imports the C API
// via raw-dylib, so no import library is involved and the Rust toolchain
// doesn't care who produced the DLL.
//
// Machine-specific defaults below are overridable via env vars:
//   SRUI_CMAKE    — cmake.exe (needs C23/clang-cl support; 3.20+)
//   SRUI_NINJA    — ninja.exe
//   SRUI_CLANG_CL — clang-cl.exe
//   SRUI_VCVARS   — vcvars64.bat
//   PRISM_MIDL    — midl.exe (default: newest Windows Kits 10 x64 midl)

const DEFAULT_CMAKE: &str = r"C:\msys64\clang64\bin\cmake.exe";
const DEFAULT_NINJA: &str = r"C:\msys64\clang64\bin\ninja.exe";
const DEFAULT_CLANG_CL: &str = r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\Llvm\x64\bin\clang-cl.exe";
const DEFAULT_VCVARS: &str = r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat";

fn main() {
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let build_dir = out_dir.join("prism-build");
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let prism_dir = manifest_dir.join("prism");

    let cmake = env_or("SRUI_CMAKE", DEFAULT_CMAKE);
    let ninja = env_or("SRUI_NINJA", DEFAULT_NINJA);
    let clang_cl = env_or("SRUI_CLANG_CL", DEFAULT_CLANG_CL);
    let vcvars = env_or("SRUI_VCVARS", DEFAULT_VCVARS);
    let midl = env::var("PRISM_MIDL")
        .ok()
        .or_else(find_midl)
        .expect("midl.exe not found; install a Windows 10/11 SDK or set PRISM_MIDL");

    // CMake wants forward slashes in cache paths; cmd wants the script
    // quoted. A batch file keeps the vcvars environment for both steps.
    let script = out_dir.join("build-prism.bat");
    std::fs::write(
        &script,
        format!(
            "@echo off\r\n\
             call \"{vcvars}\" >nul || exit /b 1\r\n\
             \"{cmake}\" -S \"{src}\" -B \"{bld}\" -G Ninja -DCMAKE_BUILD_TYPE=Release \
             \"-DCMAKE_C_COMPILER={cc}\" \"-DCMAKE_CXX_COMPILER={cc}\" \
             \"-DCMAKE_LINKER={lld}\" \
             \"-DCMAKE_CXX_FLAGS=/EHsc\" \
             \"-DCMAKE_MAKE_PROGRAM={ninja}\" \
             -DPRISM_ENABLE_TESTS=OFF -DPRISM_ENABLE_DEMOS=OFF -DPRISM_ENABLE_GDEXTENSION=OFF \
             \"-DMIDL_COMPILER={midl}\" || exit /b 1\r\n\
             \"{cmake}\" --build \"{bld}\" || exit /b 1\r\n",
            vcvars = vcvars,
            cmake = cmake,
            src = fwd(&prism_dir),
            bld = fwd(&build_dir),
            cc = fwd(Path::new(&clang_cl)),
            lld = fwd(&Path::new(&clang_cl).with_file_name("lld-link.exe")),
            ninja = fwd(Path::new(&ninja)),
            midl = midl,
        ),
    )
    .expect("writing build-prism.bat failed");

    let status = Command::new("cmd")
        .arg("/C")
        .arg(&script)
        .status()
        .expect("failed to run prism build script");
    assert!(status.success(), "prism build failed (see output above)");

    // Copy the DLL next to the built executables (OUT_DIR is
    // target/{profile}/build/{pkg}-{hash}/out; three ancestors up is
    // target/{profile}). raw-dylib in src/lib.rs references "prism.dll".
    let dll = ["prism.dll", "libprism.dll"]
        .iter()
        .map(|n| build_dir.join(n))
        .find(|p| p.exists())
        .expect("built prism DLL not found in build dir");
    if let Some(profile_dir) = out_dir.ancestors().nth(3) {
        std::fs::copy(&dll, profile_dir.join("prism.dll")).expect("copying prism.dll failed");
    }

    println!("cargo:rerun-if-changed=prism/CMakeLists.txt");
    println!("cargo:rerun-if-changed=prism/include/prism.h");
    println!("cargo:rerun-if-changed=prism/source");
    for var in [
        "SRUI_CMAKE",
        "SRUI_NINJA",
        "SRUI_CLANG_CL",
        "SRUI_VCVARS",
        "PRISM_MIDL",
    ] {
        println!("cargo:rerun-if-env-changed={var}");
    }
}

fn env_or(var: &str, default: &str) -> String {
    env::var(var).unwrap_or_else(|_| default.to_string())
}

/// Forward-slashed path string for CMake arguments.
fn fwd(p: &Path) -> String {
    p.to_string_lossy().replace('\\', "/")
}

/// Newest installed Windows Kits 10 x64 midl.exe.
fn find_midl() -> Option<String> {
    let bin = Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin");
    let mut versions: Vec<PathBuf> = std::fs::read_dir(bin)
        .ok()?
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .is_some_and(|n| n.to_string_lossy().starts_with("10."))
        })
        .collect();
    versions.sort();
    versions
        .into_iter()
        .rev()
        .map(|v| v.join("x64").join("midl.exe"))
        .find(|p| p.exists())
        .map(|p| p.to_string_lossy().replace('\\', "/"))
}
