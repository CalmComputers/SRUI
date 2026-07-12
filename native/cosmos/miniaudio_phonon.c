// miniaudio_phonon.c - Steam Audio (Phonon) integration for miniaudio
// Based on NVGT's implementation, adapted for Cosmos

#include <string.h>
#include <stdint.h>
#include <stdlib.h>
#include "miniaudio.h"
#include "phonon.h"
#include "miniaudio_phonon.h"

// Global phonon state
static IPLAudioSettings g_phonon_audio_settings = {44100, 256};
static IPLContext g_phonon_context = NULL;
static IPLHRTF g_phonon_hrtf = NULL;
static ma_bool32 g_phonon_initialized = MA_FALSE;

static ma_result ma_result_from_IPLerror(IPLerror error)
{
    switch (error)
    {
        case IPL_STATUS_SUCCESS:     return MA_SUCCESS;
        case IPL_STATUS_OUTOFMEMORY: return MA_OUT_OF_MEMORY;
        case IPL_STATUS_INITIALIZATION:
        case IPL_STATUS_FAILURE:
        default: return MA_ERROR;
    }
}

MA_API ma_result ma_phonon_init(ma_uint32 sampleRate, ma_uint32 frameSize)
{
    if (g_phonon_initialized) return MA_SUCCESS;

    g_phonon_audio_settings.samplingRate = sampleRate;
    g_phonon_audio_settings.frameSize = frameSize;

    IPLContextSettings phonon_context_settings = {0};
    phonon_context_settings.version = STEAMAUDIO_VERSION;

    if (iplContextCreate(&phonon_context_settings, &g_phonon_context) != IPL_STATUS_SUCCESS) {
        return MA_ERROR;
    }

    IPLHRTFSettings phonon_hrtf_settings = {0};
    phonon_hrtf_settings.type = IPL_HRTFTYPE_DEFAULT;
    phonon_hrtf_settings.volume = 1.0f;

    if (iplHRTFCreate(g_phonon_context, &g_phonon_audio_settings, &phonon_hrtf_settings, &g_phonon_hrtf) != IPL_STATUS_SUCCESS) {
        iplContextRelease(&g_phonon_context);
        g_phonon_context = NULL;
        return MA_ERROR;
    }

    g_phonon_initialized = MA_TRUE;
    return MA_SUCCESS;
}

MA_API void ma_phonon_uninit(void)
{
    if (!g_phonon_initialized) return;

    if (g_phonon_hrtf) {
        iplHRTFRelease(&g_phonon_hrtf);
        g_phonon_hrtf = NULL;
    }
    if (g_phonon_context) {
        iplContextRelease(&g_phonon_context);
        g_phonon_context = NULL;
    }
    g_phonon_initialized = MA_FALSE;
}

MA_API IPLContext ma_phonon_get_context(void)
{
    return g_phonon_context;
}

MA_API IPLHRTF ma_phonon_get_hrtf(void)
{
    return g_phonon_hrtf;
}

MA_API IPLAudioSettings ma_phonon_get_audio_settings(void)
{
    return g_phonon_audio_settings;
}

MA_API ma_bool32 ma_phonon_is_initialized(void)
{
    return g_phonon_initialized;
}

MA_API ma_phonon_binaural_node_config ma_phonon_binaural_node_config_init(ma_uint32 channelsIn, IPLAudioSettings iplAudioSettings, IPLContext iplContext, IPLHRTF iplHRTF)
{
    ma_phonon_binaural_node_config config;

    memset(&config, 0, sizeof(ma_phonon_binaural_node_config));
    config.nodeConfig       = ma_node_config_init();
    config.channelsIn       = channelsIn;
    config.iplAudioSettings = iplAudioSettings;
    config.iplContext       = iplContext;
    config.iplHRTF          = iplHRTF;

    return config;
}

