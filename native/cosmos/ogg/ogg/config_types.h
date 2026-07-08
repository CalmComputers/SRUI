/* Hand-written for the SRUI vendored build (normally autoconf-generated;
 * see srui-audio-native/PATCHES.md). Only reached on toolchains where
 * os_types.h falls through to the generic branch. */
#ifndef __CONFIG_TYPES_H__
#define __CONFIG_TYPES_H__

#include <stdint.h>

typedef int16_t ogg_int16_t;
typedef uint16_t ogg_uint16_t;
typedef int32_t ogg_int32_t;
typedef uint32_t ogg_uint32_t;
typedef int64_t ogg_int64_t;
typedef uint64_t ogg_uint64_t;

#endif
