// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: GeometryPool.cs
// Role: Monolithic shared vertex/index GPU memory pool and Mesh LOD descriptor table (Sprint 5b basic, 7a full).
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
    public readonly uint MeshId;

    public GeometrySlice(
        ulong vertexOffsetBytes,
        ulong indexOffsetBytes,
        uint vertexCount,
        uint indexCount,
        uint firstIndex,
        int vertexOffset,
        uint meshId = 0)
    {
        VertexOffsetBytes = vertexOffsetBytes;
        IndexOffsetBytes = indexOffsetBytes;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        FirstIndex = firstIndex;
        VertexOffset = vertexOffset;
        MeshId = meshId;
    }
}

/// <summary>
/// Sub-mesh index slice and LOD threshold data for GPU culling (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct MeshLodLevel
{
    public uint IndexCount;
    public uint FirstIndex;
    public int VertexOffset;
    public float LodThreshold;
}

/// <summary>
/// Packed 48-byte mesh LOD record holding descriptors for LOD 0, 1, and 2.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct MeshLodRecord
{
    public MeshLodLevel Lod0;
    public MeshLodLevel Lod1;
    public MeshLodLevel Lod2;
}

/// <summary>
/// Monolithic shared vertex/index buffer pool and mesh LOD table enabling indirect multidraw without rebinding buffers.
/// </summary>
public sealed class GeometryPool : IDisposable
{
    public const ulong DefaultVertexPoolSizeBytes = 64 * 1024 * 1024; // 64 MB
    public const ulong DefaultIndexPoolSizeBytes = 32 * 1024 * 1024;  // 32 MB
    public const int DefaultMaxMeshes = 4096;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;
    private GpuBuffer _meshLodBuffer;

    private readonly List<MeshLodRecord> _meshLodRecords = new(DefaultMaxMeshes);

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
    /// Gets the index format of the shared index buffer (16-bit ushort).
    /// </summary>
    public VkIndexType IndexType => VkIndexType.UInt16;

    /// <summary>
    /// Gets the GPU storage buffer holding the MeshLodRecord table for compute culling.
    /// </summary>
    public GpuBuffer MeshLodBuffer => _meshLodBuffer;

    /// <summary>
    /// Gets the total number of registered meshes.
    /// </summary>
    public int MeshCount => _meshLodRecords.Count;

    /// <summary>
    /// Constructs a geometry pool with preallocated vertex/index and LOD table capacities.
    /// </summary>
    public GeometryPool(
        VulkanContext context,
        GpuAllocator allocator,
        ulong vertexPoolSize = DefaultVertexPoolSizeBytes,
        ulong indexPoolSize = DefaultIndexPoolSizeBytes,
        int maxMeshes = DefaultMaxMeshes)
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