static void ma_phonon_binaural_node_process_pcm_frames(ma_node* pNode, const float** ppFramesIn, ma_uint32* pFrameCountIn, float** ppFramesOut, ma_uint32* pFrameCountOut)
{
    ma_phonon_binaural_node* pBinauralNode = (ma_phonon_binaural_node*)pNode;
    IPLAudioBuffer inputBufferDesc;
    IPLAudioBuffer outputBufferDesc;
    ma_uint32 totalFramesToProcess = *pFrameCountOut;
    ma_uint32 totalFramesProcessed = 0;

    inputBufferDesc.numChannels = (IPLint32)ma_node_get_input_channels(pNode, 0);

    /* We'll run this in a loop just in case our deinterleaved buffers are too small. */
    outputBufferDesc.numSamples  = pBinauralNode->iplAudioSettings.frameSize;
    outputBufferDesc.numChannels = 2;
    outputBufferDesc.data        = pBinauralNode->ppBuffersOut;

    while (totalFramesProcessed < totalFramesToProcess) {
        ma_uint32 framesToProcessThisIteration = totalFramesToProcess - totalFramesProcessed;
        if (framesToProcessThisIteration > (ma_uint32)pBinauralNode->iplAudioSettings.frameSize) {
            framesToProcessThisIteration = (ma_uint32)pBinauralNode->iplAudioSettings.frameSize;
        }

        if (inputBufferDesc.numChannels == 1) {
            /* Fast path. No need for deinterleaving since it's a mono stream. */
            pBinauralNode->ppBuffersIn[0] = (float*)ma_offset_pcm_frames_const_ptr_f32(ppFramesIn[0], totalFramesProcessed, 1);
        } else {
            /* Slow path. Need to deinterleave the input data. */
            ma_deinterleave_pcm_frames(ma_format_f32, inputBufferDesc.numChannels, framesToProcessThisIteration, ma_offset_pcm_frames_const_ptr_f32(ppFramesIn[0], totalFramesProcessed, inputBufferDesc.numChannels), (void**)&pBinauralNode->ppBuffersIn[0]);
        }

        inputBufferDesc.data       = pBinauralNode->ppBuffersIn;
        inputBufferDesc.numSamples = (IPLint32)framesToProcessThisIteration;

        /* Apply the effect. */
        iplBinauralEffectApply(pBinauralNode->iplEffect, &pBinauralNode->iplEffectParams, &inputBufferDesc, &outputBufferDesc);

        /* Interleave straight into the output buffer. */
        ma_interleave_pcm_frames(ma_format_f32, 2, framesToProcessThisIteration, (const void**)&pBinauralNode->ppBuffersOut[0], ma_offset_pcm_frames_ptr_f32(ppFramesOut[0], totalFramesProcessed, 2));

        /* Advance. */
        totalFramesProcessed += framesToProcessThisIteration;
    }

    (void)pFrameCountIn;    /* Unused. */
}

static ma_node_vtable g_ma_phonon_binaural_node_vtable =
{
    ma_phonon_binaural_node_process_pcm_frames,
    NULL,
    1,  /* 1 input channel. */
    1,  /* 1 output channel. */
    0
};

#define ma_offset_ptr64(p, offset) ((void*)((ma_uint8*)(p) + (uintptr_t)(offset)))

