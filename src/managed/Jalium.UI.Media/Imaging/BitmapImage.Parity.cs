using System.ComponentModel;
using System.Globalization;
using System.Net.Cache;
using Jalium.UI.Diagnostics;
using Jalium.UI.Markup;

namespace Jalium.UI.Media.Imaging;

/// <summary>
/// The author-controlled decode transform: a crop, a rotation and an explicit decode size, all
/// resolved against the image's NATURAL size.
/// </summary>
/// <remarks>
/// <para>Latched as one immutable tuple at <see cref="BitmapImage.EndInit"/> time and applied
/// inside the decode, ahead of the display-bucket resample. It used to be re-read from the
/// dependency properties and re-applied to whatever raster a completed decode happened to publish,
/// which made an author's <see cref="SourceRect"/> coordinates DPI- and layout-dependent: a
/// deferred source publishes a bucket whose edge is derived from the element's slot and the
/// monitor's scale factor, so the same markup cropped a different region at 100% in a large slot
/// than it did at 150% in a small one — and threw <see cref="ArgumentOutOfRangeException"/> out of
/// a decode-completion callback for a perfectly legal rect as soon as the bucket came out smaller
/// than the crop extent.</para>
/// <para>Latched rather than read live because the decode runs on a worker thread and
/// <c>GetValue</c> is dependency-property state that belongs to the UI thread.</para>
/// </remarks>
/// <param name="SourceRect">Crop in natural pixels; <see cref="Int32Rect.Empty"/> for none.</param>
/// <param name="DecodePixelWidth">Explicit decode width, or 0 to derive it from the height.</param>
/// <param name="DecodePixelHeight">Explicit decode height, or 0 to derive it from the width.</param>
/// <param name="Rotation">Rotation applied after the crop; 90/270 swap the axes.</param>
internal readonly record struct BitmapDecodeOptions(
    Int32Rect SourceRect,
    int DecodePixelWidth,
    int DecodePixelHeight,
    Rotation Rotation)
{
    /// <summary>Whether applying these options is a guaranteed no-op.</summary>
    internal bool IsIdentity =>
        SourceRect.IsEmpty &&
        DecodePixelWidth <= 0 &&
        DecodePixelHeight <= 0 &&
        Rotation == Rotation.Rotate0;
}

/// <summary>
/// WPF-compatible bitmap image surface with Jalium native-decoder extensions.
/// </summary>
public sealed partial class BitmapImage : ISupportInitialize, IUriContext
{
    public static readonly DependencyProperty CacheOptionProperty =
        DependencyProperty.Register(nameof(CacheOption), typeof(BitmapCacheOption), typeof(BitmapImage),
            new PropertyMetadata(BitmapCacheOption.Default));

    public static readonly DependencyProperty CreateOptionsProperty =
        DependencyProperty.Register(nameof(CreateOptions), typeof(BitmapCreateOptions), typeof(BitmapImage),
            new PropertyMetadata(BitmapCreateOptions.None));

