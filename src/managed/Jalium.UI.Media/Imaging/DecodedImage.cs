namespace Jalium.UI.Media.Imaging;

/// <summary>
/// 不可变的解码图像数据：BGRA8 / RGBA8 紧凑像素 + 几何信息。
/// 由 <see cref="INativeImageDecoder"/> 实现产出。
/// </summary>
public readonly struct DecodedImage
{
    /// <summary>
    /// 初始化新的 <see cref="DecodedImage"/>。
    /// </summary>
    public DecodedImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride, NativePixelFormat format)
        : this(pixels, width, height, stride, format, bufferIsExclusive: false)
    {
    }

    internal DecodedImage(
        ReadOnlyMemory<byte> pixels,
        int width,
        int height,
        int stride,
        NativePixelFormat format,
        bool bufferIsExclusive)
    {
        int requiredByteCount = PixelBufferLayout.GetRequiredByteCount(width, height, stride);
        if (pixels.Length < requiredByteCount)
        {
            throw new ArgumentException(
                "Pixel buffer is smaller than the specified dimensions and stride.",
                nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        BufferIsExclusive = bufferIsExclusive;
    }

    /// <summary>
    /// 像素缓冲是否为本次解码专属、可由消费方直接接管而无需再拷一份。
    ///
    /// <para>仅框架自带解码器在把原生像素搬进一个全新 <c>byte[]</c> 后置
    /// <see langword="true"/>。第三方 <see cref="INativeImageDecoder"/> 实现走 public
    /// 构造函数，恒为 <see langword="false"/>，保持"缓冲可能被复用/池化，消费方必须
    /// 自行拷贝"的保守语义。</para>
    ///
    /// <para>存在的理由：全分辨率位图的一次多余拷贝就是一次 LOH 分配加一次全尺寸
    /// memcpy（1910x823 BGRA 即 6 MiB），在页面切换这种同帧解码多张图的场景里直接
    /// 决定是否触发一次阻塞 GC。</para>
    /// </summary>
    internal bool BufferIsExclusive { get; }

    /// <summary>
    /// 紧凑或带行间填充的像素数据。
    /// </summary>
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>
    /// 像素宽度。
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// 像素高度。
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// 行跨度（字节）。至少为 <c>Width * 4</c>。
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// 像素格式。
    /// </summary>
    public NativePixelFormat Format { get; }
}

/// <summary>
/// 跨平台像素格式枚举，与原生 <c>jalium_pixel_format_t</c> 一一对应。
/// </summary>
public enum NativePixelFormat
{
    /// <summary>BGRA8（默认；与 D3D12 swap-chain 一致）。</summary>
    Bgra8 = 0,

    /// <summary>RGBA8（Android Vulkan 在 Mali GPU 上常用）。</summary>
    Rgba8 = 1,
}
