/* =================================================================================================
 * FILE: GpuAllocator.cs
 * LOCATION: src/RHI/Vulkan/Memory/GpuAllocator.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages all GPU memory allocations (buffers and textures/images) for our Vulkan engine 
 *   using the industry-standard AMD Vulkan Memory Allocator (VMA) library.
 *   
 *   In Vulkan, raw GPU memory allocation directly from the driver (via vkAllocateMemory) is 
 *   strictly limited by hardware (often capped at 4,096 total allocations per GPU) and is 
 *   extremely slow. This class acts as a high-performance sub-allocator: it requests large 
 *   memory blocks (e.g., 64MB or 256MB) directly from the GPU driver, and then quickly carves out 
 *   the exact byte ranges needed for vertex buffers, index buffers, uniform buffers, and textures.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. VMA Initialization:
 *      Takes an active VulkanContext (which supplies the Vulkan Instance, Physical Device, and 
 *      Logical Device) and creates a VmaAllocator configured for Vulkan 1.3.
 *
 *   2. Fast Sub-Allocation:
 *      When you request a buffer or image, VMA finds the best-fit free slot inside an existing 
 *      allocated memory block or creates a new block if needed.
 *
 *   3. Memory Usage Types:
 *      - GpuOnly: Fastest memory on dedicated VRAM (ideal for static geometry, textures, render targets).
 *      - CpuToGpu: Host-visible memory that the CPU can write to directly (ideal for dynamic uniform 
 *        buffers, camera data, transform matrices).
 *      - GpuToCpu: Memory that the GPU writes and CPU reads back (ideal for screenshots, compute output).
 *
 *   4. Direct Memory Mapping & Uploads:
 *      Provides convenient high-level methods (e.g. CreateBufferWithData<T>) and object-oriented 
 *      GpuBuffer abstractions so memory is automatically mapped, uploaded, and tracked without 
 *      manual pointer arithmetic or leaks.
 *
 *   5. Safe Disposal:
 *      Implements IDisposable to ensure all allocations are tracked and the native VMA allocator 
 *      is destroyed cleanly when the engine shuts down.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Context: The parent VulkanContext providing GPU handles.
 *   - Handle: The underlying native VmaAllocator instance.
 *   - CreateBuffer: Allocates a managed GpuBuffer or raw VkBuffer with custom size and usage.
 *   - CreateBufferWithData<T>: Allocates a buffer and immediately uploads a span of data to it.
 *   - DestroyBuffer: Safely frees a buffer and returns its memory to the VMA pool.
 *   - CreateImage: Allocates a raw VkImage with its underlying backing memory.
 *   - DestroyImage: Safely frees a VkImage and returns its memory to the VMA pool.
 *   - MapMemory: Maps an allocation into CPU address space and returns a native pointer.
 *   - UnmapMemory: Unmaps a previously mapped memory allocation.
 *   - FlushAllocation: Flushes CPU writes to GPU memory if the memory is not host-coherent.
 *
 * USAGE EXAMPLES:
 *   // Example A: Object-oriented GpuBuffer creation with data upload
 *   using var allocator = new GpuAllocator(context);
 *   using var vertexBuffer = allocator.CreateBufferWithData<Vertex>(
 *       vertices,
 *       VkBufferUsageFlags.VertexBufferBit
 *   );
 *
 *   // Example B: Raw low-level allocation
 *   allocator.CreateBuffer(
 *       size: 1024,
 *       usage: VkBufferUsageFlags.UniformBufferBit,
 *       memoryUsage: VmaMemoryUsage.CpuToGpu,
 *       flags: VmaAllocationCreateFlags.MappedBit,
 *       out VkBuffer buffer,
 *       out VmaAllocation alloc,
 *       out VmaAllocationInfo info
 *   );
 *   allocator.DestroyBuffer(buffer, alloc);
 * ================================================================================================= */

using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using HelloBrutal.RHI.Vulkan.Context;

namespace HelloBrutal.RHI.Vulkan.Memory;

