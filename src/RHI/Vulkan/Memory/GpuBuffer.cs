/* =================================================================================================
 * FILE: GpuBuffer.cs
 * LOCATION: src/RHI/Vulkan/Memory/GpuBuffer.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a GPU-allocated memory buffer in Vulkan (such as a vertex buffer, index buffer, 
 *   uniform buffer, or compute storage buffer). It wraps the raw Vulkan VkBuffer handle together 
 *   with its VMA (Vulkan Memory Allocator) allocation metadata into a safe, object-oriented, 
 *   disposable C# object.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Encapsulation:
 *      Instead of manually passing around a VkBuffer, a VmaAllocation handle, and a VmaAllocationInfo 
 *      struct everywhere in your rendering code, GpuBuffer binds them into a single coherent unit.
 *
 *   2. Direct Memory Mapping:
 *      When allocated with CPU-visible flags (such as VmaMemoryUsage.CpuToGpu with MappedBit), 
 *      the buffer holds a direct pointer to host memory (MappedData). You can upload data to the 
 *      GPU using high-speed SIMD memory copies via SetData<T>().
 *
 *   3. Cache Coherency & Flushing:
 *      On architectures where host memory is not implicitly cache-coherent, the Flush() method 
 *      ensures CPU-written cache lines are committed directly into GPU-accessible VRAM.
 *
 *   4. Safe Lifecycle (IDisposable):
 *      When Dispose() is called (or when exiting a C# using block), the buffer automatically 
 *      destroys the underlying VkBuffer and returns its suballocated memory slice back to the 
 *      GpuAllocator's memory pool.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Allocator: The parent GpuAllocator that owns this buffer's memory pool.
 *   - Handle: The raw native Vulkan VkBuffer handle used in Vulkan command buffers.
 *   - Allocation: The VMA handle tracking the memory slice in GPU/CPU RAM.
 *   - AllocationInfo: Detailed metadata about the allocation (offset, size, mapped pointer).
 *   - SizeInBytes: The total capacity of this buffer in bytes.
 *   - Usage: Vulkan usage flags (VertexBufferBit, IndexBufferBit, UniformBufferBit, etc.).
 *   - MemoryUsage: VMA heap type (GpuOnly, CpuToGpu, GpuToCpu).
 *   - IsMapped: Returns true if the buffer has an active pointer to CPU address space.
 *   - MappedData: Raw unsafe pointer to the mapped memory (null if not mapped).
 *   - SetData<T>: Fast generic method to copy an array or ReadOnlySpan<T> into the buffer.
 *   - Flush: Flushes CPU cache lines to GPU memory for non-coherent memory types.
 *
 * USAGE EXAMPLE:
 *   // 1. Allocate a vertex buffer directly through the allocator
 *   using var vertexBuffer = allocator.CreateBuffer(
 *       size: (ulong)(sizeof(Vertex) * vertices.Length),
 *       usage: VkBufferUsageFlags.VertexBufferBit,
 *       memoryUsage: VmaMemoryUsage.CpuToGpu,
 *       flags: VmaAllocationCreateFlags.MappedBit
 *   );
 *
 *   // 2. Upload vertex data cleanly
 *   vertexBuffer.SetData(vertices);
 *
 *   // 3. Bind in your command buffer
 *   commandBuffer.BindVertexBuffers(0, [vertexBuffer.Handle], [new ByteSize64(0)]);
 * ================================================================================================= */

using System;
using System.Runtime.CompilerServices;
using Brutal;
using Brutal.Pointers;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace Jatayu.RHI.Vulkan.Memory;

