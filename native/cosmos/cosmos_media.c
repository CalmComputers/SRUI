/* SRUI media additions (see PATCHES-cosmos.md): a device-free duration
 * probe, a pull data source that lets managed decoders feed the engine,
 * and struct-free wrappers over miniaudio's order-based filter nodes and
 * the phonon binauralizer, for per-sound effect chains. */

#include <stdint.h>
#include "miniaudio.h"
#include "miniaudio_libopus.h"
#include "miniaudio_phonon.h"
#include <stdlib.h>

/* ── Duration probe ─────────────────────────────────────────────── */

/* Header-only duration read using the same decoder stack as
 * cosmos_decode_file (wav/flac/mp3/vorbis/opus), no device, no full
 * decode. Returns milliseconds, or 0 when unknown or unreadable. */
MA_API ma_uint64 cosmos_probe_duration_ms(const char* path)
{
    ma_decoder_config config = ma_decoder_config_init(ma_format_f32, 0, 0);
    static ma_decoding_backend_vtable* customBackends[1];
    ma_decoder decoder;
    ma_format fmt;
    ma_uint32 channels = 0;
    ma_uint32 sr = 0;
    ma_uint64 lengthFrames = 0;

    customBackends[0] = ma_decoding_backend_libopus;
    config.ppCustomBackendVTables = customBackends;
    config.customBackendCount = 1;

    if (ma_decoder_init_file(path, &config, &decoder) != MA_SUCCESS) {
        return 0;
    }
    if (ma_decoder_get_data_format(&decoder, &fmt, &channels, &sr, NULL, 0) != MA_SUCCESS
        || sr == 0) {
        ma_decoder_uninit(&decoder);
        return 0;
    }
    ma_data_source_get_length_in_pcm_frames(&decoder, &lengthFrames);
    ma_decoder_uninit(&decoder);
    if (lengthFrames == 0) {
        return 0;
    }
    return (lengthFrames * 1000) / sr;
}

/* ── Pull data source ───────────────────────────────────────────── */

/* A data source whose frames come from caller-supplied callbacks —
 * the bridge for managed decoders (Media Foundation). Frames are
 * interleaved f32. The read callback returns the frames produced
 * (0 = end); the seek callback returns MA_TRUE on success. Callbacks
 * run on whichever thread pulls the sound (the audio thread). */

typedef ma_uint64 (*cosmos_pull_read_proc)(void* user, float* frames, ma_uint64 frameCount);
typedef ma_bool32 (*cosmos_pull_seek_proc)(void* user, ma_uint64 frameIndex);

typedef struct
{
    ma_data_source_base base;
    cosmos_pull_read_proc onRead;
    cosmos_pull_seek_proc onSeek;
    void* user;
    ma_uint32 channels;
    ma_uint32 sampleRate;
    ma_uint64 lengthFrames; /* 0 = unknown */
    ma_uint64 cursor;
} cosmos_pull_ds;

static ma_result cosmos_pull_ds_on_read(
    ma_data_source* pDS, void* pFramesOut, ma_uint64 frameCount, ma_uint64* pFramesRead)
{
    cosmos_pull_ds* ds = (cosmos_pull_ds*)pDS;
    ma_uint64 read = ds->onRead(ds->user, (float*)pFramesOut, frameCount);
    ds->cursor += read;
    if (pFramesRead != NULL) {
        *pFramesRead = read;
    }
    return read == 0 ? MA_AT_END : MA_SUCCESS;
}

static ma_result cosmos_pull_ds_on_seek(ma_data_source* pDS, ma_uint64 frameIndex)
{
    cosmos_pull_ds* ds = (cosmos_pull_ds*)pDS;
    if (!ds->onSeek(ds->user, frameIndex)) {
        return MA_ERROR;
    }
    ds->cursor = frameIndex;
    return MA_SUCCESS;
}

static ma_result cosmos_pull_ds_on_get_data_format(
    ma_data_source* pDS, ma_format* pFormat, ma_uint32* pChannels, ma_uint32* pSampleRate,
    ma_channel* pChannelMap, size_t channelMapCap)
{
    cosmos_pull_ds* ds = (cosmos_pull_ds*)pDS;
    if (pFormat != NULL) *pFormat = ma_format_f32;
    if (pChannels != NULL) *pChannels = ds->channels;
    if (pSampleRate != NULL) *pSampleRate = ds->sampleRate;
    if (pChannelMap != NULL) {
        ma_channel_map_init_standard(ma_standard_channel_map_default, pChannelMap, channelMapCap, ds->channels);
    }
    return MA_SUCCESS;
}

