/* =================================================================================================
 * FILE: BarrierBuilder.cs
 * LOCATION: src/Renderer/Graph/BarrierBuilder.cs
 * 
 * WHAT THIS FILE DOES:
 *   Vulkan 1.3 VK_KHR_synchronization2 barrier compiler.
 *   Accumulates image memory transitions and dispatches batched VkDependencyInfo structures
 *   with VkImageMemoryBarrier2 descriptors with zero GC heap allocations on the hot path.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Jatayu.Renderer.Graph;

/// <summary>
/// Batches and emits Vulkan 1.3 synchronization2 pipeline barriers.
/// </summary>
public sealed class BarrierBuilder
{
    private VkImageMemoryBarrier2[] _imageBarriers = new VkImageMemoryBarrier2[32];
    private int _imageBarrierCount;

    /// <summary>
    /// Gets the number of pending image memory barriers.
    /// </summary>
    public int ImageBarrierCount => _imageBarrierCount;

    /// <summary>
    /// Gets whether any barriers are currently queued.
    /// </summary>
    public bool HasBarriers => _imageBarrierCount > 0;

    /// <summary>
    /// Clears all queued barriers without reallocating arrays.
    /// </summary>
    public void Clear()
    {
        _imageBarrierCount = 0;
    }

    /// <summary>
    /// Queues an image layout transition and stage/access synchronization barrier.
    /// </summary>
    public void AddImageBarrier(
        VkImage image,
        VkImageAspectFlags aspect,
        VkImageLayout oldLayout,
        VkImageLayout newLayout,
        VkPipelineStageFlags2 srcStage,
        VkAccessFlags2 srcAccess,
        VkPipelineStageFlags2 dstStage,
        VkAccessFlags2 dstAccess)
    {
        if (_imageBarrierCount >= _imageBarriers.Length)
        {
            Array.Resize(ref _imageBarriers, _imageBarriers.Length * 2);
        }

        _imageBarriers[_imageBarrierCount++] = new VkImageMemoryBarrier2
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
    }

    /// <summary>
    /// Dispatches all queued barriers to the command buffer using vkCmdPipelineBarrier2 and resets the queue.
    /// </summary>
    public unsafe void Execute(CommandBuffer cmd)
    {
        if (_imageBarrierCount == 0)
            return;

        fixed (VkImageMemoryBarrier2* pBarriers = _imageBarriers)
        {
            var depInfo = new VkDependencyInfo
            {
                DependencyFlags = VkDependencyFlags.None,
                ImageMemoryBarrierCount = _imageBarrierCount,
                ImageMemoryBarriers = pBarriers,
            };

            cmd.PipelineBarrier2(in depInfo);
        }

        _imageBarrierCount = 0;
    }
}
