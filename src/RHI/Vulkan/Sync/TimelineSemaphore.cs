/* =================================================================================================
 * FILE: TimelineSemaphore.cs
 * LOCATION: src/RHI/Vulkan/Sync/TimelineSemaphore.cs
 * 
 * WHAT THIS FILE DOES:
 *   Wraps a Vulkan 1.3 Timeline Semaphore (VK_SEMAPHORE_TYPE_TIMELINE).
 *   Unlike binary semaphores which only flip between unsignaled and signaled (0 and 1), a timeline
 *   semaphore holds a strictly monotonically increasing 64-bit integer value (0, 1, 2, 3...).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Fine-Grained GPU/CPU Synchronization:
 *      Both the host CPU and the GPU queues can wait for the counter to reach at least value X,
 *      or signal value Y upon completing work.
 *   2. Async Upload Tracking:
 *      UploadManager submits asynchronous transfers to the dedicated transfer queue and signals
 *      monotonically increasing values. The CPU or Graphics Queue can wait on any specific upload value
 *      without resetting semaphores.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Sync;

/// <summary>
/// Wraps a Vulkan 1.3 Timeline Semaphore for fine-grained 64-bit counter synchronization.
/// </summary>
public sealed class TimelineSemaphore : ITimelineProgress, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the parent Vulkan context.
    /// </summary>
    public VulkanContext Context { get; }

    /// <summary>
    /// Gets the raw native Vulkan semaphore handle.
    /// </summary>
    public VkSemaphore Handle { get; }

    /// <summary>
    /// Initializes a new Timeline Semaphore with the specified initial 64-bit counter value.
    /// </summary>
    public unsafe TimelineSemaphore(VulkanContext context, ulong initialValue = 0)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));

        var typeInfo = new VkSemaphoreTypeCreateInfo
        {
            SemaphoreType = VkSemaphoreType.Timeline,
            InitialValue = (nint)initialValue
        };

        var createInfo = new VkSemaphoreCreateInfo
        {
            Next = &typeInfo
        };

        Handle = Context.Device.CreateSemaphore(createInfo, null);
    }

    /// <summary>
    /// Queries the current 64-bit counter value from the GPU timeline.
    /// </summary>
    public unsafe ulong GetCurrentValue()
    {
        ThrowIfDisposed();
        nint val = 0;
        var res = Context.Device.PInvoke.vkGetSemaphoreCounterValue.Invoke(Context.Device.Handle, Handle, &val);
        if (res != VkResult.Success)
            throw new InvalidOperationException($"Failed to query semaphore counter value: {res}");
        return (ulong)val;
    }

    /// <summary>
    /// Blocks the calling CPU thread until the timeline semaphore reaches or exceeds the target value.
    /// </summary>
    /// <param name="targetValue">The counter value to wait for.</param>
    /// <param name="timeoutNanoseconds">Timeout in nanoseconds (default: -1 / infinite).</param>
    /// <returns>True if the value was reached; false if timed out.</returns>
    public unsafe bool Wait(ulong targetValue, nint timeoutNanoseconds = -1)
    {
        ThrowIfDisposed();
        var sem = Handle;
        var val = (nint)targetValue;

        var waitInfo = new VkSemaphoreWaitInfo
        {
            SemaphoreCount = 1,
            Semaphores = &sem,
            Values = &val
        };

        var res = Context.Device.PInvoke.vkWaitSemaphores.Invoke(Context.Device.Handle, &waitInfo, timeoutNanoseconds);
        if (res == VkResult.Timeout)
            return false;

        if (res != VkResult.Success)
            throw new InvalidOperationException($"vkWaitSemaphores failed with: {res}");

        return true;
    }

    /// <summary>
    /// Signals the timeline semaphore from the host CPU with a higher counter value.
    /// </summary>
    public unsafe void Signal(ulong value)
    {
        ThrowIfDisposed();
        var signalInfo = new VkSemaphoreSignalInfo
        {
            Semaphore = Handle,
            Value = (nint)value
        };

        var res = Context.Device.PInvoke.vkSignalSemaphore.Invoke(Context.Device.Handle, &signalInfo);
        if (res != VkResult.Success)
            throw new InvalidOperationException($"vkSignalSemaphore failed with: {res}");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle.VkHandle != 0)
        {
            Context.Device.DestroySemaphore(Handle, null);
        }
    }
}
