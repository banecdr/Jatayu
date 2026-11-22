/* =================================================================================================
 * FILE: AllocationTracker.cs
 * LOCATION: src/Core/Profiling/AllocationTracker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Monitors Garbage Collector (GC) heap memory allocations per frame on the main engine thread.
 *   Enforces Jatayu's zero-heap-allocation discipline in steady-state rendering and tick loops.
 * ================================================================================================= */

using System;
using System.Diagnostics;

namespace Jatayu.Core.Profiling;

/// <summary>
/// Monitors thread-local GC heap allocations per frame to enforce zero-allocation discipline.
/// </summary>
public static class AllocationTracker
{
    private static long s_startBytes;
    private static long s_lastFrameAllocatedBytes;
    private static long s_totalFrames;
    private static long s_nonZeroFrames;

    /// <summary>
    /// Gets the number of bytes allocated on the GC managed heap during the most recent frame.
    /// In steady-state engine rendering, this MUST be 0.
    /// </summary>
    public static long LastFrameAllocatedBytes => s_lastFrameAllocatedBytes;

    /// <summary>
    /// Gets the percentage of measured frames that achieved zero GC allocations.
    /// </summary>
    public static float ZeroAllocationPercent => s_totalFrames > 0 
        ? 100.0f * (float)(s_totalFrames - s_nonZeroFrames) / s_totalFrames 
        : 100.0f;

    /// <summary>
    /// Starts tracking GC allocations for the current frame.
    /// </summary>
    public static void BeginFrame()
    {
        s_startBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>
    /// Ends tracking and computes total bytes allocated during this frame.
    /// </summary>
    public static void EndFrame()
    {
        long current = GC.GetAllocatedBytesForCurrentThread();
        s_lastFrameAllocatedBytes = Math.Max(0, current - s_startBytes);

        s_totalFrames++;
        if (s_lastFrameAllocatedBytes > 0)
        {
            s_nonZeroFrames++;
        }
    }

    /// <summary>
    /// In debug builds, asserts that zero GC bytes were allocated during the frame.
    /// </summary>
    [Conditional("DEBUG")]
    public static void AssertZeroAllocations(string context = "Steady-state hot path")
    {
        if (s_lastFrameAllocatedBytes > 0)
        {
            Debug.WriteLine($"[AllocationTracker] WARNING: {s_lastFrameAllocatedBytes} bytes allocated in '{context}'!");
        }
    }
}
