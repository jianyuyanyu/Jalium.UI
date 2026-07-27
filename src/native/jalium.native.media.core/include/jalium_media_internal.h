#pragma once

#include "jalium_media.h"
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// Decoded images and CPU video frames cross a C ABI and are normally copied
// into a second managed/GPU buffer. Bound a single 32-bpp allocation so a
// small image bomb cannot commit multiple gigabytes before managed code gets a
// chance to inspect its dimensions. 512 MiB still permits a packed
// 11585x11585 image and every practical 8K video frame.
#define JALIUM_MEDIA_MAX_PIXEL_BUFFER_BYTES ((size_t)512u * 1024u * 1024u)
#define JALIUM_MEDIA_MAX_ENCODED_IMAGE_BYTES ((size_t)512u * 1024u * 1024u)

/// Computes the tightly-packed BGRA8/RGBA8 stride in bytes for a given width.
static inline uint32_t jalium_media_compute_stride(uint32_t width)
{
    if (width > UINT32_MAX / 4u) {
        return 0;
    }
    return width * 4u;
}

/// Validates a possibly padded 32-bpp pixel buffer without integer wrap.
static inline int jalium_media_compute_bgra_buffer_layout(
    uint32_t width,
    uint32_t height,
    size_t stride,
    size_t* out_size_bytes)
{
    if (out_size_bytes) *out_size_bytes = 0;
    if (width == 0 || height == 0 || !out_size_bytes) {
        return 0;
    }

    const uint32_t minimum_stride = jalium_media_compute_stride(width);
    if (minimum_stride == 0 || stride < (size_t)minimum_stride ||
        (size_t)height > SIZE_MAX / stride) {
        return 0;
    }

    const size_t size_bytes = stride * (size_t)height;
    if (size_bytes > JALIUM_MEDIA_MAX_PIXEL_BUFFER_BYTES) {
        return 0;
    }

    *out_size_bytes = size_bytes;
    return 1;
}

/// Computes a tightly packed 32-bpp layout without integer wrap.
static inline int jalium_media_compute_bgra_layout(
    uint32_t width,
    uint32_t height,
    uint32_t* out_stride,
    size_t* out_size_bytes)
{
    if (out_stride) *out_stride = 0;
    if (out_size_bytes) *out_size_bytes = 0;
    if (width == 0 || height == 0 || !out_stride || !out_size_bytes) {
        return 0;
    }

    const uint32_t stride = jalium_media_compute_stride(width);
    size_t size_bytes = 0;
    if (stride == 0 ||
        !jalium_media_compute_bgra_buffer_layout(
            width, height, (size_t)stride, &size_bytes)) {
        return 0;
    }

    *out_stride = stride;
    *out_size_bytes = size_bytes;
    return 1;
}

/// Validates a contiguous source frame before a row-by-row 32-bpp copy.
/// The returned source stride is derived from the source byte count and the
/// final-row bound is checked explicitly so a short decoder sample cannot be
/// read past its end.
static inline int jalium_media_validate_bgra_source_layout(
    uint32_t width,
    uint32_t height,
    size_t source_size_bytes,
    uint32_t* out_destination_stride,
    size_t* out_destination_size_bytes,
    size_t* out_source_stride)
{
    if (out_source_stride) *out_source_stride = 0;
    if (!out_source_stride ||
        !jalium_media_compute_bgra_layout(
            width,
            height,
            out_destination_stride,
            out_destination_size_bytes) ||
        source_size_bytes < *out_destination_size_bytes) {
        return 0;
    }

    const size_t source_stride = source_size_bytes / (size_t)height;
    if (source_stride < (size_t)*out_destination_stride) {
        return 0;
    }

    const size_t rows_before_last = (size_t)height - 1u;
    if (rows_before_last >
        (SIZE_MAX - (size_t)*out_destination_stride) / source_stride) {
        return 0;
    }
    if (rows_before_last * source_stride +
            (size_t)*out_destination_stride >
        source_size_bytes) {
        return 0;
    }

    *out_source_stride = source_stride;
    return 1;
}

static inline int jalium_media_is_valid_pixel_format(
    jalium_pixel_format_t format)
{
    return format == JALIUM_PF_BGRA8 || format == JALIUM_PF_RGBA8;
}

/// Allocates a 64-byte aligned pixel buffer of size_bytes. Returns NULL on OOM.
JALIUM_MEDIA_API void* jalium_media_aligned_alloc(size_t size_bytes);

/// Frees a buffer obtained from jalium_media_aligned_alloc.
JALIUM_MEDIA_API void jalium_media_aligned_free(void* ptr);

/// Swaps R and B channels in a packed 32bpp BGRA<->RGBA buffer in place.
/// width / height in pixels; stride_bytes the row pitch in bytes.
JALIUM_MEDIA_API void jalium_media_swap_rb_inplace(
    uint8_t* pixels,
    uint32_t width,
    uint32_t height,
    uint32_t stride_bytes);

#ifdef __cplusplus
}
#endif
