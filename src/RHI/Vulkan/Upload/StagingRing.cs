/* =================================================================================================
 * FILE: StagingRing.cs
 * LOCATION: src/RHI/Vulkan/Upload/StagingRing.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages a circular, persistently mapped host-visible GPU staging buffer (64 MB - 256 MB).
 *   Enables zero-copy CPU-to-GPU data streaming without per-frame allocations or stalls.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Circular Head/Tail Ring:
 *      Maintains a write head where CPU writes take place and a tail representing the furthest
 *      point the GPU has finished reading.
 *   2. Lock-Free & Deadlock-Free Flow:
 *      When the ring nears capacity or needs to wrap around, it checks the GPU timeline semaphore.
 *      If the tail has not advanced far enough, it waits for the GPU to retire the oldest pending
 *      slice, guaranteeing forward progress with zero memory leaks.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Sync;

namespace Jatayu.RHI.Vulkan.Upload;

/// <summary>
/// Persistently mapped circular staging ring buffer for zero-copy CPU-to-GPU memory transfers.
/// </summary>
public sealed class StagingRing : IDisposable
{
    private struct InFlightSegment
    {
        public ulong EndOffset;
        public ulong TimelineValue;
    }

    private readonly GpuBuffer _stagingBuffer;
    private readonly TimelineSemaphore _timelineSemaphore;
    private readonly Queue<InFlightSegment> _inFlightQueue = new(128);

    private ulong _head;
    private ulong _tail;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying GPU buffer handle.
    /// </summary>
    public VkBuffer BufferHandle => _stagingBuffer.Handle;

    /// <summary>
    /// Gets the underlying GpuBuffer object.
    /// </summary>
    public GpuBuffer Buffer => _stagingBuffer;

    /// <summary>
    /// Gets the total capacity of the staging ring in bytes.
    /// </summary>
    public ulong Capacity { get; }

    /// <summary>
    /// Initializes a new circular staging ring buffer.
    /// </summary>
    /// <param name="allocator">The GPU memory allocator.</param>
    /// <param name="timelineSemaphore">The timeline semaphore tracking transfer execution progress.</param>
    /// <param name="capacityBytes">Total ring capacity in bytes (default: 64 MB).</param>
    public StagingRing(GpuAllocator allocator, TimelineSemaphore timelineSemaphore, ulong capacityBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(timelineSemaphore);

        if (capacityBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Staging ring capacity must be at least 1 MB.");

        Capacity = capacityBytes;
        _timelineSemaphore = timelineSemaphore;

        _stagingBuffer = allocator.CreateBuffer(
            size: Capacity,
            usage: VkBufferUsageFlags.TransferSrcBit,
            memoryUsage: VmaMemoryUsage.CpuToGpu,
            flags: VmaAllocationCreateFlags.MappedBit);

        _head = 0;
        _tail = 0;
    }

    /// <summary>
    /// Updates the tail pointer by retiring completed GPU transfers from the timeline semaphore.
    /// </summary>
    public void UpdateGpuProgress()
    {
        var completed = _timelineSemaphore.GetCurrentValue();
        while (_inFlightQueue.Count > 0 && _inFlightQueue.Peek().TimelineValue <= completed)
        {
            var retired = _inFlightQueue.Dequeue();
            _tail = retired.EndOffset;
        }
    }

    /// <summary>
    /// Allocates a contiguous slice of staging memory.
    /// </summary>
    /// <param name="size">Number of bytes needed.</param>
    /// <param name="alignment">Byte alignment boundary (default: 16 bytes).</param>
    /// <param name="stagingOffset">Outputs the offset into the staging ring buffer.</param>
    /// <param name="mappedPtr">Outputs the direct CPU pointer to write data into.</param>
    public unsafe void Allocate(ulong size, ulong alignment, out ulong stagingOffset, out void* mappedPtr)
    {
        ThrowIfDisposed();

        if (size > Capacity)
            throw new ArgumentOutOfRangeException(nameof(size), $"Requested size ({size} bytes) exceeds total ring capacity ({Capacity} bytes).");

        UpdateGpuProgress();

        // 1. Try to allocate after head
        var alignedHead = AlignUp(_head, alignment);

        // Check if allocation fits before end of buffer
        if (alignedHead + size <= Capacity)
        {
            // If tail is ahead of head, check for collision
            if (_tail > _head && alignedHead + size > _tail)
            {
                WaitUntilSpaceAvailable(alignedHead + size);
            }

            stagingOffset = alignedHead;
            _head = alignedHead + size;
            mappedPtr = (byte*)_stagingBuffer.MappedData + stagingOffset;
            return;
        }

        // 2. Wrap-around to start of buffer
        alignedHead = 0;
        if (_tail > 0 && size > _tail)
        {
            WaitUntilSpaceAvailable(size);
        }

        stagingOffset = 0;
        _head = size;
        mappedPtr = _stagingBuffer.MappedData;
    }

    /// <summary>
    /// Records an allocation's association with a GPU timeline submission value.
    /// </summary>
    public void SubmitSlice(ulong stagingOffset, ulong size, ulong timelineValue)
    {
        _inFlightQueue.Enqueue(new InFlightSegment
        {
            EndOffset = stagingOffset + size,
            TimelineValue = timelineValue
        });
    }

    private void WaitUntilSpaceAvailable(ulong requiredTailOffset)
    {
        while (_inFlightQueue.Count > 0)
        {
            var next = _inFlightQueue.Peek();
            _timelineSemaphore.Wait(next.TimelineValue);
            _tail = next.EndOffset;
            _inFlightQueue.Dequeue();

            if (_tail >= requiredTailOffset || _tail < _head)
                return;
        }
    }

    private static ulong AlignUp(ulong val, ulong alignment)
    {
        return (val + alignment - 1) & ~(alignment - 1);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stagingBuffer.Dispose();
    }
}