    public static readonly DependencyProperty DecodePixelHeightProperty =
        DependencyProperty.Register(nameof(DecodePixelHeight), typeof(int), typeof(BitmapImage),
            new PropertyMetadata(0, OnDecodeOptionChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty =
        DependencyProperty.Register(nameof(DecodePixelWidth), typeof(int), typeof(BitmapImage),
            new PropertyMetadata(0, OnDecodeOptionChanged));

    public static readonly DependencyProperty RotationProperty =
        DependencyProperty.Register(nameof(Rotation), typeof(Rotation), typeof(BitmapImage),
            new PropertyMetadata(Rotation.Rotate0, OnDecodeOptionChanged));

    public static readonly DependencyProperty SourceRectProperty =
        DependencyProperty.Register(nameof(SourceRect), typeof(Int32Rect), typeof(BitmapImage),
            new PropertyMetadata(Int32Rect.Empty, OnDecodeOptionChanged));

    public static readonly DependencyProperty StreamSourceProperty =
        DependencyProperty.Register(nameof(StreamSource), typeof(Stream), typeof(BitmapImage),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty UriCachePolicyProperty =
        DependencyProperty.Register(nameof(UriCachePolicy), typeof(RequestCachePolicy), typeof(BitmapImage),
            new PropertyMetadata(null));

    public static readonly DependencyProperty UriSourceProperty =
        DependencyProperty.Register(nameof(UriSource), typeof(Uri), typeof(BitmapImage),
            new PropertyMetadata(null, OnSourceChanged));

    private bool _initializing;
    private bool _initialized;
    private bool _applyingSource;

    /// <summary>Initializes an empty bitmap image.</summary>
    /// <remarks>
    /// Deliberately does NOT subscribe an internal decode-option re-application handler to
    /// <see cref="OnImageLoaded"/>. It used to, and that handler re-ran the crop/rotate/resize
    /// against whatever raster the completed decode had published — i.e. against a DPI- and
    /// layout-dependent display bucket rather than against the natural size the author authored
    /// the coordinates in. The options are now latched by <see cref="ApplySource"/> and applied
    /// inside the decode itself, before the bucket resample, so there is nothing left to re-apply.
    /// </remarks>
    public BitmapImage()
    {
    }

    /// <summary>Initializes a bitmap image from a URI.</summary>
    public BitmapImage(Uri uriSource)
        : this(uriSource, null)
    {
    }

    /// <summary>Initializes a bitmap image from a URI and cache policy.</summary>
    public BitmapImage(Uri uriSource, RequestCachePolicy? uriCachePolicy)
    {
        ArgumentNullException.ThrowIfNull(uriSource);
        BeginInit();
        UriCachePolicy = uriCachePolicy;
        UriSource = uriSource;
        EndInit();
    }

    /// <summary>Gets or sets the base URI used for relative sources.</summary>
    public Uri? BaseUri
    {
        get => _baseUri;
        set
        {
            _baseUri = value;
            BaseUriCore = value;
        }
    }

    /// <summary>Gets or sets the bitmap cache mode.</summary>
    /// <remarks>
    /// <see cref="BitmapCacheOption.OnLoad"/> reads a file <see cref="UriSource"/>'s bytes during
    /// <see cref="EndInit"/> instead of on a decode worker later, which is what makes the
    /// load-then-delete and load-then-overwrite idioms safe; a read that fails throws from there.
    /// The decode itself stays asynchronous under every option — see
    /// <c>ConfigureDeferredFileSource</c>.
    /// </remarks>
    public BitmapCacheOption CacheOption
    {
        get => (BitmapCacheOption)GetValue(CacheOptionProperty)!;
        set => SetValue(CacheOptionProperty, value);
    }

    /// <summary>Gets or sets bitmap creation options.</summary>
    public BitmapCreateOptions CreateOptions
    {
        get => (BitmapCreateOptions)GetValue(CreateOptionsProperty)!;
        set => SetValue(CreateOptionsProperty, value);
    }

    /// <summary>Gets or sets the requested decoded height.</summary>
    public int DecodePixelHeight
    {
        get => (int)GetValue(DecodePixelHeightProperty)!;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            SetValue(DecodePixelHeightProperty, value);
        }
    }

    /// <summary>Gets or sets the requested decoded width.</summary>
    /// <remarks>
    /// The decode RESOLUTION, not a hint: a source that carries one publishes exactly this width
    /// and reports it as <see cref="PixelWidth"/>, matching WPF, and the display-bucket ladder is
    /// bypassed entirely. Leave it at 0 to let the pipeline size the raster for the slot the image
    /// is drawn into, which is the cheaper default; set it when the raster size itself matters —
    /// to bound memory, or to guarantee what <see cref="CopyPixels(Int32Rect, byte[], int, int)"/>
    /// and an encoder will see.
    /// </remarks>
    public int DecodePixelWidth
    {
        get => (int)GetValue(DecodePixelWidthProperty)!;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            SetValue(DecodePixelWidthProperty, value);
        }
    }

    /// <inheritdoc />
    public override ImageMetadata? Metadata => base.Metadata;

    /// <summary>Gets or sets rotation applied after decoding.</summary>
    public Rotation Rotation
    {
        get => (Rotation)GetValue(RotationProperty)!;
        set => SetValue(RotationProperty, value);
    }

    /// <summary>Gets or sets the source pixel rectangle.</summary>
    public Int32Rect SourceRect
    {
        get => (Int32Rect)GetValue(SourceRectProperty)!;
        set => SetValue(SourceRectProperty, value);
    }

    /// <summary>Gets or sets the encoded image stream.</summary>
    public Stream? StreamSource
    {
        get => (Stream?)GetValue(StreamSourceProperty);
        set => SetValue(StreamSourceProperty, value);
    }

    /// <summary>Gets or sets the URI cache policy.</summary>
    public RequestCachePolicy? UriCachePolicy
    {
        get => (RequestCachePolicy?)GetValue(UriCachePolicyProperty);
        set => SetValue(UriCachePolicyProperty, value);
    }

    /// <summary>Gets or sets the encoded image URI.</summary>
    public Uri? UriSource
    {
        get => (Uri?)GetValue(UriSourceProperty);
        set => SetValue(UriSourceProperty, value);
    }

    /// <summary>Begins batched initialization.</summary>
    public void BeginInit()
    {
        WritePreamble();
        if (_initializing || _initialized)
        {
            throw new InvalidOperationException("BitmapImage initialization has already started.");
        }

        _initializing = true;
    }

    /// <summary>Ends initialization and decodes the configured source.</summary>
    public void EndInit()
    {
        WritePreamble();
        if (!_initializing)
        {
            throw new InvalidOperationException("BeginInit must be called before EndInit.");
        }

        if (StreamSource is not null && UriSource is not null)
        {
            throw new InvalidOperationException("StreamSource and UriSource cannot both be set.");
        }

        _initializing = false;
        _initialized = true;
        ApplySource();
    }

    /// <summary>Creates a modifiable copy.</summary>
    public new BitmapImage Clone() => (BitmapImage)base.Clone();

    /// <summary>Creates a modifiable copy with current values.</summary>
    public new BitmapImage CloneCurrentValue() => (BitmapImage)base.CloneCurrentValue();

    /// <inheritdoc />
    protected override Freezable CreateInstanceCore() => new BitmapImage();

    /// <inheritdoc />
    protected override void CloneCore(Freezable source)
    {
        base.CloneCore(source);
        CopyFacadeState((BitmapImage)source);
    }

    /// <inheritdoc />
    protected override void CloneCurrentValueCore(Freezable source)
    {
        base.CloneCurrentValueCore(source);
        CopyFacadeState((BitmapImage)source);
    }

    /// <inheritdoc />
    protected override void GetAsFrozenCore(Freezable source)
    {
        base.GetAsFrozenCore(source);
        CopyFacadeState((BitmapImage)source);
    }

    /// <inheritdoc />
    protected override void GetCurrentValueAsFrozenCore(Freezable source)
    {
        base.GetCurrentValueAsFrozenCore(source);
        CopyFacadeState((BitmapImage)source);
    }

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var bitmap = (BitmapImage)dependencyObject;
        // Jalium's pre-compatibility BitmapImage loaded a URI assigned directly
        // after construction. Preserve that extension while still batching DP
        // updates between BeginInit and EndInit.
        if (!bitmap._initializing)
        {
            bitmap.ApplySource();
        }
    }

