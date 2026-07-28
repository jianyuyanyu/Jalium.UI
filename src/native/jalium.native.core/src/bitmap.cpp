#include "jalium_internal.h"
#include "jalium_abi_guard.h"

// ============================================================================
// Bitmap C API Implementation
// ============================================================================

extern "C" {

JALIUM_API JaliumImage* jalium_bitmap_create_from_memory(
    JaliumContext* ctx,
    const uint8_t* data,
    uint32_t dataSize)
{
    if (!ctx || !data || dataSize == 0 ||
        dataSize > jalium::kMaxEncodedBitmapBytes) {
        return nullptr;
    }

    try {
        auto backend = jalium::GetBackendFromContext(ctx);
        if (!backend) {
            return nullptr;
        }

        auto bitmap = backend->CreateBitmapFromMemory(data, dataSize);
        return reinterpret_cast<JaliumImage*>(bitmap);
    } catch (...) {
        return nullptr;
    }
}

JALIUM_API JaliumImage* jalium_bitmap_create_from_pixels(
    JaliumContext* ctx,
    const uint8_t* pixels,
    uint32_t width,
    uint32_t height,
    uint32_t stride)
{
    jalium::PackedBgraLayout layout{};
    if (!ctx || !pixels ||
        !jalium::TryComputePackedBgraLayout(width, height, stride, layout)) {
        return nullptr;
    }

    try {
        auto backend = jalium::GetBackendFromContext(ctx);
        if (!backend) {
            return nullptr;
        }

        auto bitmap = backend->CreateBitmapFromPixels(pixels, width, height, stride);
        return reinterpret_cast<JaliumImage*>(bitmap);
    } catch (...) {
        return nullptr;
    }
}

JALIUM_API uint32_t jalium_bitmap_get_width(JaliumImage* bitmap) {
    if (!bitmap) return 0;
    return reinterpret_cast<jalium::Bitmap*>(bitmap)->GetWidth();
}

JALIUM_API uint32_t jalium_bitmap_get_height(JaliumImage* bitmap) {
    if (!bitmap) return 0;
    return reinterpret_cast<jalium::Bitmap*>(bitmap)->GetHeight();
}

JALIUM_API int32_t jalium_bitmap_update_pixels(
    JaliumImage* bitmap,
    const uint8_t* pixels,
    uint32_t width,
    uint32_t height,
    uint32_t stride)
{
    jalium::PackedBgraLayout layout{};
    if (!bitmap || !pixels ||
        !jalium::TryComputePackedBgraLayout(width, height, stride, layout)) {
        return 0;
    }
    try {
        auto* bmp = reinterpret_cast<jalium::Bitmap*>(bitmap);
        return bmp->UpdatePackedPixels(pixels, width, height, stride) ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

JALIUM_API void jalium_bitmap_destroy(JaliumImage* bitmap) {
    if (bitmap) {
        delete reinterpret_cast<jalium::Bitmap*>(bitmap);
    }
}

} // extern "C"