MA_API ma_result ma_phonon_binaural_node_init(ma_node_graph* pNodeGraph, const ma_phonon_binaural_node_config* pConfig, const ma_allocation_callbacks* pAllocationCallbacks, ma_phonon_binaural_node* pBinauralNode)
{
    ma_result result;
    ma_node_config baseConfig;
    ma_uint32 channelsIn;
    ma_uint32 channelsOut;
    IPLBinauralEffectSettings iplBinauralEffectSettings;
    IPLBinauralEffectParams binauralParams;
    size_t heapSizeInBytes;

    if (pBinauralNode == NULL) {
        return MA_INVALID_ARGS;
    }

    memset(pBinauralNode, 0, sizeof(ma_phonon_binaural_node));

    if (pConfig == NULL || pConfig->iplAudioSettings.frameSize == 0 || pConfig->iplContext == NULL || pConfig->iplHRTF == NULL) {
        return MA_INVALID_ARGS;
    }

    /* Steam Audio only supports mono and stereo input. */
    if (pConfig->channelsIn < 1 || pConfig->channelsIn > 2) {
        return MA_INVALID_ARGS;
    }

    channelsIn  = pConfig->channelsIn;
    channelsOut = 2;    /* Always stereo output. */

    baseConfig = ma_node_config_init();
    baseConfig.vtable          = &g_ma_phonon_binaural_node_vtable;
    baseConfig.pInputChannels  = &channelsIn;
    baseConfig.pOutputChannels = &channelsOut;
    result = ma_node_init(pNodeGraph, &baseConfig, pAllocationCallbacks, &pBinauralNode->baseNode);
    if (result != MA_SUCCESS) {
        return result;
    }

    pBinauralNode->iplAudioSettings = pConfig->iplAudioSettings;
    pBinauralNode->iplContext       = pConfig->iplContext;

    pBinauralNode->spatial_blend_max_distance = 4.0f;

    memset(&iplBinauralEffectSettings, 0, sizeof(IPLBinauralEffectSettings));
    iplBinauralEffectSettings.hrtf = pConfig->iplHRTF;
    memset(&binauralParams, 0, sizeof(IPLBinauralEffectParams));
    binauralParams.interpolation = IPL_HRTFINTERPOLATION_NEAREST;
    binauralParams.spatialBlend = 1.0f;  // Full HRTF effect
    binauralParams.hrtf          = pConfig->iplHRTF;
    binauralParams.direction.x = 0.0f;  // Default: sound in front
    binauralParams.direction.y = 1.0f;
    binauralParams.direction.z = 0.0f;
    pBinauralNode->iplEffectParams = binauralParams;

    result = ma_result_from_IPLerror(iplBinauralEffectCreate(pBinauralNode->iplContext, &pBinauralNode->iplAudioSettings, &iplBinauralEffectSettings, &pBinauralNode->iplEffect));
    if (result != MA_SUCCESS) {
        ma_node_uninit(&pBinauralNode->baseNode, pAllocationCallbacks);
        return result;
    }

    heapSizeInBytes = 0;

    /*
    Unfortunately Steam Audio uses deinterleaved buffers for everything so we'll need to use some
    intermediary buffers. We'll allocate one big buffer on the heap and then use offsets. We'll
    use the frame size from the IPLAudioSettings structure as a basis for the size of the buffer.
    */
    heapSizeInBytes += sizeof(float) * channelsOut * pBinauralNode->iplAudioSettings.frameSize; /* Output buffer. */
    heapSizeInBytes += sizeof(float) * channelsIn  * pBinauralNode->iplAudioSettings.frameSize; /* Input buffer. */

    pBinauralNode->_pHeap = ma_malloc(heapSizeInBytes, pAllocationCallbacks);
    if (pBinauralNode->_pHeap == NULL) {
        iplBinauralEffectRelease(&pBinauralNode->iplEffect);
        ma_node_uninit(&pBinauralNode->baseNode, pAllocationCallbacks);
        return MA_OUT_OF_MEMORY;
    }

    pBinauralNode->ppBuffersOut[0] = (float*)pBinauralNode->_pHeap;
    pBinauralNode->ppBuffersOut[1] = (float*)ma_offset_ptr64(pBinauralNode->_pHeap, sizeof(float) * pBinauralNode->iplAudioSettings.frameSize);

    {
        ma_uint32 iChannelIn;
        for (iChannelIn = 0; iChannelIn < channelsIn; iChannelIn += 1) {
            pBinauralNode->ppBuffersIn[iChannelIn] = (float*)ma_offset_ptr64(pBinauralNode->_pHeap, sizeof(float) * pBinauralNode->iplAudioSettings.frameSize * (channelsOut + iChannelIn));
        }
    }

    return MA_SUCCESS;
}

MA_API void ma_phonon_binaural_node_uninit(ma_phonon_binaural_node* pBinauralNode, const ma_allocation_callbacks* pAllocationCallbacks)
{
    if (pBinauralNode == NULL) {
        return;
    }
    /* The base node is always uninitialized first. */
    ma_node_uninit(&pBinauralNode->baseNode, pAllocationCallbacks);
    /*
    The Steam Audio objects are deleted after the base node. This ensures the base node is removed from the graph
    first to ensure these objects aren't getting used by the audio thread.
    */
    iplBinauralEffectRelease(&pBinauralNode->iplEffect);
    ma_free(pBinauralNode->_pHeap, pAllocationCallbacks);
}