        ulong lodBufferSize = (ulong)(Marshal.SizeOf<MeshLodRecord>() * Math.Max(256, maxMeshes));
        _meshLodBuffer = _allocator.CreateBuffer(
            lodBufferSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );
    }

    /// <summary>
    /// Allocates and uploads a single-LOD mesh into the shared geometry pool.
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
            uint meshId = (uint)_meshLodRecords.Count;

            _vertexBytesAllocated += vSize;
            _indexBytesAllocated += iSize;
            _totalIndicesAllocated += (uint)indices.Length;
            _totalVerticesAllocated += vertices.Length;

            // Upload vertex & index slices
            uploadManager.UploadBuffer(_vertexBuffer, vOffset, MemoryMarshal.AsBytes(vertices));
            uploadManager.UploadBuffer(_indexBuffer, iOffset, MemoryMarshal.AsBytes(indices));

            var record = new MeshLodRecord
            {
                Lod0 = new MeshLodLevel
                {
                    IndexCount = (uint)indices.Length,
                    FirstIndex = firstIndex,
                    VertexOffset = vertexOffset,
                    LodThreshold = 100000.0f,
                }
            };

            _meshLodRecords.Add(record);
            Span<MeshLodRecord> singleSpan = stackalloc MeshLodRecord[1] { record };
            _meshLodBuffer.SetData<MeshLodRecord>(singleSpan, offsetInBytes: (ulong)(meshId * Marshal.SizeOf<MeshLodRecord>()));

            return new GeometrySlice(vOffset, iOffset, (uint)vertices.Length, (uint)indices.Length, firstIndex, vertexOffset, meshId);
        }
    }

    /// <summary>
    /// Allocates and uploads a mesh with up to 3 discrete LOD levels into the shared geometry pool.
    /// </summary>
    public GeometrySlice AllocateMeshWithLods(
        UploadManager uploadManager,
        ReadOnlySpan<Vertex3D> vertices,
        ReadOnlySpan<ushort> lod0Indices,
        ReadOnlySpan<ushort> lod1Indices = default,
        ReadOnlySpan<ushort> lod2Indices = default,
        float lod0Threshold = 50.0f,
        float lod1Threshold = 150.0f)
    {
        lock (_lock)
        {
            ulong vSize = (ulong)(Marshal.SizeOf<Vertex3D>() * vertices.Length);
            ulong vOffset = _vertexBytesAllocated;
            int vertexOffset = _totalVerticesAllocated;

            _vertexBytesAllocated += vSize;
            _totalVerticesAllocated += vertices.Length;
            uploadManager.UploadBuffer(_vertexBuffer, vOffset, MemoryMarshal.AsBytes(vertices));

            uint meshId = (uint)_meshLodRecords.Count;

            // LOD 0
            ulong i0Offset = _indexBytesAllocated;
            uint firstIndex0 = _totalIndicesAllocated;
            _indexBytesAllocated += (ulong)(sizeof(ushort) * lod0Indices.Length);
            _totalIndicesAllocated += (uint)lod0Indices.Length;
            uploadManager.UploadBuffer(_indexBuffer, i0Offset, MemoryMarshal.AsBytes(lod0Indices));

            // LOD 1
            uint firstIndex1 = 0;
            uint count1 = 0;
            if (!lod1Indices.IsEmpty)
            {
                ulong i1Offset = _indexBytesAllocated;
                firstIndex1 = _totalIndicesAllocated;
                count1 = (uint)lod1Indices.Length;
                _indexBytesAllocated += (ulong)(sizeof(ushort) * lod1Indices.Length);
                _totalIndicesAllocated += (uint)lod1Indices.Length;
                uploadManager.UploadBuffer(_indexBuffer, i1Offset, MemoryMarshal.AsBytes(lod1Indices));
            }

            // LOD 2
            uint firstIndex2 = 0;
            uint count2 = 0;
            if (!lod2Indices.IsEmpty)
            {
                ulong i2Offset = _indexBytesAllocated;
                firstIndex2 = _totalIndicesAllocated;
                count2 = (uint)lod2Indices.Length;
                _indexBytesAllocated += (ulong)(sizeof(ushort) * lod2Indices.Length);
                _totalIndicesAllocated += (uint)lod2Indices.Length;
                uploadManager.UploadBuffer(_indexBuffer, i2Offset, MemoryMarshal.AsBytes(lod2Indices));
            }

            var record = new MeshLodRecord
            {
                Lod0 = new MeshLodLevel { IndexCount = (uint)lod0Indices.Length, FirstIndex = firstIndex0, VertexOffset = vertexOffset, LodThreshold = lod0Threshold },
                Lod1 = new MeshLodLevel { IndexCount = count1, FirstIndex = firstIndex1, VertexOffset = vertexOffset, LodThreshold = lod1Threshold },
                Lod2 = new MeshLodLevel { IndexCount = count2, FirstIndex = firstIndex2, VertexOffset = vertexOffset, LodThreshold = 100000.0f },
            };

            _meshLodRecords.Add(record);
            Span<MeshLodRecord> singleSpan = stackalloc MeshLodRecord[1] { record };
            _meshLodBuffer.SetData<MeshLodRecord>(singleSpan, offsetInBytes: (ulong)(meshId * Marshal.SizeOf<MeshLodRecord>()));

            return new GeometrySlice(vOffset, i0Offset, (uint)vertices.Length, (uint)lod0Indices.Length, firstIndex0, vertexOffset, meshId);
        }
    }

    /// <summary>
    /// Gets the LOD descriptor record for the given mesh ID.
    /// </summary>
    public MeshLodRecord GetLodRecord(uint meshId)
    {
        lock (_lock)
        {
            if (meshId >= _meshLodRecords.Count)
                throw new ArgumentOutOfRangeException(nameof(meshId));
            return _meshLodRecords[(int)meshId];
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            _meshLodBuffer.Dispose();
            _disposed = true;
        }
    }
}