    private void ApplySource()
    {
        if (_applyingSource)
        {
            return;
        }

        _applyingSource = true;
        try
        {
            // Latched BEFORE the source is configured, and that order is load-bearing. Configuring
            // a deferred source can enqueue a decode job immediately, and that job reads the
            // latched options on a worker thread; latching afterwards would race the very first
            // decode and publish an untransformed raster whose canonical size then anchors the
            // whole bucket ladder.
            SetDecodeOptions(new BitmapDecodeOptions(
                SourceRect, DecodePixelWidth, DecodePixelHeight, Rotation));

            BaseUriCore = _baseUri;
            if (StreamSource is { } stream)
            {
                LoadFromStreamSource(stream);
            }
            else if (UriSource is { } uri)
            {
                UriSourceCore = uri;
            }
        }
        finally
        {
            _applyingSource = false;
        }
    }

    /// <summary>
    /// Reports a decode option that was changed after <see cref="EndInit"/> and therefore has no
    /// effect, matching WPF — where the decode pipeline is built once when initialization ends.
    /// </summary>
    /// <remarks>
    /// <para>Silently ignoring it is what this must NOT do. Jalium used to re-apply the options on
    /// every decode completion, so a post-<c>EndInit</c> write appeared to work; removing that
    /// re-entrancy makes the write a no-op, and a no-op the author cannot see is the shape of bug
    /// this whole work item exists to remove. The record is live in Release, so a support capture
    /// from a machine where "the crop stopped working" shows the cause on one line.</para>
    /// <para>Reported rather than thrown: an <see cref="InvalidOperationException"/> here would
    /// come out of a dependency-property write — frequently a binding or a style setter — and
    /// take down an application that today merely renders an uncropped image.</para>
    /// </remarks>
    private static void OnDecodeOptionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var bitmap = (BitmapImage)dependencyObject;

        // Inside BeginInit/EndInit, or replayed by Clone/GetAsFrozen onto an uninitialized target:
        // both are the supported way to set these, and ApplySource latches the final values.
        if (!bitmap._initialized || bitmap._initializing || bitmap._applyingSource)
        {
            return;
        }

