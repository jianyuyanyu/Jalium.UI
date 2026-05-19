#include "jalium_bitmap_stats.h"

#include <atomic>
#include <cstring>

namespace jalium {
namespace bitmap_stats {
namespace {

struct BitmapStatsState {
    std::atomic<uint64_t> uploadCount{0};
    std::atomic<uint64_t> uploadBytes{0};
    std::atomic<uint64_t> fastPathHits{0};
    std::atomic<uint64_t> dynamicReuses{0};
    std::atomic<uint64_t> memcmpShortCircuits{0};
    std::atomic<int64_t>  gpuResidentBytes{0};
    std::atomic<uint64_t> atlasHits{0};
    std::atomic<uint64_t> cacheEvictions{0};
};

BitmapStatsState& State() noexcept {
    static BitmapStatsState s;
    return s;
}

}  // namespace

void AddUpload(uint64_t bytes) noexcept {
    State().uploadCount.fetch_add(1, std::memory_order_relaxed);
    if (bytes != 0) State().uploadBytes.fetch_add(bytes, std::memory_order_relaxed);
}

void AddFastPathHit() noexcept {
    State().fastPathHits.fetch_add(1, std::memory_order_relaxed);
}

void AddDynamicReuse() noexcept {
    State().dynamicReuses.fetch_add(1, std::memory_order_relaxed);
}

void AddMemcmpShortCircuit() noexcept {
    State().memcmpShortCircuits.fetch_add(1, std::memory_order_relaxed);
}

void AddGpuResidentBytes(int64_t delta) noexcept {
    if (delta != 0) State().gpuResidentBytes.fetch_add(delta, std::memory_order_relaxed);
}

void AddAtlasHit() noexcept {
    State().atlasHits.fetch_add(1, std::memory_order_relaxed);
}

void AddCacheEviction(uint64_t count) noexcept {
    if (count != 0) State().cacheEvictions.fetch_add(count, std::memory_order_relaxed);
}

}  // namespace bitmap_stats
}  // namespace jalium

extern "C" {

JALIUM_API void jalium_query_bitmap_stats(JaliumBitmapStats* out) {
    if (!out) return;
    auto& s = jalium::bitmap_stats::State();
    std::memset(out, 0, sizeof(*out));
    out->version             = JALIUM_BITMAP_STATS_VERSION;
    out->uploadCount         = s.uploadCount.load(std::memory_order_relaxed);
    out->uploadBytes         = s.uploadBytes.load(std::memory_order_relaxed);
    out->fastPathHits        = s.fastPathHits.load(std::memory_order_relaxed);
    out->dynamicReuses       = s.dynamicReuses.load(std::memory_order_relaxed);
    out->memcmpShortCircuits = s.memcmpShortCircuits.load(std::memory_order_relaxed);
    out->gpuResidentBytes    = s.gpuResidentBytes.load(std::memory_order_relaxed);
    out->atlasHits           = s.atlasHits.load(std::memory_order_relaxed);
    out->cacheEvictions      = s.cacheEvictions.load(std::memory_order_relaxed);
}

JALIUM_API void jalium_reset_bitmap_stats(void) {
    auto& s = jalium::bitmap_stats::State();
    s.uploadCount.store(0, std::memory_order_relaxed);
    s.uploadBytes.store(0, std::memory_order_relaxed);
    s.fastPathHits.store(0, std::memory_order_relaxed);
    s.dynamicReuses.store(0, std::memory_order_relaxed);
    s.memcmpShortCircuits.store(0, std::memory_order_relaxed);
    s.gpuResidentBytes.store(0, std::memory_order_relaxed);
    s.atlasHits.store(0, std::memory_order_relaxed);
    s.cacheEvictions.store(0, std::memory_order_relaxed);
}

}  // extern "C"
