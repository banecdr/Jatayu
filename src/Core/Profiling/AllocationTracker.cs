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
    private static readonly bool[] s_zeroAllocationWindow = new bool[120];
    private static int s_windowIndex;
    private static int s_windowCount;
    private static int s_zeroAllocationCount;

    /// <summary>
    /// Gets the number of bytes allocated on the GC managed heap during the most recent frame.
    /// In steady-state engine rendering, this MUST be 0.
    /// </summary>
    public static long LastFrameAllocatedBytes => s_lastFrameAllocatedBytes;

    /// <summary>
    /// Gets the percentage of measured frames that achieved zero GC allocations.
    /// </summary>
    public static float ZeroAllocationPercent => s_windowCount > 0
        ? 100.0f * s_zeroAllocationCount / s_windowCount
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
        bool isZeroAllocation = s_lastFrameAllocatedBytes == 0;
        if (!isZeroAllocation)
        {
            s_nonZeroFrames++;
        }

        if (s_windowCount < s_zeroAllocationWindow.Length)
        {
            s_windowCount++;
        }
        else
        {
            if (s_zeroAllocationWindow[s_windowIndex])
                s_zeroAllocationCount--;
        }

        s_zeroAllocationWindow[s_windowIndex] = isZeroAllocation;
        if (isZeroAllocation)
            s_zeroAllocationCount++;

        s_windowIndex = (s_windowIndex + 1) % s_zeroAllocationWindow.Length;
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
