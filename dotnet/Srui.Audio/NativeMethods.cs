using System.Runtime.InteropServices;

namespace Srui.Audio;

/// <summary>P/Invoke surface of cosmos.dll (miniaudio + cosmos nodes +
/// Steam Audio glue). Everything crosses as pointers, scalars, and UTF-8
/// strings; no structs. MA_SUCCESS is 0.</summary>
internal static class NativeMethods
{
    private const string Lib = "cosmos";

    internal const uint SoundFlagDecode = 0x00000002; // MA_SOUND_FLAG_DECODE

    // ── Engine ──
    [DllImport(Lib)] internal static extern IntPtr ma_engine_alloc();
    [DllImport(Lib)] internal static extern void ma_engine_free(IntPtr engine);
    [DllImport(Lib)] internal static extern int ma_engine_init_with_caching(IntPtr engine);
    [DllImport(Lib)] internal static extern void ma_engine_uninit_with_caching(IntPtr engine);
    [DllImport(Lib)] internal static extern uint ma_engine_get_sample_rate(IntPtr engine);
    [DllImport(Lib)] internal static extern IntPtr ma_engine_get_endpoint(IntPtr engine);
    [DllImport(Lib)] internal static extern IntPtr ma_engine_get_node_graph(IntPtr engine);
    [DllImport(Lib)] internal static extern int ma_engine_listener_set_position(IntPtr engine, uint index, float x, float y, float z);

    // ── Steam Audio (via native glue; no IPL types cross) ──
    [DllImport(Lib)] internal static extern int ma_phonon_init(uint sampleRate, uint frameSize);
    [DllImport(Lib)] internal static extern void ma_phonon_uninit();
    [DllImport(Lib)] internal static extern uint ma_phonon_is_initialized();
    [DllImport(Lib)] internal static extern IntPtr cosmos_binaural_node_create(IntPtr nodeGraph, uint channelsIn);
    [DllImport(Lib)] internal static extern void cosmos_binaural_node_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern int ma_phonon_binaural_node_set_direction(IntPtr node, float x, float y, float z, float distance);

