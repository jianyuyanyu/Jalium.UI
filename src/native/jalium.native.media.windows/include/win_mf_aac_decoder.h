#pragma once

#include "jalium_audio.h"
#include "../../jalium.native.media.core/src/audio/audio_internal.h"

namespace jalium::media::win {

/// Open an AAC/M4A file via Windows Media Foundation IMFSourceReader. The
/// reader is configured for float interleaved PCM output at the source's
/// native sample rate / channel count. Registered into the cross-platform
/// audio dispatch table by win_media_init.cpp Initialize().
jalium::audio::audio_decoder_impl* MfAacDecoderOpenFile(
    const char*               utf8_path,
    jalium_media_status_t&    outStatus) noexcept;

// Memory-based AAC decoding is deferred (would need IMFByteStream wrapping
// CreateStreamOnHGlobal). Until then, the hook for OpenMemory stays null and
// the dispatch returns JALIUM_MEDIA_E_NOT_IMPLEMENTED for that path.

} // namespace jalium::media::win
