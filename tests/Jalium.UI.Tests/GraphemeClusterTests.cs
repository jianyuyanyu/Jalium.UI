using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Unit tests for <see cref="GraphemeClusters"/> — the shared grapheme-cluster
/// (user-perceived character) navigation every text control routes through.
/// </summary>
public class GraphemeClusterTests
{
    /// <summary>
    /// Strings that are each exactly one extended grapheme cluster, yet span
    /// several UTF-16 code units. Stepping or selecting must treat each as a
    /// single indivisible unit.
    /// </summary>
    public static TheoryData<string, string> SingleClusters { get; } = new()
    {
        { "rocket (surrogate pair)",         EmojiTestStrings.Rocket },
        { "wave + skin-tone modifier",       EmojiTestStrings.WaveDarkSkin },
        { "family ZWJ sequence",             EmojiTestStrings.Family },
        { "regional-indicator flag",         EmojiTestStrings.FlagCN },
        { "keycap sequence",                 EmojiTestStrings.Keycap },
        { "arrow + VS16",                    EmojiTestStrings.ArrowVs16 },
        { "head shaking (Emoji 15.1 ZWJ)",   EmojiTestStrings.HeadShaking },
        { "base letter + combining mark",    EmojiTestStrings.ECombiningAcute },
    };

    [Theory]
    [MemberData(nameof(SingleClusters))]
    public void SingleCluster_IsNavigatedAsOneUnit(string description, string cluster)
    {
        _ = description;
        int length = cluster.Length;
        Assert.True(length > 1, "test data must be a multi-code-unit cluster");

        // One forward step from the start jumps the whole cluster.
        Assert.Equal(length, GraphemeClusters.NextBoundary(cluster, 0));
        // One backward step from the end jumps the whole cluster.
        Assert.Equal(0, GraphemeClusters.PreviousBoundary(cluster, length));

        // Only 0 and length are boundaries; every interior offset splits the cluster.
        Assert.True(GraphemeClusters.IsBoundary(cluster, 0));
        Assert.True(GraphemeClusters.IsBoundary(cluster, length));
        for (int i = 1; i < length; i++)
        {
            Assert.False(GraphemeClusters.IsBoundary(cluster, i), $"offset {i} must not be a boundary");
            Assert.Equal(length, GraphemeClusters.Snap(cluster, i, forward: true));
            Assert.Equal(0, GraphemeClusters.Snap(cluster, i, forward: false));
        }

        // Splitting yields exactly one piece — the whole cluster.
        Assert.Equal(new[] { cluster }, GraphemeClusters.Split(cluster));
    }

    [Fact]
    public void EmptyOrNull_HasASingleBoundaryAtZero()
    {
        foreach (string? text in new[] { null, string.Empty })
        {
            Assert.Equal(0, GraphemeClusters.NextBoundary(text, 0));
            Assert.Equal(0, GraphemeClusters.PreviousBoundary(text, 0));
            Assert.Equal(0, GraphemeClusters.Snap(text, 5, forward: true));
            Assert.True(GraphemeClusters.IsBoundary(text, 0));
            Assert.Empty(GraphemeClusters.Split(text));
        }
    }

    [Fact]
    public void Ascii_TreatsEveryCodeUnitAsItsOwnCluster()
    {
        const string text = "abc";
        for (int i = 0; i <= text.Length; i++)
            Assert.True(GraphemeClusters.IsBoundary(text, i));

        Assert.Equal(1, GraphemeClusters.NextBoundary(text, 0));
        Assert.Equal(2, GraphemeClusters.PreviousBoundary(text, 3));
        Assert.Equal(new[] { "a", "b", "c" }, GraphemeClusters.Split(text));
    }

    [Fact]
    public void EmojiBetweenLetters_StepsOverTheWholeEmoji()
    {
        // "a" + rocket (surrogate pair) + "b" — cluster boundaries 0, 1, 3, 4.
        string text = "a" + EmojiTestStrings.Rocket + "b";

        Assert.Equal(1, GraphemeClusters.NextBoundary(text, 0));
        Assert.Equal(3, GraphemeClusters.NextBoundary(text, 1));   // jumps the whole emoji
        Assert.Equal(4, GraphemeClusters.NextBoundary(text, 3));
        Assert.Equal(1, GraphemeClusters.PreviousBoundary(text, 3)); // jumps the whole emoji
        Assert.False(GraphemeClusters.IsBoundary(text, 2));          // mid-emoji
        Assert.Equal(new[] { "a", EmojiTestStrings.Rocket, "b" }, GraphemeClusters.Split(text));
    }

    [Fact]
    public void OutOfRangeOffsets_AreClamped()
    {
        string text = EmojiTestStrings.Rocket; // length 2

        Assert.Equal(2, GraphemeClusters.NextBoundary(text, 99));
        Assert.Equal(2, GraphemeClusters.NextBoundary(text, 2));
        Assert.Equal(0, GraphemeClusters.PreviousBoundary(text, -5));
        Assert.Equal(0, GraphemeClusters.PreviousBoundary(text, 0));
        Assert.Equal(0, GraphemeClusters.Snap(text, -1, forward: true));
        Assert.Equal(2, GraphemeClusters.Snap(text, 99, forward: false));
    }

    [Fact]
    public void SnapNearest_ResolvesToTheCloserClusterEdge()
    {
        string text = EmojiTestStrings.WaveDarkSkin; // one cluster of 4 code units

        Assert.Equal(0, GraphemeClusters.SnapNearest(text, 1)); // closer to the start
        Assert.Equal(4, GraphemeClusters.SnapNearest(text, 3)); // closer to the end
        Assert.Equal(4, GraphemeClusters.SnapNearest(text, 2)); // tie resolves to the later boundary
        Assert.Equal(0, GraphemeClusters.SnapNearest(text, 0));
        Assert.Equal(4, GraphemeClusters.SnapNearest(text, 4));
    }
}
