using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Effects;

namespace Jalium.UI.Tests;

public sealed class LiveResizeEffectPolicyTests
{
    [Fact]
    public void DisabledElementEffectCapture_RendersContentDirectly()
    {
        var element = new DrawingElement
        {
            Width = 80,
            Height = 48,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 3,
            },
        };
        element.Measure(new Size(80, 48));
        element.Arrange(new Rect(0, 0, 80, 48));

        var context = new PolicyContext(effectCaptureEnabled: false)
        {
            Offset = new Point(12, 16),
        };

        element.Render(context);

        Assert.Equal(1, context.RectangleCount);
        Assert.Equal(0, context.BeginCount);
        Assert.Equal(0, context.EndCount);
        Assert.Equal(0, context.ApplyCount);
    }

    [Fact]
    public void EnabledElementEffectCapture_PreservesNormalEffectPath()
    {
        var element = new DrawingElement
        {
            Width = 80,
            Height = 48,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 3,
            },
        };
        element.Measure(new Size(80, 48));
        element.Arrange(new Rect(0, 0, 80, 48));

        var context = new PolicyContext(effectCaptureEnabled: true)
        {
            Offset = new Point(12, 16),
        };

        element.Render(context);

        Assert.Equal(1, context.RectangleCount);
        Assert.Equal(1, context.BeginCount);
        Assert.Equal(1, context.EndCount);
        Assert.Equal(1, context.ApplyCount);
    }

    [Fact]
    public void DisabledEffects_AllowEffectSubtreeToUseRetainedLayer()
    {
        var root = CreateLayerBoundaryTree();
        var context = new PolicyContext(effectCaptureEnabled: false);

        root.Render(context); // establish clean retained content
        context.ResetCounts();
        root.Render(context);

        Assert.Equal(1, context.LayerBeginCount);
        Assert.Equal(1, context.LayerEndCount);
        Assert.Equal(1, context.LayerCompositeCount);
        Assert.Equal(0, context.BeginCount);
        Assert.Equal(0, context.ApplyCount);
    }

    [Fact]
    public void EnabledEffects_KeepEffectSubtreeOffRetainedLayer()
    {
        var root = CreateLayerBoundaryTree();
        var context = new PolicyContext(effectCaptureEnabled: true);

        root.Render(context);
        context.ResetCounts();
        root.Render(context);

        Assert.Equal(0, context.LayerBeginCount);
        Assert.Equal(0, context.LayerCompositeCount);
        Assert.Equal(1, context.BeginCount);
        Assert.Equal(1, context.ApplyCount);
    }

    private static Grid CreateLayerBoundaryTree()
    {
        var boundary = new LayerBoundaryElement
        {
            Width = 80,
            Height = 48,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 3 },
        };
        boundary.Child = new DrawingElement { Width = 80, Height = 48 };
        var host = new Grid { Width = 80, Height = 48 };
        host.Children.Add(boundary);
        host.Measure(new Size(80, 48));
        host.Arrange(new Rect(0, 0, 80, 48));
        return host;
    }

    private sealed class LayerBoundaryElement : FrameworkElement
    {
        private UIElement? _child;

        public LayerBoundaryElement() => IsLayerBoundary = true;

        public UIElement? Child
        {
            get => _child;
            set
            {
                if (_child != null) RemoveVisualChild(_child);
                _child = value;
                if (_child != null) AddVisualChild(_child);
            }
        }

        protected override int VisualChildrenCount => _child == null ? 0 : 1;

        protected override Visual GetVisualChild(int index) =>
            index == 0 && _child != null ? _child : throw new ArgumentOutOfRangeException(nameof(index));

        protected override Size MeasureOverride(Size availableSize)
        {
            _child?.Measure(availableSize);
            return _child?.DesiredSize ?? Size.Empty;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child?.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
    }

    private sealed class DrawingElement : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) =>
            drawingContext.DrawRectangle(
                Brushes.White,
                null,
                new Rect(0, 0, RenderSize.Width, RenderSize.Height));
    }

    private sealed class PolicyContext : DrawingContextAdapter,
        IOffsetDrawingContext,
        IEffectDrawingContext,
        ILayerCompositingDrawingContext
    {
        public PolicyContext(bool effectCaptureEnabled) =>
            IsElementEffectCaptureEnabled = effectCaptureEnabled;

        public bool IsElementEffectCaptureEnabled { get; }

        public Point Offset { get; set; }

        public int RectangleCount { get; private set; }
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public int ApplyCount { get; private set; }
        public int LayerBeginCount { get; private set; }
        public int LayerEndCount { get; private set; }
        public int LayerCompositeCount { get; private set; }

        public bool SupportsRetainedLayers => true;

        public nint BeginLayerCapture(nint existingLayer, Rect worldBounds)
        {
            LayerBeginCount++;
            return existingLayer != 0 ? existingLayer : new nint(0x1234);
        }

        public void EndLayerCapture(nint layer) => LayerEndCount++;

        public void CompositeLayer(
            nint layer,
            Rect worldBounds,
            double opacity,
            Transform? transform,
            double originX,
            double originY) => LayerCompositeCount++;

        public void ResetCounts()
        {
            RectangleCount = 0;
            BeginCount = 0;
            EndCount = 0;
            ApplyCount = 0;
            LayerBeginCount = 0;
            LayerEndCount = 0;
            LayerCompositeCount = 0;
        }

        public void BeginEffectCapture(float x, float y, float w, float h) => BeginCount++;

        public void EndEffectCapture() => EndCount++;

        public void ApplyElementEffect(
            IEffect effect,
            float x,
            float y,
            float w,
            float h,
            float captureOriginX = 0,
            float captureOriginY = 0,
            float cornerTL = 0,
            float cornerTR = 0,
            float cornerBR = 0,
            float cornerBL = 0) => ApplyCount++;

        public override void DrawRectangle(Brush? brush, Pen? pen, Rect rectangle) => RectangleCount++;
        public override void DrawLine(Pen pen, Point point0, Point point1) { }
        public override void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rectangle, double radiusX, double radiusY) { }
        public override void DrawEllipse(Brush? brush, Pen? pen, Point center, double radiusX, double radiusY) { }
        public override void DrawText(FormattedText formattedText, Point origin) { }
        public override void DrawGeometry(Brush? brush, Pen? pen, Geometry geometry) { }
        public override void DrawImage(ImageSource imageSource, Rect rectangle) { }
        public override void DrawBackdropEffect(Rect rectangle, IBackdropEffect effect, CornerRadius cornerRadius) { }
        public override void PushClip(Geometry clipGeometry) { }
        public override void PushTransform(Transform transform) { }
        public override void PushOpacity(double opacity) { }
        public override void Pop() { }
        public override void Close() { }
    }
}
