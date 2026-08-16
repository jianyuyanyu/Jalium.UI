using Jalium.UI.Media;
using Jalium.UI.Media.Effects;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class EffectCaptureExceptionTests
{
    [Fact]
    public void RenderException_BalancesClipAndEffectCaptureWithoutApplyingPartialContent()
    {
        var element = new ThrowingElement
        {
            Width = 40,
            Height = 24,
            ClipToBounds = true,
            Effect = new BlurEffect(3),
        };
        element.Measure(new Size(40, 24));
        element.Arrange(new Rect(0, 0, 40, 24));

        var context = new RecordingContext { Offset = new Point(12, 9) };

        Assert.Throws<InvalidOperationException>(() => element.Render(context));
        Assert.Equal(["begin", "push-clip", "pop-clip", "end"], context.Events);
        Assert.Equal(0, context.OpenCaptureCount);
        Assert.Equal(0, context.OpenClipCount);
        Assert.DoesNotContain("apply", context.Events);
    }

    private sealed class ThrowingElement : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) =>
            throw new InvalidOperationException("intentional render failure");
    }

    private sealed class RecordingContext : DrawingContextAdapter,
        IOffsetDrawingContext,
        IClipDrawingContext,
        IEffectDrawingContext
    {
        public Point Offset { get; set; }

        public List<string> Events { get; } = [];

        public int OpenCaptureCount { get; private set; }

        public int OpenClipCount { get; private set; }

        public void BeginEffectCapture(float x, float y, float w, float h)
        {
            OpenCaptureCount++;
            Events.Add("begin");
        }

        public void EndEffectCapture()
        {
            OpenCaptureCount--;
            Events.Add("end");
        }

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
            float cornerBL = 0) => Events.Add("apply");

        public override void PushClip(Geometry clipGeometry)
        {
            OpenClipCount++;
            Events.Add("push-clip");
        }

        public override void Pop()
        {
            OpenClipCount--;
            Events.Add("pop-clip");
        }

        public override void DrawLine(Pen pen, Point point0, Point point1) { }
        public override void DrawRectangle(Brush? brush, Pen? pen, Rect rectangle) { }
        public override void DrawRoundedRectangle(Brush? brush, Pen? pen, Rect rectangle, double radiusX, double radiusY) { }
        public override void DrawEllipse(Brush? brush, Pen? pen, Point center, double radiusX, double radiusY) { }
        public override void DrawText(FormattedText formattedText, Point origin) { }
        public override void DrawGeometry(Brush? brush, Pen? pen, Geometry geometry) { }
        public override void DrawImage(ImageSource imageSource, Rect rectangle) { }
        public override void DrawBackdropEffect(Rect rectangle, IBackdropEffect effect, CornerRadius cornerRadius) { }
        public override void PushTransform(Transform transform) { }
        public override void PushOpacity(double opacity) { }
        public override void Close() { }
    }
}
