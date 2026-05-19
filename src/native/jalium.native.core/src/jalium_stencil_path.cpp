#include "jalium_stencil_path.h"
#include "jalium_triangulate.h"
#include "jalium_path_stats.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace jalium {

namespace {

constexpr uint64_t kFnvOffset = 0xcbf29ce484222325ULL;
constexpr uint64_t kFnvPrime  = 0x100000001b3ULL;

inline uint64_t FnvMix(uint64_t h, uint64_t v) noexcept {
    h ^= v;
    h *= kFnvPrime;
    return h;
}

inline uint64_t FnvMixBytes(uint64_t h, const void* data, size_t bytes) noexcept {
    auto* p = static_cast<const uint8_t*>(data);
    for (size_t i = 0; i < bytes; ++i) {
        h ^= p[i];
        h *= kFnvPrime;
    }
    return h;
}

inline uint32_t FloatBits(float f) noexcept {
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    return bits;
}

} // namespace

uint64_t HashStencilPathInput(float startX,
                              float startY,
                              const float* commands,
                              uint32_t commandLength,
                              uint32_t scaleBucket) noexcept
{
    uint64_t h = kFnvOffset;
    h = FnvMix(h, FloatBits(startX));
    h = FnvMix(h, FloatBits(startY));
    h = FnvMix(h, static_cast<uint64_t>(scaleBucket));
    h = FnvMix(h, static_cast<uint64_t>(commandLength));
    if (commands && commandLength > 0) {
        h = FnvMixBytes(h, commands,
                        static_cast<size_t>(commandLength) * sizeof(float));
    }
    return h;
}

