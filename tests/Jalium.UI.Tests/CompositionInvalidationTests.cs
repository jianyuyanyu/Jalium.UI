using System.Reflection;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

/// <summary>
/// Verifies the composition-only invalidation pathway for Opacity, RenderTransform,
/// and RenderTransformOrigin. Before the fix, animations on these properties
/// (typical hover transitions on every card in a 30-card list) drove every
/// animated visual to flip _isRenderDirty each frame, evicting its retained-mode
/// drawing cache and re-recording the OnRender command list. The parent's
/// child-render loop already reads these values live each frame via PushOpacity /
/// PushTransform, so the cached drawing is unaffected by the change. The new
/// path schedules a present without flipping the render-dirty flag.
/// </summary>
public class CompositionInvalidationTests
{
    private static FieldInfo? s_isRenderDirtyField;
    private static FieldInfo IsRenderDirtyField =>
        s_isRenderDirtyField ??= typeof(Visual).GetField("_isRenderDirty",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Visual._isRenderDirty field not found");

    private static bool GetIsRenderDirty(Visual v) =>
        (bool)IsRenderDirtyField.GetValue(v)!;

    private static void ClearIsRenderDirty(Visual v) =>
        IsRenderDirtyField.SetValue(v, false);

    private static FieldInfo? s_layerContentDirtyField;
    private static FieldInfo LayerContentDirtyField =>
        s_layerContentDirtyField ??= typeof(Visual).GetField("_layerContentDirty",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Visual._layerContentDirty field not found");

    private static bool GetLayerContentDirty(Visual v) =>
        (bool)LayerContentDirtyField.GetValue(v)!;

    private static void ClearLayerContentDirty(Visual v) =>
        LayerContentDirtyField.SetValue(v, false);

    private static FieldInfo? s_subtreeCompositionDirtyField;
    private static FieldInfo SubtreeCompositionDirtyField =>
        s_subtreeCompositionDirtyField ??= typeof(Visual).GetField("_isSubtreeCompositionDirty",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Visual._isSubtreeCompositionDirty field not found");

    private static void SetSubtreeCompositionDirty(Visual v) =>
        SubtreeCompositionDirtyField.SetValue(v, true);

    [Fact]
    public void InvalidateComposition_DoesNotFlipRenderDirty()
    {
        var element = new TestVisualElement();
        // Establish a clean baseline: pretend we just rendered.
        ClearIsRenderDirty(element);

        element.InvalidateComposition();

        Assert.False(GetIsRenderDirty(element),
            "InvalidateComposition must not flip _isRenderDirty — the parent re-traverses " +
            "the child loop each frame and reads PushOpacity/PushTransform live.");
    }

    [Fact]
    public void InvalidateComposition_NotifiesWindowHost()
    {
        var host = new RecordingWindowHost();
        var child = new TestVisualElement();
        host.AddChild(child);
        // Adding the child fires OnVisualParentChanged which can invalidate; reset so
        // we measure only the InvalidateComposition call below.
        host.Reset();

        child.InvalidateComposition();

        Assert.True(host.InvalidateWindowCalled, "InvalidateComposition must request a present.");
        Assert.Equal(1, host.AddDirtyElementCount);
        Assert.Same(child, host.LastDirtyElement);
    }

    [Fact]
    public void OpacityChange_DoesNotFlipRenderDirty()
    {
        var element = new TestVisualElement();
        ClearIsRenderDirty(element);

        element.Opacity = 0.5;

        Assert.False(GetIsRenderDirty(element),
            "Opacity is a composition-only property — changing it must not invalidate the cache.");
    }

    [Fact]
    public void RenderTransformChange_DoesNotFlipRenderDirty()
    {
        var element = new TestVisualElement();
        ClearIsRenderDirty(element);

        element.RenderTransform = new ScaleTransform(2.0, 2.0);

        Assert.False(GetIsRenderDirty(element),
            "RenderTransform is a composition-only property — changing it must not invalidate the cache.");
    }

    /// <summary>
    /// The retained-layer quad path only places a pure translate (or opacity) correctly, so
    /// ANY scale != 1 must fall back to inline rendering — upscaling because the cached texture
    /// would be resampled blurry, downscaling because the composite lands it at the wrong
    /// offset (the scale is composed about the wrong fixed point; symptom measured in the XAML
    /// designer: preview content correct at 100%, shifted down-right at 50%, up-left at 200%).
    /// Mirror-only transforms (|scale| == 1) keep the fast path: they resample nothing.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, false)]
    [InlineData(-1.0, 1.0, false)]
    [InlineData(1.0, -1.0, false)]
    [InlineData(0.5, 0.75, true)]
    [InlineData(0.99, 1.0, true)]
    [InlineData(2.1666666667, 2.1666666667, true)]
    [InlineData(1.0, 1.01, true)]
    [InlineData(-2.0, 1.0, true)]
    public void RetainedLayerEligibility_RejectsAnyTextureRescaling(
        double scaleX, double scaleY, bool expected)
    {
        var transform = new ScaleTransform(scaleX, scaleY);

        Assert.Equal(expected, Visual.TransformWouldRescaleRetainedLayer(transform));
    }

    [Fact]
    public void RenderTransformOriginChange_DoesNotFlipRenderDirty()
    {
        var element = new TestVisualElement();
        ClearIsRenderDirty(element);

        element.RenderTransformOrigin = new Point(0.5, 0.5);

        Assert.False(GetIsRenderDirty(element),
            "RenderTransformOrigin is a composition-only property — changing it must not invalidate the cache.");
    }

    [Fact]
    public void OpacityChange_StillSchedulesPresentViaCompositionPath()
    {
        var host = new RecordingWindowHost();
        var element = new TestVisualElement();
        host.AddChild(element);

        host.Reset();
        element.Opacity = 0.7;

        Assert.True(host.InvalidateWindowCalled, "Opacity change must still request a present (via InvalidateComposition).");
        Assert.True(host.AddDirtyElementCount >= 1);
    }

    [Fact]
    public void NormalRenderProperty_StillFlipsRenderDirty()
    {
        // Sanity check: properties without AffectsCompositionOnly still flip the
        // render-dirty flag (the historical behaviour). This protects against
        // accidentally mis-categorising regular DPs as composition-only.
        var element = new TestVisualElement();
        ClearIsRenderDirty(element);

        element.InvalidateVisual();

        Assert.True(GetIsRenderDirty(element));
    }

    [Fact]
    public void PositionOnlyArrange_DoesNotInvalidateLocalDrawing()
    {
        var element = new TestVisualElement { Width = 80, Height = 40 };
        element.Measure(new Size(200, 200));
        element.Arrange(new Rect(10, 10, 80, 40));
        ClearIsRenderDirty(element);

        element.Arrange(new Rect(70, 55, 80, 40));

        Assert.False(GetIsRenderDirty(element));
    }

    [Fact]
    public void SizeChangingArrange_StillInvalidatesLocalDrawing()
    {
        var element = new TestVisualElement();
        element.Measure(new Size(200, 200));
        element.Arrange(new Rect(10, 10, 80, 40));
        ClearIsRenderDirty(element);

        element.Arrange(new Rect(10, 10, 120, 60));

        Assert.True(GetIsRenderDirty(element));
    }

    [Fact]
    public void OpacityProperty_IsCompositionOnly()
    {
        var meta = UIElement.OpacityProperty.DefaultMetadata as FrameworkPropertyMetadata;
        Assert.NotNull(meta);
        Assert.True(meta!.AffectsCompositionOnly,
            "OpacityProperty must be flagged AffectsCompositionOnly so animation ticks " +
            "use the composition path.");
        Assert.True(meta.AffectsRender);
    }

    [Fact]
    public void RenderTransformProperty_IsCompositionOnly()
    {
        var meta = UIElement.RenderTransformProperty.DefaultMetadata as FrameworkPropertyMetadata;
        Assert.NotNull(meta);
        Assert.True(meta!.AffectsCompositionOnly);
        Assert.True(meta.AffectsRender);
    }

    [Fact]
    public void RenderTransformOriginProperty_IsCompositionOnly()
    {
        var meta = UIElement.RenderTransformOriginProperty.DefaultMetadata as FrameworkPropertyMetadata;
        Assert.NotNull(meta);
        Assert.True(meta!.AffectsCompositionOnly);
        Assert.True(meta.AffectsRender);
    }

    // ── A descendant's composition change must invalidate every ancestor's retained layer ──
    //
    // A retained GPU layer bakes its whole subtree — every descendant's Opacity /
    // RenderTransform included — into ONE texture, then composites that texture applying
    // only the LAYER OWNER's own live opacity/transform. So a DESCENDANT's composition
    // change makes the cached texture stale. The composite gate reads content flags only,
    // hence the layer-content flag has to be raised on the ancestors by the composition
    // walk itself. Symptom when it is not: a list row (generated item containers are
    // compositor boundaries) whose layer happened to be realized on the frame its fade-in
    // sat at Opacity 0 stayed invisible for good — laid out, IsVisible, Opacity back at 1,
    // simply never re-realized.

    [Fact]
    public void DescendantOpacityChange_MarksEveryAncestorLayerContentDirty()
    {
        var root = new TestVisualContainer();
        var mid = new TestVisualContainer();
        var leaf = new TestVisualElement();
        root.Add(mid);
        mid.Add(leaf);
        ClearLayerContentDirty(root);
        ClearLayerContentDirty(mid);
        ClearLayerContentDirty(leaf);

        leaf.Opacity = 0.5;

        Assert.True(GetLayerContentDirty(mid),
            "The immediate ancestor bakes the leaf's opacity into its layer texture — it must re-realize.");
        Assert.True(GetLayerContentDirty(root),
            "Every caching ancestor bakes the leaf, not just the immediate parent.");
        Assert.False(GetLayerContentDirty(leaf),
            "The animated element's OWN opacity is applied live at composite time — marking it " +
            "content-dirty would re-record the very subtree the composited-animation fast path exists to skip.");
    }

    [Fact]
    public void DescendantOpacityChange_MarksAncestors_EvenWhenItsOwnCompositionFlagIsStale()
    {
        // An element inside a composited layer is never walked by the renderer, so its
        // dirty flags are never cleared. The flag-propagation short-circuit must therefore
        // not gate the layer invalidation: this is exactly the frame-2-onward case of a
        // running fade inside a cached row.
        var root = new TestVisualContainer();
        var mid = new TestVisualContainer();
        var leaf = new TestVisualElement();
        root.Add(mid);
        mid.Add(leaf);
        SetSubtreeCompositionDirty(leaf);
        SetSubtreeCompositionDirty(mid);
        SetSubtreeCompositionDirty(root);
        ClearLayerContentDirty(root);
        ClearLayerContentDirty(mid);
        ClearLayerContentDirty(leaf);

        leaf.Opacity = 0.25;

        Assert.True(GetLayerContentDirty(mid),
            "A stale composition flag on the animating element must not stop the layer invalidation.");
        Assert.True(GetLayerContentDirty(root),
            "…nor may a stale flag on an intermediate ancestor.");
    }

    [Fact]
    public void DescendantRenderTransformChange_MarksAncestorLayerContentDirty()
    {
        var root = new TestVisualContainer();
        var leaf = new TestVisualElement();
        root.Add(leaf);
        ClearLayerContentDirty(root);
        ClearLayerContentDirty(leaf);

        leaf.RenderTransform = new ScaleTransform(0.9, 0.9);

        Assert.True(GetLayerContentDirty(root),
            "RenderTransform is baked into an ancestor's layer texture exactly like Opacity.");
    }

    private sealed class TestVisualElement : FrameworkElement
    {
    }

    /// <summary>Minimal visual container: parents children so the dirty walk has a chain to climb.</summary>
    private sealed class TestVisualContainer : FrameworkElement
    {
        private readonly List<Visual> _children = new();

        public void Add(Visual child)
        {
            _children.Add(child);
            AddVisualChild(child);
        }

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];
    }

    /// <summary>
    /// Test fake that satisfies <see cref="IWindowHost"/> so children can resolve
    /// a host via the visual-tree walk in <c>UIElement.GetWindowHost</c>.
    /// </summary>
    private sealed class RecordingWindowHost : FrameworkElement, IWindowHost
    {
        public bool InvalidateWindowCalled { get; private set; }
        public int AddDirtyElementCount { get; private set; }
        public UIElement? LastDirtyElement { get; private set; }

        public void AddChild(UIElement child) => AddVisualChild(child);

        public void Reset()
        {
            InvalidateWindowCalled = false;
            AddDirtyElementCount = 0;
            LastDirtyElement = null;
        }

        public void InvalidateWindow() => InvalidateWindowCalled = true;

        public void AddDirtyElement(UIElement element)
        {
            AddDirtyElementCount++;
            LastDirtyElement = element;
        }

        public void RequestFullInvalidation() { }
        public void SetNativeCapture() { }
        public void ReleaseNativeCapture() { }
    }
}
