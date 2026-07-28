using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Jalium.UI.Gpu;
using Jalium.UI.Gpu.Backend;
using Jalium.UI.Gpu.Resources;
using Jalium.UI.Gpu.Shaders;
using Jalium.UI.Interop;

namespace Jalium.UI.Tests;

public sealed class NativeGpuInteropAbiTests
{
    [Fact]
    public void AdapterInfo_HasStableFixedWidthUtf16Layout()
    {
        Assert.Equal(288, Marshal.SizeOf<AdapterInfo>());
        Assert.Equal(0, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.Name)).ToInt32());
        Assert.Equal(256, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.AdapterType)).ToInt32());
        Assert.Equal(264, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.DedicatedVideoMemory)).ToInt32());
        Assert.Equal(272, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.SharedSystemMemory)).ToInt32());
        Assert.Equal(280, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.VendorId)).ToInt32());
        Assert.Equal(284, Marshal.OffsetOf<AdapterInfo>(nameof(AdapterInfo.DeviceId)).ToInt32());
    }

    [Fact]
    public void SoftwareContext_GetAdapterInfo_ReturnsNullWithoutCorruptingInteropFrame()
    {
        using var context = new RenderContext(RenderBackend.Software);

        Assert.Null(context.GetAdapterInfo());
    }

    [Fact]
    public void D3D12ShaderBackend_UpdateBufferRejectsNegativeOffsetBeforeInterop()
    {
        var backend = (D3D12ShaderBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(D3D12ShaderBackend));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => backend.UpdateBuffer((nint)1, -1, new byte[] { 0x7f }));
    }

    [Fact]
    public void DescriptorManagerRejectsSamplerDoubleFree()
    {
        var backend = (D3D12ShaderBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(D3D12ShaderBackend));
        using var manager = new DescriptorHeapManager(
            backend,
            srvCbvUavCount: 1,
            samplerCount: 1);

        var sampler = manager.AllocateSampler(default);
        manager.Free(sampler);

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.Free(sampler));
        Assert.Equal(0, manager.AllocatedSamplerCount);
    }

    [Fact]
    public void DefaultGpuHandlesAreInvalid()
    {
        Assert.False(default(DescriptorHandle).IsValid);
        Assert.False(default(TextureHandle).IsValid);
        Assert.Equal(DescriptorHandle.Invalid, default);
        Assert.Equal(TextureHandle.Invalid, default);
    }

    [Fact]
    public void FrameUploadAllocationRejectsUnsafeSizeAndAlignmentBeforePointerArithmetic()
    {
        var frame = (FrameResources)RuntimeHelpers.GetUninitializedObject(
            typeof(FrameResources));

        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Allocate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Allocate(1, 0));
        Assert.Throws<ArgumentException>(() => frame.Allocate(1, 3));
    }

    [Fact]
    public void ConstantBufferViewRejectsUnsafeArgumentsBeforeInterop()
    {
        var backend = (D3D12ShaderBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(D3D12ShaderBackend));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => backend.CreateCbv((nint)1, -1, 256));
        Assert.Throws<ArgumentException>(
            () => backend.CreateCbv((nint)1, 1, 256));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => backend.CreateCbv((nint)1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => backend.CreateCbv((nint)1, 0, 65_537));
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12NativeBufferUpdateRejectsWriteBeforeMappedAllocation()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        nint buffer = backend.CreateBuffer(4, BufferUsage.Upload);
        nint source = Marshal.AllocHGlobal(1);

        try
        {
            Assert.NotEqual(nint.Zero, buffer);
            nint mapped = backend.GetBufferMappedPointer(buffer);
            Assert.NotEqual(nint.Zero, mapped);
            Marshal.WriteByte(mapped, 0, 0x11);
            Marshal.WriteByte(source, 0, 0x7f);

            NativeBufferUpdate(context.Handle, buffer, -1, source, 1);

            Assert.Equal(0x11, Marshal.ReadByte(mapped, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(source);
            backend.DestroyBuffer(buffer);
        }
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12PipelineRemainsUsableUntilItsLastManagedOwnerIsDisposed()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var first = new D3D12ShaderBackend(context.Handle);
        using var second = new D3D12ShaderBackend(context.Handle);

        first.Dispose();

        nint buffer = second.CreateBuffer(4, BufferUsage.Upload);
        try
        {
            Assert.NotEqual(nint.Zero, buffer);
            Assert.NotEqual(nint.Zero, second.GetBufferMappedPointer(buffer));
        }
        finally
        {
            second.DestroyBuffer(buffer);
        }
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void DescriptorManagerReturnsAndFreesTheNativeDescriptor()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        using var manager = new DescriptorHeapManager(
            backend,
            srvCbvUavCount: 2,
            samplerCount: 1);
        nint firstTexture = backend.CreateTexture2D(
            1,
            1,
            TextureFormat.RGBA8,
            TextureUsage.ShaderResource);
        nint secondTexture = backend.CreateTexture2D(
            1,
            1,
            TextureFormat.RGBA8,
            TextureUsage.ShaderResource);
        var external = DescriptorHandle.Invalid;

        try
        {
            Assert.NotEqual(nint.Zero, firstTexture);
            Assert.NotEqual(nint.Zero, secondTexture);

            external = backend.CreateSrv(firstTexture);
            var managed = manager.AllocateSrv(secondTexture);

            Assert.NotEqual(external.HeapIndex, managed.HeapIndex);
            Assert.Equal(1, manager.AllocatedSrvCbvUavCount);

            manager.Free(managed);

            Assert.Equal(0, manager.AllocatedSrvCbvUavCount);
            Assert.Throws<ArgumentException>(() => manager.Free(managed));
        }
        finally
        {
            if (external.IsValid)
            {
                backend.FreeDescriptor(external);
            }

            backend.DestroyTexture(secondTexture);
            backend.DestroyTexture(firstTexture);
        }
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void TextureCreationFailureReleasesItsPartialDescriptorAllocation()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        using var descriptors = new DescriptorHeapManager(
            backend,
            srvCbvUavCount: 1,
            samplerCount: 1);
        using var textures = new TextureManager(backend, descriptors);

        Assert.Throws<InvalidOperationException>(
            () => textures.CreateReadWrite(1, 1, TextureFormat.RGBA8));
        Assert.Equal(0, descriptors.AllocatedSrvCbvUavCount);

        var renderTarget = textures.CreateRenderTarget(1, 1, TextureFormat.RGBA8);
        Assert.True(renderTarget.IsValid);

        textures.Destroy(renderTarget);

        Assert.Equal(0, descriptors.AllocatedSrvCbvUavCount);
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void FailedRenderTargetResizeKeepsTheOriginalTextureUsable()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        using var descriptors = new DescriptorHeapManager(
            backend,
            srvCbvUavCount: 1,
            samplerCount: 1);
        using var textures = new TextureManager(backend, descriptors);
        var renderTarget = textures.CreateRenderTarget(1, 1, TextureFormat.RGBA8);
        var originalNativeHandle = textures.GetNativeHandle(renderTarget);

        Assert.Throws<InvalidOperationException>(
            () => textures.ResizeRenderTarget(renderTarget, 2, 2));

        Assert.Equal(originalNativeHandle, textures.GetNativeHandle(renderTarget));
        Assert.Equal((1, 1), textures.GetSize(renderTarget));
        Assert.Equal(1, descriptors.AllocatedSrvCbvUavCount);
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12NativeEntryPointsRejectMismatchedResourceTypes()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        nint buffer = backend.CreateBuffer(256, BufferUsage.Upload);
        nint texture = backend.CreateTexture2D(
            1,
            1,
            TextureFormat.RGBA8,
            TextureUsage.ShaderResource);

        try
        {
            Assert.NotEqual(nint.Zero, buffer);
            Assert.NotEqual(nint.Zero, texture);

            Assert.Equal(nint.Zero, NativeBufferGetMappedPointer(context.Handle, texture));
            Assert.Equal(-1, NativeDescriptorCreateCbv(context.Handle, texture, 0, 256));
            Assert.Equal(-1, NativeDescriptorCreateCbv(context.Handle, buffer, -256, 256));

            NativeBufferDestroy(context.Handle, texture);
            NativeTextureDestroy(context.Handle, buffer);

            Assert.NotEqual(nint.Zero, backend.GetBufferMappedPointer(buffer));
            var descriptor = backend.CreateSrv(texture);
            backend.FreeDescriptor(descriptor);
        }
        finally
        {
            backend.DestroyTexture(texture);
            backend.DestroyBuffer(buffer);
        }
    }

    [RequiresWindowsBackendFact(RenderBackend.D3D12)]
    public void D3D12ShaderCompilationUsesUtf8ByteLength()
    {
        using var context = new RenderContext(RenderBackend.D3D12);
        using var backend = new D3D12ShaderBackend(context.Handle);
        const string shader =
            "// 非 ASCII 注释\n" +
            "float4 main(float4 position : POSITION) : SV_POSITION { return position; }";

        nint bytecode = backend.CompileShader(shader, "main", ShaderStage.Vertex);
        try
        {
            Assert.NotEqual(nint.Zero, bytecode);
        }
        finally
        {
            backend.DestroyShader(bytecode);
        }
    }

    [DllImport(
        "jalium.native.d3d12",
        EntryPoint = "jalium_buffer_update",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeBufferUpdate(
        nint context,
        nint buffer,
        int offset,
        nint data,
        int size);

    [DllImport(
        "jalium.native.d3d12",
        EntryPoint = "jalium_buffer_get_mapped_ptr",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NativeBufferGetMappedPointer(nint context, nint buffer);

    [DllImport(
        "jalium.native.d3d12",
        EntryPoint = "jalium_buffer_destroy",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeBufferDestroy(nint context, nint buffer);

    [DllImport(
        "jalium.native.d3d12",
        EntryPoint = "jalium_texture_destroy",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeTextureDestroy(nint context, nint texture);

    [DllImport(
        "jalium.native.d3d12",
        EntryPoint = "jalium_descriptor_create_cbv",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeDescriptorCreateCbv(
        nint context,
        nint buffer,
        int offset,
        int size);
}