std::shared_ptr<const StencilPathGeometry>
BuildStencilPathGeometry(float startX,
                         float startY,
                         const float* commands,
                         uint32_t commandLength,
                         float flattenTolerance)
{
    path_stats::ScopedFlattenTimer flattenTimer(commandLength);

    auto geom = std::make_shared<StencilPathGeometry>();

    if (!(flattenTolerance > 0.0f)) flattenTolerance = 0.5f;

    // Flatten the path to polyline contours (this is the only Bezier work
    // we do per build; cached afterwards across frames).
    std::vector<Contour> contours = FlattenPathToContours(
        startX, startY, commands, commandLength, flattenTolerance);

    if (contours.empty()) {
        flattenTimer.RecordOutputVerts(0);
        return geom;
    }

    // Compute bounds across all contours.
    float minX =  std::numeric_limits<float>::infinity();
    float minY =  std::numeric_limits<float>::infinity();
    float maxX = -std::numeric_limits<float>::infinity();
    float maxY = -std::numeric_limits<float>::infinity();
    uint64_t outputVerts = 0;

    for (const auto& c : contours) {
        uint32_t n = c.VertexCount();
        outputVerts += n;
        for (uint32_t i = 0; i < n; ++i) {
            float x = c.X(i), y = c.Y(i);
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
    }
    flattenTimer.RecordOutputVerts(outputVerts);

    if (!(minX < maxX) || !(minY < maxY)) {
        // Degenerate / sub-pixel — nothing to draw.
        return geom;
    }

    geom->boundsMinX = minX;
    geom->boundsMinY = minY;
    geom->boundsMaxX = maxX;
    geom->boundsMaxY = maxY;
    geom->hasBounds = true;

    // Anchor for the fan: outside the bbox, far enough that none of the
    // generated triangles is degenerate against any contour edge. Match
    // the reference impl (margin = max(8, max(W,H) + 8)).
    float w = maxX - minX;
    float h = maxY - minY;
    float margin = (std::max)(8.0f, (std::max)(w, h) + 8.0f);
    StencilPathVertex anchor{ minX - margin, minY - margin };

    // Fan triangles for stencil pass.
    // For every edge (a → b) in every contour, emit { anchor, a, b }.
    // Stencil INVERT (EvenOdd) or INCR/DECR (NonZero) accumulates winding;
    // pixels inside the path end up non-zero, outside end up zero.
    size_t triReserve = 0;
    for (const auto& c : contours) {
        uint32_t n = c.VertexCount();
        if (n >= 2) triReserve += n;  // at most n edges (closed contour)
    }
    geom->fillTriangles.reserve(triReserve * 3);

    for (const auto& c : contours) {
        uint32_t n = c.VertexCount();
        if (n < 2) continue;

        for (uint32_t i = 0; i + 1 < n; ++i) {
            float ax = c.X(i),     ay = c.Y(i);
            float bx = c.X(i + 1), by = c.Y(i + 1);
            // Skip zero-length edges so we don't pollute stencil with empties.
            if (std::fabs(ax - bx) < 1e-6f && std::fabs(ay - by) < 1e-6f) continue;
            geom->fillTriangles.push_back(anchor);
            geom->fillTriangles.push_back({ ax, ay });
            geom->fillTriangles.push_back({ bx, by });
        }

        // Implicit close: last → first if not coincident.
        if (n >= 3) {
            float fx = c.X(0),     fy = c.Y(0);
            float lx = c.X(n - 1), ly = c.Y(n - 1);
            if (std::fabs(fx - lx) > 1e-6f || std::fabs(fy - ly) > 1e-6f) {
                geom->fillTriangles.push_back(anchor);
                geom->fillTriangles.push_back({ lx, ly });
                geom->fillTriangles.push_back({ fx, fy });
            }
        }
    }

    if (geom->fillTriangles.empty()) {
        return geom;  // no usable edges — stays empty
    }

    // Cover quad: must cover EVERY pixel the stencil pass could have
    // touched, not just the path's bbox. The fan triangles span from the
    // anchor (placed in the upper-left, outside the bbox) to each edge,
    // so the union of all fan triangles is the rectangle
    // [anchor, (maxX+1, maxY+1)]. Cover-pass STENCIL_OP_REPLACE then resets
    // stencil to 0 across that entire region, leaving the buffer clean for
    // the next path. If we used the tighter bbox-only quad, stencil bits
    // written in the wedge between the anchor and the path edges would
    // leak into subsequent paths' stencil tests — manifesting as ragged /
    // ghosted strokes on every path drawn after the first.
    //
    // Coverage testing inside the bbox is unchanged: stencil-NotEqual-0
    // is only non-zero inside the original path interior, so widening the
    // cover quad doesn't paint anything new — it just guarantees stencil
    // gets reset everywhere it could have been dirty.
    float coverL = anchor.x;
    float coverT = anchor.y;
    float coverR = maxX + 1.0f;
    float coverB = maxY + 1.0f;
    geom->coverTriangles = {
        { coverL, coverT }, { coverR, coverT }, { coverR, coverB },
        { coverL, coverT }, { coverR, coverB }, { coverL, coverB },
    };

    return geom;
}

// ────────────────────────────────────────────────────────────────────────────
// LRU cache
// ────────────────────────────────────────────────────────────────────────────

StencilPathCache::StencilPathCache(size_t capacity) : capacity_(capacity) {
    map_.reserve(capacity * 2);
}

StencilPathCache::~StencilPathCache() = default;

std::shared_ptr<const StencilPathGeometry>
StencilPathCache::FindAndTouch(uint64_t key)
{
    auto it = map_.find(key);
    if (it == map_.end()) {
        path_stats::AddGeometryMiss();
        return nullptr;
    }
    list_.splice(list_.begin(), list_, it->second);
    path_stats::AddGeometryHit();
    return it->second->entry;
}

void StencilPathCache::Insert(uint64_t key,
                              std::shared_ptr<const StencilPathGeometry> entry)
{
    if (!entry) return;

    auto existing = map_.find(key);
    if (existing != map_.end()) {
        existing->second->entry = std::move(entry);
        list_.splice(list_.begin(), list_, existing->second);
        return;
    }

    uint64_t evicted = 0;
    while (list_.size() >= capacity_ && !list_.empty()) {
        const auto& tail = list_.back();
        map_.erase(tail.key);
        list_.pop_back();
        ++evicted;
    }
    if (evicted != 0) {
        path_stats::AddCacheEviction(evicted);
    }

    list_.push_front(Node{ key, std::move(entry) });
    map_.emplace(key, list_.begin());
}

void StencilPathCache::Clear() {
    list_.clear();
    map_.clear();
}

} // namespace jalium
