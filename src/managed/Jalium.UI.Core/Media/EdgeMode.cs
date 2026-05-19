namespace Jalium.UI.Media;

/// <summary>
/// Specifies how the edges of non-text drawing primitives are rendered.
/// </summary>
public enum EdgeMode
{
    /// <summary>
    /// Inherit from parent or use system default (Antialiased).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Render edges without anti-aliasing (sharp binary pixel edges).
    /// Use for pixel-art icons or single-pixel hairline rulings.
    /// </summary>
    Aliased = 1,

    /// <summary>
    /// Render edges with analytic per-pixel coverage anti-aliasing
    /// (the default smooth appearance).
    /// </summary>
    Antialiased = 2,
}
