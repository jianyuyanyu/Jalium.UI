#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

#include "jalium_rendering_engine.h"  // EngineTransform (used in callers)

namespace jalium {

// Helpers for picking a flatten tolerance and quantizing transform scale into
// a stable cache-key bucket. These are pure header-only functions — every
// backend dll inlines them. No cross-dll dependencies.
//
// Why a bucket and not the raw scale: PathGeometryCache (jalium_path_cache.h)
// keys by source-space path data. If we mixed transform scale into the key
// directly (e.g. as a float), the cache would re-miss on every sub-pixel
// transform change — the gallery's smooth scale animations would never hit.
// Quantizing to log-scale buckets (4 per octave, ~19% step) makes the cache
// hit when the scale change is below the per-octave resolution threshold,
// and miss only when a new octave actually needs a different vertex density.
//
// Tolerance picking: pixel-space flatten already produces per-pixel-correct
// vertex counts (see d3d12_impeller_engine.cpp:1737-1755 commentary). We
// keep the base 0.25-pixel tolerance there and use ComputePixelTolerance
// only when callers want device-pixel-ratio-aware nudging — high-DPI
// surfaces (1.5×/2× DPR) want a tighter tolerance so on-screen pixels stay
// crisp, while low-DPR / smaller surfaces can afford to relax it.

// Largest non-translation scale factor from the 2x2 linear part of a 2x3
// affine. Used to decide:
//  1) effective flatten tolerance ("how big does a 0.25-source-unit error
//     look on screen?")
//  2) PathGeometryCache scaleBucket — see ScaleBucketFromMaxScale below.
inline float MaxScaleFromMatrix(float m11, float m12,
                                float m21, float m22) noexcept {
    float r0 = std::sqrt(m11 * m11 + m12 * m12);
    float r1 = std::sqrt(m21 * m21 + m22 * m22);
    return (std::max)(r0, r1);
}

inline float MaxScaleFromTransform(const EngineTransform& t) noexcept {
    return MaxScaleFromMatrix(t.m11, t.m12, t.m21, t.m22);
}

// Pixel-space flatten tolerance. The 0.25-pixel baseline is conservative
// against aliasing without exploding Wang's-formula N. We let high-DPR
// callers tighten it slightly and low-DPR callers relax it, clamped to
// [0.125, 1.0] so ear-clip can't blow up on one end nor edges become
// polygon-faceted on the other.
//
// devicePixelRatio: physical-pixels-per-DIP at the destination surface.
//                   1.0 on standard DPI; 1.5 / 1.75 / 2.0 on hi-DPI.
// qualityHint:      caller-supplied scale (e.g. animation phase, gallery
//                   quality slider). 1.0 = default.
inline float ComputePixelTolerance(float devicePixelRatio,
                                   float qualityHint = 1.0f) noexcept {
    constexpr float kBase     = 0.25f;
    constexpr float kFloor    = 0.125f;
    constexpr float kCeiling  = 1.0f;
    float s = devicePixelRatio * qualityHint;
    if (!(s > 0.5f)) s = 0.5f;  // also catches NaN via !(s>0.5)
    float tol = kBase / s;
    if (tol < kFloor) tol = kFloor;
    if (tol > kCeiling) tol = kCeiling;
    return tol;
}

// Log-scale bucket of a transform's max scale. Buckets are 4 per octave —
// roughly 19% step — so a 1.0×→1.18× scale change stays in the same bucket
// while 1.0×→1.20× crosses. This is below human-perceptible vertex-density
// change for icon-scale paths but coarse enough that cache hit rate stays
// useful under smooth animation.
//
// Convention: bucket 0 corresponds to maxScale ≈ 1.0×. Negative buckets are
// small scales, positive are large. The cast to uint32_t reinterprets — we
// only compare for equality, never order, so the bit pattern uniqueness is
// what matters.
inline uint32_t ScaleBucketFromMaxScale(float maxScale) noexcept {
    if (!(maxScale > 0.0f)) return 0u;  // also catches NaN
    int32_t b = static_cast<int32_t>(std::lround(std::log2(maxScale) * 4.0f));
    // Reinterpret the signed bucket as unsigned for hashing — equal bucket
    // → equal uint32_t, which is what HashPathInput needs.
    return static_cast<uint32_t>(b);
}

}  // namespace jalium
