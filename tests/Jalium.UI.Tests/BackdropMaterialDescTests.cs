using Jalium.UI.Interop;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

/// <summary>
/// Pure-managed contract of <see cref="BackdropMaterialDesc.FromEffect"/>: every
/// <see cref="IBackdropEffect"/> member reaches the native material struct,
/// tint alpha is folded the WinUI way, and the struct stays ABI-sized.
/// </summary>
public sealed class BackdropMaterialDescTests
{
    [Fact]
    public void StructSize_MatchesNativeLayout()
    {
        Assert.Equal(26u * 4u, BackdropMaterialDesc.NativeStructSize);
        Assert.Equal((int)BackdropMaterialDesc.NativeStructSize,
            System.Runtime.InteropServices.Marshal.SizeOf<BackdropMaterialDesc>());
    }

    [Fact]
    public void FromEffect_CarriesEveryColorPipelineParameter()
    {
        var effect = new ColorAdjustmentEffect
        {
            BlurRadius = 12f,
            BlurSigma = 5f,
            BlurType = BackdropBlurType.Frosted,
            NoiseIntensity = 0.04f,
            Brightness = 1.2f,
            Contrast = 0.8f,
            Saturation = 1.5f,
            HueRotation = 1.25f,
            Grayscale = 0.3f,
            Sepia = 0.4f,
            Invert = 0.5f,
            Opacity = 0.75f,
            Luminosity = 1.03f,
            TintColor = Color.FromArgb(255, 255, 0, 0),
            TintOpacity = 0.6f,
        };

        var desc = BackdropMaterialDesc.FromEffect(
            effect, 10f, 20f, 300f, 200f, new CornerRadius(4, 6, 8, 10));

        Assert.Equal(BackdropMaterialDesc.NativeStructSize, desc.StructSize);
        Assert.Equal((uint)BackdropMaterialBlurType.Frosted, desc.BlurType);
        Assert.Equal(10f, desc.X);
        Assert.Equal(20f, desc.Y);
        Assert.Equal(300f, desc.Width);
        Assert.Equal(200f, desc.Height);
        Assert.Equal(12f, desc.BlurRadius);
        Assert.Equal(5f, desc.BlurSigma);
        Assert.Equal(0.04f, desc.NoiseIntensity);
        Assert.Equal(1.2f, desc.Brightness);
        Assert.Equal(0.8f, desc.Contrast);
        Assert.Equal(1.5f, desc.Saturation);
        Assert.Equal(1.25f, desc.HueRotation);
        Assert.Equal(0.3f, desc.Grayscale);
        Assert.Equal(0.4f, desc.Sepia);
        Assert.Equal(0.5f, desc.Invert);
        Assert.Equal(0.75f, desc.Opacity);
        Assert.Equal(1.03f, desc.Luminosity);
        Assert.Equal(1f, desc.TintR);
        Assert.Equal(0f, desc.TintG);
        Assert.Equal(0f, desc.TintB);
        Assert.Equal(0.6, desc.TintA, 3);
        Assert.Equal(4f, desc.CornerRadiusTL);
        Assert.Equal(6f, desc.CornerRadiusTR);
        Assert.Equal(8f, desc.CornerRadiusBR);
        Assert.Equal(10f, desc.CornerRadiusBL);
    }

    [Fact]
    public void FromEffect_FoldsTintColorAlphaIntoTintOpacity()
    {
        // The external Gallery's Acrylic preset: Color.FromArgb(200, 30, 30, 40)
        // at TintOpacity 0.6 must not lose the 200/255 colour alpha.
        var effect = new AcrylicEffect(Color.FromArgb(200, 30, 30, 40), tintOpacity: 0.6f, blurRadius: 30f);

        var desc = BackdropMaterialDesc.FromEffect(effect, 0f, 0f, 10f, 10f, new CornerRadius(0));

        Assert.Equal(30.0 / 255.0, desc.TintR, 4);
        Assert.Equal(30.0 / 255.0, desc.TintG, 4);
        Assert.Equal(40.0 / 255.0, desc.TintB, 4);
        Assert.Equal((200.0 / 255.0) * 0.6, desc.TintA, 4);
    }

