/* SRUI additions for C# consumers: struct-free convenience wrappers so
 * the managed side only ever passes pointers, scalars, and strings. */

#include <stdint.h> /* phonon.h (via miniaudio_phonon.h) needs uint8_t */
#include "miniaudio.h"
#include "miniaudio_phonon.h"
#include <stdlib.h>

/* Decode an audio file entirely into memory as interleaved f32, resampled
 * to targetSampleRate. Returns a malloc'd buffer to release with
 * cosmos_free, or NULL on failure. */
MA_API float* cosmos_decode_file(
    const char* path,
    ma_uint32 targetSampleRate,
    ma_uint32* outChannels,
    ma_uint32* outSampleRate,
    ma_uint64* outFrames)
{
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 0, targetSampleRate);
    ma_decoder decoder;
    ma_format fmt;
    ma_uint32 channels = 0;
    ma_uint32 sr = 0;
    ma_uint64 lengthFrames = 0;
    ma_uint64 cap;
    float* samples;
    ma_uint64 total = 0; /* frames */

    if (ma_decoder_init_file(path, &config, &decoder) != MA_SUCCESS) {
        return NULL;
    }
    if (ma_decoder_get_data_format(&decoder, &fmt, &channels, &sr, NULL, 0) != MA_SUCCESS
        || channels == 0) {
        ma_decoder_uninit(&decoder);
        return NULL;
    }

    ma_data_source_get_length_in_pcm_frames(&decoder, &lengthFrames); /* best-effort */
    cap = (lengthFrames > 0 ? lengthFrames : 4096) * channels;
    samples = (float*)malloc((size_t)cap * sizeof(float));
    if (samples == NULL) {
        ma_decoder_uninit(&decoder);
        return NULL;
    }

    for (;;) {
        ma_uint64 framesRead = 0;
        ma_result r;
        if ((total + 4096) * channels > cap) {
            ma_uint64 newCap = cap * 2;
            float* grown = (float*)realloc(samples, (size_t)newCap * sizeof(float));
            if (grown == NULL) {
                free(samples);
                ma_decoder_uninit(&decoder);
                return NULL;
            }
            samples = grown;
            cap = newCap;
        }
        r = ma_decoder_read_pcm_frames(&decoder, samples + total * channels, 4096, &framesRead);
        total += framesRead;
        if (r != MA_SUCCESS || framesRead == 0) {
            break;
        }
    }

    ma_decoder_uninit(&decoder);
    *outChannels = channels;
    *outSampleRate = sr;
    *outFrames = total;
    return samples;
}

MA_API void cosmos_free(void* p)
{
    free(p);
}

/* Non-owning data-source view over caller-provided f32 PCM. The PCM must
 * outlive the returned ref (the C# Sound owns both). */
MA_API ma_audio_buffer_ref* cosmos_buffer_ref_create(
    ma_uint32 channels,
    const float* pcm,
    ma_uint64 frames)
{
    ma_audio_buffer_ref* ref = (ma_audio_buffer_ref*)malloc(sizeof(ma_audio_buffer_ref));
    if (ref == NULL) {
        return NULL;
    }
    if (ma_audio_buffer_ref_init(ma_format_f32, channels, pcm, frames, ref) != MA_SUCCESS) {
        free(ref);
        return NULL;
    }
    return ref;
}

MA_API void cosmos_buffer_ref_destroy(ma_audio_buffer_ref* ref)
{
    if (ref != NULL) {
        ma_audio_buffer_ref_uninit(ref);
        free(ref);
    }
}

/* Binaural node with the global phonon context baked in, so consumers
 * never touch IPL types. NULL when phonon is unavailable. */
MA_API ma_phonon_binaural_node* cosmos_binaural_node_create(
    ma_node_graph* graph,
    ma_uint32 channelsIn)
{
    ma_phonon_binaural_node* node;
    ma_phonon_binaural_node_config cfg;

    if (!ma_phonon_is_initialized()) {
        return NULL;
    }
    node = ma_phonon_binaural_node_alloc();
    if (node == NULL) {
        return NULL;
    }
    cfg = ma_phonon_binaural_node_config_init(
        channelsIn,
        ma_phonon_get_audio_settings(),
        ma_phonon_get_context(),
        ma_phonon_get_hrtf());
    if (ma_phonon_binaural_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        ma_phonon_binaural_node_free(node);
        return NULL;
    }
    return node;
}

MA_API void cosmos_binaural_node_destroy(ma_phonon_binaural_node* node)
{
    if (node != NULL) {
        ma_phonon_binaural_node_uninit(node, NULL);
        ma_phonon_binaural_node_free(node);
    }
}

/* Effect-node create/destroy pairs. The per-node config structs stay on
 * this side of the boundary; consumers get an opaque node pointer that is
 * also directly usable as a ma_node* for graph wiring. Channels are
 * always 2 (the nodes require stereo). */
#include "ma_convreverb.h"
#include "ma_delay.h"
#include "ma_disperser.h"
#include "ma_distortion.h"
#include "ma_eq.h"
#include "ma_filter.h"
#include "ma_vocoder.h"

#define COSMOS_EFFECT_PAIR(name, type)                                        \
    MA_API type* cosmos_##name##_create(ma_node_graph* graph, ma_uint32 sampleRate) \
    {                                                                         \
        type* node = type##_alloc_fn();                                       \
        if (node == NULL) return NULL;                                        \
        type##_config cfg = type##_config_init(2, sampleRate);                \
        if (type##_init(graph, &cfg, NULL, node) != MA_SUCCESS) {             \
            type##_free_fn(node);                                             \
            return NULL;                                                      \
        }                                                                     \
        return node;                                                          \
    }                                                                         \
    MA_API void cosmos_##name##_destroy(type* node)                           \
    {                                                                         \
        if (node != NULL) {                                                   \
            type##_uninit(node, NULL);                                        \
            type##_free_fn(node);                                             \
        }                                                                     \
    }

/* The alloc/free function names don't follow one convention (ma_delay_fx
 * vs ma_eq_node), so alias them per type before instantiating. */
#define ma_convreverb_node_alloc_fn ma_convreverb_node_alloc
#define ma_convreverb_node_free_fn  ma_convreverb_node_free
#define ma_delay_fx_alloc_fn        ma_delay_fx_alloc
#define ma_delay_fx_free_fn         ma_delay_fx_free
#define ma_disperser_node_alloc_fn  ma_disperser_node_alloc
#define ma_disperser_node_free_fn   ma_disperser_node_free
#define ma_distortion_node_alloc_fn ma_distortion_node_alloc
#define ma_distortion_node_free_fn  ma_distortion_node_free
#define ma_eq_node_alloc_fn         ma_eq_node_alloc
#define ma_eq_node_free_fn          ma_eq_node_free
#define ma_filter_node_alloc_fn     ma_filter_node_alloc
#define ma_filter_node_free_fn      ma_filter_node_free
#define ma_vocoder_node_alloc_fn    ma_vocoder_node_alloc
#define ma_vocoder_node_free_fn     ma_vocoder_node_free

COSMOS_EFFECT_PAIR(reverb, ma_convreverb_node)
COSMOS_EFFECT_PAIR(delay, ma_delay_fx)
COSMOS_EFFECT_PAIR(disperser, ma_disperser_node)
COSMOS_EFFECT_PAIR(distortion, ma_distortion_node)
COSMOS_EFFECT_PAIR(eq, ma_eq_node)
COSMOS_EFFECT_PAIR(filter, ma_filter_node)
COSMOS_EFFECT_PAIR(vocoder, ma_vocoder_node)
