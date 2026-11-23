/* =================================================================================================
 * FILE: UploadManager.cs
 * LOCATION: src/RHI/Vulkan/Upload/UploadManager.cs
 * 
 * WHAT THIS FILE DOES:
 *   Coordinates asynchronous GPU memory uploads from CPU host memory to device-local VRAM.
 *   Uses a dedicated transfer queue, circular StagingRing buffer, ring of reset command pools,
 *   and Vulkan 1.3 Timeline Semaphore.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. CPU-to-GPU Staging:
 *      Allocates contiguous memory inside StagingRing (zero GC allocations).
 *   2. Command Recording & Batching:
 *      Records vkCmdCopyBuffer or vkCmdCopyBufferToImage into batched transfer command buffers
 *      allocated from a circular ring of CommandPoolManagers.
 *   3. Command Pool Recycling:
 *      Recycles and resets command pools after the associated batch timeline value has retired
 *      on the GPU timeline, eliminating command buffer leaks without per-frame allocations.
 *   4. Timeline Progression:
 *      Submits batched commands to TransferQueue with monotonically increasing timeline signals.
 * ================================================================================================= */

using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.AssetFormats.Loaders;
using Jatayu.RHI.Vulkan.Commands;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Sync;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.RHI.Vulkan.Upload;

/// <summary>
/// Asynchronous GPU transfer coordinator using a dedicated transfer queue, staging ring, and recycled command pools.
/// </summary>
public sealed class UploadManager : IDisposable
{
    private sealed class CommandPoolSlot : IDisposable
    {
        public readonly CommandPoolManager Pool;
        public CommandBuffer? ActiveCommandBuffer;
        public ulong RetiredTimelineValue;

        public CommandPoolSlot(VulkanContext context, int queueFamily)
        {
            Pool = new CommandPoolManager(context, queueFamily, VkCommandPoolCreateFlags.ResetCommandBufferBit);
            ActiveCommandBuffer = null;
            RetiredTimelineValue = 0;
        }

        public void Dispose()
        {
            Pool.Dispose();
        }
    }

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly StagingRing _stagingRing;
    private readonly TimelineSemaphore _timelineSemaphore;
    private readonly Queue _transferQueue;
    private readonly CommandPoolSlot[] _poolSlots;
    private readonly object _lock = new();

    private int _currentSlotIndex;
    private ulong _currentTimelineValue;
    private bool _disposed;

    /// <summary>
    /// Gets the parent Vulkan context.
    /// </summary>
    public VulkanContext Context => _context;

    /// <summary>
    /// Gets the underlying circular staging ring.
    /// </summary>
    public StagingRing StagingRing => _stagingRing;

    /// <summary>
    /// Gets the timeline semaphore tracking GPU transfer completion.
    /// </summary>
    public TimelineSemaphore Timeline => _timelineSemaphore;

    /// <summary>
    /// Gets the most recently submitted timeline semaphore value.
    /// </summary>
    public ulong LastSubmittedValue => _currentTimelineValue;

    /// <summary>
    /// Initializes a new UploadManager with a ring of recycled command pools.
    /// </summary>
    public UploadManager(VulkanContext context, GpuAllocator allocator, ulong stagingRingBytes = 64 * 1024 * 1024)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        _timelineSemaphore = new TimelineSemaphore(context, 0);
        _stagingRing = new StagingRing(allocator, _timelineSemaphore, stagingRingBytes);

        var queueFamily = context.QueueFamilies.TransferFamily ?? context.QueueFamilies.GraphicsFamily!.Value;
        _transferQueue = context.TransferQueue;

        const int poolCount = 4;
        _poolSlots = new CommandPoolSlot[poolCount];
        for (int i = 0; i < poolCount; i++)
        {
            _poolSlots[i] = new CommandPoolSlot(context, queueFamily);
        }

