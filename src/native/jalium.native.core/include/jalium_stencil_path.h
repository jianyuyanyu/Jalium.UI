#pragma once

// jalium_stencil_path.h
//
// Local-space (pre-transform) stencil-then-cover path geometry, plus a bounded
// LRU cache keyed on path command bytes.
//
// Why this exists: see [[project_d3d12_path_reference.md]] / reference impl in
// docs/reference/pure_d3d12_path_renderer.h. The reference renders SVG paths
// by:
//   1) flatten path commands once → list of contour polylines (CPU);
//   2) emit fan triangles { anchor, edge[i], edge[i+1] } for every edge,
//      written into stencil with INVERT (EvenOdd) or INCR/DECR (NonZero);
//   3) draw a bbox quad with stencil-NotEqual-zero — that's the fill.
//
// The fan-anchor scheme works for *any* contour, including self-intersecting
// and multi-figure SVGs, because stencil winding handles the parity. No
// triangulation. CPU work per path is O(verts), not O(verts^3) ear-clip nor
// O(rows * subsamples * edges) scanline AA.
//
// Geometry is cached in *local* (pre-transform) space so the same icon at
// different scales/positions reuses the same vertex buffer. Cache key
// includes scaleBucket so vertex density scales with on-screen size — see
// jalium_flatten.h ScaleBucketFromMaxScale.

#include <cstdint>
#include <list>
#include <memory>
#include <optional>
#include <unordered_map>
#include <vector>

#include "jalium_api.h"

namespace jalium {

// One float2 vertex in local (path-source) space. Vertex shader applies the
// 2x3 affine transform supplied via root constants per draw call.
struct StencilPathVertex {
    float x;
    float y;
};

// Geometry built from a single path. fillTriangles are fed to the stencil
// pass; coverTriangles are fed to the cover pass once stencil is set.
struct StencilPathGeometry {
    std::vector<StencilPathVertex> fillTriangles;   // 3 verts per triangle
    std::vector<StencilPathVertex> coverTriangles;  // 6 verts (bbox quad)

    // Local-space bounds, used by callers for scissor clipping.
    float boundsMinX = 0.0f;
    float boundsMinY = 0.0f;
    float boundsMaxX = 0.0f;
    float boundsMaxY = 0.0f;
    bool  hasBounds  = false;
};

// Builds the stencil-then-cover geometry from raw path commands. Flatten
// tolerance is in source-space units (callers typically pass
// 0.5f / max(scale, 1.0f) so on-screen tolerance stays around half a pixel).
JALIUM_API std::shared_ptr<const StencilPathGeometry>
BuildStencilPathGeometry(float startX,
                         float startY,
                         const float* commands,
                         uint32_t commandLength,
                         float flattenTolerance);

// Bounded LRU. Same shape as PathGeometryCache — backends own one instance.
class JALIUM_API StencilPathCache {
public:
    explicit StencilPathCache(size_t capacity);
    ~StencilPathCache();

    std::shared_ptr<const StencilPathGeometry> FindAndTouch(uint64_t key);
    void Insert(uint64_t key, std::shared_ptr<const StencilPathGeometry> entry);

    void   Clear();
    size_t Size()      const noexcept { return list_.size(); }
    size_t Capacity()  const noexcept { return capacity_; }

private:
    struct Node {
        uint64_t key;
        std::shared_ptr<const StencilPathGeometry> entry;
    };
    using ListIt = std::list<Node>::iterator;

    std::list<Node> list_;
    std::unordered_map<uint64_t, ListIt> map_;
    size_t capacity_;
};

// Hash key for the stencil path cache. Same FNV-1a 64-bit construction as
// HashPathInput in jalium_path_cache.h, parameterised on the same inputs.
JALIUM_API uint64_t HashStencilPathInput(float startX,
                                         float startY,
                                         const float* commands,
                                         uint32_t commandLength,
                                         uint32_t scaleBucket) noexcept;

} // namespace jalium