        ImageDiagnostics.Degraded(
            bitmap.DiagnosticSourceName,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{e.Property.Name} changed after EndInit and was ignored; set decode options between BeginInit and EndInit"),
            0,
            0);
    }

    private void CopyFacadeState(BitmapImage source)
    {
        _initializing = false;
        _initialized = source._initialized;
        _baseUri = source._baseUri;
        CopyBitmapStateFrom(source);
        CopyDeferredStateFrom(source);
    }

    /// <summary>
    /// Applies the author's decode options to a freshly decoded, natural-size image.
    /// </summary>
    /// <remarks>
    /// <para>Pure, and deliberately so: the deferred decoder derives its canonical size from this
    /// method's output and the upgrade predicate compares against that canonical size, so the same
    /// (decoded, options) pair must resolve to the same dimensions on every attempt or the decode
    /// chain stops terminating. Nothing in here may read a dependency property, the resident
    /// raster, the DPI, or the layout slot.</para>
    /// <para>Order matches WPF's pipeline: crop the natural image, rotate the crop, then scale to
    /// the explicit decode size. Rotation is applied to the CROP rather than the other way round,
    /// so <see cref="SourceRect"/> coordinates are always read in the encoded image's own
    /// orientation.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/> carries a <see cref="SourceRect"/> that does not lie inside
    /// <paramref name="decoded"/>. The caller decides what an author error costs; the decoder
    /// reports it and publishes the untransformed raster rather than blanking the element.
    /// </exception>
    private static DecodedImage ApplyDecodeOptions(DecodedImage decoded, in BitmapDecodeOptions options)
    {
        if (options.IsIdentity)
        {
            return decoded;
        }

        var width = decoded.Width;
        var height = decoded.Height;
        var stride = decoded.Stride;
        if (width <= 0 || height <= 0)
        {
            return decoded;
        }

        var pixels = decoded.Pixels;
        var transformed = false;

        var sourceRect = options.SourceRect;
        if (!sourceRect.IsEmpty)
        {
            if (sourceRect.X < 0 || sourceRect.Y < 0 ||
                sourceRect.Width <= 0 || sourceRect.Height <= 0 ||
                sourceRect.X + sourceRect.Width > width ||
                sourceRect.Y + sourceRect.Height > height)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    sourceRect,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"SourceRect must lie inside the {width}x{height} natural image."));
            }

            var croppedStride = checked(sourceRect.Width * 4);
            var cropped = new byte[checked(croppedStride * sourceRect.Height)];
            var source = pixels.Span;
            for (var row = 0; row < sourceRect.Height; row++)
            {
                source
                    .Slice(((sourceRect.Y + row) * stride) + (sourceRect.X * 4), croppedStride)
                    .CopyTo(cropped.AsSpan(row * croppedStride));
            }

            pixels = cropped;
            width = sourceRect.Width;
            height = sourceRect.Height;
            stride = croppedStride;
            transformed = true;
        }

        if (options.Rotation != Rotation.Rotate0)
        {
            var quarterTurn = options.Rotation is Rotation.Rotate90 or Rotation.Rotate270;
            var rotatedWidth = quarterTurn ? height : width;
            var rotatedHeight = quarterTurn ? width : height;
            var rotatedStride = checked(rotatedWidth * 4);
            var rotated = new byte[checked(rotatedStride * rotatedHeight)];
            var source = pixels.Span;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (destinationX, destinationY) = options.Rotation switch
                    {
                        Rotation.Rotate90 => (height - 1 - y, x),
                        Rotation.Rotate180 => (width - 1 - x, height - 1 - y),
                        Rotation.Rotate270 => (y, width - 1 - x),
                        _ => (x, y),
                    };

                    source
                        .Slice((y * stride) + (x * 4), 4)
                        .CopyTo(rotated.AsSpan((destinationY * rotatedStride) + (destinationX * 4)));
                }
            }

            pixels = rotated;
            width = rotatedWidth;
            height = rotatedHeight;
            stride = rotatedStride;
            transformed = true;
        }

        if (options.DecodePixelWidth > 0 || options.DecodePixelHeight > 0)
        {
            var targetWidth = options.DecodePixelWidth;
            var targetHeight = options.DecodePixelHeight;
            if (targetWidth <= 0)
            {
                targetWidth = Math.Max(1, (int)Math.Round(width * (targetHeight / (double)height)));
            }
            else if (targetHeight <= 0)
            {
                targetHeight = Math.Max(1, (int)Math.Round(height * (targetWidth / (double)width)));
            }

            if (targetWidth != width || targetHeight != height)
            {
                var targetStride = checked(targetWidth * 4);
                var resized = new byte[checked(targetStride * targetHeight)];
                var source = pixels.Span;
                for (var y = 0; y < targetHeight; y++)
                {
                    var sourceY = Math.Min(height - 1, (int)((long)y * height / targetHeight));
                    for (var x = 0; x < targetWidth; x++)
                    {
                        var sourceX = Math.Min(width - 1, (int)((long)x * width / targetWidth));
                        source
                            .Slice((sourceY * stride) + (sourceX * 4), 4)
                            .CopyTo(resized.AsSpan((y * targetStride) + (x * 4)));
                    }
                }

                pixels = resized;
                width = targetWidth;
                height = targetHeight;
                stride = targetStride;
                transformed = true;
            }
        }

        if (!transformed)
        {
            return decoded;
        }

        // The buffer was allocated here and nothing else holds it, so the consumer may adopt it
        // without a defensive copy — one avoided full-size copy per decode of a cropped source.
        return new DecodedImage(pixels, width, height, stride, decoded.Format, bufferIsExclusive: true);
    }
}
