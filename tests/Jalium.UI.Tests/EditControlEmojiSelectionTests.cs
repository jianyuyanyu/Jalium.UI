using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Verifies that <see cref="EditControl"/> moves the caret, extends the
/// selection and deletes by whole grapheme clusters, so a multi-code-unit emoji
/// can never be split.
/// </summary>
public class EditControlEmojiSelectionTests
{
    private static readonly string Family = EmojiTestStrings.Family;
    private static readonly string WaveDark = EmojiTestStrings.WaveDarkSkin;

    [Fact]
    public void MoveCaretRight_StepsOverAWholeEmoji_InOnePress()
    {
        var editor = new EditControl();
        editor.LoadText("a" + Family + "b");
        editor.MoveCaretToDocumentStart();

        editor.MoveCaretRight();                       // over "a"
        editor.MoveCaretRight(extendSelection: true);  // select the emoji

        Assert.Equal(Family, editor.SelectedText);
    }

    [Fact]
    public void DeleteLeft_RemovesAWholeEmoji()
    {
        var editor = new EditControl();
        editor.LoadText("a" + Family + "b");
        editor.MoveCaretToDocumentEnd();
        editor.MoveCaretLeft();   // before "b"

        editor.DeleteLeft();      // delete the emoji preceding the caret

        Assert.Equal("ab", editor.Text);
    }

    [Fact]
    public void Select_SnapsTheRangeOutwardToCoverAWholeEmoji()
    {
        var editor = new EditControl();
        editor.LoadText("a" + WaveDark + "b");

        // Request a range whose endpoints fall inside the emoji's surrogate pairs.
        editor.Select(2, 1);

        Assert.Equal(WaveDark, editor.SelectedText);
    }

    [Fact]
    public void SelectCurrentWord_OnAnEmojiSelectsTheWholeCluster()
    {
        var editor = new EditControl();
        editor.LoadText("hi " + WaveDark + " yo");
        editor.MoveCaretToDocumentStart();
        // Move the caret onto the emoji: over "h", "i", " ".
        editor.MoveCaretRight();
        editor.MoveCaretRight();
        editor.MoveCaretRight();

        editor.SelectCurrentWord();

        Assert.Equal(WaveDark, editor.SelectedText);
    }
}
