// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: IndirectDrawBuffer.cs
// Role: Manages GPU indirect command and count buffers for vkCmdDrawIndexedIndirectCount (Sprint 7a).
// ==================================================================================================

using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// Manages multi-view indirect draw command buffers and count buffers populated by GPU compute culling.
/// </summary>
public sealed class IndirectDrawBuffer : IDisposable
{
    public const int DefaultViewCount = 5; // View 0: Main Camera, Views 1..4: Shadow Cascades 0..3
    public const int DefaultMaxDrawsPerView = 65536;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly int _viewCount;
    private readonly int _maxDrawsPerView;

    private readonly GpuBuffer _commandBuffer;
    private readonly GpuBuffer _countBuffer;
    private bool _disposed;

    /// <summary>
    /// Gets the indirect draw commands buffer.
    /// </summary>
    public GpuBuffer CommandBuffer => _commandBuffer;

    /// <summary>
    /// Gets the indirect draw count buffer (one uint per view).
    /// </summary>
    public GpuBuffer CountBuffer => _countBuffer;

    /// <summary>
    /// Gets the number of view partitions configured.
    /// </summary>
    public int ViewCount => _viewCount;

    /// <summary>
    /// Gets the maximum number of draws accommodated per view.
    /// </summary>
    public int MaxDrawsPerView => _maxDrawsPerView;

    /// <summary>
    /// Initializes indirect command and count buffers partitioned across the specified number of views.
    /// </summary>
    public IndirectDrawBuffer(
        VulkanContext context,
        GpuAllocator allocator,
        int viewCount = DefaultViewCount,
        int maxDrawsPerView = DefaultMaxDrawsPerView)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _viewCount = Math.Max(1, viewCount);
        _maxDrawsPerView = Math.Max(1024, maxDrawsPerView);

        ulong commandSize = (ulong)(Marshal.SizeOf<VkDrawIndexedIndirectCommand>() * _viewCount * _maxDrawsPerView);
        _commandBuffer = _allocator.CreateBuffer(
            commandSize,
            VkBufferUsageFlags.IndirectBufferBit | VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );

        ulong countSize = (ulong)(sizeof(uint) * _viewCount);
        _countBuffer = _allocator.CreateBuffer(
            countSize,
            VkBufferUsageFlags.IndirectBufferBit | VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu // CpuToGpu allows CPU inspection or fast zeroing without staging
        );

        ResetCountsCpu();
    }

    /// <summary>
    /// Returns the byte offset into CommandBuffer for the start of the given view partition.
    /// </summary>
    public ByteSize64 GetCommandOffset(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= _viewCount)
            throw new ArgumentOutOfRangeException(nameof(viewIndex));

        return (ByteSize64)((ulong)viewIndex * (ulong)_maxDrawsPerView * (ulong)Marshal.SizeOf<VkDrawIndexedIndirectCommand>());
    }

    /// <summary>
    /// Returns the byte offset into CountBuffer for the draw count of the given view partition.
    /// </summary>
    public ByteSize64 GetCountOffset(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= _viewCount)
            throw new ArgumentOutOfRangeException(nameof(viewIndex));

        return (ByteSize64)((ulong)viewIndex * sizeof(uint));
    }

    /// <summary>
    /// Resets all per-view draw counts to zero from the CPU (zero GC allocations).
    /// </summary>
    public void ResetCountsCpu()
    {
        Span<uint> zeros = stackalloc uint[_viewCount];
        zeros.Clear();
        _countBuffer.SetData<uint>(zeros);
    }

    /// <summary>
    /// Records a GPU fill command to reset all per-view draw counts to zero before compute culling.
    /// </summary>
    public unsafe void ResetCountsGpu(VkCommandBuffer cmd)
    {
        _context.Device.PInvoke.vkCmdFillBuffer.Invoke(
            cmd,
            _countBuffer.Handle,
            new ByteSize64(0),
            (ByteSize64)((ulong)(_viewCount * sizeof(uint))),
            0
        );
    }

    /// <summary>
    /// Emits a Vulkan 1.3 synchronization2 barrier ensuring compute culling writes to indirect buffers are visible to vkCmdDrawIndexedIndirectCount.
    /// </summary>
    public unsafe void EmitExecutionBarrier(VkCommandBuffer cmd)
    {
        var barriers = stackalloc VkBufferMemoryBarrier2[2];
        barriers[0] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = VkAccessFlags2.IndirectCommandReadBit,
            Buffer = _commandBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(_commandBuffer.SizeInBytes),
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        };
        barriers[1] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = VkAccessFlags2.IndirectCommandReadBit,
            Buffer = _countBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(_countBuffer.SizeInBytes),
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        };

        var depInfo = new VkDependencyInfo
        {
            BufferMemoryBarrierCount = 2,
            BufferMemoryBarriers = barriers,
        };

        _context.Device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &depInfo);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _commandBuffer.Dispose();
            _countBuffer.Dispose();
            _disposed = true;
        }
    }
}