MA_API ma_result ma_phonon_binaural_node_set_direction(ma_phonon_binaural_node* pBinauralNode, float x, float y, float z, float distance)
{
    if (pBinauralNode == NULL) {
        return MA_INVALID_ARGS;
    }
    pBinauralNode->iplEffectParams.direction.x = x;
    pBinauralNode->iplEffectParams.direction.y = y;
    pBinauralNode->iplEffectParams.direction.z = z;
    pBinauralNode->iplEffectParams.spatialBlend = pBinauralNode->spatial_blend_max_distance > 0 ? distance / pBinauralNode->spatial_blend_max_distance : 1.0f;
    if (pBinauralNode->iplEffectParams.spatialBlend > 1.0f) pBinauralNode->iplEffectParams.spatialBlend = 1.0f;
    return MA_SUCCESS;
}

MA_API ma_result ma_phonon_binaural_node_set_spatial_blend_max_distance(ma_phonon_binaural_node* pBinauralNode, float max_distance)
{
    if (pBinauralNode == NULL) {
        return MA_INVALID_ARGS;
    }
    pBinauralNode->spatial_blend_max_distance = max_distance;
    return MA_SUCCESS;
}

// Allocation functions for D bindings (ensures correct struct sizes)
MA_API ma_phonon_binaural_node* ma_phonon_binaural_node_alloc(void)
{
    return (ma_phonon_binaural_node*)malloc(sizeof(ma_phonon_binaural_node));
}

MA_API void ma_phonon_binaural_node_free(ma_phonon_binaural_node* pNode)
{
    free(pNode);
}

/* -- Binauralizer -- ported from the Lightspeed rust-era glue (see
   PATCHES-cosmos.md): stereo in, stereo out, two parallel HRTFs. */

MA_API ma_phonon_binauralizer_node_config ma_phonon_binauralizer_node_config_init(IPLAudioSettings iplAudioSettings, IPLContext iplContext, IPLHRTF iplHRTF)
{
    ma_phonon_binauralizer_node_config config;
    memset(&config, 0, sizeof(config));
    config.nodeConfig       = ma_node_config_init();
    config.iplAudioSettings = iplAudioSettings;
    config.iplContext       = iplContext;
    config.iplHRTF          = iplHRTF;
    return config;
}

static void ma_phonon_binauralizer_node_process_pcm_frames(ma_node* pNode, const float** ppFramesIn, ma_uint32* pFrameCountIn, float** ppFramesOut, ma_uint32* pFrameCountOut)
{
    ma_phonon_binauralizer_node* pBzNode = (ma_phonon_binauralizer_node*)pNode;
    IPLAudioBuffer inputBuf;
    IPLAudioBuffer outputBufL;
    IPLAudioBuffer outputBufR;
    ma_uint32 totalFramesToProcess = *pFrameCountOut;
    ma_uint32 totalFramesProcessed = 0;
    ma_uint32 frameSize = (ma_uint32)pBzNode->iplAudioSettings.frameSize;

    /* Binauralizer is always stereo in, stereo out. Each per-effect
       pass is a mono-in, stereo-out convolution. */
    inputBuf.numChannels   = 1;
    outputBufL.numChannels = 2;
    outputBufR.numChannels = 2;
    outputBufL.data        = pBzNode->ppBuffersOutL;
    outputBufR.data        = pBzNode->ppBuffersOutR;

    while (totalFramesProcessed < totalFramesToProcess) {
        ma_uint32 framesThisIter = totalFramesToProcess - totalFramesProcessed;
        if (framesThisIter > frameSize) {
            framesThisIter = frameSize;
        }

        /* Deinterleave LR-LR-LR stereo input into two mono slices. */
        ma_deinterleave_pcm_frames(
            ma_format_f32, 2, framesThisIter,
            ma_offset_pcm_frames_const_ptr_f32(ppFramesIn[0], totalFramesProcessed, 2),
            (void**)&pBzNode->ppBuffersIn[0]
        );

        outputBufL.numSamples = (IPLint32)framesThisIter;
        outputBufR.numSamples = (IPLint32)framesThisIter;

        /* L-channel mono → L-virtual-speaker effect → stereo outL. */
        inputBuf.numSamples = (IPLint32)framesThisIter;
        inputBuf.data       = &pBzNode->ppBuffersIn[0];
        iplBinauralEffectApply(pBzNode->iplEffectL, &pBzNode->iplParamsL, &inputBuf, &outputBufL);

        /* R-channel mono → R-virtual-speaker effect → stereo outR. */
        inputBuf.data       = &pBzNode->ppBuffersIn[1];
        iplBinauralEffectApply(pBzNode->iplEffectR, &pBzNode->iplParamsR, &inputBuf, &outputBufR);

        /* Sum the two stereo outputs into outL in place, then interleave
           straight to the caller's buffer. One fewer scratch buffer than
           the "third buffer for the sum" approach. */
        {
            ma_uint32 i;
            for (i = 0; i < framesThisIter; i += 1) {
                pBzNode->ppBuffersOutL[0][i] += pBzNode->ppBuffersOutR[0][i];
                pBzNode->ppBuffersOutL[1][i] += pBzNode->ppBuffersOutR[1][i];
            }
        }

        ma_interleave_pcm_frames(
            ma_format_f32, 2, framesThisIter,
            (const void**)&pBzNode->ppBuffersOutL[0],
            ma_offset_pcm_frames_ptr_f32(ppFramesOut[0], totalFramesProcessed, 2)
        );

        totalFramesProcessed += framesThisIter;
    }

    (void)pFrameCountIn;
}

