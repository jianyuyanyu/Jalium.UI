namespace Jalium.UI.Gpu.Resources;

/// <summary>
/// 描述符堆管理器 - 管理 SRV/CBV/UAV/Sampler 描述符分配
/// </summary>
public sealed class DescriptorHeapManager : IDisposable
{
    private readonly IRenderBackendEx _backend;
    private readonly int _srvCbvUavCapacity;
    private readonly HashSet<DescriptorHandle> _nativeDescriptors = [];
    private readonly DescriptorPool _samplerPool;
    private bool _disposed;

    /// <summary>
    /// 默认 SRV/CBV/UAV 描述符数量
    /// </summary>
    public const int DefaultSrvCbvUavCount = 2048;

    /// <summary>
    /// 默认 Sampler 描述符数量
    /// </summary>
    public const int DefaultSamplerCount = 64;

    public DescriptorHeapManager(
        IRenderBackendEx backend,
        int srvCbvUavCount = DefaultSrvCbvUavCount,
        int samplerCount = DefaultSamplerCount)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srvCbvUavCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplerCount);

        _backend = backend;
        _srvCbvUavCapacity = srvCbvUavCount;
        _samplerPool = new DescriptorPool(DescriptorType.Sampler, samplerCount);
    }

    /// <summary>
    /// 分配 Shader Resource View 描述符
    /// </summary>
    public DescriptorHandle AllocateSrv(nint resource)
    {
        ThrowIfDisposed();
        return TrackNativeDescriptor(() => _backend.CreateSrv(resource));
    }

    /// <summary>
    /// 分配 Constant Buffer View 描述符
    /// </summary>
    public DescriptorHandle AllocateCbv(nint buffer, int offset, int size)
    {
        ThrowIfDisposed();
        return TrackNativeDescriptor(() => _backend.CreateCbv(buffer, offset, size));
    }

    /// <summary>
    /// 分配 Unordered Access View 描述符
    /// </summary>
    public DescriptorHandle AllocateUav(nint resource)
    {
        ThrowIfDisposed();
        return TrackNativeDescriptor(() => _backend.CreateUav(resource));
    }

    /// <summary>
    /// 分配 Sampler 描述符
    /// </summary>
    public DescriptorHandle AllocateSampler(SamplerDesc desc)
    {
        ThrowIfDisposed();
        var slot = _samplerPool.Allocate();
        return new DescriptorHandle(slot, DescriptorType.Sampler);
    }

    /// <summary>
    /// 释放描述符
    /// </summary>
    public void Free(DescriptorHandle handle)
    {
        ThrowIfDisposed();

        if (handle.Type == DescriptorType.Sampler)
        {
            _samplerPool.Free(handle.HeapIndex);
        }
        else
        {
            if (handle.Type != DescriptorType.SrvCbvUav ||
                !_nativeDescriptors.Remove(handle))
            {
                throw new ArgumentException(
                    "The descriptor is not allocated by this manager.",
                    nameof(handle));
            }

            try
            {
                _backend.FreeDescriptor(handle);
            }
            catch
            {
                _nativeDescriptors.Add(handle);
                throw;
            }
        }
    }

    /// <summary>
    /// 已分配的 SRV/CBV/UAV 数量
    /// </summary>
    public int AllocatedSrvCbvUavCount => _nativeDescriptors.Count;

    /// <summary>
    /// 已分配的 Sampler 数量
    /// </summary>
    public int AllocatedSamplerCount => _samplerPool.AllocatedCount;

    public void Dispose()
    {
        if (_disposed) return;

        List<Exception>? exceptions = null;
        foreach (var descriptor in _nativeDescriptors)
        {
            try
            {
                _backend.FreeDescriptor(descriptor);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        _nativeDescriptors.Clear();
        _samplerPool.Dispose();
        _disposed = true;

        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more native descriptors could not be released.",
                exceptions);
        }
    }

    private DescriptorHandle TrackNativeDescriptor(Func<DescriptorHandle> create)
    {
        if (_nativeDescriptors.Count >= _srvCbvUavCapacity)
        {
            throw new InvalidOperationException(
                $"Descriptor heap exhausted. Type={DescriptorType.SrvCbvUav}, Capacity={_srvCbvUavCapacity}");
        }

        var handle = create();
        if (!handle.IsValid || handle.Type != DescriptorType.SrvCbvUav)
        {
            if (handle.IsValid)
            {
                _backend.FreeDescriptor(handle);
            }

            throw new InvalidOperationException(
                "The backend returned an invalid SRV/CBV/UAV descriptor.");
        }

        if (!_nativeDescriptors.Add(handle))
        {
            _backend.FreeDescriptor(handle);
            throw new InvalidOperationException(
                $"The backend returned duplicate descriptor {handle.HeapIndex}.");
        }

        return handle;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// 描述符句柄
/// </summary>
public readonly struct DescriptorHandle : IEquatable<DescriptorHandle>
{
    private readonly bool _initialized;

    /// <summary>
    /// 堆内索引
    /// </summary>
    public readonly uint HeapIndex;

    /// <summary>
    /// 描述符类型
    /// </summary>
    public readonly DescriptorType Type;

    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsValid => _initialized && HeapIndex != uint.MaxValue;

    public DescriptorHandle(uint heapIndex, DescriptorType type)
    {
        HeapIndex = heapIndex;
        Type = type;
        _initialized = heapIndex != uint.MaxValue;
    }

    public static DescriptorHandle Invalid => default;

    public bool Equals(DescriptorHandle other)
    {
        if (!IsValid || !other.IsValid)
        {
            return IsValid == other.IsValid;
        }

        return HeapIndex == other.HeapIndex && Type == other.Type;
    }

    public override bool Equals(object? obj) => obj is DescriptorHandle other && Equals(other);
    public override int GetHashCode() => IsValid ? HashCode.Combine(HeapIndex, Type) : 0;
}

/// <summary>
/// 描述符类型
/// </summary>
public enum DescriptorType : byte
{
    SrvCbvUav,
    Sampler,
    Rtv,
    Dsv
}

/// <summary>
/// 采样器描述符
/// </summary>
public readonly struct SamplerDesc
{
    public readonly SamplerFilter Filter;
    public readonly SamplerAddressMode AddressU;
    public readonly SamplerAddressMode AddressV;
    public readonly float MaxAnisotropy;

    public SamplerDesc(
        SamplerFilter filter = SamplerFilter.Linear,
        SamplerAddressMode addressU = SamplerAddressMode.Clamp,
        SamplerAddressMode addressV = SamplerAddressMode.Clamp,
        float maxAnisotropy = 1.0f)
    {
        Filter = filter;
        AddressU = addressU;
        AddressV = addressV;
        MaxAnisotropy = maxAnisotropy;
    }

    public static SamplerDesc LinearClamp => new(SamplerFilter.Linear, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp);
    public static SamplerDesc PointClamp => new(SamplerFilter.Point, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp);
    public static SamplerDesc LinearWrap => new(SamplerFilter.Linear, SamplerAddressMode.Wrap, SamplerAddressMode.Wrap);
    public static SamplerDesc Anisotropic => new(SamplerFilter.Anisotropic, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, 16f);
}

public enum SamplerFilter : byte
{
    Point,
    Linear,
    Anisotropic
}

public enum SamplerAddressMode : byte
{
    Wrap,
    Mirror,
    Clamp,
    Border
}

/// <summary>
/// 描述符池 - 简单的空闲列表分配器
/// </summary>
internal sealed class DescriptorPool : IDisposable
{
    private readonly DescriptorType _type;
    private readonly int _capacity;
    private readonly Stack<uint> _freeList;
    private readonly bool[] _allocated;
    private int _allocatedCount;
    private bool _disposed;

    public int AllocatedCount => _allocatedCount;

    public DescriptorPool(DescriptorType type, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _type = type;
        _capacity = capacity;
        _freeList = new Stack<uint>(capacity);
        _allocated = new bool[capacity];

        // 反向压入空闲列表，使低索引先分配
        for (int i = capacity - 1; i >= 0; i--)
        {
            _freeList.Push((uint)i);
        }
    }

    public uint Allocate()
    {
        ThrowIfDisposed();

        if (_freeList.Count == 0)
        {
            throw new InvalidOperationException(
                $"Descriptor heap exhausted. Type={_type}, Capacity={_capacity}");
        }

        var index = _freeList.Pop();
        _allocated[index] = true;
        _allocatedCount++;
        return index;
    }

    public void Free(uint index)
    {
        ThrowIfDisposed();

        if (index >= (uint)_capacity || !_allocated[index])
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "The descriptor slot is outside this pool or is not allocated.");
        }

        _allocated[index] = false;
        _freeList.Push(index);
        _allocatedCount--;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _freeList.Clear();
        Array.Clear(_allocated);
        _allocatedCount = 0;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
