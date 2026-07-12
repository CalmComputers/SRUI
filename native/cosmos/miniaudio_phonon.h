// miniaudio_phonon.h - Steam Audio (Phonon) integration for miniaudio
// Based on NVGT's implementation

#pragma once

#include "phonon.h"
#include "miniaudio.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct
{
    ma_node_config nodeConfig;
    ma_uint32 channelsIn;
    IPLAudioSettings iplAudioSettings;
    IPLContext iplContext;
    IPLHRTF iplHRTF;   /* There is one HRTF object to many binaural effect objects. */
} ma_phonon_binaural_node_config;

MA_API ma_phonon_binaural_node_config ma_phonon_binaural_node_config_init(ma_uint32 channelsIn, IPLAudioSettings iplAudioSettings, IPLContext iplContext, IPLHRTF iplHRTF);


typedef struct
{
    ma_node_base baseNode;
    IPLAudioSettings iplAudioSettings;
    IPLContext iplContext;
    IPLBinauralEffect iplEffect;
    IPLBinauralEffectParams iplEffectParams;
    float spatial_blend_max_distance;

    float* ppBuffersIn[2];      /* Each buffer is an offset of _pHeap. */
    float* ppBuffersOut[2];     /* Each buffer is an offset of _pHeap. */
    void* _pHeap;
} ma_phonon_binaural_node;

MA_API ma_result ma_phonon_binaural_node_init(ma_node_graph* pNodeGraph, const ma_phonon_binaural_node_config* pConfig, const ma_allocation_callbacks* pAllocationCallbacks, ma_phonon_binaural_node* pBinauralNode);
MA_API void ma_phonon_binaural_node_uninit(ma_phonon_binaural_node* pBinauralNode, const ma_allocation_callbacks* pAllocationCallbacks);
MA_API ma_result ma_phonon_binaural_node_set_direction(ma_phonon_binaural_node* pBinauralNode, float x, float y, float z, float distance);
MA_API ma_result ma_phonon_binaural_node_set_spatial_blend_max_distance(ma_phonon_binaural_node* pBinauralNode, float max_distance);

// Allocation helpers (defined in miniaudio_phonon.c; declared here so
// callers avoid C implicit-int truncation — SRUI local patch).
MA_API ma_phonon_binaural_node* ma_phonon_binaural_node_alloc(void);
MA_API void ma_phonon_binaural_node_free(ma_phonon_binaural_node* pNode);

/* Binauralizer — stereo in, stereo out, two parallel HRTFs: the L
   channel spatialised at one direction, the R at another, summed.
   Ported from the Lightspeed rust-era glue (SRUI local patch). */
typedef struct
{
    ma_node_config nodeConfig;
    IPLAudioSettings iplAudioSettings;
    IPLContext iplContext;
    IPLHRTF iplHRTF;
} ma_phonon_binauralizer_node_config;

MA_API ma_phonon_binauralizer_node_config ma_phonon_binauralizer_node_config_init(IPLAudioSettings iplAudioSettings, IPLContext iplContext, IPLHRTF iplHRTF);

typedef struct
{
    ma_node_base baseNode;
    IPLAudioSettings iplAudioSettings;
    IPLContext iplContext;
    /* One binaural effect per input channel — L and R virtual speakers. */
    IPLBinauralEffect iplEffectL;
    IPLBinauralEffect iplEffectR;
    IPLBinauralEffectParams iplParamsL;
    IPLBinauralEffectParams iplParamsR;

    float* ppBuffersIn[2];      /* L and R mono inputs after deinterleave. */
    float* ppBuffersOutL[2];    /* Stereo output of the L-virtual-speaker effect. */
    float* ppBuffersOutR[2];    /* Stereo output of the R-virtual-speaker effect. */
    void*  _pHeap;
} ma_phonon_binauralizer_node;

MA_API ma_result ma_phonon_binauralizer_node_init(ma_node_graph* pNodeGraph, const ma_phonon_binauralizer_node_config* pConfig, const ma_allocation_callbacks* pAllocationCallbacks, ma_phonon_binauralizer_node* pNode);
MA_API void ma_phonon_binauralizer_node_uninit(ma_phonon_binauralizer_node* pNode, const ma_allocation_callbacks* pAllocationCallbacks);
MA_API ma_result ma_phonon_binauralizer_node_set_directions(ma_phonon_binauralizer_node* pNode, float lx, float ly, float lz, float rx, float ry, float rz);
MA_API ma_phonon_binauralizer_node* ma_phonon_binauralizer_node_alloc(void);
MA_API void ma_phonon_binauralizer_node_free(ma_phonon_binauralizer_node* pNode);

// Global phonon context management
MA_API ma_result ma_phonon_init(ma_uint32 sampleRate, ma_uint32 frameSize);
MA_API void ma_phonon_uninit(void);
MA_API IPLContext ma_phonon_get_context(void);
MA_API IPLHRTF ma_phonon_get_hrtf(void);
MA_API IPLAudioSettings ma_phonon_get_audio_settings(void);
MA_API ma_bool32 ma_phonon_is_initialized(void);

#ifdef __cplusplus
}
#endif