        _currentSlotIndex = 0;
        _currentTimelineValue = 0;
    }

    /// <summary>
    /// Asynchronously uploads data into a destination GPU buffer.
    /// </summary>
    /// <param name="target">The destination GpuBuffer in VRAM.</param>
    /// <param name="dstOffset">Byte offset within destination buffer.</param>
    /// <param name="data">Source data bytes to copy.</param>
    /// <returns>Timeline semaphore value that will be signaled when the upload finishes on the GPU.</returns>
    public unsafe ulong UploadBuffer(GpuBuffer target, ulong dstOffset, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(target);

        if (data.IsEmpty)
            return _currentTimelineValue;

        var size = (ulong)data.Length;

        // 1. Allocate staging slice
        _stagingRing.Allocate(size, 16, out var stagingOffset, out var mappedPtr);

        // 2. Copy CPU bytes into mapped staging memory
        fixed (byte* srcPtr = data)
        {
            Buffer.MemoryCopy(srcPtr, mappedPtr, size, size);
        }

        lock (_lock)
        {
            var cmd = GetOrCreateActiveCommandBuffer();

            var copyRegion = new VkBufferCopy
            {
                SrcOffset = new ByteSize64(stagingOffset),
                DstOffset = new ByteSize64(dstOffset),
                Size = new ByteSize64(size)
            };

            _context.Device.PInvoke.vkCmdCopyBuffer.Invoke(
                cmd.Handle,
                _stagingRing.BufferHandle,
                target.Handle,
                1,
                &copyRegion
            );

            _currentTimelineValue++;
            SubmitBatch_Locked(cmd, _currentTimelineValue, stagingOffset, size);
            return _currentTimelineValue;
        }
    }

    /// <summary>
    /// Asynchronously uploads pixel data to a 2D GPU texture.
    /// </summary>
    /// <param name="texture">Target GPU texture.</param>
    /// <param name="pixelData">Raw pixel bytes.</param>
    /// <param name="finalLayout">Final image layout (default: ShaderReadOnlyOptimal).</param>
    /// <returns>Timeline semaphore value that will be signaled when upload completes.</returns>
    public unsafe ulong UploadTexture2D(
        GpuTexture texture,
        ReadOnlySpan<byte> pixelData,
        VkImageLayout finalLayout = VkImageLayout.ShaderReadOnlyOptimal)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);

        var size = (ulong)pixelData.Length;

        // 1. Allocate staging memory with 256-byte alignment
        _stagingRing.Allocate(size, 256, out var stagingOffset, out var mappedPtr);

        // 2. Copy pixels into staging buffer
        fixed (byte* src = pixelData)
        {
            Buffer.MemoryCopy(src, mappedPtr, size, size);
        }

        lock (_lock)
        {
            var cmd = GetOrCreateActiveCommandBuffer();

            // Transition image Undefined -> TransferDstOptimal
            texture.TransitionLayout(
                cmd,
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit,
                VkPipelineStageFlags.TransferBit
            );

            // Generate copy regions for each mip level
            int mipCount = Math.Max(1, texture.MipLevels);
            var regions = new VkBufferImageCopy[mipCount];
            ulong currentBufferOffset = stagingOffset;

            int mipW = texture.Width;
            int mipH = texture.Height;

            // Determine format byte-size per pixel / block
            int bytesPerPixel = GetBytesPerPixel(texture.Format);

            for (int mip = 0; mip < mipCount; mip++)
            {
                regions[mip] = new VkBufferImageCopy
                {
                    BufferOffset = new ByteSize64(currentBufferOffset),
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new VkImageSubresourceLayers
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        MipLevel = mip,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new VkOffset3D(0, 0, 0),
                    ImageExtent = new VkExtent3D(mipW, mipH, 1),
                };

                ulong mipBytes = (ulong)(mipW * mipH * bytesPerPixel);
                currentBufferOffset += mipBytes;

                // Stop if provided pixelData ends before higher mips (e.g. only base mip was packed)
                if (currentBufferOffset - stagingOffset > size)
                {
                    // Fallback: only copy up to mips that fit in pixelData
                    mipCount = mip + 1;
                    break;
                }

                mipW = Math.Max(1, mipW / 2);
                mipH = Math.Max(1, mipH / 2);
            }

            fixed (VkBufferImageCopy* pRegions = regions)
            {
                _context.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(
                    cmd.Handle,
                    _stagingRing.BufferHandle,
                    texture.Image,
                    VkImageLayout.TransferDstOptimal,
                    mipCount,
                    pRegions
                );
            }

            // Transition image TransferDstOptimal -> finalLayout
            // On transfer queue families, fragment/compute shader stages are invalid.
            // Use TransferBit for valid transfer queue pipeline synchronization.
            texture.TransitionLayout(
                cmd,
                finalLayout,
                VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.TransferBit
            );

            _currentTimelineValue++;
            SubmitBatch_Locked(cmd, _currentTimelineValue, stagingOffset, size);
            return _currentTimelineValue;
        }
    }

    /// <summary>
    /// Creates and asynchronously uploads a 2D texture from loaded image data.
    /// </summary>
    public GpuTexture CreateTextureFromImageData(
        LoadedImageData imageData,
        out ulong uploadTimelineValue,
        VkImageLayout finalLayout = VkImageLayout.ShaderReadOnlyOptimal)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        var format = GpuTexture.MapPixelFormat(imageData.Format);
        var texture = GpuTexture.Create2D(
            _context,
            _allocator,
            imageData.Width,
            imageData.Height,
            format,
            VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
            imageData.MipLevels
        );

        uploadTimelineValue = UploadTexture2D(texture, imageData.Data, finalLayout);
        return texture;
    }

    /// <summary>
    /// Blocks until the transfer queue reaches or exceeds the specified timeline value.
    /// </summary>
    public bool WaitForUpload(ulong timelineValue, nint timeoutNanoseconds = -1)
    {
        ThrowIfDisposed();
        return _timelineSemaphore.Wait(timelineValue, timeoutNanoseconds);
    }

    /// <summary>
    /// Checks if a given upload operation has completed on the GPU timeline.
    /// </summary>
    public bool IsUploadComplete(ulong timelineValue)
    {
        ThrowIfDisposed();
        return _timelineSemaphore.GetCurrentValue() >= timelineValue;
    }

    private CommandBuffer GetOrCreateActiveCommandBuffer()
    {
        var slot = _poolSlots[_currentSlotIndex];
        if (slot.ActiveCommandBuffer.HasValue)
        {
            return slot.ActiveCommandBuffer.Value;
        }

        // Check if this slot was previously submitted and wait/reset if needed
        if (slot.RetiredTimelineValue > 0)
        {
            if (_timelineSemaphore.GetCurrentValue() < slot.RetiredTimelineValue)
            {
                _timelineSemaphore.Wait(slot.RetiredTimelineValue);
            }
            slot.Pool.Reset(VkCommandPoolResetFlags.None);
        }

        var cmd = slot.Pool.Allocate(VkCommandBufferLevel.Primary);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        slot.ActiveCommandBuffer = cmd;
        return cmd;
    }

    private unsafe void SubmitBatch_Locked(CommandBuffer cmd, ulong signalValue, ulong stagingOffset, ulong size)
    {
        cmd.End();

        var slot = _poolSlots[_currentSlotIndex];
        slot.ActiveCommandBuffer = null;
        slot.RetiredTimelineValue = signalValue;

        var cmdHandle = cmd.Handle;
        var sem = _timelineSemaphore.Handle;
        var sigVal = (nint)signalValue;

        var timelineInfo = new VkTimelineSemaphoreSubmitInfo
        {
            SignalSemaphoreValueCount = 1,
            SignalSemaphoreValues = &sigVal
        };

        var submitInfo = new VkSubmitInfo
        {
            Next = &timelineInfo,
            CommandBufferCount = 1,
            CommandBuffers = &cmdHandle,
            SignalSemaphoreCount = 1,
            SignalSemaphores = &sem
        };

        var res = _context.Device.PInvoke.vkQueueSubmit.Invoke(_transferQueue.Handle, 1, &submitInfo, default);
        if (res != VkResult.Success)
            throw new InvalidOperationException($"vkQueueSubmit failed on transfer queue: {res}");

        // Track in-flight slice in StagingRing
        _stagingRing.SubmitSlice(stagingOffset, size, signalValue);

        // Advance to next command pool slot in the ring
        _currentSlotIndex = (_currentSlotIndex + 1) % _poolSlots.Length;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _context.WaitIdle();

            for (int i = 0; i < _poolSlots.Length; i++)
            {
                _poolSlots[i].Dispose();
            }

            _stagingRing.Dispose();
            _timelineSemaphore.Dispose();
        }
    }

    private static int GetBytesPerPixel(VkFormat format)
    {
        return format switch
        {
            VkFormat.R8UNorm => 1,
            VkFormat.R8G8B8A8UNorm or VkFormat.R8G8B8A8SRGB => 4,
            VkFormat.R16UNorm => 2,
            VkFormat.R32SFloat => 4,
            VkFormat.Bc7UNormBlock or VkFormat.Bc7SRGBBlock or VkFormat.Bc5UNormBlock or VkFormat.Bc4UNormBlock => 1,
            _ => 4,
        };
    }
}
