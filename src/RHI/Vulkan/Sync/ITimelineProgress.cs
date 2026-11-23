/* =================================================================================================
 * FILE: ITimelineProgress.cs
 * LOCATION: src/RHI/Vulkan/Sync/ITimelineProgress.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines the timeline synchronization seam for querying and waiting on monotonically
 *   increasing GPU/CPU timeline values. Allows unit testing of staging allocators without a GPU.
 * ================================================================================================= */

namespace Jatayu.RHI.Vulkan.Sync;

/// <summary>
/// Abstraction for querying and waiting on a monotonic timeline progress counter.
/// </summary>
public interface ITimelineProgress
{
    /// <summary>
    /// Gets the current completed timeline value.
    /// </summary>
    ulong GetCurrentValue();

    /// <summary>
    /// Waits until the timeline reaches or exceeds the specified target value.
    /// </summary>
    /// <param name="targetValue">Target timeline counter value.</param>
    /// <param name="timeoutNanoseconds">Timeout in nanoseconds (-1 for infinite).</param>
    /// <returns>True if the target value was reached; false if timed out.</returns>
    bool Wait(ulong targetValue, nint timeoutNanoseconds = -1);
}