    // ── Sounds ──
    [DllImport(Lib)] internal static extern IntPtr ma_sound_alloc();
    [DllImport(Lib)] internal static extern void ma_sound_free(IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_init_from_file(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags, IntPtr group, IntPtr fence, IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_init_from_data_source(IntPtr engine, IntPtr dataSource, uint flags, IntPtr group, IntPtr sound);
    [DllImport(Lib)] internal static extern void ma_sound_uninit(IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_start(IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_stop(IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_seek_to_pcm_frame(IntPtr sound, ulong frame);
    [DllImport(Lib)] internal static extern int ma_sound_get_cursor_in_pcm_frames(IntPtr sound, out ulong cursor);
    [DllImport(Lib)] internal static extern int ma_sound_get_length_in_pcm_frames(IntPtr sound, out ulong length);
    [DllImport(Lib)] internal static extern void ma_sound_set_volume(IntPtr sound, float volume);
    [DllImport(Lib)] internal static extern void ma_sound_set_pan(IntPtr sound, float pan);
    [DllImport(Lib)] internal static extern float ma_sound_get_pan(IntPtr sound);
    [DllImport(Lib)] internal static extern void ma_sound_set_pitch(IntPtr sound, float pitch);
    [DllImport(Lib)] internal static extern void ma_sound_set_looping(IntPtr sound, uint looping);
    [DllImport(Lib)] internal static extern uint ma_sound_is_looping(IntPtr sound);
    [DllImport(Lib)] internal static extern uint ma_sound_is_playing(IntPtr sound);
    [DllImport(Lib)] internal static extern uint ma_sound_at_end(IntPtr sound);
    [DllImport(Lib)] internal static extern void ma_sound_set_spatialization_enabled(IntPtr sound, uint enabled);
    [DllImport(Lib)] internal static extern IntPtr ma_sound_get_node_ptr(IntPtr sound);
    [DllImport(Lib)] internal static extern int ma_sound_group_init(IntPtr engine, uint flags, IntPtr parent, IntPtr group);
    [DllImport(Lib)] internal static extern void ma_sound_group_uninit(IntPtr group);

    // ── Node graph wiring ──
    [DllImport(Lib)] internal static extern int ma_node_attach_output_bus(IntPtr node, uint outputBus, IntPtr otherNode, uint otherInputBus);
    [DllImport(Lib)] internal static extern int ma_node_detach_output_bus(IntPtr node, uint outputBus);

    // ── Decode / PCM views ──
    [DllImport(Lib)] internal static extern unsafe float* cosmos_decode_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint targetSampleRate, out uint channels, out uint sampleRate, out ulong frames);
    [DllImport(Lib)] internal static extern unsafe void cosmos_free(void* p);
    [DllImport(Lib)] internal static extern unsafe IntPtr cosmos_buffer_ref_create(uint channels, float* pcm, ulong frames);
    [DllImport(Lib)] internal static extern void cosmos_buffer_ref_destroy(IntPtr bufferRef);

    // ── Effects: create/destroy (channels fixed at 2 native-side) ──
    [DllImport(Lib)] internal static extern IntPtr cosmos_reverb_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_reverb_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_delay_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_delay_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_disperser_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_disperser_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_distortion_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_distortion_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_eq_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_eq_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_filter_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_filter_destroy(IntPtr node);
    [DllImport(Lib)] internal static extern IntPtr cosmos_vocoder_create(IntPtr graph, uint sampleRate);
    [DllImport(Lib)] internal static extern void cosmos_vocoder_destroy(IntPtr node);

    // ── Effect parameters ──
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_wet(IntPtr node, float v);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_dry(IntPtr node, float v);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_predelay(IntPtr node, float ms);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_ir_gain(IntPtr node, float v);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_width(IntPtr node, float v);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_lowcut(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_highcut(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_diffuse(IntPtr node, float v);
    [DllImport(Lib)] internal static extern void ma_convreverb_node_set_decay(IntPtr node, float v);
    [DllImport(Lib)] internal static extern int ma_convreverb_node_load_ir_file(IntPtr node, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib)] internal static extern int ma_convreverb_node_load_default_ir(IntPtr node);

    [DllImport(Lib)] internal static extern void ma_delay_fx_set_delay_ms(IntPtr node, float ms);
    [DllImport(Lib)] internal static extern void ma_delay_fx_set_feedback(IntPtr node, float fb);
    [DllImport(Lib)] internal static extern void ma_delay_fx_set_wet(IntPtr node, float wet);
    [DllImport(Lib)] internal static extern void ma_delay_fx_set_dry(IntPtr node, float dry);

    [DllImport(Lib)] internal static extern void ma_disperser_node_set_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_disperser_node_set_q(IntPtr node, float q);
    [DllImport(Lib)] internal static extern void ma_disperser_node_set_stages(IntPtr node, int stages);

    [DllImport(Lib)] internal static extern void ma_distortion_node_set_drive(IntPtr node, float drive);
    [DllImport(Lib)] internal static extern void ma_distortion_node_set_tone(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_distortion_node_set_wet(IntPtr node, float wet);
    [DllImport(Lib)] internal static extern void ma_distortion_node_set_dry(IntPtr node, float dry);

    [DllImport(Lib)] internal static extern void ma_eq_node_set_low_gain(IntPtr node, float db);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_mid_gain(IntPtr node, float db);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_high_gain(IntPtr node, float db);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_low_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_mid_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_high_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_eq_node_set_mid_q(IntPtr node, float q);

    [DllImport(Lib)] internal static extern void ma_filter_node_set_mode(IntPtr node, int mode);
    [DllImport(Lib)] internal static extern void ma_filter_node_set_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_filter_node_set_q(IntPtr node, float q);
    [DllImport(Lib)] internal static extern void ma_filter_node_set_gain(IntPtr node, float db);

    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_bands(IntPtr node, int bands);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_carrier(IntPtr node, int carrier);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_carrier_freq(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_attack(IntPtr node, float ms);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_release(IntPtr node, float ms);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_wet(IntPtr node, float wet);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_dry(IntPtr node, float dry);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_rand(IntPtr node, int on);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_rand_rate(IntPtr node, float hz);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_rand_depth(IntPtr node, float octaves);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_formant(IntPtr node, float factor);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_spread(IntPtr node, float spread);
    [DllImport(Lib)] internal static extern void ma_vocoder_node_set_sibilance(IntPtr node, float amt);
}
