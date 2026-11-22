/* =================================================================================================
 * FILE: UploadManager.cs
 * LOCATION: src/RHI/Vulkan/Upload/UploadManager.cs
 * 
 * WHAT THIS FILE DOES:
 *   Coordinates asynchronous GPU memory uploads from CPU host memory to device-local VRAM.
 *   Uses a dedicated transfer queue, circular StagingRing buffer, and Vulkan 1.3 Timeline Semaphore.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. CPU-to-GPU Staging:
 *      Allocates contiguous memory inside StagingRing (zero GC allocations).
 *   2. Command Recording:
 *      Records vkCmdCopyBuffer or vkCmdCopyBufferToImage on a dedicated transfer command buffer.
 *   3. Timeline Progression:
 *      Submits work to TransferQueue and signals a monotonically increasing timeline semaphore value.
 *      Callers can query or wait on this value to know when resources are ready on the GPU.
 *   4. Replaces D14 temporary synchronous uploads:
 *      Eliminates blocking vkQueueWaitIdle / fence waits on the main thread during texture and
 *      buffer streaming.
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
/// Asynchronous GPU transfer coordinator using a dedicated transfer queue and staging ring.
/// </summary>
public sealed class UploadManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly StagingRing _stagingRing;
    private readonly TimelineSemaphore _timelineSemaphore;
    private readonly CommandPoolManager _commandPool;
    private readonly Queue _transferQueue;
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
    /// Initializes a new UploadManager.
    /// </summary>
    public UploadManager(VulkanContext context, GpuAllocator allocator, ulong stagingRingBytes = 64 * 1024 * 1024)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        _timelineSemaphore = new TimelineSemaphore(context, 0);
        _stagingRing = new StagingRing(allocator, _timelineSemaphore, stagingRingBytes);

        var queueFamily = context.QueueFamilies.TransferFamily ?? context.QueueFamilies.GraphicsFamily!.Value;
        _transferQueue = context.TransferQueue;
        _commandPool = new CommandPoolManager(context, queueFamily);
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

        // 3. Record transfer command
        var cmd = _commandPool.Allocate(VkCommandBufferLevel.Primary);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

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

        cmd.End();

        // 4. Submit to TransferQueue with TimelineSemaphore signal
        _currentTimelineValue++;
        SubmitTransferCommand(cmd.Handle, _currentTimelineValue);

        // 5. Track in-flight slice in StagingRing
        _stagingRing.SubmitSlice(stagingOffset, size, _currentTimelineValue);

        return _currentTimelineValue;
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

        // 1. Allocate staging memory with 256-byte alignment (optimal for image pitch)
        _stagingRing.Allocate(size, 256, out var stagingOffset, out var mappedPtr);

        // 2. Copy pixels into staging buffer
        fixed (byte* src = pixelData)
        {
            Buffer.MemoryCopy(src, mappedPtr, size, size);
        }

        // 3. Record transfer commands
        var cmd = _commandPool.Allocate(VkCommandBufferLevel.Primary);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        // Transition image Undefined -> TransferDstOptimal
        texture.TransitionLayout(
            cmd,
            VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags.TopOfPipeBit,
            VkPipelineStageFlags.TransferBit
        );

        var region = new VkBufferImageCopy
        {
            BufferOffset = new ByteSize64(stagingOffset),
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new VkOffset3D(0, 0, 0),
            ImageExtent = new VkExtent3D(texture.Width, texture.Height, 1),
        };

        _context.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(
            cmd.Handle,
            _stagingRing.BufferHandle,
            texture.Image,
            VkImageLayout.TransferDstOptimal,
            1,
            &region
        );

        // Transition image TransferDstOptimal -> finalLayout
        texture.TransitionLayout(
            cmd,
            finalLayout,
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit
        );

        cmd.End();

        // 4. Submit to TransferQueue with TimelineSemaphore signal
        _currentTimelineValue++;
        SubmitTransferCommand(cmd.Handle, _currentTimelineValue);

        // 5. Track in-flight slice in StagingRing
        _stagingRing.SubmitSlice(stagingOffset, size, _currentTimelineValue);

        return _currentTimelineValue;
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

    private unsafe void SubmitTransferCommand(VkCommandBuffer cmdHandle, ulong signalValue)
    {
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
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _context.WaitIdle();
        _commandPool.Dispose();
        _stagingRing.Dispose();
        _timelineSemaphore.Dispose();
    }
}