static ma_node_vtable g_ma_phonon_binauralizer_node_vtable =
{
    ma_phonon_binauralizer_node_process_pcm_frames,
    NULL,
    1,  /* 1 input bus. */
    1,  /* 1 output bus. */
    0
};

MA_API ma_result ma_phonon_binauralizer_node_init(ma_node_graph* pNodeGraph, const ma_phonon_binauralizer_node_config* pConfig, const ma_allocation_callbacks* pAllocationCallbacks, ma_phonon_binauralizer_node* pNode)
{
    ma_result result;
    ma_node_config baseConfig;
    ma_uint32 channelsIn  = 2;
    ma_uint32 channelsOut = 2;
    IPLBinauralEffectSettings iplBinauralEffectSettings;
    size_t heapSizeInBytes;

    if (pNode == NULL) return MA_INVALID_ARGS;
    memset(pNode, 0, sizeof(*pNode));
    if (pConfig == NULL || pConfig->iplAudioSettings.frameSize == 0 ||
        pConfig->iplContext == NULL || pConfig->iplHRTF == NULL) {
        return MA_INVALID_ARGS;
    }

    baseConfig = ma_node_config_init();
    baseConfig.vtable          = &g_ma_phonon_binauralizer_node_vtable;
    baseConfig.pInputChannels  = &channelsIn;
    baseConfig.pOutputChannels = &channelsOut;
    result = ma_node_init(pNodeGraph, &baseConfig, pAllocationCallbacks, &pNode->baseNode);
    if (result != MA_SUCCESS) return result;

    pNode->iplAudioSettings = pConfig->iplAudioSettings;
    pNode->iplContext       = pConfig->iplContext;

    memset(&iplBinauralEffectSettings, 0, sizeof(iplBinauralEffectSettings));
    iplBinauralEffectSettings.hrtf = pConfig->iplHRTF;

    /* Default to ±30° on the horizontal plane. Steam Audio's convention
       is +X right, +Y up, -Z ahead; a listener facing -Z hears these as
       front-left and front-right virtual speakers. sin(30°)=0.5,
       cos(30°)≈0.866025. */
    memset(&pNode->iplParamsL, 0, sizeof(pNode->iplParamsL));
    pNode->iplParamsL.interpolation = IPL_HRTFINTERPOLATION_NEAREST;
    pNode->iplParamsL.spatialBlend  = 1.0f;
    pNode->iplParamsL.hrtf          = pConfig->iplHRTF;
    pNode->iplParamsL.direction.x   = -0.5f;
    pNode->iplParamsL.direction.y   =  0.0f;
    pNode->iplParamsL.direction.z   = -0.866025f;

    memset(&pNode->iplParamsR, 0, sizeof(pNode->iplParamsR));
    pNode->iplParamsR.interpolation = IPL_HRTFINTERPOLATION_NEAREST;
    pNode->iplParamsR.spatialBlend  = 1.0f;
    pNode->iplParamsR.hrtf          = pConfig->iplHRTF;
    pNode->iplParamsR.direction.x   =  0.5f;
    pNode->iplParamsR.direction.y   =  0.0f;
    pNode->iplParamsR.direction.z   = -0.866025f;

    result = ma_result_from_IPLerror(iplBinauralEffectCreate(pNode->iplContext, &pNode->iplAudioSettings, &iplBinauralEffectSettings, &pNode->iplEffectL));
    if (result != MA_SUCCESS) {
        ma_node_uninit(&pNode->baseNode, pAllocationCallbacks);
        return result;
    }
    result = ma_result_from_IPLerror(iplBinauralEffectCreate(pNode->iplContext, &pNode->iplAudioSettings, &iplBinauralEffectSettings, &pNode->iplEffectR));
    if (result != MA_SUCCESS) {
        iplBinauralEffectRelease(&pNode->iplEffectL);
        ma_node_uninit(&pNode->baseNode, pAllocationCallbacks);
        return result;
    }

    /* Six frame-size slices: outL.L, outL.R, outR.L, outR.R, inL, inR. */
    heapSizeInBytes = sizeof(float) * 6 * pNode->iplAudioSettings.frameSize;
    pNode->_pHeap = ma_malloc(heapSizeInBytes, pAllocationCallbacks);
    if (pNode->_pHeap == NULL) {
        iplBinauralEffectRelease(&pNode->iplEffectL);
        iplBinauralEffectRelease(&pNode->iplEffectR);
        ma_node_uninit(&pNode->baseNode, pAllocationCallbacks);
        return MA_OUT_OF_MEMORY;
    }

    {
        float* base = (float*)pNode->_pHeap;
        ma_uint32 fs = (ma_uint32)pNode->iplAudioSettings.frameSize;
        pNode->ppBuffersOutL[0] = base + fs * 0;
        pNode->ppBuffersOutL[1] = base + fs * 1;
        pNode->ppBuffersOutR[0] = base + fs * 2;
        pNode->ppBuffersOutR[1] = base + fs * 3;
        pNode->ppBuffersIn[0]   = base + fs * 4;
        pNode->ppBuffersIn[1]   = base + fs * 5;
    }

    return MA_SUCCESS;
}