static ma_result cosmos_pull_ds_on_get_cursor(ma_data_source* pDS, ma_uint64* pCursor)
{
    *pCursor = ((cosmos_pull_ds*)pDS)->cursor;
    return MA_SUCCESS;
}

static ma_result cosmos_pull_ds_on_get_length(ma_data_source* pDS, ma_uint64* pLength)
{
    cosmos_pull_ds* ds = (cosmos_pull_ds*)pDS;
    if (ds->lengthFrames == 0) {
        return MA_NOT_IMPLEMENTED;
    }
    *pLength = ds->lengthFrames;
    return MA_SUCCESS;
}

static ma_data_source_vtable g_cosmos_pull_ds_vtable =
{
    cosmos_pull_ds_on_read,
    cosmos_pull_ds_on_seek,
    cosmos_pull_ds_on_get_data_format,
    cosmos_pull_ds_on_get_cursor,
    cosmos_pull_ds_on_get_length,
    NULL, /* onSetLooping */
    0
};

MA_API cosmos_pull_ds* cosmos_pull_ds_create(
    ma_uint32 channels,
    ma_uint32 sampleRate,
    ma_uint64 lengthFrames,
    cosmos_pull_read_proc onRead,
    cosmos_pull_seek_proc onSeek,
    void* user)
{
    cosmos_pull_ds* ds;
    ma_data_source_config config;

    if (channels == 0 || sampleRate == 0 || onRead == NULL || onSeek == NULL) {
        return NULL;
    }
    ds = (cosmos_pull_ds*)calloc(1, sizeof(cosmos_pull_ds));
    if (ds == NULL) {
        return NULL;
    }
    config = ma_data_source_config_init();
    config.vtable = &g_cosmos_pull_ds_vtable;
    if (ma_data_source_init(&config, &ds->base) != MA_SUCCESS) {
        free(ds);
        return NULL;
    }
    ds->onRead = onRead;
    ds->onSeek = onSeek;
    ds->user = user;
    ds->channels = channels;
    ds->sampleRate = sampleRate;
    ds->lengthFrames = lengthFrames;
    ds->cursor = 0;
    return ds;
}

MA_API void cosmos_pull_ds_destroy(cosmos_pull_ds* ds)
{
    if (ds == NULL) {
        return;
    }
    ma_data_source_uninit(&ds->base);
    free(ds);
}

/* ── Order-based filter nodes ───────────────────────────────────── */

/* Wrappers over miniaudio's built-in filter nodes — the same DSP the
 * Lightspeed rust era used for its per-sound chains. All stereo f32;
 * create returns NULL on failure; reinit swaps coefficients in place
 * (glitch-free) and returns MA_TRUE on success. */

#define COSMOS_FX_CHANNELS 2

