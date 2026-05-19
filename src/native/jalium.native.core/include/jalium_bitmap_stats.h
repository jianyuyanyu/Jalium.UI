#pragma once

#include <cstdint>
#include "jalium_api.h"

// Unified bitmap-upload telemetry. Single source of truth for upload count /
// bytes / fast-path hits / dynamic reuses / memcmp short-circuits / GPU
// resident bytes / atlas hits. Lives in jalium.native.core so that every
// backend dll (d3d12, vulkan, software) links against the same atomic state.
//
// Same pattern as jalium_path_stats.h — see that header for the rationale
// (cross-dll global state must live in core, not per-backend).

#ifdef __cplusplus
extern "C" {
#endif

typedef struct JaliumBitmapStats {
    uint64_t version;
    uint64_t uploadCount;
    uint64_t uploadBytes;
    uint64_t fastPathHits;
    uint64_t dynamicReuses;
    uint64_t memcmpShortCircuits;
    int64_t  gpuResidentBytes;     // signed: producers Add(+bytes) on create,
                                   // Add(-bytes) on destroy. Net = currently
                                   // pinned bytes across all live bitmaps.
    uint64_t atlasHits;            // future use: glyph/texture atlas hits.
    uint64_t cacheEvictions;       // bitmap downscale cache evictions
                                   // (managed-side LRU, written via Add).
    uint64_t reserved[16];
} JaliumBitmapStats;

#define JALIUM_BITMAP_STATS_VERSION 1u

JALIUM_API void jalium_query_bitmap_stats(JaliumBitmapStats* out);
JALIUM_API void jalium_reset_bitmap_stats(void);

#ifdef __cplusplus
}  // extern "C"

namespace jalium {
namespace bitmap_stats {

JALIUM_API void AddUpload(uint64_t bytes) noexcept;
JALIUM_API void AddFastPathHit() noexcept;
JALIUM_API void AddDynamicReuse() noexcept;
JALIUM_API void AddMemcmpShortCircuit() noexcept;
JALIUM_API void AddGpuResidentBytes(int64_t delta) noexcept;
JALIUM_API void AddAtlasHit() noexcept;
JALIUM_API void AddCacheEviction(uint64_t count) noexcept;

}  // namespace bitmap_stats
}  // namespace jalium

#endif  // __cplusplus
