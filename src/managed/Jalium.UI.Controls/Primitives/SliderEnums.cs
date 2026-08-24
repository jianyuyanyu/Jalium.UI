namespace Jalium.UI.Controls.Primitives;

/// <summary>
/// Specifies the position of tick marks in a <see cref="Jalium.UI.Controls.Slider"/> control
/// with respect to the track.
/// </summary>
public enum TickPlacement
{
    /// <summary>No tick marks appear.</summary>
    None,

    /// <summary>Tick marks appear above the track for a horizontal slider, or to the left for a vertical slider.</summary>
    TopLeft,

    /// <summary>Tick marks appear below the track for a horizontal slider, or to the right for a vertical slider.</summary>
    BottomRight,

    /// <summary>Tick marks appear above and below the track for a horizontal slider, or to the left and right for a vertical slider.</summary>
    Both
}

/// <summary>
/// Specifies the position of the auto tooltip on a <see cref="Jalium.UI.Controls.Slider"/> control.
/// </summary>
public enum AutoToolTipPlacement
{
    /// <summary>No auto tooltip appears.</summary>
    None,

    /// <summary>For a horizontal slider, the tooltip appears above the thumb. For a vertical slider, the tooltip appears to the left.</summary>
    TopLeft,

    /// <summary>For a horizontal slider, the tooltip appears below the thumb. For a vertical slider, the tooltip appears to the right.</summary>
    BottomRight
}

/// <summary>
/// Specifies how the track of a <see cref="Jalium.UI.Controls.Slider"/> or
/// <see cref="Jalium.UI.Controls.RangeSlider"/> is painted.
/// </summary>
public enum SliderTrackMode
{
    /// <summary>The track is a single continuous bar.</summary>
    Continuous,

    /// <summary>
    /// The track is split into discrete segments at every tick position, separated by a
    /// gap. Segments covered by the current value are painted with the fill brush.
    /// </summary>
    Segmented
}
