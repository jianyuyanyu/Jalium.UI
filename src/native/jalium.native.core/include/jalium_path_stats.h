#pragma once

#include <cstdint>
#include "jalium_api.h"

// Unified path-rendering telemetry. Single source of truth for cache hit/miss,
// flatten/triangulate timing, and output vertex counters. Lives in
// jalium.native.core so that every backend dll (d3d12, vulkan, software) links
// against the same atomic state — otherwise each dll would carry its own
// counters and the managed DevTools P/Invoke would only see one of them.

#ifdef __cplusplus
extern "C" {
#endif

typedef struct JaliumPathStats {
    uint64_t version;
    uint64_t strokeHits;
    uint64_t strokeMisses;
    uint64_t strokeRects;
    uint64_t fillHits;
    uint64_t fillMisses;
    uint64_t fillRects;
    uint64_t geometryHits;
    uint64_t geometryMisses;
    uint64_t flattenNs;
    uint64_t flattenInputSegments;
    uint64_t flattenOutputVerts;
    uint64_t triangulateNs;
    uint64_t triangulateOk;
    uint64_t triangulateFail;
    uint64_t cacheEvictions;
    uint64_t reserved[16];
} JaliumPathStats;

#define JALIUM_PATH_STATS_VERSION 1u

JALIUM_API void jalium_query_path_stats(JaliumPathStats* out);
JALIUM_API void jalium_reset_path_stats(void);

#ifdef __cplusplus
}  // extern "C"

namespace jalium {
namespace path_stats {

JALIUM_API void AddStrokeHit(uint64_t rects) noexcept;
JALIUM_API void AddStrokeMiss() noexcept;
JALIUM_API void AddFillHit(uint64_t rects) noexcept;
JALIUM_API void AddFillMiss() noexcept;
JALIUM_API void AddGeometryHit() noexcept;
JALIUM_API void AddGeometryMiss() noexcept;
JALIUM_API void AddFlatten(uint64_t ns, uint64_t inputSegments, uint64_t outputVerts) noexcept;
JALIUM_API void AddTriangulate(uint64_t ns, bool ok) noexcept;
JALIUM_API void AddCacheEviction(uint64_t count) noexcept;

JALIUM_API uint64_t NowNs() noexcept;

class ScopedFlattenTimer {
public:
    explicit ScopedFlattenTimer(uint64_t inputSegments) noexcept
        : startNs_(NowNs()), inputSegments_(inputSegments), outputVerts_(0) {}
    ~ScopedFlattenTimer() noexcept {
        AddFlatten(NowNs() - startNs_, inputSegments_, outputVerts_);
    }
    void RecordOutputVerts(uint64_t verts) noexcept { outputVerts_ += verts; }
    void AddInputSegments(uint64_t count) noexcept { inputSegments_ += count; }
    ScopedFlattenTimer(const ScopedFlattenTimer&) = delete;
    ScopedFlattenTimer& operator=(const ScopedFlattenTimer&) = delete;
private:
    uint64_t startNs_;
    uint64_t inputSegments_;
    uint64_t outputVerts_;
};

class ScopedTriangulateTimer {
public:
    ScopedTriangulateTimer() noexcept : startNs_(NowNs()), ok_(false) {}
    ~ScopedTriangulateTimer() noexcept {
        AddTriangulate(NowNs() - startNs_, ok_);
    }
    void MarkOk() noexcept { ok_ = true; }
    void MarkFail() noexcept { ok_ = false; }
    ScopedTriangulateTimer(const ScopedTriangulateTimer&) = delete;
    ScopedTriangulateTimer& operator=(const ScopedTriangulateTimer&) = delete;
private:
    uint64_t startNs_;
    bool ok_;
};

}  // namespace path_stats
}  // namespace jalium

#endif  // __cplusplus