/// <summary>
/// High-performance GPU memory manager wrapping the AMD Vulkan Memory Allocator (VMA).
/// Handles buffer and image allocations, memory pooling, and host mapping.
/// </summary>
public sealed class GpuAllocator : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the parent VulkanContext.
    /// </summary>
    public VulkanContext Context { get; }

    /// <summary>
    /// Gets the underlying VMA allocator handle.
    /// </summary>
    public VmaAllocator Handle { get; }

    /// <summary>
    /// Initializes a new instance of the GPU memory allocator.
    /// </summary>
    /// <param name="context">The active VulkanContext.</param>
    /// <param name="flags">Optional VMA allocator configuration flags.</param>
    public GpuAllocator(VulkanContext context, VmaAllocatorCreateFlags flags = VmaAllocatorCreateFlags.None)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        var createInfo = new VmaAllocatorCreateInfo
        {
            Flags = flags,
            Instance = context.Instance,
            PhysicalDevice = context.PhysicalDevice,
            Device = context.Device,
            VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
        };

        Handle = context.Device.CreateVmaAllocator(createInfo);
    }

    /// <summary>
    /// Creates and returns a high-level managed GpuBuffer.
    /// </summary>
    /// <param name="size">Total size of the buffer in bytes.</param>
    /// <param name="usage">Vulkan buffer usage flags (e.g. VertexBufferBit, UniformBufferBit).</param>
    /// <param name="memoryUsage">Desired memory heap type (e.g. GpuOnly, CpuToGpu).</param>
    /// <param name="flags">Optional allocation flags (e.g. MappedBit).</param>
    /// <returns>A new managed GpuBuffer instance.</returns>
    public GpuBuffer CreateBuffer(
        ulong size,
        VkBufferUsageFlags usage,
        VmaMemoryUsage memoryUsage,
        VmaAllocationCreateFlags flags = VmaAllocationCreateFlags.None)
    {
        CreateBuffer(size, usage, memoryUsage, flags, out var buffer, out var allocation, out var allocationInfo);
        return new GpuBuffer(this, buffer, allocation, allocationInfo, size, usage, memoryUsage);
    }

    /// <summary>
    /// Creates a GPU buffer pre-populated with the provided data span.
    /// </summary>
    /// <typeparam name="T">Unmanaged data type.</typeparam>
    /// <param name="data">The source data to upload.</param>
    /// <param name="usage">Vulkan buffer usage flags.</param>
    /// <param name="memoryUsage">Memory heap type (defaults to CpuToGpu for direct CPU upload).</param>
    /// <returns>A new managed GpuBuffer pre-loaded with data.</returns>
    public unsafe GpuBuffer CreateBufferWithData<T>(
        ReadOnlySpan<T> data,
        VkBufferUsageFlags usage,
        VmaMemoryUsage memoryUsage = VmaMemoryUsage.CpuToGpu) where T : unmanaged
    {
        var sizeInBytes = (ulong)(data.Length * sizeof(T));
        var flags = memoryUsage == VmaMemoryUsage.CpuToGpu
            ? VmaAllocationCreateFlags.MappedBit
            : VmaAllocationCreateFlags.None;

        var buffer = CreateBuffer(sizeInBytes, usage, memoryUsage, flags);
        buffer.SetData(data);
        return buffer;
    }

    /// <summary>
    /// Allocates a raw Vulkan buffer with the requested size, usage flags, and memory heap placement.
    /// </summary>
    /// <param name="size">Total size of the buffer in bytes.</param>
    /// <param name="usage">Vulkan buffer usage flags (e.g., VertexBufferBit, IndexBufferBit, UniformBufferBit).</param>
    /// <param name="memoryUsage">Desired VMA memory type (e.g., GpuOnly, CpuToGpu, GpuToCpu).</param>
    /// <param name="flags">Allocation flags (e.g., MappedBit to automatically map host memory).</param>
    /// <param name="buffer">Outputs the created Vulkan buffer handle.</param>
    /// <param name="allocation">Outputs the VMA allocation handle used to track and free memory.</param>
    /// <param name="allocationInfo">Outputs details about the allocation, including mapped pointers and offset.</param>
    public void CreateBuffer(
        ulong size,
        VkBufferUsageFlags usage,
        VmaMemoryUsage memoryUsage,
        VmaAllocationCreateFlags flags,
        out VkBuffer buffer,
        out VmaAllocation allocation,
        out VmaAllocationInfo allocationInfo)
    {
        var bufferCreateInfo = new VkBufferCreateInfo
        {
            Size = new ByteSize64(size),
            Usage = usage,
            SharingMode = VkSharingMode.Exclusive,
        };

        var allocationCreateInfo = new VmaAllocationCreateInfo
        {
            Usage = memoryUsage,
            Flags = flags,
        };

        Handle.CreateBuffer(
            bufferCreateInfo,
            allocationCreateInfo,
            out buffer,
            out allocation,
            out allocationInfo
        );
    }

    /// <summary>
    /// Allocates a raw Vulkan buffer with default allocation flags.
    /// </summary>
    public void CreateBuffer(
        ulong size,
        VkBufferUsageFlags usage,
        VmaMemoryUsage memoryUsage,
        out VkBuffer buffer,
        out VmaAllocation allocation,
        out VmaAllocationInfo allocationInfo)
    {
        CreateBuffer(size, usage, memoryUsage, VmaAllocationCreateFlags.None, out buffer, out allocation, out allocationInfo);
    }

    /// <summary>
    /// Destroys a managed GpuBuffer and releases its memory.
    /// </summary>
    /// <param name="buffer">The GpuBuffer to destroy.</param>
    public void DestroyBuffer(GpuBuffer buffer)
    {
        buffer?.Dispose();
    }

    /// <summary>
    /// Destroys a raw Vulkan buffer and returns its underlying memory back to the VMA pool.
    /// </summary>
    /// <param name="buffer">The Vulkan buffer to destroy.</param>
    /// <param name="allocation">The VMA allocation handle associated with this buffer.</param>
    public void DestroyBuffer(VkBuffer buffer, VmaAllocation allocation)
    {
        if (buffer.VkHandle != 0 && !allocation.IsNull())
        {
            Handle.DestroyBuffer(buffer, allocation);
        }
    }

    /// <summary>
    /// Allocates a Vulkan image (texture, render target, depth buffer) backed by VMA memory.
    /// </summary>
    /// <param name="imageInfo">Vulkan image creation description.</param>
    /// <param name="memoryUsage">Desired memory heap placement (typically GpuOnly for rendering textures).</param>
    /// <param name="flags">Allocation flags.</param>
    /// <param name="image">Outputs the created Vulkan image handle.</param>
    /// <param name="allocation">Outputs the VMA allocation handle.</param>
    /// <param name="allocationInfo">Outputs allocation details.</param>
    public void CreateImage(
        in VkImageCreateInfo imageInfo,
        VmaMemoryUsage memoryUsage,
        VmaAllocationCreateFlags flags,
        out VkImage image,
        out VmaAllocation allocation,
        out VmaAllocationInfo allocationInfo)
    {
        var allocCreateInfo = new VmaAllocationCreateInfo
        {
            Usage = memoryUsage,
            Flags = flags,
        };

        Handle.CreateImage(
            imageInfo,
            allocCreateInfo,
            out image,
            out allocation,
            out allocationInfo
        );
    }

    /// <summary>
    /// Destroys a Vulkan image and returns its memory back to the VMA pool.
    /// </summary>
    /// <param name="image">The Vulkan image handle to destroy.</param>
    /// <param name="allocation">The VMA allocation handle associated with this image.</param>
    public void DestroyImage(VkImage image, VmaAllocation allocation)
    {
        if (image.VkHandle != 0 && !allocation.IsNull())
        {
            Handle.DestroyImage(image, allocation);
        }
    }

    /*
    /// <summary>
    /// Maps host-visible GPU memory into CPU address space.
    /// </summary>
    /// <param name="allocation">The allocation to map.</param>
    /// <returns>A native pointer to the mapped memory.</returns>
    public unsafe void* MapMemory(VmaAllocation allocation)
    {
        Handle.MapMemory(allocation, out var pData);
        return (void*)pData;
    }

    /// <summary>
    /// Unmaps a previously mapped memory allocation.
    /// </summary>
    /// <param name="allocation">The allocation to unmap.</param>
    public void UnmapMemory(VmaAllocation allocation)
    {
        Handle.UnmapMemory(allocation);
    }
    */

    /// <summary>
    /// Flushes all CPU writes to GPU memory for the entire allocation if the memory is non-coherent.
    /// </summary>
    /// <param name="allocation">The allocation to flush.</param>
    public void FlushAllocation(VmaAllocation allocation)
    {
        Handle.FlushAllocation(allocation, new ByteSize((nuint)0), new ByteSize((nuint)VK.WHOLE_SIZE));
    }

    /// <summary>
    /// Flushes a specific byte range of CPU writes to GPU memory if the memory is non-coherent.
    /// </summary>
    /// <param name="allocation">The allocation to flush.</param>
    /// <param name="offset">Byte offset within the allocation to start flushing.</param>
    /// <param name="size">Number of bytes to flush.</param>
    public void FlushAllocation(VmaAllocation allocation, ulong offset, ulong size)
    {
        Handle.FlushAllocation(allocation, new ByteSize((nuint)offset), new ByteSize((nuint)size));
    }

    /// <summary>
    /// Disposes the GPU memory allocator and frees all native VMA resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Context.WaitIdle();
            Context.Device.DestroyVmaAllocator(Handle);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
