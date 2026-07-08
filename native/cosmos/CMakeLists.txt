# cosmos.dll — miniaudio + the cosmos DSP/HRTF nodes with every MA_API
# symbol dllexported, the vendored xiph opus stack (libogg, libopus,
# libopusfile) as the opus decoding backend, linked against the bundled
# Steam Audio (phonon). The C# side (Srui.Audio) P/Invokes this DLL.
#
# Driven by native/build-native.ps1 (Ninja, MSVC cl inside vcvars).
cmake_minimum_required(VERSION 3.20)
project(cosmos C)

# Vendored xiph stack (versions and pruning in ../PATCHES-cosmos.md). Scalar
# builds, no SIMD dispatch: decode is cheap and portability beats the
# last few percent. Warnings are silenced for vendored code only.

add_library(ogg OBJECT
    ogg/bitwise.c
    ogg/framing.c)
target_include_directories(ogg PRIVATE ogg)
target_compile_options(ogg PRIVATE /w)

file(GLOB OPUS_SOURCES CONFIGURE_DEPENDS
    opus/celt/*.c
    opus/silk/*.c
    opus/silk/float/*.c
    opus/src/*.c)
add_library(opus OBJECT ${OPUS_SOURCES})
target_include_directories(opus PRIVATE
    opus/include opus/celt opus/silk opus/silk/float)
# Float decode with alloca-based scratch (MSVC has no VLAs); WIN32
# steers stack_alloc.h to <malloc.h>.
target_compile_definitions(opus PRIVATE OPUS_BUILD USE_ALLOCA WIN32)
target_compile_options(opus PRIVATE /w)

add_library(opusfile OBJECT
    opusfile/opusfile.c
    opusfile/info.c
    opusfile/internal.c
    opusfile/stream.c
    # http.c/wincerts.c compile to stubs without OP_ENABLE_HTTP.
    opusfile/http.c
    opusfile/wincerts.c)
target_include_directories(opusfile PRIVATE opusfile ogg opus/include)
target_compile_options(opusfile PRIVATE /w)

add_library(cosmos SHARED
    miniaudio_impl.c
    cosmos_extra.c
    miniaudio_libopus.c
    ma_convreverb.c
    ma_delay.c
    ma_disperser.c
    ma_distortion.c
    ma_eq.c
    ma_filter.c
    ma_vocoder.c
    miniaudio_phonon.c
    $<TARGET_OBJECTS:ogg>
    $<TARGET_OBJECTS:opus>
    $<TARGET_OBJECTS:opusfile>)
target_include_directories(cosmos PRIVATE . opusfile opus/include ogg)
target_compile_definitions(cosmos PRIVATE
    MA_NO_WEBAUDIO
    MA_NO_NULL
    # Export every MA_API function from the DLL.
    "MA_API=__declspec(dllexport)")

# Steam Audio import library; phonon.dll ships beside cosmos.dll.
target_link_libraries(cosmos PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../phonon/phonon.lib")

set_target_properties(cosmos PROPERTIES OUTPUT_NAME cosmos PREFIX "")
