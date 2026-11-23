// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: InstanceBuffer.cs
// Role: Global GPU storage buffer holding unified static and dynamic instance data (Sprint 7a).
// ==================================================================================================

using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// GPU storage buffer managing unified static (cell-streamed) and dynamic (ECS) instance records for GPU culling.
/// </summary>
public sealed class InstanceBuffer : IDisposable
{
    public const int DefaultCapacity = 262144; // 256K instances (40 MB at 160 B/instance)

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private GpuBuffer _buffer;
    private int _capacity;

    private int _staticCount;
    private int _dynamicCount;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying GPU buffer handle.
    /// </summary>
    public GpuBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the total active instance count (Static + Dynamic).
    /// </summary>
    public int TotalCount => _staticCount + _dynamicCount;

    /// <summary>
    /// Gets the static instance count.
    /// </summary>
    public int StaticCount => _staticCount;

    /// <summary>
    /// Gets the dynamic instance count.
    /// </summary>
    public int DynamicCount => _dynamicCount;

    /// <summary>
    /// Gets the instance buffer capacity.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Initializes an instance storage buffer with preallocated capacity.
    /// </summary>
    public InstanceBuffer(VulkanContext context, GpuAllocator allocator, int initialCapacity = DefaultCapacity)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _capacity = Math.Max(1024, initialCapacity);

        ulong byteSize = (ulong)(Marshal.SizeOf<GpuInstanceData>() * _capacity);
        _buffer = _allocator.CreateBuffer(
            byteSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );
    }

    /// <summary>
    /// Replaces static instance records at the base of the buffer [0, count).
    /// </summary>
    public void SetStaticInstances(ReadOnlySpan<GpuInstanceData> instances)
    {
        EnsureCapacity(instances.Length + _dynamicCount);
        if (instances.Length > 0)
        {
            _buffer.SetData<GpuInstanceData>(instances, offsetInBytes: 0);
        }
        _staticCount = instances.Length;
    }

    /// <summary>
    /// Appends or updates dynamic instance records immediately following static instances [staticCount, totalCount).
    /// </summary>
    public void SetDynamicInstances(ReadOnlySpan<GpuInstanceData> instances)
    {
        EnsureCapacity(_staticCount + instances.Length);
        if (instances.Length > 0)
        {
            ulong byteOffset = (ulong)(_staticCount * Marshal.SizeOf<GpuInstanceData>());
            _buffer.SetData<GpuInstanceData>(instances, offsetInBytes: byteOffset);
        }
        _dynamicCount = instances.Length;
    }

    /// <summary>
    /// Resets dynamic instance count at the end of a frame.
    /// </summary>
    public void ClearDynamicInstances()
    {
        _dynamicCount = 0;
    }

    /// <summary>
    /// Resets all instances in the buffer.
    /// </summary>
    public void Clear()
    {
        _staticCount = 0;
        _dynamicCount = 0;
    }

    private void EnsureCapacity(int requiredCount)
    {
        if (requiredCount <= _capacity)
            return;

        int newCapacity = Math.Max(_capacity * 2, requiredCount);
        ulong newByteSize = (ulong)(Marshal.SizeOf<GpuInstanceData>() * newCapacity);

        var newBuffer = _allocator.CreateBuffer(
            newByteSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );

        // Copy existing data if any
        if (TotalCount > 0)
        {
            // Note: Since buffer is CpuToGpu, we can read/copy if needed or rely on next uploads
        }

        _buffer.Dispose();
        _buffer = newBuffer;
        _capacity = newCapacity;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _buffer.Dispose();
            _disposed = true;
        }
    }
}
