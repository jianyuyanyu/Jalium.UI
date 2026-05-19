#include "jalium_path_stats.h"

#include <atomic>
#include <chrono>
#include <cstring>

namespace jalium {
namespace path_stats {
namespace {

struct PathStatsState {
    std::atomic<uint64_t> strokeHits{0};
    std::atomic<uint64_t> strokeMisses{0};
    std::atomic<uint64_t> strokeRects{0};
    std::atomic<uint64_t> fillHits{0};
    std::atomic<uint64_t> fillMisses{0};
    std::atomic<uint64_t> fillRects{0};
    std::atomic<uint64_t> geometryHits{0};
    std::atomic<uint64_t> geometryMisses{0};
    std::atomic<uint64_t> flattenNs{0};
    std::atomic<uint64_t> flattenInputSegments{0};
    std::atomic<uint64_t> flattenOutputVerts{0};
    std::atomic<uint64_t> triangulateNs{0};
    std::atomic<uint64_t> triangulateOk{0};
    std::atomic<uint64_t> triangulateFail{0};
    std::atomic<uint64_t> cacheEvictions{0};
};

PathStatsState& State() noexcept {
    static PathStatsState s;
    return s;
}

}  // namespace

uint64_t NowNs() noexcept {
    using namespace std::chrono;
    return static_cast<uint64_t>(
        duration_cast<nanoseconds>(steady_clock::now().time_since_epoch()).count());
}

void AddStrokeHit(uint64_t rects) noexcept {
    State().strokeHits.fetch_add(1, std::memory_order_relaxed);
    if (rects != 0) State().strokeRects.fetch_add(rects, std::memory_order_relaxed);
}

void AddStrokeMiss() noexcept {
    State().strokeMisses.fetch_add(1, std::memory_order_relaxed);
}

void AddFillHit(uint64_t rects) noexcept {
    State().fillHits.fetch_add(1, std::memory_order_relaxed);
    if (rects != 0) State().fillRects.fetch_add(rects, std::memory_order_relaxed);
}

void AddFillMiss() noexcept {
    State().fillMisses.fetch_add(1, std::memory_order_relaxed);
}

void AddGeometryHit() noexcept {
    State().geometryHits.fetch_add(1, std::memory_order_relaxed);
}

void AddGeometryMiss() noexcept {
    State().geometryMisses.fetch_add(1, std::memory_order_relaxed);
}

void AddFlatten(uint64_t ns, uint64_t inputSegments, uint64_t outputVerts) noexcept {
    State().flattenNs.fetch_add(ns, std::memory_order_relaxed);
    if (inputSegments != 0)
        State().flattenInputSegments.fetch_add(inputSegments, std::memory_order_relaxed);
    if (outputVerts != 0)
        State().flattenOutputVerts.fetch_add(outputVerts, std::memory_order_relaxed);
}

void AddTriangulate(uint64_t ns, bool ok) noexcept {
    State().triangulateNs.fetch_add(ns, std::memory_order_relaxed);
    if (ok)
        State().triangulateOk.fetch_add(1, std::memory_order_relaxed);
    else
        State().triangulateFail.fetch_add(1, std::memory_order_relaxed);
}

void AddCacheEviction(uint64_t count) noexcept {
    State().cacheEvictions.fetch_add(count, std::memory_order_relaxed);
}

}  // namespace path_stats
}  // namespace jalium

extern "C" {

JALIUM_API void jalium_query_path_stats(JaliumPathStats* out) {
    if (!out) return;
    auto& s = jalium::path_stats::State();
    std::memset(out, 0, sizeof(*out));
    out->version              = JALIUM_PATH_STATS_VERSION;
    out->strokeHits           = s.strokeHits.load(std::memory_order_relaxed);
    out->strokeMisses         = s.strokeMisses.load(std::memory_order_relaxed);
    out->strokeRects          = s.strokeRects.load(std::memory_order_relaxed);
    out->fillHits             = s.fillHits.load(std::memory_order_relaxed);
    out->fillMisses           = s.fillMisses.load(std::memory_order_relaxed);
    out->fillRects            = s.fillRects.load(std::memory_order_relaxed);
    out->geometryHits         = s.geometryHits.load(std::memory_order_relaxed);
    out->geometryMisses       = s.geometryMisses.load(std::memory_order_relaxed);
    out->flattenNs            = s.flattenNs.load(std::memory_order_relaxed);
    out->flattenInputSegments = s.flattenInputSegments.load(std::memory_order_relaxed);
    out->flattenOutputVerts   = s.flattenOutputVerts.load(std::memory_order_relaxed);
    out->triangulateNs        = s.triangulateNs.load(std::memory_order_relaxed);
    out->triangulateOk        = s.triangulateOk.load(std::memory_order_relaxed);
    out->triangulateFail      = s.triangulateFail.load(std::memory_order_relaxed);
    out->cacheEvictions       = s.cacheEvictions.load(std::memory_order_relaxed);
}

JALIUM_API void jalium_reset_path_stats(void) {
    auto& s = jalium::path_stats::State();
    s.strokeHits.store(0, std::memory_order_relaxed);
    s.strokeMisses.store(0, std::memory_order_relaxed);
    s.strokeRects.store(0, std::memory_order_relaxed);
    s.fillHits.store(0, std::memory_order_relaxed);
    s.fillMisses.store(0, std::memory_order_relaxed);
    s.fillRects.store(0, std::memory_order_relaxed);
    s.geometryHits.store(0, std::memory_order_relaxed);
    s.geometryMisses.store(0, std::memory_order_relaxed);
    s.flattenNs.store(0, std::memory_order_relaxed);
    s.flattenInputSegments.store(0, std::memory_order_relaxed);
    s.flattenOutputVerts.store(0, std::memory_order_relaxed);
    s.triangulateNs.store(0, std::memory_order_relaxed);
    s.triangulateOk.store(0, std::memory_order_relaxed);
    s.triangulateFail.store(0, std::memory_order_relaxed);
    s.cacheEvictions.store(0, std::memory_order_relaxed);
}

}  // extern "C"
