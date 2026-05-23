#include "jalium_text_options.h"
#include "jalium_backend.h"

#include <atomic>
#include <cstdio>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN  // cmake already defines this; guard so MSVC C4005 stays quiet
#endif
#include <windows.h>
#endif

namespace jalium {
namespace text_options {
namespace {

// Default: AUTO. Each backend resolves Auto to its platform-native choice
// when reading the value so we don't have to know the host OS at init time.
std::atomic<int32_t>  g_mode{JALIUM_TEXT_AA_AUTO};
std::atomic<uint64_t> g_generation{0};

}  // namespace

int32_t ResolveMode(int32_t mode) noexcept {
    // Auto resolves to Grayscale on every platform. Earlier this returned
    // ClearType on Windows to match the WPF / Win32 convention, but the
    // framework now defaults to Grayscale everywhere because:
    //   - High-DPI screens (the common case for new installs) make ClearType's
    //     sub-pixel fringe more distracting than helpful.
    //   - Any render target that gets resampled — off-screen bitmaps,
    //     RenderTargetBitmap, ScaleTransform, ImageBrush — must NOT carry
    //     sub-pixel fringes; ClearType makes the resampled text look broken,
    //     and the framework can't always know which path a glyph will take.
    //   - The Vello / Impeller / Vulkan / software backends already render
    //     grayscale natively; only D3D12 had a separate ClearType path. Picking
    //     Grayscale as the universal default keeps text identical across
    //     backends so a single asset / screenshot is reproducible everywhere.
    // Windows callers that still want sub-pixel ClearType opt in explicitly:
    //     TextOptions.ProcessTextRenderingMode = TextRenderingMode.ClearType;
    // Per-element overrides via TextOptions.TextRenderingModeProperty also
    // still work; only the Auto fallback chain landed here.
    if (mode == JALIUM_TEXT_AA_AUTO) {
        return JALIUM_TEXT_AA_GRAYSCALE;
    }
    if (mode < JALIUM_TEXT_AA_AUTO || mode > JALIUM_TEXT_AA_CLEARTYPE) {
        return JALIUM_TEXT_AA_GRAYSCALE;
    }
    return mode;
}

}  // namespace text_options

int32_t TextFormat::ResolveEffectiveTextRenderingMode() const noexcept {
    // Per-format mode wins when the caller explicitly picked one. Auto
    // (the managed-side default) delegates to the process-wide setting so
    // a single TextOptions.ProcessTextRenderingMode = ClearType still flips
    // every glyph globally; only formats that opt out by setting their own
    // value override that. Process-wide Auto resolves to Grayscale on every
    // platform via ResolveMode — opting back into Windows-style sub-pixel
    // ClearType means setting the process-wide mode explicitly.
    int32_t mode = text_rendering_mode_;
    if (mode == JALIUM_TEXT_AA_AUTO) {
        mode = jalium_text_get_global_antialias_mode();
    }
    return jalium::text_options::ResolveMode(mode);
}

}  // namespace jalium

extern "C" {

JALIUM_API void jalium_text_set_global_antialias_mode(int32_t mode) {
    using namespace jalium::text_options;
    if (mode < JALIUM_TEXT_AA_AUTO || mode > JALIUM_TEXT_AA_CLEARTYPE) {
        mode = JALIUM_TEXT_AA_AUTO;
    }
    int32_t previous = g_mode.exchange(mode, std::memory_order_release);
    if (previous != mode) {
        // Bump the generation so live atlases notice the change on their next
        // frame and reset their cached glyph entries. This is the only signal
        // that crosses the C ABI — backends compare lastSeenGen against this.
        g_generation.fetch_add(1, std::memory_order_acq_rel);

#if defined(_WIN32)
        // Always-on trace: there's no harm in one line per mode flip and the
        // information is critical for diagnosing "I set Grayscale but still
        // see ClearType fringe" reports — confirms the P/Invoke reached the
        // native dll, with the resolved-to value the backend will actually use.
        char buf[160];
        const int resolved = ResolveMode(mode);
        const char* names[] = { "Auto", "Aliased", "Grayscale", "ClearType" };
        const char* nm  = (mode     >= 0 && mode     <= 3) ? names[mode]     : "?";
        const char* res = (resolved >= 0 && resolved <= 3) ? names[resolved] : "?";
        std::snprintf(buf, sizeof(buf),
            "[jalium text] antialias mode -> %s (resolved=%s) gen=%llu\n",
            nm, res, (unsigned long long)g_generation.load(std::memory_order_acquire));
        OutputDebugStringA(buf);
#endif
    }
}

JALIUM_API int32_t jalium_text_get_global_antialias_mode(void) {
    return jalium::text_options::g_mode.load(std::memory_order_acquire);
}

JALIUM_API uint64_t jalium_text_get_antialias_generation(void) {
    return jalium::text_options::g_generation.load(std::memory_order_acquire);
}

}  // extern "C"