/// <summary>
/// Represents a GPU-allocated buffer (vertex, index, uniform, or storage) managed by VMA.
/// </summary>
public sealed class GpuBuffer : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the parent GPU allocator that created and owns this buffer.
    /// </summary>
    public GpuAllocator Allocator { get; }

    /// <summary>
    /// Gets the native Vulkan buffer handle.
    /// </summary>
    public VkBuffer Handle { get; }

    /// <summary>
    /// Gets the VMA allocation handle for tracking and freeing memory.
    /// </summary>
    public VmaAllocation Allocation { get; }

    /// <summary>
    /// Gets detailed VMA allocation metadata (mapped pointer, offset, block).
    /// </summary>
    public VmaAllocationInfo AllocationInfo { get; }

    /// <summary>
    /// Gets the total capacity of the buffer in bytes.
    /// </summary>
    public ulong SizeInBytes { get; }

    /// <summary>
    /// Gets the Vulkan buffer usage flags (e.g. VertexBufferBit, UniformBufferBit).
    /// </summary>
    public VkBufferUsageFlags Usage { get; }

    /// <summary>
    /// Gets the memory heap placement (e.g. GpuOnly, CpuToGpu).
    /// </summary>
    public VmaMemoryUsage MemoryUsage { get; }

    /// <summary>
    /// Gets a value indicating whether this buffer has memory mapped into CPU address space.
    /// </summary>
    public unsafe bool IsMapped => (void*)AllocationInfo.PMappedData != null;

    /// <summary>
    /// Gets the raw native pointer to host-mapped memory, or null if the buffer is not mapped.
    /// </summary>
    public unsafe void* MappedData => (void*)AllocationInfo.PMappedData;

    /// <summary>
    /// Constructs a new GpuBuffer wrapper. Typically called internally by GpuAllocator.
    /// </summary>
    internal GpuBuffer(
        GpuAllocator allocator,
        VkBuffer handle,
        VmaAllocation allocation,
        in VmaAllocationInfo allocationInfo,
        ulong sizeInBytes,
        VkBufferUsageFlags usage,
        VmaMemoryUsage memoryUsage)
    {
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        Handle = handle;
        Allocation = allocation;
        AllocationInfo = allocationInfo;
        SizeInBytes = sizeInBytes;
        Usage = usage;
        MemoryUsage = memoryUsage;
    }

    /// <summary>
    /// Copies a span of typed values into this buffer at the specified byte offset.
    /// The buffer must be mapped (created with MappedBit or CpuToGpu).
    /// </summary>
    /// <typeparam name="T">Unmanaged structure type (e.g., Vertex, Matrix4x4, float).</typeparam>
    /// <param name="data">The source elements to copy.</param>
    /// <param name="offsetInBytes">Target offset in bytes from the start of the buffer.</param>
    public unsafe void SetData<T>(ReadOnlySpan<T> data, ulong offsetInBytes = 0) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
            return;

        ulong elementSize = (ulong)sizeof(T);
        ulong byteCount;
        try
        {
            byteCount = checked((ulong)data.Length * elementSize);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Data size exceeds supported buffer limits.");
        }

        if (offsetInBytes > SizeInBytes || byteCount > SizeInBytes - offsetInBytes)
            throw new ArgumentOutOfRangeException(nameof(data), "Data size exceeds the buffer capacity.");

        if (MappedData != null)
        {
            var destination = new Span<T>((byte*)MappedData + offsetInBytes, data.Length);
            data.CopyTo(destination);
        }
        else
        {
            // If not persistently mapped, copy using VMA copy helper
            fixed (T* pSource = data)
            {
                Allocator.Handle.CopyMemoryToAllocation(
                    new Ptr(pSource),
                    Allocation,
                    new ByteSize((nuint)offsetInBytes),
                    new ByteSize((nuint)byteCount)
                );
            }
        }
    }

    /// <summary>
    /// Flushes all CPU writes to GPU memory for the entire buffer.
    /// </summary>
    public void Flush()
    {
        Allocator.FlushAllocation(Allocation);
    }

    /// <summary>
    /// Flushes a specific range of CPU writes to GPU memory for non-coherent memory types.
    /// </summary>
    /// <param name="offset">Byte offset to start flushing.</param>
    /// <param name="size">Number of bytes to flush.</param>
    public void Flush(ulong offset, ulong size)
    {
        Allocator.FlushAllocation(Allocation, offset, size);
    }

    /// <summary>
    /// Safely frees the Vulkan buffer and returns its memory to the VMA pool.
    /// </summary>
    public void Dispose()
    {
        lock (this)
        {
            if (_disposed)
                return;

            if (Handle.VkHandle != 0)
            {
                Allocator.DestroyBuffer(Handle, Allocation);
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
