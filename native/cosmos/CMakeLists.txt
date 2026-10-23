# cosmos.dll / libcosmos.dylib — miniaudio + the cosmos DSP/HRTF nodes with
# every MA_API symbol exported, the vendored xiph opus stack (libogg,
# libopus, libopusfile) as the opus decoding backend, linked against the
# bundled Steam Audio (phonon). The C# side (Srui.Audio) P/Invokes this
# library.
#
# Driven by native/build-native.ps1 on Windows (Ninja, MSVC cl inside
# vcvars) and native/build-native.sh on macOS (Ninja, Apple clang).
cmake_minimum_required(VERSION 3.20)
project(cosmos C)

# Vendored code builds with warnings off, in each compiler's own spelling.
if(MSVC)
    set(COSMOS_NO_WARNINGS /w)
else()
    set(COSMOS_NO_WARNINGS -w)
endif()

# Vendored xiph stack (versions and pruning in ../PATCHES-cosmos.md). Scalar
# builds, no SIMD dispatch: decode is cheap and portability beats the
# last few percent. Warnings are silenced for vendored code only.

add_library(ogg OBJECT
    ogg/bitwise.c
    ogg/framing.c)
target_include_directories(ogg PRIVATE ogg)
target_compile_options(ogg PRIVATE ${COSMOS_NO_WARNINGS})

file(GLOB OPUS_SOURCES CONFIGURE_DEPENDS
    opus/celt/*.c
    opus/silk/*.c
    opus/silk/float/*.c
    opus/src/*.c)
add_library(opus OBJECT ${OPUS_SOURCES})
target_include_directories(opus PRIVATE
    opus/include opus/celt opus/silk opus/silk/float)
# Float decode with alloca-based scratch (MSVC has no VLAs); WIN32
# steers stack_alloc.h to <malloc.h>, elsewhere it includes <alloca.h>.
target_compile_definitions(opus PRIVATE OPUS_BUILD USE_ALLOCA)
if(WIN32)
    target_compile_definitions(opus PRIVATE WIN32)
endif()
target_compile_options(opus PRIVATE ${COSMOS_NO_WARNINGS})

add_library(opusfile OBJECT
    opusfile/opusfile.c
    opusfile/info.c
    opusfile/internal.c
    opusfile/stream.c
    # http.c/wincerts.c compile to stubs without OP_ENABLE_HTTP.
    opusfile/http.c
    opusfile/wincerts.c)
target_include_directories(opusfile PRIVATE opusfile ogg opus/include)
target_compile_options(opusfile PRIVATE ${COSMOS_NO_WARNINGS})

add_library(cosmos SHARED
    miniaudio_impl.c
    cosmos_extra.c
    cosmos_media.c
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
    MA_NO_NULL)

if(WIN32)
    # Export every MA_API function from the DLL.
    target_compile_definitions(cosmos PRIVATE "MA_API=__declspec(dllexport)")

    # Steam Audio import library; phonon.dll ships beside cosmos.dll.
    target_link_libraries(cosmos PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/../phonon/phonon.lib")

    set_target_properties(cosmos PROPERTIES OUTPUT_NAME cosmos PREFIX "")
elseif(APPLE)
    # AAC/M4A/M4B/ALAC through AudioToolbox, the macOS counterpart of the
    # managed Media Foundation reader on Windows.
    target_sources(cosmos PRIVATE cosmos_apple_audio.c)

    # Everything else is hidden, so the dylib exports exactly the MA_API
    # surface, as the DLL does.
    set_target_properties(cosmos PROPERTIES C_VISIBILITY_PRESET hidden)
    target_compile_definitions(cosmos PRIVATE
        "MA_API=__attribute__((visibility(\"default\")))"
        # Link CoreAudio at build time instead of dlopen-ing it at run time,
        # which Apple's notarization requires (miniaudio.h, "macOS").
        MA_NO_RUNTIME_LINKING)
    target_link_libraries(cosmos PRIVATE
        "-framework CoreFoundation"
        "-framework CoreAudio"
        "-framework AudioToolbox"
        # libphonon.dylib ships beside libcosmos.dylib; its install name is
        # @rpath/libphonon.dylib, so the rpath is the loader's own directory.
        "${CMAKE_CURRENT_SOURCE_DIR}/../phonon/macos/libphonon.dylib")
    set_target_properties(cosmos PROPERTIES
        OUTPUT_NAME cosmos
        INSTALL_NAME_DIR "@rpath"
        BUILD_RPATH "@loader_path"
        INSTALL_RPATH "@loader_path")
else()
    message(FATAL_ERROR "cosmos: no build recipe for ${CMAKE_SYSTEM_NAME}")
endif()
