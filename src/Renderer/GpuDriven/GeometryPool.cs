// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: GeometryPool.cs
// Role: Monolithic shared vertex and index GPU memory pool (Sprint 5b basic, 7a full).
// ==================================================================================================

using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Mesh;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Upload;

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// Slice descriptor representing a mesh's allocated subregion within the global GeometryPool.
/// </summary>
public readonly struct GeometrySlice
{
    public readonly ulong VertexOffsetBytes;
    public readonly ulong IndexOffsetBytes;
    public readonly uint VertexCount;
    public readonly uint IndexCount;
    public readonly uint FirstIndex;
    public readonly int VertexOffset;

    public GeometrySlice(ulong vertexOffsetBytes, ulong indexOffsetBytes, uint vertexCount, uint indexCount, uint firstIndex, int vertexOffset)
    {
        VertexOffsetBytes = vertexOffsetBytes;
        IndexOffsetBytes = indexOffsetBytes;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        FirstIndex = firstIndex;
        VertexOffset = vertexOffset;
    }
}

/// <summary>
/// Monolithic shared vertex/index buffer pool enabling drawing multiple sub-meshes without rebinding buffers.
/// </summary>
public sealed class GeometryPool : IDisposable
{
    public const ulong DefaultVertexPoolSizeBytes = 64 * 1024 * 1024; // 64 MB
    public const ulong DefaultIndexPoolSizeBytes = 32 * 1024 * 1024;  // 32 MB

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;

    private ulong _vertexBytesAllocated;
    private ulong _indexBytesAllocated;
    private uint _totalIndicesAllocated;
    private int _totalVerticesAllocated;

    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the shared GPU vertex buffer.
    /// </summary>
    public GpuBuffer VertexBuffer => _vertexBuffer;

    /// <summary>
    /// Gets the shared GPU index buffer.
    /// </summary>
    public GpuBuffer IndexBuffer => _indexBuffer;

    /// <summary>
    /// Constructs a basic geometry pool with preallocated buffer limits.
    /// </summary>
    public GeometryPool(
        VulkanContext context,
        GpuAllocator allocator,
        ulong vertexPoolSize = DefaultVertexPoolSizeBytes,
        ulong indexPoolSize = DefaultIndexPoolSizeBytes)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        _vertexBuffer = _allocator.CreateBuffer(
            vertexPoolSize,
            VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );

        _indexBuffer = _allocator.CreateBuffer(
            indexPoolSize,
            VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );
    }

    /// <summary>
    /// Allocates and uploads a mesh into the shared geometry pool.
    /// </summary>
    public GeometrySlice AllocateMesh(
        UploadManager uploadManager,
        ReadOnlySpan<Vertex3D> vertices,
        ReadOnlySpan<ushort> indices)
    {
        lock (_lock)
        {
            ulong vSize = (ulong)(Marshal.SizeOf<Vertex3D>() * vertices.Length);
            ulong iSize = (ulong)(sizeof(ushort) * indices.Length);

            ulong vOffset = _vertexBytesAllocated;
            ulong iOffset = _indexBytesAllocated;
            uint firstIndex = _totalIndicesAllocated;
            int vertexOffset = _totalVerticesAllocated;

            _vertexBytesAllocated += vSize;
            _indexBytesAllocated += iSize;
            _totalIndicesAllocated += (uint)indices.Length;
            _totalVerticesAllocated += vertices.Length;

            // Upload slices
            uploadManager.UploadBuffer(_vertexBuffer, vOffset, MemoryMarshal.AsBytes(vertices));
            uploadManager.UploadBuffer(_indexBuffer, iOffset, MemoryMarshal.AsBytes(indices));

            return new GeometrySlice(vOffset, iOffset, (uint)vertices.Length, (uint)indices.Length, firstIndex, vertexOffset);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            _disposed = true;
        }
    }
}