    [Fact]
    public void FromEffect_TransparentTintMeansUnsetAndResolvesToWhite()
    {
        // The parameterless presets leave TintColor = Transparent; they have
        // always rendered a white veil at TintOpacity and must keep doing so.
        var acrylic = new AcrylicEffect();
        var frosted = new FrostedGlassEffect();

        var acrylicDesc = BackdropMaterialDesc.FromEffect(acrylic, 0f, 0f, 10f, 10f, new CornerRadius(0));
        var frostedDesc = BackdropMaterialDesc.FromEffect(frosted, 0f, 0f, 10f, 10f, new CornerRadius(0));

        Assert.Equal(1f, acrylicDesc.TintR);
        Assert.Equal(1f, acrylicDesc.TintG);
        Assert.Equal(1f, acrylicDesc.TintB);
        Assert.Equal(0.6, acrylicDesc.TintA, 4);
        Assert.Equal((uint)BackdropMaterialBlurType.Gaussian, acrylicDesc.BlurType);

        Assert.Equal(1f, frostedDesc.TintR);
        Assert.Equal(0.4, frostedDesc.TintA, 4);
        Assert.Equal((uint)BackdropMaterialBlurType.Frosted, frostedDesc.BlurType);
        Assert.True(frostedDesc.NoiseIntensity > 0f);
    }

    [Theory]
    [InlineData(BackdropBlurType.Gaussian, BackdropMaterialBlurType.Gaussian)]
    [InlineData(BackdropBlurType.Box, BackdropMaterialBlurType.Box)]
    [InlineData(BackdropBlurType.Frosted, BackdropMaterialBlurType.Frosted)]
    [InlineData(BackdropBlurType.Directional, BackdropMaterialBlurType.Gaussian)]
    [InlineData(BackdropBlurType.Radial, BackdropMaterialBlurType.Gaussian)]
    [InlineData(BackdropBlurType.Zoom, BackdropMaterialBlurType.Gaussian)]
    public void MapBlurType_UnimplementedKernelsFallBackToGaussian(
        BackdropBlurType blurType, BackdropMaterialBlurType expected)
    {
        Assert.Equal(expected, BackdropMaterialDesc.MapBlurType(blurType));
    }

    [Fact]
    public void FromEffect_ZeroSigmaStaysZeroSoNativeDerivesRadiusOverThree()
    {
        var effect = new BackdropBlurEffect { BlurRadius = 20f, BlurSigma = 0f };

        var desc = BackdropMaterialDesc.FromEffect(effect, 0f, 0f, 10f, 10f, new CornerRadius(0));

        Assert.Equal(20f, desc.BlurRadius);
        Assert.Equal(0f, desc.BlurSigma);
        Assert.Equal(1f, desc.Opacity);
        Assert.Equal(1f, desc.Brightness);
        Assert.Equal(1f, desc.Contrast);
        Assert.Equal(0f, desc.Grayscale);
    }

    [Fact]
    public void FromEffect_ClampsUnitRangesAndRejectsNegativeMultipliers()
    {
        var effect = new ColorAdjustmentEffect
        {
            NoiseIntensity = 2f,
            Grayscale = -1f,
            Sepia = 7f,
            Invert = 1.5f,
            Opacity = 3f,
            Brightness = -2f,
            BlurRadius = -5f,
        };

        var desc = BackdropMaterialDesc.FromEffect(effect, 0f, 0f, 10f, 10f, new CornerRadius(0));

        Assert.Equal(1f, desc.NoiseIntensity);
        Assert.Equal(0f, desc.Grayscale);
        Assert.Equal(1f, desc.Sepia);
        Assert.Equal(1f, desc.Invert);
        Assert.Equal(1f, desc.Opacity);
        Assert.Equal(0f, desc.Brightness);
        Assert.Equal(0f, desc.BlurRadius);
    }
}