MA_API ma_lpf_node* cosmos_lpf_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_lpf_node* node = (ma_lpf_node*)malloc(sizeof(ma_lpf_node));
    ma_lpf_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_lpf_node_config_init(COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    if (ma_lpf_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_lpf_node_reinit(
    ma_lpf_node* node, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_lpf_config cfg = ma_lpf_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    return ma_lpf_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_lpf_node_destroy(ma_lpf_node* node)
{
    if (node == NULL) return;
    ma_lpf_node_uninit(node, NULL);
    free(node);
}

MA_API ma_hpf_node* cosmos_hpf_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_hpf_node* node = (ma_hpf_node*)malloc(sizeof(ma_hpf_node));
    ma_hpf_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_hpf_node_config_init(COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    if (ma_hpf_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_hpf_node_reinit(
    ma_hpf_node* node, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_hpf_config cfg = ma_hpf_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    return ma_hpf_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_hpf_node_destroy(ma_hpf_node* node)
{
    if (node == NULL) return;
    ma_hpf_node_uninit(node, NULL);
    free(node);
}

MA_API ma_bpf_node* cosmos_bpf_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_bpf_node* node = (ma_bpf_node*)malloc(sizeof(ma_bpf_node));
    ma_bpf_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_bpf_node_config_init(COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    if (ma_bpf_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_bpf_node_reinit(
    ma_bpf_node* node, ma_uint32 sampleRate, double cutoff, ma_uint32 order)
{
    ma_bpf_config cfg = ma_bpf_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, cutoff, order);
    return ma_bpf_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_bpf_node_destroy(ma_bpf_node* node)
{
    if (node == NULL) return;
    ma_bpf_node_uninit(node, NULL);
    free(node);
}

MA_API ma_loshelf_node* cosmos_loshelf_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double gainDb, double slope, double frequency)
{
    ma_loshelf_node* node = (ma_loshelf_node*)malloc(sizeof(ma_loshelf_node));
    ma_loshelf_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_loshelf_node_config_init(COSMOS_FX_CHANNELS, sampleRate, gainDb, slope, frequency);
    if (ma_loshelf_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_loshelf_node_reinit(
    ma_loshelf_node* node, ma_uint32 sampleRate, double gainDb, double slope, double frequency)
{
    ma_loshelf2_config cfg = ma_loshelf2_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, gainDb, slope, frequency);
    return ma_loshelf_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_loshelf_node_destroy(ma_loshelf_node* node)
{
    if (node == NULL) return;
    ma_loshelf_node_uninit(node, NULL);
    free(node);
}

MA_API ma_hishelf_node* cosmos_hishelf_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double gainDb, double slope, double frequency)
{
    ma_hishelf_node* node = (ma_hishelf_node*)malloc(sizeof(ma_hishelf_node));
    ma_hishelf_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_hishelf_node_config_init(COSMOS_FX_CHANNELS, sampleRate, gainDb, slope, frequency);
    if (ma_hishelf_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_hishelf_node_reinit(
    ma_hishelf_node* node, ma_uint32 sampleRate, double gainDb, double slope, double frequency)
{
    ma_hishelf2_config cfg = ma_hishelf2_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, gainDb, slope, frequency);
    return ma_hishelf_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_hishelf_node_destroy(ma_hishelf_node* node)
{
    if (node == NULL) return;
    ma_hishelf_node_uninit(node, NULL);
    free(node);
}

MA_API ma_peak_node* cosmos_peak_node_create(
    ma_node_graph* graph, ma_uint32 sampleRate, double gainDb, double q, double frequency)
{
    ma_peak_node* node = (ma_peak_node*)malloc(sizeof(ma_peak_node));
    ma_peak_node_config cfg;
    if (node == NULL) return NULL;
    cfg = ma_peak_node_config_init(COSMOS_FX_CHANNELS, sampleRate, gainDb, q, frequency);
    if (ma_peak_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        free(node);
        return NULL;
    }
    return node;
}

MA_API ma_bool32 cosmos_peak_node_reinit(
    ma_peak_node* node, ma_uint32 sampleRate, double gainDb, double q, double frequency)
{
    ma_peak2_config cfg = ma_peak2_config_init(
        ma_format_f32, COSMOS_FX_CHANNELS, sampleRate, gainDb, q, frequency);
    return ma_peak_node_reinit(&cfg, node) == MA_SUCCESS;
}

MA_API void cosmos_peak_node_destroy(ma_peak_node* node)
{
    if (node == NULL) return;
    ma_peak_node_uninit(node, NULL);
    free(node);
}

/* ── Binauralizer wrapper ───────────────────────────────────────── */

/* Mirrors cosmos_binaural_node_create: the global phonon context baked
 * in, NULL when phonon is unavailable (callers degrade to passthrough). */
MA_API ma_phonon_binauralizer_node* cosmos_binauralizer_node_create(ma_node_graph* graph)
{
    ma_phonon_binauralizer_node* node;
    ma_phonon_binauralizer_node_config cfg;

    if (!ma_phonon_is_initialized()) {
        return NULL;
    }
    node = ma_phonon_binauralizer_node_alloc();
    if (node == NULL) {
        return NULL;
    }
    cfg = ma_phonon_binauralizer_node_config_init(
        ma_phonon_get_audio_settings(), ma_phonon_get_context(), ma_phonon_get_hrtf());
    if (ma_phonon_binauralizer_node_init(graph, &cfg, NULL, node) != MA_SUCCESS) {
        ma_phonon_binauralizer_node_free(node);
        return NULL;
    }
    return node;
}

MA_API void cosmos_binauralizer_node_destroy(ma_phonon_binauralizer_node* node)
{
    if (node == NULL) {
        return;
    }
    ma_phonon_binauralizer_node_uninit(node, NULL);
    ma_phonon_binauralizer_node_free(node);
}
