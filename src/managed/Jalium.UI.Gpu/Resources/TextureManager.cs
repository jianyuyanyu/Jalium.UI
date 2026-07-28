namespace Jalium.UI.Gpu.Resources;

/// <summary>
/// 纹理管理器 - 管理纹理生命周期、渲染目标、SRV 创建
/// </summary>
public sealed class TextureManager : IDisposable
{
    private readonly IRenderBackendEx _backend;
    private readonly DescriptorHeapManager _descriptors;
    private readonly Dictionary<uint, TextureEntry> _textures = new();
    private uint _nextId;
    private bool _disposed;

    public TextureManager(IRenderBackendEx backend, DescriptorHeapManager descriptors)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(descriptors);

        _backend = backend;
        _descriptors = descriptors;
    }

    /// <summary>
    /// 加载纹理文件
    /// </summary>
    public TextureHandle LoadTexture(string path, TextureFormat format)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return CreateAndRegisterTexture(
            () => _backend.LoadTexture(path, format),
            width: 0,
            height: 0,
            format,
            TextureUsage.ShaderResource,
            isRenderTarget: false,
            createUav: false);
    }

    /// <summary>
    /// 创建渲染目标纹理
    /// </summary>
    public TextureHandle CreateRenderTarget(int width, int height, TextureFormat format)
    {
        ThrowIfDisposed();
        ValidateDimensions(width, height);

        var usage = TextureUsage.RenderTarget | TextureUsage.ShaderResource;
        return CreateAndRegisterTexture(
            () => _backend.CreateTexture2D(width, height, format, usage),
            width,
            height,
            format,
            usage,
            isRenderTarget: true,
            createUav: false);
    }

    /// <summary>
    /// 创建可读写纹理（用于 compute shader）
    /// </summary>
    public TextureHandle CreateReadWrite(int width, int height, TextureFormat format)
    {
        ThrowIfDisposed();
        ValidateDimensions(width, height);

        var usage = TextureUsage.UnorderedAccess | TextureUsage.ShaderResource;
        return CreateAndRegisterTexture(
            () => _backend.CreateTexture2D(width, height, format, usage),
            width,
            height,
            format,
            usage,
            isRenderTarget: false,
            createUav: true);
    }

    /// <summary>
    /// 创建字形图集纹理
    /// </summary>
    public TextureHandle CreateGlyphAtlas(string fontId, float fontSize, int width, int height)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fontId);
        if (!float.IsFinite(fontSize) || fontSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "Font size must be finite and positive.");
        }
        ValidateDimensions(width, height);

        return CreateAndRegisterTexture(
            () => _backend.CreateGlyphAtlas(fontId, fontSize, width, height),
            width,
            height,
            TextureFormat.R8,
            TextureUsage.ShaderResource,
            isRenderTarget: false,
            createUav: false);
    }

    /// <summary>
    /// 获取纹理的 SRV
    /// </summary>
    public DescriptorHandle GetSrv(TextureHandle handle)
    {
        ThrowIfDisposed();
        return handle.IsValid && _textures.TryGetValue(handle.Id, out var entry)
            ? entry.Srv
            : DescriptorHandle.Invalid;
    }

    /// <summary>
    /// 获取纹理的 UAV
    /// </summary>
    public DescriptorHandle GetUav(TextureHandle handle)
    {
        ThrowIfDisposed();
        return handle.IsValid &&
               _textures.TryGetValue(handle.Id, out var entry) &&
               entry.Uav.IsValid
            ? entry.Uav
            : DescriptorHandle.Invalid;
    }

    /// <summary>
    /// 获取纹理的 native handle
    /// </summary>
    public nint GetNativeHandle(TextureHandle handle)
    {
        ThrowIfDisposed();
        return handle.IsValid && _textures.TryGetValue(handle.Id, out var entry)
            ? entry.NativeHandle
            : nint.Zero;
    }

    /// <summary>
    /// 获取纹理尺寸
    /// </summary>
    public (int Width, int Height) GetSize(TextureHandle handle)
    {
        ThrowIfDisposed();
        return handle.IsValid && _textures.TryGetValue(handle.Id, out var entry)
            ? (entry.Width, entry.Height)
            : (0, 0);
    }

    /// <summary>
    /// 调整渲染目标大小（窗口 resize 时）
    /// </summary>
    public void ResizeRenderTarget(TextureHandle handle, int newWidth, int newHeight)
    {
        ThrowIfDisposed();
        ValidateDimensions(newWidth, newHeight);

        if (!handle.IsValid ||
            !_textures.TryGetValue(handle.Id, out var entry) ||
            !entry.IsRenderTarget)
        {
            return;
        }

        var replacement = CreateTextureEntry(
            () => _backend.CreateTexture2D(newWidth, newHeight, entry.Format, entry.Usage),
            newWidth,
            newHeight,
            entry.Format,
            entry.Usage,
            isRenderTarget: true,
            createUav: false);

        try
        {
            ReleaseTrackedEntry(handle.Id, entry);
            _textures.Add(handle.Id, replacement);
        }
        catch (Exception replacementError)
        {
            try
            {
                ReleaseDetachedEntry(replacement);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "The render target could not be resized and its replacement could not be fully released.",
                    replacementError,
                    cleanupError);
            }

            throw;
        }
    }

    /// <summary>
    /// 销毁纹理
    /// </summary>
    public void Destroy(TextureHandle handle)
    {
        ThrowIfDisposed();
        if (!handle.IsValid || !_textures.TryGetValue(handle.Id, out var entry))
            return;

        ReleaseTrackedEntry(handle.Id, entry);
    }

    public void Dispose()
    {
        if (_disposed) return;

        List<Exception>? exceptions = null;
        foreach (var id in _textures.Keys.ToArray())
        {
            try
            {
                ReleaseTrackedEntry(id, _textures[id]);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more textures could not be released.",
                exceptions);
        }

        _disposed = true;
    }

    private TextureHandle CreateAndRegisterTexture(
        Func<nint> createNative,
        int width,
        int height,
        TextureFormat format,
        TextureUsage usage,
        bool isRenderTarget,
        bool createUav)
    {
        var entry = CreateTextureEntry(
            createNative,
            width,
            height,
            format,
            usage,
            isRenderTarget,
            createUav);

        try
        {
            var id = AllocateId();
            _textures.Add(id, entry);
            return new TextureHandle(id);
        }
        catch (Exception registrationError)
        {
            try
            {
                ReleaseDetachedEntry(entry);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "The texture could not be registered and its native resources could not be fully released.",
                    registrationError,
                    cleanupError);
            }

            throw;
        }
    }

    private TextureEntry CreateTextureEntry(
        Func<nint> createNative,
        int width,
        int height,
        TextureFormat format,
        TextureUsage usage,
        bool isRenderTarget,
        bool createUav)
    {
        var nativeHandle = nint.Zero;
        var srv = DescriptorHandle.Invalid;
        var uav = DescriptorHandle.Invalid;

        try
        {
            nativeHandle = createNative();
            if (nativeHandle == nint.Zero)
            {
                throw new InvalidOperationException("The backend failed to create the texture.");
            }

            srv = _descriptors.AllocateSrv(nativeHandle);
            if (createUav)
            {
                uav = _descriptors.AllocateUav(nativeHandle);
            }

            return new TextureEntry
            {
                NativeHandle = nativeHandle,
                Srv = srv,
                Uav = uav,
                Width = width,
                Height = height,
                Format = format,
                Usage = usage,
                IsRenderTarget = isRenderTarget
            };
        }
        catch (Exception creationError)
        {
            var partialEntry = new TextureEntry
            {
                NativeHandle = nativeHandle,
                Srv = srv,
                Uav = uav
            };

            try
            {
                ReleaseDetachedEntry(partialEntry);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Texture creation failed and its partial native resources could not be fully released.",
                    creationError,
                    cleanupError);
            }

            throw;
        }
    }

    private void ReleaseTrackedEntry(uint id, TextureEntry entry)
    {
        if (entry.Uav.IsValid)
        {
            _descriptors.Free(entry.Uav);
            entry.Uav = DescriptorHandle.Invalid;
            _textures[id] = entry;
        }

        if (entry.Srv.IsValid)
        {
            _descriptors.Free(entry.Srv);
            entry.Srv = DescriptorHandle.Invalid;
            _textures[id] = entry;
        }

        if (entry.NativeHandle != nint.Zero)
        {
            _backend.DestroyTexture(entry.NativeHandle);
            entry.NativeHandle = nint.Zero;
            _textures[id] = entry;
        }

        _textures.Remove(id);
    }

    private void ReleaseDetachedEntry(TextureEntry entry)
    {
        List<Exception>? exceptions = null;

        if (entry.Uav.IsValid)
        {
            TryRelease(() => _descriptors.Free(entry.Uav), ref exceptions);
        }

        if (entry.Srv.IsValid)
        {
            TryRelease(() => _descriptors.Free(entry.Srv), ref exceptions);
        }

        if (entry.NativeHandle != nint.Zero)
        {
            TryRelease(() => _backend.DestroyTexture(entry.NativeHandle), ref exceptions);
        }

        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more texture resources could not be released.",
                exceptions);
        }
    }

    private static void TryRelease(Action release, ref List<Exception>? exceptions)
    {
        try
        {
            release();
        }
        catch (Exception exception)
        {
            (exceptions ??= []).Add(exception);
        }
    }

    private uint AllocateId()
    {
        for (var attempt = 0; attempt <= _textures.Count; attempt++)
        {
            var candidate = _nextId;
            _nextId = candidate == uint.MaxValue ? 0 : candidate + 1;

            if (candidate != uint.MaxValue && !_textures.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No texture handle identifiers are available.");
    }

    private static void ValidateDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// 纹理句柄（轻量级标识符）
/// </summary>
public readonly struct TextureHandle : IEquatable<TextureHandle>
{
    private readonly bool _initialized;

    public readonly uint Id;

    public TextureHandle(uint id)
    {
        Id = id;
        _initialized = id != uint.MaxValue;
    }

    public static TextureHandle Invalid => default;
    public bool IsValid => _initialized && Id != uint.MaxValue;

    public bool Equals(TextureHandle other)
    {
        if (!IsValid || !other.IsValid)
        {
            return IsValid == other.IsValid;
        }

        return Id == other.Id;
    }

    public override bool Equals(object? obj) => obj is TextureHandle other && Equals(other);
    public override int GetHashCode() => IsValid ? (int)Id : 0;
}

/// <summary>
/// 纹理条目（内部追踪）
/// </summary>
internal struct TextureEntry
{
    public nint NativeHandle;
    public DescriptorHandle Srv;
    public DescriptorHandle Uav;
    public int Width;
    public int Height;
    public TextureFormat Format;
    public TextureUsage Usage;
    public bool IsRenderTarget;
}
