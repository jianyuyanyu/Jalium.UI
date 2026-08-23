namespace Jalium.UI.Controls;

/// <summary>
/// Implemented by the leaf elements the <see cref="Markdown"/> control renders (text runs and
/// code blocks) so the control can coordinate a single continuous text selection across them.
/// Character indices refer to the element's rendered (visual) text.
/// </summary>
/// <remarks>
/// The selection highlight brush is not part of this contract: it travels down the visual tree as
/// the inherited <see cref="MarkdownTextPresenter.SelectionBrushProperty"/>, so a leaf picks it up
/// from whatever the <see cref="Markdown"/> control (or an intervening block style) supplies.
/// </remarks>
internal interface IMarkdownSelectable
{
    /// <summary>Gets the number of selectable characters in this element's rendered text.</summary>
    int SelectableLength { get; }

    /// <summary>Gets the substring of the rendered text in the half-open range <c>[start, end)</c>.</summary>
    string GetSelectionText(int start, int end);

    /// <summary>Highlights the half-open character range <c>[start, end)</c>; an empty range clears it.</summary>
    void SetSelectionRange(int start, int end);

    /// <summary>Clears any selection highlight on this element.</summary>
    void ClearSelectionRange();

    /// <summary>
    /// Maps a point in this element's coordinate space to the nearest character index.
    /// </summary>
    bool TryHitTestCharacter(Point localPoint, out int charIndex);
}
