using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

/// <summary>
/// Pixel-level contract of <see cref="RenderTarget.DrawBackdropMaterial"/> on the
/// D3D12, Vulkan and Software backends: the blur radius is honoured past the old 8-texel D3D12 clamp,
/// and every colour-pipeline parameter (grayscale, invert, tint alpha, grain,
/// opacity) visibly changes the backdrop. Each test renders a tiny scene into a
/// hidden swap chain and reads the back buffer back (BGRA8).
/// </summary>
[Collection("Application")]
public sealed class BackdropMaterialQualityTests
{
    private const int Width = 320;
    private const int Height = 128;
    private const int PanelX = 24;
    private const int PanelY = 24;
    private const int PanelWidth = 272;
    private const int PanelHeight = 80;
    private const int Inset = 10;

    // --- D3D12 --------------------------------------------------------------

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_Blur_LargerRadiusProducesWiderTransition() => AssertBlurRadiusHonoured(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_Grayscale_RemovesChroma() => AssertGrayscaleRemovesChroma(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_Invert_FlipsLuminance() => AssertInvertFlipsLuminance(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_OpacityZero_LeavesSceneUntouched() => AssertOpacityZeroIsNoOp(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_Noise_AddsGrain() => AssertNoiseAddsGrain(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_TintAlpha_Honoured() => AssertTintAlphaHonoured(RenderBackend.D3D12);

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12_LegacyFilterEx_MatchesMaterial() => AssertLegacyEntryPointMatchesMaterial(RenderBackend.D3D12);

    // --- Vulkan --------------------------------------------------------------

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_Blur_LargerRadiusProducesWiderTransition() => AssertBlurRadiusHonoured(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_Grayscale_RemovesChroma() => AssertGrayscaleRemovesChroma(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_Invert_FlipsLuminance() => AssertInvertFlipsLuminance(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_OpacityZero_LeavesSceneUntouched() => AssertOpacityZeroIsNoOp(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_Noise_AddsGrain() => AssertNoiseAddsGrain(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_TintAlpha_Honoured() => AssertTintAlphaHonoured(RenderBackend.Vulkan);

    [RequiresWindowsBackendFact(RenderBackend.Vulkan)]
    public void Vulkan_LegacyFilterEx_MatchesMaterial() => AssertLegacyEntryPointMatchesMaterial(RenderBackend.Vulkan);

    // --- Software (CPU material path) ---------------------------------------

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_Blur_LargerRadiusProducesWiderTransition() => AssertBlurRadiusHonoured(RenderBackend.Software);

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_Grayscale_RemovesChroma() => AssertGrayscaleRemovesChroma(RenderBackend.Software);

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_Invert_FlipsLuminance() => AssertInvertFlipsLuminance(RenderBackend.Software);

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_OpacityZero_LeavesSceneUntouched() => AssertOpacityZeroIsNoOp(RenderBackend.Software);

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_Noise_AddsGrain() => AssertNoiseAddsGrain(RenderBackend.Software);

    [RequiresWindowsBackendFact(RenderBackend.Software)]
    public void Software_TintAlpha_Honoured() => AssertTintAlphaHonoured(RenderBackend.Software);

    // --- Scenarios -------------------------------------------------------------

    private static void AssertBlurRadiusHonoured(RenderBackend backend)
    {
        byte[] radius8 = Render(backend, Scene.BlackWhite, Material(blurRadius: 8f));
        byte[] radius32 = Render(backend, Scene.BlackWhite, Material(blurRadius: 32f));

        int narrowWidth = MeasureTransitionWidth(radius8, Height / 2);
        int wideWidth = MeasureTransitionWidth(radius32, Height / 2);

        Assert.True(narrowWidth >= 3,
            $"{backend}: 8px backdrop blur did not soften the edge: width={narrowWidth}px");
        Assert.True(wideWidth >= narrowWidth + 10,
            $"{backend}: 32px backdrop blur was still effectively clamped near 8px: " +
            $"narrow={narrowWidth}px, wide={wideWidth}px");
    }

    private static void AssertGrayscaleRemovesChroma(RenderBackend backend)
    {
        byte[] colour = Render(backend, Scene.RedGreen, Material(blurRadius: 0f));
        byte[] grey = Render(backend, Scene.RedGreen, Material(blurRadius: 0f, grayscale: 1f));

        (double chromaBefore, _) = MeanChromaAndLuma(colour);
        (double chromaAfter, double lumaAfter) = MeanChromaAndLuma(grey);

        Assert.True(chromaBefore > 100,
            $"{backend}: the red/green scene should be strongly chromatic before grayscale (|r-g| mean={chromaBefore:F1})");
        Assert.True(chromaAfter < 6,
            $"{backend}: grayscale=1 left chroma in the backdrop (|r-g| mean={chromaAfter:F1})");
        Assert.True(lumaAfter > 40 && lumaAfter < 220,
            $"{backend}: grayscale output luma out of range ({lumaAfter:F1})");
    }

    private static void AssertInvertFlipsLuminance(RenderBackend backend)
    {
        byte[] inverted = Render(backend, Scene.BlackWhite, Material(blurRadius: 0f, invert: 1f));

        double leftLuma = MeanLuma(inverted, PanelX + Inset, Width / 2 - Inset);
        double rightLuma = MeanLuma(inverted, Width / 2 + Inset, PanelX + PanelWidth - Inset);

        Assert.True(leftLuma > 230,
            $"{backend}: invert=1 should turn the black half white inside the panel (luma={leftLuma:F1})");
        Assert.True(rightLuma < 25,
            $"{backend}: invert=1 should turn the white half black inside the panel (luma={rightLuma:F1})");
    }

    private static void AssertOpacityZeroIsNoOp(RenderBackend backend)
    {
        byte[] plain = Render(backend, Scene.RedGreen, material: null);
        byte[] zeroOpacity = Render(backend, Scene.RedGreen,
            Material(blurRadius: 24f, grayscale: 1f, invert: 1f, tintA: 1f, opacity: 0f));

        AssertImagesNear(plain, zeroOpacity,
            $"{backend}: opacity=0 must leave the scene byte-identical under the panel");
    }

    private static void AssertNoiseAddsGrain(RenderBackend backend)
    {
        byte[] smooth = Render(backend, Scene.Grey, Material(blurRadius: 0f, noiseIntensity: 0f));
        byte[] grainy = Render(backend, Scene.Grey, Material(blurRadius: 0f, noiseIntensity: 0.2f));

        double smoothDeviation = LumaStdDev(smooth);
        double grainyDeviation = LumaStdDev(grainy);

        Assert.True(smoothDeviation < 1.0,
            $"{backend}: a flat grey backdrop without noise should be flat (stddev={smoothDeviation:F2})");
        Assert.True(grainyDeviation > 6.0,
            $"{backend}: noiseIntensity=0.2 produced no measurable grain (stddev={grainyDeviation:F2})");
    }

    private static void AssertTintAlphaHonoured(RenderBackend backend)
    {
        byte[] half = Render(backend, Scene.Black, Material(blurRadius: 0f, tintA: 0.5f));
        byte[] full = Render(backend, Scene.Black, Material(blurRadius: 0f, tintA: 1f));

        double halfLuma = MeanLuma(half, PanelX + Inset, PanelX + PanelWidth - Inset);
        double fullLuma = MeanLuma(full, PanelX + Inset, PanelX + PanelWidth - Inset);

        Assert.True(Math.Abs(halfLuma - 127.5) < 8,
            $"{backend}: white tint at alpha 0.5 over black should read ~128 (got {halfLuma:F1})");
        Assert.True(fullLuma > 245,
            $"{backend}: white tint at alpha 1 over black should read ~255 (got {fullLuma:F1})");
    }

    private static void AssertLegacyEntryPointMatchesMaterial(RenderBackend backend)
    {
        byte[] material = Render(backend, Scene.RedGreen,
            Material(blurRadius: 16f, tintR: 0.2f, tintG: 0.4f, tintB: 0.8f, tintA: 0.35f,
                     saturation: 1.3f, luminosity: 1.05f));
        byte[] legacy = Render(backend, Scene.RedGreen, material: null, legacy: true);

        AssertImagesNear(material, legacy,
            $"{backend}: DrawBackdropFilterEx must render through the same material path");
    }

    // --- Rendering -------------------------------------------------------------

    private enum Scene { BlackWhite, RedGreen, Grey, Black }

    private static BackdropMaterialDesc Material(
        float blurRadius,
        float grayscale = 0f,
        float invert = 0f,
        float tintR = 1f, float tintG = 1f, float tintB = 1f, float tintA = 0f,
        float noiseIntensity = 0f,
        float opacity = 1f,
        float saturation = 1f,
        float luminosity = 1f)
    {
        return new BackdropMaterialDesc
        {
            StructSize = BackdropMaterialDesc.NativeStructSize,
            BlurType = (uint)BackdropMaterialBlurType.Gaussian,
            X = PanelX,
            Y = PanelY,
            Width = PanelWidth,
            Height = PanelHeight,
            BlurRadius = blurRadius,
            BlurSigma = 0f,
            NoiseIntensity = noiseIntensity,
            TintR = tintR,
            TintG = tintG,
            TintB = tintB,
            TintA = tintA,
            Saturation = saturation,
            Luminosity = luminosity,
            Brightness = 1f,
            Contrast = 1f,
            HueRotation = 0f,
            Grayscale = grayscale,
            Sepia = 0f,
            Invert = invert,
            Opacity = opacity,
        };
    }

    private static byte[] Render(RenderBackend backend, Scene scene, BackdropMaterialDesc? material, bool legacy = false)
    {
        using var window = new HiddenNativeWindow(Width, Height);
        using var context = new RenderContext(backend);
        Assert.Equal(backend, context.Backend);

        using var renderTarget = context.CreateRenderTarget(window.Hwnd, Width, Height);
        Assert.True(renderTarget.IsValid);
        using var black = context.CreateSolidBrush(0f, 0f, 0f, 1f);
        using var white = context.CreateSolidBrush(1f, 1f, 1f, 1f);
        using var red = context.CreateSolidBrush(1f, 0f, 0f, 1f);
        using var green = context.CreateSolidBrush(0f, 1f, 0f, 1f);
        using var grey = context.CreateSolidBrush(0.5f, 0.5f, 0.5f, 1f);
        Assert.True(black.IsValid && white.IsValid && red.IsValid && green.IsValid && grey.IsValid);

        // Two frames: the first primes swap-chain / scratch allocations so the
        // second (the one read back) is the steady-state frame.
        for (int frame = 0; frame < 2; frame++)
        {
            Assert.True(renderTarget.TryBeginDraw());
            renderTarget.Clear(0f, 0f, 0f, 1f);

            switch (scene)
            {
                case Scene.BlackWhite:
                    renderTarget.FillRectangle(0f, 0f, Width, Height, black);
                    renderTarget.FillRectangle(Width / 2f, 0f, Width / 2f, Height, white);
                    break;
                case Scene.RedGreen:
                    renderTarget.FillRectangle(0f, 0f, Width / 2f, Height, red);
                    renderTarget.FillRectangle(Width / 2f, 0f, Width / 2f, Height, green);
                    break;
                case Scene.Grey:
                    renderTarget.FillRectangle(0f, 0f, Width, Height, grey);
                    break;
                case Scene.Black:
                    renderTarget.FillRectangle(0f, 0f, Width, Height, black);
                    break;
            }

            if (legacy)
            {
                renderTarget.DrawBackdropFilterEx(
                    PanelX, PanelY, PanelWidth, PanelHeight,
                    backdropFilter: null,
                    material: null,
                    materialTint: "#3366CC",
                    materialTintOpacity: 0.35f,
                    materialBlurRadius: 16f,
                    noiseIntensity: 0f,
                    saturation: 1.3f,
                    luminosity: 1.05f,
                    cornerRadiusTL: 0f,
                    cornerRadiusTR: 0f,
                    cornerRadiusBR: 0f,
                    cornerRadiusBL: 0f);
            }
            else if (material.HasValue)
            {
                renderTarget.DrawBackdropMaterial(material.Value);
            }

            if (frame == 1)
            {
                Assert.Equal(JaliumResult.Ok, renderTarget.RequestReadback());
            }
            Assert.Equal(JaliumResult.Ok, renderTarget.TryEndDraw());
        }

        var pixels = new byte[Width * Height * 4];
        Assert.Equal(JaliumResult.Ok,
            renderTarget.FetchReadback(pixels, Width * 4u, out int capturedWidth, out int capturedHeight));
        Assert.True(capturedWidth >= Width && capturedHeight >= Height);
        return pixels;
    }

    // --- Measurement -----------------------------------------------------------

    private static int MeasureTransitionWidth(byte[] pixels, int y)
    {
        int low = -1;
        int high = -1;
        for (int x = PanelX; x < PanelX + PanelWidth; x++)
        {
            int offset = (y * Width + x) * 4;
            int luminance = (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
            if (low < 0 && luminance >= 26)
            {
                low = x;
            }
            if (high < 0 && luminance >= 230)
            {
                high = x;
                break;
            }
        }
        return (low < 0 || high < 0) ? 0 : high - low;
    }

    private static double MeanLuma(byte[] pixels, int x0, int x1)
    {
        double sum = 0;
        int count = 0;
        for (int y = PanelY + Inset; y < PanelY + PanelHeight - Inset; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int offset = (y * Width + x) * 4;
                sum += (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3.0;
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    private static (double chroma, double luma) MeanChromaAndLuma(byte[] pixels)
    {
        double chroma = 0;
        double luma = 0;
        int count = 0;
        for (int y = PanelY + Inset; y < PanelY + PanelHeight - Inset; y++)
        {
            for (int x = PanelX + Inset; x < PanelX + PanelWidth - Inset; x++)
            {
                int offset = (y * Width + x) * 4;
                int b = pixels[offset];
                int g = pixels[offset + 1];
                int r = pixels[offset + 2];
                chroma += Math.Abs(r - g);
                luma += (r + g + b) / 3.0;
                count++;
            }
        }
        return count == 0 ? (0, 0) : (chroma / count, luma / count);
    }

    private static double LumaStdDev(byte[] pixels)
    {
        double sum = 0;
        double sumSq = 0;
        int count = 0;
        for (int y = PanelY + Inset; y < PanelY + PanelHeight - Inset; y++)
        {
            for (int x = PanelX + Inset; x < PanelX + PanelWidth - Inset; x++)
            {
                int offset = (y * Width + x) * 4;
                double l = (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3.0;
                sum += l;
                sumSq += l * l;
                count++;
            }
        }
        if (count == 0) return 0;
        double mean = sum / count;
        return Math.Sqrt(Math.Max(0, sumSq / count - mean * mean));
    }

    private static void AssertImagesNear(byte[] expected, byte[] actual, string message)
    {
        int maxDifference = 0;
        long totalDifference = 0;
        int comparedChannels = 0;
        for (int y = PanelY + Inset; y < PanelY + PanelHeight - Inset; y++)
        {
            for (int x = PanelX + Inset; x < PanelX + PanelWidth - Inset; x++)
            {
                int offset = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = Math.Abs(expected[offset + channel] - actual[offset + channel]);
                    maxDifference = Math.Max(maxDifference, difference);
                    totalDifference += difference;
                    comparedChannels++;
                }
            }
        }

        double meanDifference = (double)totalDifference / comparedChannels;
        Assert.True(maxDifference <= 3 && meanDifference <= 0.5,
            $"{message}: maxDiff={maxDifference}, meanDiff={meanDifference:F3}");
    }
}
