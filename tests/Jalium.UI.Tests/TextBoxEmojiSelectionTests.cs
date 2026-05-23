using Jalium.UI.Controls;
using Jalium.UI.Input;

namespace Jalium.UI.Tests;

/// <summary>
/// Verifies that <see cref="TextBox"/> (and its <c>TextBoxBase</c> siblings)
/// move the caret, extend the selection and delete by whole grapheme clusters,
/// so a multi-code-unit emoji can never be split.
/// </summary>
public class TextBoxEmojiSelectionTests
{
    private static readonly string Family = EmojiTestStrings.Family;
    private static readonly string WaveDark = EmojiTestStrings.WaveDarkSkin;

    [Fact]
    public void ShiftRight_SelectsAWholeZwjEmoji_InOnePress()
    {
        var box = new TextBox { Text = "a" + Family + "b" };
        box.CaretIndex = 1; // between "a" and the emoji

        box.RaiseEvent(ShiftKeyDown(Key.Right));

        Assert.Equal(Family, box.SelectedText);
    }

    [Fact]
    public void ShiftLeft_SelectsAWholeSkinToneEmoji_InOnePress()
    {
        var box = new TextBox { Text = "a" + WaveDark + "b" };
        box.CaretIndex = 1 + WaveDark.Length; // between the emoji and "b"

        box.RaiseEvent(ShiftKeyDown(Key.Left));

        Assert.Equal(WaveDark, box.SelectedText);
    }

    [Fact]
    public void RightArrow_StepsOverAWholeEmoji_InOnePress()
    {
        var box = new TextBox { Text = Family };
        box.CaretIndex = 0;

        box.RaiseEvent(KeyDown(Key.Right));

        Assert.Equal(Family.Length, box.CaretIndex);
    }

    [Fact]
    public void Backspace_DeletesAWholeEmoji()
    {
        var box = new TextBox { Text = "a" + Family + "b" };
        box.CaretIndex = 1 + Family.Length; // just after the emoji

        box.RaiseEvent(KeyDown(Key.Back));

        Assert.Equal("ab", box.Text);
    }

    [Fact]
    public void Delete_RemovesAWholeEmoji()
    {
        var box = new TextBox { Text = "a" + WaveDark + "b" };
        box.CaretIndex = 1; // just before the emoji

        box.RaiseEvent(KeyDown(Key.Delete));

        Assert.Equal("ab", box.Text);
    }

    [Fact]
    public void Select_SnapsTheRangeOutwardToCoverAWholeEmoji()
    {
        var box = new TextBox { Text = "a" + WaveDark + "b" };

        // Request a range whose endpoints both fall inside the emoji's
        // surrogate pairs — it must widen to the whole cluster.
        box.Select(2, 1);

        Assert.Equal(WaveDark, box.SelectedText);
    }

    [Fact]
    public void SelectionStart_SnappedOffAnEmojiInterior()
    {
        var box = new TextBox { Text = WaveDark };

        // Offset 2 is the seam between the two surrogate pairs of one emoji.
        box.SelectionStart = 2;

        Assert.True(box.SelectionStart is 0 or 4, $"expected a cluster boundary, got {box.SelectionStart}");
    }

    private static KeyEventArgs KeyDown(Key key)
        => new(UIElement.KeyDownEvent, key, ModifierKeys.None, isDown: true, isRepeat: false, timestamp: 0);

    private static KeyEventArgs ShiftKeyDown(Key key)
        => new(UIElement.KeyDownEvent, key, ModifierKeys.Shift, isDown: true, isRepeat: false, timestamp: 0);
}
