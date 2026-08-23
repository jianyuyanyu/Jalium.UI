namespace Jalium.UI;

/// <summary>
/// Interface for backdrop effects that can be applied to UI elements.
/// Implement this interface to create custom backdrop effects.
/// </summary>
public interface IBackdropEffect
{
    /// <summary>
    /// Gets the blur radius in pixels.
    /// </summary>
    float BlurRadius { get; }

    /// <summary>
    /// Gets the blur sigma value (for Gaussian blur).
    /// </summary>
    float BlurSigma { get; }

    /// <summary>
    /// Gets the type of blur to apply.
    /// </summary>
    BackdropBlurType BlurType { get; }

    /// <summary>
    /// Gets the noise intensity for frosted effects (0.0 - 1.0). This is the
    /// amplitude at which full-range grain is mixed into the material; typical
    /// values are 0.02 - 0.05 (Acrylic uses 0.02).
    /// </summary>
    float NoiseIntensity { get; }

    /// <summary>
    /// Gets the brightness adjustment factor (1.0 = original).
    /// </summary>
    float Brightness { get; }

    /// <summary>
    /// Gets the contrast adjustment factor (1.0 = original).
    /// </summary>
    float Contrast { get; }

    /// <summary>
    /// Gets the saturation adjustment factor (1.0 = original, 0.0 = grayscale).
    /// </summary>
    float Saturation { get; }

    /// <summary>
    /// Gets the hue rotation in radians.
    /// </summary>
    float HueRotation { get; }

    /// <summary>
    /// Gets the grayscale amount (0.0 - 1.0).
    /// </summary>
    float Grayscale { get; }

    /// <summary>
    /// Gets the sepia amount (0.0 - 1.0).
    /// </summary>
    float Sepia { get; }

    /// <summary>
    /// Gets the invert amount (0.0 - 1.0).
    /// </summary>
    float Invert { get; }

    /// <summary>
    /// Gets the opacity of the effect (0.0 - 1.0).
    /// </summary>
    float Opacity { get; }

    /// <summary>
    /// Gets the tint color as ARGB uint.
    /// </summary>
    uint TintColorArgb { get; }

    /// <summary>
    /// Gets the tint opacity (0.0 - 1.0).
    /// </summary>
    float TintOpacity { get; }

    /// <summary>
    /// Gets the luminosity adjustment.
    /// </summary>
    float Luminosity { get; }

    /// <summary>
    /// Gets a value indicating whether this effect has any visual impact.
    /// </summary>
    bool HasEffect { get; }
}

/// <summary>
/// Specifies the type of blur to apply.
/// </summary>
public enum BackdropBlurType
{
    /// <summary>
    /// Gaussian blur (smooth, natural-looking). Sigma is
    /// <see cref="IBackdropEffect.BlurSigma"/>, or radius / 3 when unset.
    /// </summary>
    Gaussian,

    /// <summary>
    /// Box blur. Rendered as the box-equivalent Gaussian (sigma = radius / √3)
    /// so every backend produces the same softness.
    /// </summary>
    Box,

    /// <summary>
    /// Frosted glass: Gaussian blur plus per-pixel sample jitter, so the
    /// blurred backdrop picks up a fine frost grain on top of
    /// <see cref="IBackdropEffect.NoiseIntensity"/>.
    /// </summary>
    Frosted,

    /// <summary>
    /// Directional blur along a specific axis. Not implemented by the native
    /// backends yet; currently rendered as <see cref="Gaussian"/>.
    /// </summary>
    Directional,

    /// <summary>
    /// Radial blur from center point. Not implemented by the native backends
    /// yet; currently rendered as <see cref="Gaussian"/>.
    /// </summary>
    Radial,

    /// <summary>
    /// Zoom blur effect. Not implemented by the native backends yet; currently
    /// rendered as <see cref="Gaussian"/>.
    /// </summary>
    Zoom
}