MA_API void ma_phonon_binauralizer_node_uninit(ma_phonon_binauralizer_node* pNode, const ma_allocation_callbacks* pAllocationCallbacks)
{
    if (pNode == NULL) return;
    ma_node_uninit(&pNode->baseNode, pAllocationCallbacks);
    iplBinauralEffectRelease(&pNode->iplEffectL);
    iplBinauralEffectRelease(&pNode->iplEffectR);
    ma_free(pNode->_pHeap, pAllocationCallbacks);
}

MA_API ma_result ma_phonon_binauralizer_node_set_directions(ma_phonon_binauralizer_node* pNode, float lx, float ly, float lz, float rx, float ry, float rz)
{
    if (pNode == NULL) return MA_INVALID_ARGS;
    pNode->iplParamsL.direction.x = lx;
    pNode->iplParamsL.direction.y = ly;
    pNode->iplParamsL.direction.z = lz;
    pNode->iplParamsR.direction.x = rx;
    pNode->iplParamsR.direction.y = ry;
    pNode->iplParamsR.direction.z = rz;
    return MA_SUCCESS;
}

MA_API ma_phonon_binauralizer_node* ma_phonon_binauralizer_node_alloc(void)
{
    return (ma_phonon_binauralizer_node*)malloc(sizeof(ma_phonon_binauralizer_node));
}

MA_API void ma_phonon_binauralizer_node_free(ma_phonon_binauralizer_node* pNode)
{
    free(pNode);
}
