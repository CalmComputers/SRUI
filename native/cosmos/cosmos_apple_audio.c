/* SRUI macOS additions (see PATCHES-cosmos.md): AAC / M4A / M4B / ALAC
 * decoding through AudioToolbox's ExtAudioFile, the formats miniaudio has
 * no backend for. The macOS counterpart of the C# side's Media Foundation
 * reader: a handle, a format query at open, and streaming reads converted
 * to interleaved f32 at the file's own rate and channel count. Built only
 * on macOS; Srui.Audio's AudioToolboxAudioSource P/Invokes it. */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <AudioToolbox/AudioToolbox.h>
#include "miniaudio.h"

typedef struct
{
    ExtAudioFileRef file;
    uint32_t channels;
} cosmos_atx_file;

/* Open a file for decoding. Writes its channel count, sample rate, length
 * in frames (0 when unknown), and duration in milliseconds, and returns a
 * handle, or NULL when AudioToolbox cannot read it. path is UTF-8. */
MA_API void* cosmos_atx_open(const char* path, uint32_t* channels, uint32_t* sampleRate,
                             uint64_t* lengthFrames, uint64_t* durationMs)
{
    CFURLRef url;
    ExtAudioFileRef file = NULL;
    AudioStreamBasicDescription fileFormat;
    AudioStreamBasicDescription clientFormat;
    UInt32 size;
    SInt64 frames = 0;
    cosmos_atx_file* handle;

    url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault, (const UInt8*)path, (CFIndex)strlen(path), false);
    if (url == NULL)
        return NULL;
    if (ExtAudioFileOpenURL(url, &file) != noErr)
    {
        CFRelease(url);
        return NULL;
    }
    CFRelease(url);

    size = sizeof(fileFormat);
    if (ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &fileFormat) != noErr
        || fileFormat.mChannelsPerFrame == 0 || fileFormat.mSampleRate <= 0)
    {
        ExtAudioFileDispose(file);
        return NULL;
    }

    /* Decode to packed interleaved f32 at the file's own rate and channel
     * count, so frame positions in the client format are file frames. */
    memset(&clientFormat, 0, sizeof(clientFormat));
    clientFormat.mSampleRate = fileFormat.mSampleRate;
    clientFormat.mFormatID = kAudioFormatLinearPCM;
    clientFormat.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    clientFormat.mChannelsPerFrame = fileFormat.mChannelsPerFrame;
    clientFormat.mBitsPerChannel = 32;
    clientFormat.mBytesPerFrame = 4 * fileFormat.mChannelsPerFrame;
    clientFormat.mFramesPerPacket = 1;
    clientFormat.mBytesPerPacket = clientFormat.mBytesPerFrame;
    if (ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat,
                                sizeof(clientFormat), &clientFormat) != noErr)
    {
        ExtAudioFileDispose(file);
        return NULL;
    }

    /* Length in file frames, already net of the encoder's priming and
     * remainder frames; absent for some streams. */
    size = sizeof(frames);
    if (ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileLengthFrames, &size, &frames) != noErr
        || frames < 0)
        frames = 0;

    handle = (cosmos_atx_file*)malloc(sizeof(cosmos_atx_file));
    if (handle == NULL)
    {
        ExtAudioFileDispose(file);
        return NULL;
    }
    handle->file = file;
    handle->channels = fileFormat.mChannelsPerFrame;

    *channels = fileFormat.mChannelsPerFrame;
    *sampleRate = (uint32_t)(fileFormat.mSampleRate + 0.5);
    *lengthFrames = (uint64_t)frames;
    *durationMs = (uint64_t)((double)frames * 1000.0 / fileFormat.mSampleRate);
    return handle;
}

/* Decode up to frameCount frames into out (frameCount * channels floats).
 * Returns the frames produced; 0 is end of stream or a decode error. */
MA_API uint64_t cosmos_atx_read(void* handle, float* out, uint64_t frameCount)
{
    cosmos_atx_file* h = (cosmos_atx_file*)handle;
    uint64_t total = 0;

    if (h == NULL)
        return 0;
    while (total < frameCount)
    {
        AudioBufferList list;
        uint64_t chunk = frameCount - total;
        UInt32 frames;

        if (chunk > 0x10000)
            chunk = 0x10000;
        frames = (UInt32)chunk;
        list.mNumberBuffers = 1;
        list.mBuffers[0].mNumberChannels = h->channels;
        list.mBuffers[0].mDataByteSize = frames * 4 * h->channels;
        list.mBuffers[0].mData = out + total * h->channels;
        if (ExtAudioFileRead(h->file, &frames, &list) != noErr || frames == 0)
            break;
        total += frames;
    }
    return total;
}

/* Reposition to an absolute frame. Returns 0 on success. */
MA_API int cosmos_atx_seek(void* handle, uint64_t frameIndex)
{
    cosmos_atx_file* h = (cosmos_atx_file*)handle;
    if (h == NULL)
        return -1;
    return ExtAudioFileSeek(h->file, (SInt64)frameIndex) == noErr ? 0 : -1;
}

/* Close a handle from cosmos_atx_open. NULL is ignored. */
MA_API void cosmos_atx_close(void* handle)
{
    cosmos_atx_file* h = (cosmos_atx_file*)handle;
    if (h == NULL)
        return;
    ExtAudioFileDispose(h->file);
    free(h);
}
