#!/bin/sh
# Builds every native library the .NET projects need on macOS, into
# native/out/, as universal (arm64 + x86_64) binaries:
#
#   libcosmos.dylib  — miniaudio + cosmos DSP + opus stack (CMake, Apple clang)
#   libprism.dylib   — speech via Prism's VoiceOver and AVSpeech backends
#                      (CMake, Apple clang)
#   libphonon.dylib  — Steam Audio, shipped as a prebuilt binary
#                      (native/phonon/macos/, from the Steam Audio SDK)
#   libSDL3.dylib    — prebuilt (native/prebuilt/macos/); --rebuild-sdl
#                      rebuilds it from the pinned SDL source release
#
# The macOS counterpart of build-native.ps1. Each library keeps an
# @rpath install name, and cosmos finds libphonon.dylib through an
# @loader_path rpath, so the set loads from any one directory.
#
# Requires cmake and ninja on PATH (Homebrew: brew install cmake ninja) and
# the Xcode command line tools. Overridable via env vars:
#   SRUI_CMAKE        — cmake
#   SRUI_NINJA        — ninja
#   SRUI_MACOS_ARCHS  — CMAKE_OSX_ARCHITECTURES (default "arm64;x86_64")
#   SRUI_MACOS_MIN    — CMAKE_OSX_DEPLOYMENT_TARGET (default 11.0)

set -eu

native=$(cd "$(dirname "$0")" && pwd)
cmake=${SRUI_CMAKE:-cmake}
ninja=${SRUI_NINJA:-ninja}
archs=${SRUI_MACOS_ARCHS:-"arm64;x86_64"}
min=${SRUI_MACOS_MIN:-11.0}

# The SDL release the prebuilt matches; keep in step with prebuilt/SDL3.dll.
sdl_version=3.4.12

out="$native/out"
build="$native/build"
mkdir -p "$out" "$build"

configure() {
    # configure <source> <build dir> [extra cmake args...]
    src=$1
    dir=$2
    shift 2
    "$cmake" -S "$src" -B "$dir" -G Ninja -DCMAKE_BUILD_TYPE=Release \
        "-DCMAKE_MAKE_PROGRAM=$ninja" \
        "-DCMAKE_OSX_ARCHITECTURES=$archs" \
        "-DCMAKE_OSX_DEPLOYMENT_TARGET=$min" \
        "$@"
}

if [ "${1:-}" = "--rebuild-sdl" ]; then
    sdl_build="$build/sdl"
    rm -rf "$sdl_build"
    mkdir -p "$sdl_build"
    curl -fsSL -o "$sdl_build/SDL3.tar.gz" \
        "https://github.com/libsdl-org/SDL/releases/download/release-$sdl_version/SDL3-$sdl_version.tar.gz"
    tar -xzf "$sdl_build/SDL3.tar.gz" -C "$sdl_build"
    configure "$sdl_build/SDL3-$sdl_version" "$sdl_build/out" \
        -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TEST_LIBRARY=OFF \
        -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
    "$cmake" --build "$sdl_build/out"
    mkdir -p "$native/prebuilt/macos"
    cp "$sdl_build/out/libSDL3.0.dylib" "$native/prebuilt/macos/libSDL3.dylib"
    # .NET resolves "SDL3" to libSDL3.dylib; match the install name to the
    # file name, then re-sign, since editing the header voids the signature.
    install_name_tool -id @rpath/libSDL3.dylib "$native/prebuilt/macos/libSDL3.dylib"
    codesign --force --sign - "$native/prebuilt/macos/libSDL3.dylib"
fi

# cosmos — Apple clang via Ninja
configure "$native/cosmos" "$build/cosmos-macos"
"$cmake" --build "$build/cosmos-macos"

# prism — Apple clang via Ninja
configure "$native/prism" "$build/prism-macos" \
    -DPRISM_ENABLE_TESTS=OFF -DPRISM_ENABLE_DEMOS=OFF -DPRISM_ENABLE_GDEXTENSION=OFF
"$cmake" --build "$build/prism-macos"

# Stage everything into out/.
cp -f "$build/cosmos-macos/libcosmos.dylib" "$out/"
cp -f "$build/prism-macos/libprism.dylib" "$out/"
cp -f "$native/phonon/macos/libphonon.dylib" "$out/"
cp -f "$native/prebuilt/macos/libSDL3.dylib" "$out/"

echo "native libraries staged in $out"
