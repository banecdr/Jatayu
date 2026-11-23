// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: DynamicInstanceRing.cs
// Role: Ring-buffered per-frame instance buffer region for dynamic ECS entities (Decisions D17 & D25).
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// Packed 160-byte GPU instance data struct holding current and previous camera-relative transforms, bounds, mesh/material IDs (D25).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 160)]
public struct GpuInstanceData
{
    public Matrix4x4 CurrentTransform;  // 64 bytes: Camera-relative float4x4 transform
    public Matrix4x4 PreviousTransform; // 64 bytes: Previous frame transform for TAA motion vectors
    public Vector3 BoundingCenter;      // 12 bytes: Camera-relative bounding center
    public float BoundingRadius;        // 4 bytes: Bounding sphere radius
    public uint MeshId;                 // 4 bytes: Mesh index in GeometryPool/MeshLodTable
    public uint MaterialId;             // 4 bytes: Material ID in MaterialTable
    public uint Flags;                  // 4 bytes: Bit 0 = CastShadow, Bit 1 = Dynamic, Bit 2 = TwoSided
    public float LodBias;               // 4 bytes: Screen-space LOD bias offset
}

/// <summary>
/// Ring-buffered dynamic instance storage buffer written each frame by CPU systems with zero allocations.
/// </summary>
public sealed class DynamicInstanceRing : IDisposable
{
    public const int DefaultCapacity = 16384;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly int _capacityPerFrame;
    private readonly int _frameCount;

    private readonly GpuBuffer[] _buffers;
    private readonly GpuInstanceData[][] _cpuInstances;
    private readonly int[] _counts;
    private int _currentFrame;
    private bool _disposed;

    /// <summary>
    /// Gets the GPU instance buffer for the active frame.
    /// </summary>
    public GpuBuffer CurrentBuffer => _buffers[_currentFrame];

    /// <summary>
    /// Gets the number of dynamic instances written in the current frame.
    /// </summary>
    public int CurrentInstanceCount => _counts[_currentFrame];

    public DynamicInstanceRing(VulkanContext context, GpuAllocator allocator, int capacityPerFrame = DefaultCapacity, int frameCount = 2)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _capacityPerFrame = capacityPerFrame;
        _frameCount = frameCount;

        _buffers = new GpuBuffer[frameCount];
        _cpuInstances = new GpuInstanceData[frameCount][];
        _counts = new int[frameCount];

        ulong bufferSize = (ulong)(Marshal.SizeOf<GpuInstanceData>() * capacityPerFrame);

        for (int i = 0; i < frameCount; i++)
        {
            _cpuInstances[i] = new GpuInstanceData[capacityPerFrame];
            _buffers[i] = _allocator.CreateBuffer(
                bufferSize,
                VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.VertexBufferBit,
                VmaMemoryUsage.CpuToGpu
            );
        }
    }

    /// <summary>
    /// Begins recording dynamic instances for the specified frame slot.
    /// </summary>
    public void BeginFrame(int frameIndex)
    {
        _currentFrame = frameIndex % _frameCount;
        _counts[_currentFrame] = 0;
    }

    /// <summary>
    /// Appends a dynamic instance to the current frame ring buffer.
    /// </summary>
    public int AddInstance(in GpuInstanceData instance)
    {
        int count = _counts[_currentFrame];
        if (count >= _capacityPerFrame)
        {
            return -1; // Capacity exceeded
        }

        _cpuInstances[_currentFrame][count] = instance;
        _counts[_currentFrame] = count + 1;
        return count;
    }

    /// <summary>
    /// Flushes CPU instance array to the mapped GPU buffer.
    /// </summary>
    public void Flush()
    {
        int count = _counts[_currentFrame];
        if (count == 0) return;

        ReadOnlySpan<GpuInstanceData> slice = _cpuInstances[_currentFrame].AsSpan(0, count);
        _buffers[_currentFrame].SetData<GpuInstanceData>(slice);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            for (int i = 0; i < _frameCount; i++)
            {
                _buffers[i].Dispose();
            }
            _disposed = true;
        }
    }
}
