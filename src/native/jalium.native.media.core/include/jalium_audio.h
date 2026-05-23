// jalium_audio.h - audio device + decoder C ABI (lives inside jalium.native.media).
//
// Sits beside jalium_media.h and reuses its JALIUM_MEDIA_API export macro plus
// the jalium_media_status_t status enum. Design notes in
// docs/plans/snazzy-meandering-trinket.md (self-hosted audio stack replacing
// SoundFlow).
//
// Threading:
//   - jalium_audio_initialize / shutdown:
//       refcounted, callable from any caller thread.
//   - jalium_audio_decoder_*:
//       single-threaded contract. A decoder instance is owned by exactly one
//       thread (typically the AudioPlayer pump worker); no internal locks.
//   - jalium_audio_device_open / close / start / stop / submit / flush /
//     set_volume / signal_end_of_stream / played_frames:
//       safe to call from any non-audio-callback thread. submit goes through
//       a lock-free ring buffer and never blocks the audio callback.
//   - jalium_audio_playback_ended_fn:
//       runs on miniaudio's data-callback thread. The callback MUST NOT call
//       any jalium_audio_* function and MUST return promptly (typical managed
//       implementation: restore the GCHandle from user_data and post to the
//       ThreadPool).
//
// Ownership:
//   - Decoder output PCM is caller-owned (managed Span<float> via float*);
//     the decoder only touches it during read_frames and does not retain
//     pointers after return.
//   - device_submit copies PCM into the internal ring, so the caller can
//     reuse / drop the source buffer immediately.
//   - Opaque handles (jalium_audio_decoder_t* / jalium_audio_device_t*) are
//     library-owned and must be released through the matching _close.

#pragma once

#include "jalium_media.h"   // JALIUM_MEDIA_API + jalium_media_status_t

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// Enums
// ============================================================================

typedef enum jalium_audio_sample_format {
    JALIUM_ASF_F32 = 0,        ///< 32-bit float interleaved. v1's only output format.
    JALIUM_ASF_S16 = 1,        ///< Reserved (v1 not implemented).
    JALIUM_ASF_S24 = 2,        ///< Reserved.
    JALIUM_ASF_S32 = 3,        ///< Reserved.
} jalium_audio_sample_format_t;

typedef enum jalium_audio_codec {
    JALIUM_ACODEC_AUTO = 0,    ///< Detected on open_file/open_memory by content + extension.
                               ///< Doubles as the UNKNOWN sentinel.
    JALIUM_ACODEC_WAV  = 1,    ///< dr_wav.
    JALIUM_ACODEC_FLAC = 2,    ///< dr_flac.
    JALIUM_ACODEC_MP3  = 3,    ///< minimp3.
    JALIUM_ACODEC_OGG  = 4,    ///< Ogg/Vorbis via stb_vorbis.
    JALIUM_ACODEC_AAC  = 5,    ///< Platform-native bridge: MF / MediaCodec / AudioToolbox / GStreamer.
} jalium_audio_codec_t;

// ============================================================================
// Lifecycle (force-linkable from aot_register.cpp)
// ============================================================================

JALIUM_MEDIA_API jalium_media_status_t jalium_audio_initialize(void);
JALIUM_MEDIA_API void                  jalium_audio_shutdown(void);

// ============================================================================
// Decoder
// ============================================================================

typedef struct jalium_audio_decoder jalium_audio_decoder_t;

typedef struct jalium_audio_info {
    uint32_t                     sample_rate;     ///< Hz (source native).
    uint32_t                     channels;        ///< 1..8.
    int64_t                      duration_us;     ///< Total duration in microseconds; 0 = unknown.
    jalium_audio_codec_t         codec;           ///< Actually-detected codec.
    jalium_audio_sample_format_t output_format;   ///< v1 always JALIUM_ASF_F32.
} jalium_audio_info_t;

/// Open a local audio file. utf8_path must be NUL-terminated. When hint is AUTO
/// the codec is probed from content + extension; otherwise the probe is skipped
/// and the file is decoded with the requested codec directly.
///
/// Returns:
///   JALIUM_MEDIA_OK                  success; *out_decoder holds a valid handle.
///   JALIUM_MEDIA_E_INVALID_ARG       null parameter.
///   JALIUM_MEDIA_E_IO                file cannot be opened or read.
///   JALIUM_MEDIA_E_UNSUPPORTED_CODEC probe failed, or codec is not supported on this platform.
///   JALIUM_MEDIA_E_OUT_OF_MEMORY     allocation failed.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_decoder_open_file(
    const char*                  utf8_path,
    jalium_audio_codec_t         hint,
    jalium_audio_decoder_t**     out_decoder);

/// Open audio from a memory buffer. The data pointer must stay valid for the
/// decoder's lifetime unless the codec implementation declares it copies the
/// buffer internally; in v1 every codec makes its own copy, so the caller may
/// release the original buffer once this call returns.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_decoder_open_memory(
    const uint8_t*               data,
    size_t                       size,
    jalium_audio_codec_t         hint,
    jalium_audio_decoder_t**     out_decoder);

/// Query source info. decoder must be open.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_decoder_get_info(
    jalium_audio_decoder_t*      decoder,
    jalium_audio_info_t*         out_info);

