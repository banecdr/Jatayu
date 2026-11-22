/* =================================================================================================
 * FILE: DeferredDeletionQueue.cs
 * LOCATION: src/RHI/Vulkan/Memory/DeferredDeletionQueue.cs
 * 
 * WHAT THIS FILE DOES:
 *   Safely defers the destruction of GPU objects (buffers, images, image views, samplers, pipelines)
 *   until all in-flight GPU frames have finished executing.
 *
 * HOW IT WORKS INTERNALLY:
 *   When a resource is retired (e.g. during texture streaming or dynamic geometry reallocation),
 *   it is enqueued with the current submission frame index. At the start of each frame, Flush()
 *   destroys any resources whose frame index is <= completedFrameIndex.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Memory;

/// <summary>
/// Holds GPU resources for destruction until in-flight frames have finished on the GPU timeline.
/// </summary>
public sealed class DeferredDeletionQueue : IDisposable
{
    private struct DeletionItem
    {
        public ulong FrameIndex;
        public Action? Callback;
        public IDisposable? Disposable;
    }

    private readonly VulkanContext _context;
    private readonly Queue<DeletionItem> _queue = new(256);
    private bool _disposed;

    /// <summary>
    /// Gets the number of pending resource deletions currently queued.
    /// </summary>
    public int PendingCount => _queue.Count;

    public DeferredDeletionQueue(VulkanContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Enqueues an arbitrary disposal action to execute once the given frame finishes on the GPU.
    /// </summary>
    public void Enqueue(Action callback, ulong frameIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(callback);

        _queue.Enqueue(new DeletionItem
        {
            FrameIndex = frameIndex,
            Callback = callback
        });
    }

    /// <summary>
    /// Enqueues an IDisposable GPU resource (e.g. GpuBuffer, GpuTexture) to dispose after frame completion.
    /// </summary>
    public void Enqueue(IDisposable resource, ulong frameIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resource);

        _queue.Enqueue(new DeletionItem
        {
            FrameIndex = frameIndex,
            Disposable = resource
        });
    }

    /// <summary>
    /// Enqueues a raw Vulkan image view for destruction.
    /// </summary>
    public void Enqueue(VkImageView imageView, ulong frameIndex)
    {
        ThrowIfDisposed();
        if (imageView.VkHandle == 0) return;

        Enqueue(() =>
        {
            if (imageView.VkHandle != 0)
                _context.Device.DestroyImageView(imageView, null);
        }, frameIndex);
    }

    /// <summary>
    /// Enqueues a raw Vulkan sampler for destruction.
    /// </summary>
    public void Enqueue(VkSampler sampler, ulong frameIndex)
    {
        ThrowIfDisposed();
        if (sampler.VkHandle == 0) return;

        Enqueue(() =>
        {
            if (sampler.VkHandle != 0)
                _context.Device.DestroySampler(sampler, null);
        }, frameIndex);
    }

    /// <summary>
    /// Enqueues a VMA-allocated buffer for destruction.
    /// </summary>
    public void Enqueue(GpuAllocator allocator, VkBuffer buffer, VmaAllocation allocation, ulong frameIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(allocator);

        Enqueue(() =>
        {
            allocator.DestroyBuffer(buffer, allocation);
        }, frameIndex);
    }

    /// <summary>
    /// Enqueues a VMA-allocated image for destruction.
    /// </summary>
    public void Enqueue(GpuAllocator allocator, VkImage image, VmaAllocation allocation, ulong frameIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(allocator);

        Enqueue(() =>
        {
            allocator.DestroyImage(image, allocation);
        }, frameIndex);
    }

    /// <summary>
    /// Destroys all queued resources whose frame index is less than or equal to completedFrameIndex.
    /// </summary>
    public void Flush(ulong completedFrameIndex)
    {
        ThrowIfDisposed();

        while (_queue.Count > 0 && _queue.Peek().FrameIndex <= completedFrameIndex)
        {
            var item = _queue.Dequeue();
            try
            {
                item.Callback?.Invoke();
                item.Disposable?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DeferredDeletionQueue] Exception during deferred resource disposal: {ex}");
            }
        }
    }

    /// <summary>
    /// Destroys all queued resources immediately. Call only when device is idle (e.g. during shutdown).
    /// </summary>
    public void FlushAll()
    {
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            try
            {
                item.Callback?.Invoke();
                item.Disposable?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DeferredDeletionQueue] Exception during shutdown flush: {ex}");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FlushAll();
    }
}
