/* =================================================================================================
 * FILE: StagingRing.cs
 * LOCATION: src/RHI/Vulkan/Upload/StagingRing.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages a circular, persistently mapped host-visible GPU staging buffer (64 MB - 256 MB).
 *   Enables zero-copy CPU-to-GPU data streaming without per-frame allocations or stalls.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Monotonic 64-Bit Head/Tail Ring:
 *      Tracks monotonically increasing 64-bit logical head and tail offsets:
 *        physicalOffset = logicalOffset % Capacity
 *        freeSpace = Capacity - (logicalHead - logicalTail)
 *      This strictly distinguishes empty (head == tail) from full (head - tail == Capacity).
 *   2. End-of-Buffer Gap Handling:
 *      When an allocation cannot fit between the current physical head and Capacity, the end gap
 *      is padded out by advancing the logical head to the next Capacity-aligned boundary. The gap
 *      is tracked as part of in-flight reservations so it cannot be reclaimed until retired.
 *   3. GPU Timeline Synchronization:
 *      Allocations block on the timeline only when free space is insufficient.
 *      Thread-safe allocation and retirement tracking via synchronization lock.
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
        public ulong LogicalEndOffset;
        public ulong TimelineValue;
    }

    private readonly GpuBuffer? _stagingBuffer;
    private readonly unsafe void* _mappedBasePtr;
    private readonly ITimelineProgress _timeline;
    private readonly Queue<InFlightSegment> _inFlightQueue = new(128);
    private readonly object _lock = new();

    private ulong _logicalHead;
    private ulong _logicalTail;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying GPU buffer handle.
    /// </summary>
    public VkBuffer BufferHandle => _stagingBuffer?.Handle ?? default;

    /// <summary>
    /// Gets the underlying GpuBuffer object, or null in mock test mode.
    /// </summary>
    public GpuBuffer? Buffer => _stagingBuffer;

    /// <summary>
    /// Gets the total capacity of the staging ring in bytes.
    /// </summary>
    public ulong Capacity { get; }

    /// <summary>
    /// Initializes a new circular staging ring buffer backed by GPU VMA memory.
    /// </summary>
    /// <param name="allocator">The GPU memory allocator.</param>
    /// <param name="timelineSemaphore">The timeline semaphore tracking transfer execution progress.</param>
    /// <param name="capacityBytes">Total ring capacity in bytes (default: 64 MB).</param>
    public unsafe StagingRing(GpuAllocator allocator, TimelineSemaphore timelineSemaphore, ulong capacityBytes = 64 * 1024 * 1024)
        : this(allocator, (ITimelineProgress)timelineSemaphore, capacityBytes)
    {
    }

    /// <summary>
    /// Initializes a new circular staging ring buffer backed by GPU VMA memory with an ITimelineProgress seam.
    /// </summary>
    public unsafe StagingRing(GpuAllocator allocator, ITimelineProgress timeline, ulong capacityBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(timeline);

        if (capacityBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Staging ring capacity must be at least 1 MB.");

        Capacity = capacityBytes;
        _timeline = timeline;

        _stagingBuffer = allocator.CreateBuffer(
            size: Capacity,
            usage: VkBufferUsageFlags.TransferSrcBit,
            memoryUsage: VmaMemoryUsage.CpuToGpu,
            flags: VmaAllocationCreateFlags.MappedBit);

        _mappedBasePtr = _stagingBuffer.MappedData;
        _logicalHead = 0;
        _logicalTail = 0;
    }

    /// <summary>
    /// Internal constructor for unit testing without GPU allocations.
    /// </summary>
    internal unsafe StagingRing(ulong capacityBytes, ITimelineProgress timeline, void* mockMappedPtr = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        if (capacityBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Staging ring capacity must be at least 1 MB.");

        Capacity = capacityBytes;
        _timeline = timeline;
        _stagingBuffer = null;
        _mappedBasePtr = mockMappedPtr;
        _logicalHead = 0;
        _logicalTail = 0;
    }

    /// <summary>
    /// Updates the tail pointer by retiring completed GPU transfers from the timeline.
    /// </summary>
    public void UpdateGpuProgress()
    {
        lock (_lock)
        {
            RetireCompletedSegments_Locked(_timeline.GetCurrentValue());
        }
    }

    /// <summary>
    /// Allocates a contiguous slice of staging memory.
    /// </summary>
    /// <param name="size">Number of bytes needed.</param>
    /// <param name="alignment">Byte alignment boundary (default: 16 bytes).</param>
    /// <param name="stagingOffset">Outputs the physical offset into the staging ring buffer.</param>
    /// <param name="mappedPtr">Outputs the direct CPU pointer to write data into.</param>
    public unsafe void Allocate(ulong size, ulong alignment, out ulong stagingOffset, out void* mappedPtr)
    {
        ThrowIfDisposed();

        if (size > Capacity)
            throw new ArgumentOutOfRangeException(nameof(size), $"Requested size ({size} bytes) exceeds total ring capacity ({Capacity} bytes).");

        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("Alignment must be a non-zero power of two.", nameof(alignment));

        lock (_lock)
        {
            // Update progress first
            RetireCompletedSegments_Locked(_timeline.GetCurrentValue());

            // Determine if aligned allocation fits before physical end of ring
            ulong currentPhysicalHead = _logicalHead % Capacity;
            ulong alignedPhysicalHead = AlignUp(currentPhysicalHead, alignment);

            // Check if allocation would exceed the end of the circular buffer
            if (alignedPhysicalHead + size > Capacity)
            {
                // Must pad to end of buffer and wrap logical head to next Capacity boundary
                ulong padToEnd = Capacity - currentPhysicalHead;
                ulong paddedLogicalHead = _logicalHead + padToEnd;

                // Ensure waiting until space is available for the pad gap + requested size
                ulong neededBytes = padToEnd + size;
                EnsureSpaceAvailable_Locked(neededBytes);

                _logicalHead = paddedLogicalHead;
                alignedPhysicalHead = 0;
            }
            else
            {
                ulong alignmentPadding = alignedPhysicalHead - currentPhysicalHead;
                ulong neededBytes = alignmentPadding + size;
                EnsureSpaceAvailable_Locked(neededBytes);

                _logicalHead += alignmentPadding;
            }

            stagingOffset = alignedPhysicalHead;
            _logicalHead += size;

            if (_mappedBasePtr != null)
            {
                mappedPtr = (byte*)_mappedBasePtr + stagingOffset;
            }
            else
            {
                mappedPtr = (void*)(nint)stagingOffset;
            }
        }
    }

    /// <summary>
    /// Records an allocation's association with a GPU timeline submission value.
    /// </summary>
    public void SubmitSlice(ulong stagingOffset, ulong size, ulong timelineValue)
    {
        lock (_lock)
        {
            // Calculate logical end offset corresponding to this slice
            // The slice ends at the current _logicalHead
            _inFlightQueue.Enqueue(new InFlightSegment
            {
                LogicalEndOffset = _logicalHead,
                TimelineValue = timelineValue
            });
        }
    }

    private void EnsureSpaceAvailable_Locked(ulong requiredBytes)
    {
        while (Capacity - (_logicalHead - _logicalTail) < requiredBytes)
        {
            if (_inFlightQueue.Count == 0)
            {
                // All submitted slices have retired, tail advances to head
                _logicalTail = _logicalHead;
                break;
            }

            var next = _inFlightQueue.Peek();
            _timeline.Wait(next.TimelineValue);
            _logicalTail = next.LogicalEndOffset;
            _inFlightQueue.Dequeue();
        }
    }

    private void RetireCompletedSegments_Locked(ulong completedTimelineValue)
    {
        while (_inFlightQueue.Count > 0 && _inFlightQueue.Peek().TimelineValue <= completedTimelineValue)
        {
            var retired = _inFlightQueue.Dequeue();
            _logicalTail = retired.LogicalEndOffset;
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
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _stagingBuffer?.Dispose();
        }
    }
}