/// Read interleaved float PCM frames (1 frame = `channels` samples).
///
/// `dst` must have room for at least frame_capacity * channels floats.
/// *out_frames_read is set to the number of frames actually written and may
/// be less than frame_capacity (a short read does not imply EOS; the caller
/// should keep polling until the return code is E_END_OF_STREAM).
///
/// Returns:
///   JALIUM_MEDIA_OK                  success; frames_read may be 0 if no data
///                                    is available right now but the stream
///                                    has not ended (caller retries).
///   JALIUM_MEDIA_E_END_OF_STREAM     EOS reached; frames_read is always 0.
///   JALIUM_MEDIA_E_DECODE_FAILED     decode error (corrupt bitstream, ...).
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_decoder_read_frames(
    jalium_audio_decoder_t*      decoder,
    float*                       dst,
    uint32_t                     frame_capacity,
    uint32_t*                    out_frames_read);

/// Seek to the given position in microseconds. 0 means the start of the stream;
/// positions past `duration` are clamped to `duration`.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_decoder_seek_us(
    jalium_audio_decoder_t*      decoder,
    int64_t                      position_us);

/// Release the decoder. Passing NULL is safe (no-op).
JALIUM_MEDIA_API void                  jalium_audio_decoder_close(
    jalium_audio_decoder_t*      decoder);

// ============================================================================
// Playback device
// ============================================================================

typedef struct jalium_audio_device jalium_audio_device_t;

/// Fires from the audio callback thread when the ring drains AFTER the caller
/// has invoked signal_end_of_stream. user_data is the pointer that was passed
/// in device_config (managed side typically stuffs a GCHandle.ToIntPtr there).
///
/// The callback MUST NOT:
///   - call any jalium_audio_* function (including _close),
///   - block or run long work (keep it under ~100 microseconds).
typedef void (*jalium_audio_playback_ended_fn)(void* user_data);

typedef struct jalium_audio_device_config {
    uint32_t                       sample_rate;          ///< Output Hz.
    uint32_t                       channels;             ///< Output channel count.
    jalium_audio_sample_format_t   sample_format;        ///< v1 always F32.
    uint32_t                       ring_capacity_frames; ///< 0 -> default (8192).
    jalium_audio_playback_ended_fn on_playback_ended;    ///< May be NULL.
    void*                          ended_user_data;
} jalium_audio_device_config_t;

JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_open(
    const jalium_audio_device_config_t* config,
    jalium_audio_device_t**             out_device);

JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_start(
    jalium_audio_device_t*              device);

JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_stop(
    jalium_audio_device_t*              device);

/// Copy frame_count interleaved float frames (frame_count * channels floats)
/// into the ring. When the ring is full *out_frames_written may be less than
/// frame_count and the caller must retain the leftover frames and retry.
/// Never blocks longer than the cost of a single atomic operation.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_submit(
    jalium_audio_device_t*              device,
    const float*                        pcm,
    uint32_t                            frame_count,
    uint32_t*                           out_frames_written);

/// Tells the device no further submits are coming. The next time the ring
/// drains, on_playback_ended fires exactly once. A briefly-empty ring before
/// this call is treated as an underrun and does NOT fire the callback. The
/// flag clears automatically as soon as the next submit arrives.
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_signal_end_of_stream(
    jalium_audio_device_t*              device);

/// Drops every unplayed frame in the ring (use this when seeking).
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_flush(
    jalium_audio_device_t*              device);

/// Linear volume. 0.0 = mute, 1.0 = unity gain. Clamped to [0, infinity).
JALIUM_MEDIA_API jalium_media_status_t jalium_audio_device_set_volume(
    jalium_audio_device_t*              device,
    float                               linear_volume);

/// Total frames pushed to the audio HAL since open. Divide by sample_rate to
/// recover the wall-clock playback position.
JALIUM_MEDIA_API uint64_t              jalium_audio_device_played_frames(
    jalium_audio_device_t*              device);

JALIUM_MEDIA_API void                  jalium_audio_device_close(
    jalium_audio_device_t*              device);

// ============================================================================
// Telemetry (pattern mirrors jalium_bitmap_stats.h)
// ============================================================================

typedef struct jalium_audio_stats {
    uint64_t version;
    uint64_t decoders_opened;
    uint64_t decoders_closed;
    uint64_t devices_opened;
    uint64_t devices_closed;
    uint64_t frames_decoded_wav;
    uint64_t frames_decoded_flac;
    uint64_t frames_decoded_mp3;
    uint64_t frames_decoded_ogg;
    uint64_t frames_decoded_aac;
    uint64_t frames_submitted;
    uint64_t frames_played;
    uint64_t ring_underruns;        ///< Times the callback found an empty ring.
    uint64_t ring_overflows;        ///< submit calls that returned short because the ring was full.
    uint64_t codec_probe_failures;
    uint64_t platform_aac_fallbacks;
    uint64_t reserved[16];
} jalium_audio_stats_t;

#define JALIUM_AUDIO_STATS_VERSION 1u

JALIUM_MEDIA_API void jalium_query_audio_stats(jalium_audio_stats_t* out);
JALIUM_MEDIA_API void jalium_reset_audio_stats(void);

#ifdef __cplusplus
} // extern "C"
#endif
