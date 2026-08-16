using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Markup;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using ImageControl = Jalium.UI.Controls.Image;

namespace Jalium.UI.Tests;

public sealed class ImageParityTests
{
    [Fact]
    public void SurfaceMatchesWpfUriDpiAndFailureContracts()
    {
        Assert.Contains(typeof(IUriContext), typeof(ImageControl).GetInterfaces());
        Assert.Equal(typeof(FrameworkElement).Assembly, typeof(IUriContext).Assembly);
        Assert.Same(
            typeof(IUriContext),
            Type.GetType("Jalium.UI.Markup.IUriContext, Jalium.UI.Xaml", throwOnError: true));
        Assert.Equal("Jalium.UI", typeof(Jalium.UI.ExceptionRoutedEventArgs).Namespace);

        var baseUri = typeof(ImageControl).GetProperty(
            "BaseUri",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(baseUri);
        Assert.Equal(typeof(Uri), baseUri!.PropertyType);
        Assert.True(baseUri.GetMethod!.IsFamily);
        Assert.True(baseUri.SetMethod!.IsFamily);
        Assert.True(baseUri.GetMethod.IsVirtual);
        Assert.True(baseUri.SetMethod.IsVirtual);

        var onDpiChanged = typeof(ImageControl).GetMethod(
            "OnDpiChanged",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null,
            new[] { typeof(DpiScale), typeof(DpiScale) },
            null);
        Assert.NotNull(onDpiChanged);
        Assert.True(onDpiChanged!.IsFamily);
        Assert.True(onDpiChanged.IsVirtual);
        Assert.False(onDpiChanged.IsFinal);
        Assert.NotEqual(onDpiChanged, onDpiChanged.GetBaseDefinition());

        Assert.Equal(RoutingStrategy.Bubble, ImageControl.DpiChangedEvent.RoutingStrategy);
        Assert.Equal(typeof(DpiChangedEventHandler), ImageControl.DpiChangedEvent.HandlerType);
        Assert.Equal(RoutingStrategy.Bubble, ImageControl.ImageFailedEvent.RoutingStrategy);
        Assert.Equal(
            typeof(EventHandler<Jalium.UI.ExceptionRoutedEventArgs>),
            ImageControl.ImageFailedEvent.HandlerType);

        Assert.Equal(
            typeof(DpiChangedEventHandler),
            typeof(ImageControl).GetEvent(nameof(ImageControl.DpiChanged))!.EventHandlerType);
        Assert.Equal(
            typeof(EventHandler<Jalium.UI.ExceptionRoutedEventArgs>),
            typeof(ImageControl).GetEvent(nameof(ImageControl.ImageFailed))!.EventHandlerType);
    }

    [Fact]
    public void BaseUri_ExplicitAndProtectedContractsShareStateAndFlowToSource()
    {
        var image = new ProbeImage();
        var context = (IUriContext)image;
        var firstBase = new Uri("file:///C:/assets/");

        Assert.Null(context.BaseUri);

        context.BaseUri = firstBase;

        Assert.Same(firstBase, image.ExposedBaseUri);

        var source = new UriAwareImageSource();
        image.Source = source;

        Assert.Same(firstBase, source.BaseUri);

        var sourceBase = new Uri("file:///D:/source-owned/");
        var sourceWithContext = new UriAwareImageSource { BaseUri = sourceBase };
        image.Source = sourceWithContext;
        image.ExposedBaseUri = new Uri("file:///E:/new-owner/");

        Assert.Same(sourceBase, sourceWithContext.BaseUri);
        Assert.Same(image.ExposedBaseUri, context.BaseUri);
    }

    [Fact]
    public void DpiChanged_FiresOnceOnFirstMeasureThenBubblesExplicitChanges()
    {
        var parent = new StackPanel();
        var image = new ProbeImage();
        parent.Children.Add(image);
        var imageEvents = new List<DpiChangedEventArgs>();
        var parentEvents = new List<DpiChangedEventArgs>();
        image.DpiChanged += (_, e) => imageEvents.Add(e);
        parent.AddHandler(
            ImageControl.DpiChangedEvent,
            new DpiChangedEventHandler((_, e) => parentEvents.Add(e)));

        image.Measure(new Size(100, 100));

        var initial = Assert.Single(imageEvents);
        Assert.Equal(initial.OldDpi, initial.NewDpi);
        Assert.Same(image, initial.Source);
        Assert.Single(parentEvents);

        image.Measure(new Size(120, 120));
        Assert.Single(imageEvents);

        var oldDpi = new DpiScale(1.25, 1.5);
        var newDpi = new DpiScale(2, 2.25);
        image.RaiseDpiChanged(oldDpi, newDpi);

        Assert.Equal(2, imageEvents.Count);
        Assert.Equal(2, parentEvents.Count);
        Assert.Equal(oldDpi, imageEvents[1].OldDpi);
        Assert.Equal(newDpi, imageEvents[1].NewDpi);
        Assert.Same(image, imageEvents[1].Source);
        Assert.Same(imageEvents[1], parentEvents[1]);
    }

    /// <summary>
    /// Reporting a failure must not rewrite <c>Image.Source</c>.
    /// </summary>
    /// <remarks>
    /// This previously asserted the opposite — <c>OnSourceLoadFailed</c> did
    /// <c>SetCurrentValue(SourceProperty, null)</c> before raising. WPF never clears the
    /// application's Source, and clearing it made a transient failure permanently lossy: the
    /// application's value was destroyed, and because <c>TryGetSourceSize</c> returns false for a
    /// null source and <c>OnRender</c> bails before drawing, no later frame could recover or
    /// degrade gracefully. The routed-event contract itself is unchanged.
    /// </remarks>
    [Fact]
    public void ImageFailed_PreservesSourceBeforeHandlerAndBubblesOriginalException()
    {
        var parent = new StackPanel();
        var image = new ImageControl();
        parent.Children.Add(image);
        var source = new FailingImageSource();
        var failure = new InvalidDataException("decode failed");
        var order = new List<string>();
        Jalium.UI.ExceptionRoutedEventArgs? received = null;

        image.ImageFailed += (_, e) =>
        {
            order.Add("image");
            Assert.Same(source, image.Source);
            received = e;
        };
        parent.AddHandler(
            ImageControl.ImageFailedEvent,
            new EventHandler<Jalium.UI.ExceptionRoutedEventArgs>((_, _) => order.Add("parent")));
        image.Source = source;

        source.Fail(failure);

        Assert.Equal(new[] { "image", "parent" }, order);
        Assert.NotNull(received);
        Assert.Same(failure, received!.ErrorException);
        Assert.Same(image, received.Source);
        Assert.Same(ImageControl.ImageFailedEvent, received.RoutedEvent);
        Assert.Same(source, image.Source);
    }

    /// <summary>
    /// A failure must leave a binding on <c>Image.Source</c> intact.
    /// </summary>
    /// <remarks>
    /// <c>SetCurrentValue</c> does not remove a binding, but it does overwrite the value the binding
    /// produced — so with a one-way binding the application could not get its image back without
    /// re-raising the source property change itself. Asserting the expression AND the value pins
    /// both halves.
    /// </remarks>
    [Fact]
    public void ImageFailed_LeavesABoundSourceAndItsBindingExpressionIntact()
    {
        var image = new ImageControl();
        var source = new FailingImageSource();
        var viewModel = new ImageHolder { Image = source };
        image.DataContext = viewModel;

        var expression = BindingOperations.SetBinding(
            image,
            ImageControl.SourceProperty,
            new Binding(nameof(ImageHolder.Image)));

        Assert.Same(source, image.Source);

        var failures = new List<Exception>();
        image.ImageFailed += (_, e) => failures.Add(e.ErrorException);

        source.Fail(new InvalidDataException("decode failed"));

        Assert.Single(failures);
        Assert.Same(source, image.Source);
        Assert.Same(
            expression,
            BindingOperations.GetBindingExpression(image, ImageControl.SourceProperty));
        Assert.True(BindingOperations.IsDataBound(image, ImageControl.SourceProperty));
    }

    [Fact]
    public void ImageFailed_IgnoresDetachedSourcesAndReplaysPendingBitmapFailure()
    {
        var image = new ImageControl();
        var detached = new FailingImageSource();
        var replacement = new UriAwareImageSource();
        var failures = new List<Exception>();
        image.ImageFailed += (_, e) => failures.Add(e.ErrorException);
        image.Source = detached;
        image.Source = replacement;

        detached.Fail(new InvalidOperationException("stale"));

        Assert.Empty(failures);
        Assert.Same(replacement, image.Source);

        var bitmap = new BitmapImage
        {
            UriSource = new Uri("__jalium_missing_image_parity__.png", UriKind.Relative)
        };

        image.Source = bitmap;

        var failure = Assert.Single(failures);
        Assert.IsType<FileNotFoundException>(failure);

        // The failed source stays assigned. Reporting is not a reason to discard what the
        // application set — see ImageFailed_PreservesSourceBeforeHandlerAndBubblesOriginalException.
        Assert.Same(bitmap, image.Source);
    }

    /// <summary>
    /// A failed source is not painted, and the very same instance is painted again once it clears
    /// its failure.
    /// </summary>
    /// <remarks>
    /// The recovery half is the point of the test. Suppressing the draw behind a private field would
    /// just be the old <c>SetCurrentValue(SourceProperty, null)</c> bug in a disguise if nothing ever
    /// lifted the suppression — and a plain <see cref="ImageSource"/> raises no load event this
    /// element subscribes to, so the lift may not depend on one.
    /// </remarks>
    [Fact]
    public void AFailedSourceIsNotDrawnAndIsDrawnAgainOnceItRecovers()
    {
        var image = new ImageControl();
        var source = new FailingImageSource();
        image.Source = source;
        image.Measure(new Size(64, 48));
        image.Arrange(new Rect(0, 0, 64, 48));

        var context = new ImageRecordingDrawingContext();
        image.Render(context);
        Assert.Equal(1, context.DrawImageCalls);

        source.Fail(new InvalidDataException("decode failed"));

        image.Render(context);
        Assert.Equal(1, context.DrawImageCalls);
        Assert.Same(source, image.Source);

        source.Recover();

        image.Render(context);
        Assert.Equal(2, context.DrawImageCalls);
    }

    /// <summary>
    /// A GPU upload failure must never stop the element drawing, and an upload that fails once and
    /// then succeeds must end up drawing.
    /// </summary>
    /// <remarks>
    /// <para>This is the reported symptom reached through the failure path. A GPU upload is
    /// attempted from INSIDE <c>DrawImage</c> — <c>RenderTargetDrawingContext.GetNativeBitmap</c> —
    /// so gating the draw on an upload failure removes the retry that is the only thing which ever
    /// clears one. The latch is then held forever, and one unlucky frame on one machine (VRAM
    /// pressure, a device-removed frame, a texture dimension over the adapter's limit on WARP or
    /// Remote Desktop) leaves that <c>Image</c> blank for the life of the window, where it
    /// previously recovered on the very next frame. It is also asymmetric: an <c>ImageBrush</c>
    /// over the same source keeps uploading and painting, so the same picture appears in one place
    /// and not in another.</para>
    /// <para>The two halves the element must get right are counted separately below: it keeps
    /// issuing the draw while an upload failure is outstanding, and it still refuses to draw a
    /// source that failed to LOAD — where there are no pixels, nothing to retry, and skipping costs
    /// nothing.</para>
    /// </remarks>
    [Fact]
    public void AnUploadFailureKeepsDrawingSoTheNextFrameCanRetryAndSucceed()
    {
        var image = new ImageControl();
        var source = new FailingImageSource();
        image.Source = source;
        image.Measure(new Size(64, 48));
        image.Arrange(new Rect(0, 0, 64, 48));

        var context = new ImageRecordingDrawingContext();
        image.Render(context);
        Assert.Equal(1, context.DrawImageCalls);

        var failures = new List<Exception>();
        image.ImageFailed += (_, e) => failures.Add(e.ErrorException);

        var uploadFault = new InvalidOperationException("gpu upload failed");
        source.FailUpload(uploadFault);

        // Still reported: an upload failure is not swallowed to make the retry possible — the
        // application hears about it once per episode, and ImageDiagnostics.UploadFailed records
        // every occurrence at the site that discovers it.
        Assert.Same(uploadFault, Assert.Single(failures));

        // The regression assertion. Gated on the failure this stays at 1 for the life of the
        // element, and because the upload lives inside the call being skipped, no later frame can
        // ever clear what suppressed it.
        image.Render(context);
        Assert.Equal(2, context.DrawImageCalls);

        // The retry succeeds — the call GetNativeBitmap makes on its successful branch.
        source.CompleteUpload();
        Assert.Null(source.LoadFailure);

        image.Render(context);
        Assert.Equal(3, context.DrawImageCalls);

        // The gate still exists for the class it was built for.
        source.Fail(new InvalidDataException("decode failed"));
        image.Render(context);
        Assert.Equal(3, context.DrawImageCalls);

        // And a later successful upload must NOT lift that one: uploading an older resident raster
        // says nothing about the decode that failed, so clearing it would un-suppress an element
        // whose source really is broken.
        source.CompleteUpload();
        image.Render(context);
        Assert.Equal(3, context.DrawImageCalls);

        // Only the source's own recovery does.
        source.Recover();
        image.Render(context);
        Assert.Equal(4, context.DrawImageCalls);
    }

    private sealed class ImageHolder
    {
        public ImageSource? Image { get; set; }
    }

    private sealed class ImageRecordingDrawingContext : DrawingContextAdapter
    {
        public int DrawImageCalls { get; private set; }

        public override void DrawImage(ImageSource imageSource, Rect rectangle) => DrawImageCalls++;

        public override void DrawLine(Pen pen, Point point0, Point point1)
        {
        }

        public override void DrawRectangle(Brush? brush, Pen? pen, Rect rectangle)
        {
        }

        public override void DrawRoundedRectangle(
            Brush? brush, Pen? pen, Rect rectangle, double radiusX, double radiusY)
        {
        }

        public override void DrawEllipse(
            Brush? brush, Pen? pen, Point center, double radiusX, double radiusY)
        {
        }

        public override void DrawGeometry(Brush? brush, Pen? pen, Geometry geometry)
        {
        }

        public override void DrawText(FormattedText formattedText, Point origin)
        {
        }

        public override void DrawBackdropEffect(
            Rect rectangle, IBackdropEffect effect, CornerRadius cornerRadius)
        {
        }

        public override void PushClip(Geometry clipGeometry)
        {
        }

        public override void PushTransform(Transform transform)
        {
        }

        public override void PushOpacity(double opacity)
        {
        }

        public override void Pop()
        {
        }

        public override void Close()
        {
        }
    }

    private sealed class ProbeImage : ImageControl
    {
        public Uri? ExposedBaseUri
        {
            get => BaseUri;
            set => BaseUri = value;
        }

        public void RaiseDpiChanged(DpiScale oldDpi, DpiScale newDpi) =>
            OnDpiChanged(oldDpi, newDpi);
    }

    private sealed class UriAwareImageSource : ImageSource, IUriContext
    {
        public Uri? BaseUri { get; set; }

        public override double Width => 16;

        public override double Height => 12;

        public override nint NativeHandle => nint.Zero;

        public override ImageMetadata? Metadata => null;
    }

    private sealed class FailingImageSource : ImageSource
    {
        public override double Width => 16;

        public override double Height => 12;

        public override nint NativeHandle => nint.Zero;

        public override ImageMetadata? Metadata => null;

        public void Fail(Exception exception) => ReportLoadFailure(exception);

        /// <summary>Stands in for the ClearLoadFailure a real source performs on a successful load.</summary>
        public void Recover() => ClearLoadFailure();

        /// <summary>
        /// Stands in for the render backend's upload catch: latch on the thread that discovered the
        /// failure, announce on the thread that may run application handlers.
        /// </summary>
        /// <remarks>
        /// Both halves, in the order <c>BitmapDecodeNotifier.PostSourceFailure</c> and its drain
        /// perform them. Driven directly rather than through the notifier because the subject here
        /// is the render gate, not the marshalling — the notifier's own half is pinned by
        /// <c>ImagePipelineFailureContractTests</c>.
        /// </remarks>
        public void FailUpload(Exception exception)
        {
            Assert.True(TryLatchUploadFailure(exception));
            RaiseLatchedLoadFailure(exception);
        }

        /// <summary>
        /// Stands in for the successful upload the next frame's <c>GetNativeBitmap</c> performs.
        /// </summary>
        public void CompleteUpload() => ClearUploadFailure();
    }
}
