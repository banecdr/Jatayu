/* =================================================================================================
 * FILE: Mesh.cs
 * LOCATION: src/Renderer/Mesh/Mesh.cs
 * 
 * WHAT THIS FILE DOES:
 *   GPU geometry container holding vertex and index buffer slices, triangle counts, bounding geometry
 *   (AABB and bounding sphere radius), and LOD offsets for mesh drawing.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.AssetFormats;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.Renderer.Mesh;

/// <summary>
/// GPU geometry container holding vertex and index buffer slices, triangle counts, and bounding volumes.
/// </summary>
public sealed class Mesh : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the GPU vertex buffer.
    /// </summary>
    public GpuBuffer VertexBuffer { get; }

    /// <summary>
    /// Gets the GPU index buffer.
    /// </summary>
    public GpuBuffer IndexBuffer { get; }

    /// <summary>
    /// Gets the byte offset into the vertex buffer.
    /// </summary>
    public ulong VertexOffset { get; }

    /// <summary>
    /// Gets the byte offset into the index buffer.
    /// </summary>
    public ulong IndexOffset { get; }

    /// <summary>
    /// Gets the total vertex count.
    /// </summary>
    public uint VertexCount { get; }

    /// <summary>
    /// Gets the total index count.
    /// </summary>
    public uint IndexCount { get; }

    /// <summary>
    /// Gets the Vulkan index type (UInt16 or UInt32).
    /// </summary>
    public VkIndexType IndexType { get; }

    /// <summary>
    /// Gets the total triangle count.
    /// </summary>
    public uint TriangleCount => IndexCount / 3;

    /// <summary>
    /// Minimum point of the axis-aligned bounding box (AABB).
    /// </summary>
    public Vector3 BoundingMin { get; }

    /// <summary>
    /// Maximum point of the axis-aligned bounding box (AABB).
    /// </summary>
    public Vector3 BoundingMax { get; }

    /// <summary>
    /// Bounding sphere radius encompassing the mesh from its center.
    /// </summary>
    public float BoundingRadius { get; }

    /// <summary>
    /// Array of Level-of-Detail (LOD) descriptors.
    /// </summary>
    public CookedMeshLod[] Lods { get; }

    /// <summary>
    /// Indicates whether this mesh owns the lifetime of its GPU buffers.
    /// </summary>
    public bool OwnsBuffers { get; }

    public Mesh(
        GpuBuffer vertexBuffer,
        GpuBuffer indexBuffer,
        uint vertexCount,
        uint indexCount,
        ulong vertexOffset = 0,
        ulong indexOffset = 0,
        VkIndexType indexType = VkIndexType.UInt16,
        Vector3 boundingMin = default,
        Vector3 boundingMax = default,
        float boundingRadius = 0f,
        CookedMeshLod[]? lods = null,
        bool ownsBuffers = true)
    {
        VertexBuffer = vertexBuffer ?? throw new ArgumentNullException(nameof(vertexBuffer));
        IndexBuffer = indexBuffer ?? throw new ArgumentNullException(nameof(indexBuffer));
        VertexCount = vertexCount;
        IndexCount = indexCount;
        VertexOffset = vertexOffset;
        IndexOffset = indexOffset;
        IndexType = indexType;
        BoundingMin = boundingMin;
        BoundingMax = boundingMax;
        BoundingRadius = boundingRadius;
        Lods = lods ?? [new CookedMeshLod { LodIndex = 0, VertexCount = vertexCount, IndexCount = indexCount }];
        OwnsBuffers = ownsBuffers;
    }

    /// <summary>
    /// Binds the vertex and index buffers to the command buffer.
    /// </summary>
    public void Bind(CommandBuffer cmd, int binding = 0)
    {
        cmd.BindVertexBuffers(binding, [VertexBuffer.Handle], [new ByteSize64(VertexOffset)]);
        cmd.BindIndexBuffer(IndexBuffer.Handle, new ByteSize64(IndexOffset), IndexType);
    }

    /// <summary>
    /// Draws the mesh with the specified instance count.
    /// </summary>
    public void Draw(CommandBuffer cmd, uint instanceCount = 1, uint firstInstance = 0)
    {
        cmd.DrawIndexed((int)IndexCount, (int)instanceCount, 0, 0, (int)firstInstance);
    }

    /// <summary>
    /// Draws a specific LOD of this mesh.
    /// </summary>
    public void DrawLod(CommandBuffer cmd, int lodIndex, uint instanceCount = 1, uint firstInstance = 0)
    {
        if (lodIndex < 0 || lodIndex >= Lods.Length)
            lodIndex = 0;

        ref readonly var lod = ref Lods[lodIndex];
        cmd.DrawIndexed((int)lod.IndexCount, (int)instanceCount, (int)lod.IndexOffset, (int)lod.VertexOffset, (int)firstInstance);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (OwnsBuffers)
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }

        _disposed = true;
    }
}
