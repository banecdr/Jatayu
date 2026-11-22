/* =================================================================================================
 * FILE: AllocationTrackerTests.cs
 * LOCATION: tests/Jatayu.Tests/AllocationTrackerTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating thread-local GC allocation tracking:
 *   - Accurate detection of managed heap allocations
 *   - Zero allocation percentage metric calculation
 * ================================================================================================= */

using System;
using Jatayu.Core.Profiling;
using Xunit;

namespace Jatayu.Tests;

public class AllocationTrackerTests
{
    [Fact]
    public void AllocationTracker_DetectsHeapAllocations()
    {
        AllocationTracker.BeginFrame();

        // Perform an explicit managed heap allocation
        var data = new byte[8192];
        GC.KeepAlive(data);

        AllocationTracker.EndFrame();

        Assert.True(AllocationTracker.LastFrameAllocatedBytes >= 8192, 
            $"Expected at least 8192 allocated bytes, but measured {AllocationTracker.LastFrameAllocatedBytes}");
    }

    [Fact]
    public void ZeroAllocationPercent_IsBetweenZeroAndOneHundred()
    {
        AllocationTracker.BeginFrame();
        AllocationTracker.EndFrame();

        float percent = AllocationTracker.ZeroAllocationPercent;
        Assert.InRange(percent, 0.0f, 100.0f);
    }
}
