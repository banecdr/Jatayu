/* =================================================================================================
 * FILE: StagingRingStressTests.cs
 * LOCATION: tests/Jatayu.Tests/StagingRingStressTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit and randomized stress tests validating the StagingRing with a fake timeline:
 *   - Verifies monotonic 64-bit offsets and wrapping logic
 *   - Verifies end-gap consumption on circular buffer wrap
 *   - Performs randomized multi-iteration stress testing asserting no active in-flight
 *     staging memory ranges are ever concurrently overwritten.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Jatayu.RHI.Vulkan.Sync;
using Jatayu.RHI.Vulkan.Upload;
using Xunit;

namespace Jatayu.Tests;

public class StagingRingStressTests
{
    private sealed class FakeTimelineProgress : ITimelineProgress
    {
        public ulong CurrentValue { get; set; }

        public ulong GetCurrentValue() => CurrentValue;

        public bool Wait(ulong targetValue, nint timeoutNanoseconds = -1)
        {
            if (CurrentValue < targetValue)
            {
                CurrentValue = targetValue;
            }
            return true;
        }
    }

    private readonly struct LiveRange
    {
        public readonly ulong PhysicalStart;
        public readonly ulong Size;
        public readonly ulong TimelineValue;

        public LiveRange(ulong start, ulong size, ulong timeline)
        {
            PhysicalStart = start;
            Size = size;
            TimelineValue = timeline;
        }

        public bool Overlaps(ulong start, ulong size)
        {
            ulong end = PhysicalStart + Size;
            ulong newEnd = start + size;
            return Math.Max(PhysicalStart, start) < Math.Min(end, newEnd);
        }
    }

    [Fact]
    public unsafe void Allocate_WithinCapacity_Succeeds()
    {
        var timeline = new FakeTimelineProgress();
        const ulong capacity = 2 * 1024 * 1024; // 2 MB
        using var ring = new StagingRing(capacity, timeline);

        ring.Allocate(1024, 16, out var offset1, out _);
        Assert.Equal(0UL, offset1);
        ring.SubmitSlice(offset1, 1024, 1);

        ring.Allocate(2048, 256, out var offset2, out _);
        Assert.Equal(1024UL, offset2); // 1024 is already 256-byte aligned
        ring.SubmitSlice(offset2, 2048, 2);
    }

    [Fact]
    public unsafe void Allocate_Wraparound_ConsumesGapAndWaitsUntilRetired()
    {
        var timeline = new FakeTimelineProgress();
        const ulong capacity = 1024 * 1024; // 1 MB
        using var ring = new StagingRing(capacity, timeline);

        // Allocate 700 KB
        ulong alloc1Size = 700 * 1024;
        ring.Allocate(alloc1Size, 16, out var off1, out _);
        Assert.Equal(0UL, off1);
        ring.SubmitSlice(off1, alloc1Size, 1);

        // Next 500 KB cannot fit in remaining 324 KB -> Must wrap to 0
        ulong alloc2Size = 500 * 1024;
        ring.Allocate(alloc2Size, 16, out var off2, out _);
        Assert.Equal(0UL, off2);

        // Since timeline was at 0 and required space needed retiring alloc1, timeline must have advanced to at least 1!
        Assert.True(timeline.CurrentValue >= 1);
        ring.SubmitSlice(off2, alloc2Size, 2);
    }

    [Fact]
    public unsafe void RandomizedStressTest_NoOverlapsInFlight()
    {
        var timeline = new FakeTimelineProgress();
        const ulong capacity = 4 * 1024 * 1024; // 4 MB
        using var ring = new StagingRing(capacity, timeline);

        var random = new Random(1337);
        var activeRanges = new List<LiveRange>();
        ulong currentTimelineVal = 0;

        ulong[] possibleAlignments = [16, 64, 128, 256];

        for (int iter = 0; iter < 2000; iter++)
        {
            // Randomly advance timeline to simulate GPU making progress
            if (activeRanges.Count > 0 && random.Next(10) < 4)
            {
                // Advance to some active timeline value
                int idx = random.Next(activeRanges.Count);
                ulong advanceTo = activeRanges[idx].TimelineValue;
                if (advanceTo > timeline.CurrentValue)
                {
                    timeline.CurrentValue = advanceTo;
                }
            }

            // Remove retired ranges from tracking
            activeRanges.RemoveAll(r => r.TimelineValue <= timeline.CurrentValue);

            // Allocate a random size between 256 bytes and 512 KB
            ulong size = (ulong)random.Next(256, 512 * 1024);
            ulong alignment = possibleAlignments[random.Next(possibleAlignments.Length)];

            ring.Allocate(size, alignment, out var offset, out _);

            // Verify alignment
            Assert.Equal(0UL, offset % alignment);
            // Verify within physical buffer capacity
            Assert.True(offset + size <= capacity, $"Allocation [{offset}, {offset + size}) exceeded capacity {capacity}");

            // Verify NO overlap with any currently active range that hasn't completed on GPU timeline
            // (Note: ring.Allocate will have called timeline.Wait if needed to ensure space)
            activeRanges.RemoveAll(r => r.TimelineValue <= timeline.CurrentValue);

            foreach (var active in activeRanges)
            {
                Assert.False(
                    active.Overlaps(offset, size),
                    $"Collision detected! New alloc [{offset}, {offset + size}) overlaps active range [{active.PhysicalStart}, {active.PhysicalStart + active.Size}) with timeline {active.TimelineValue} (current: {timeline.CurrentValue})");
            }

            currentTimelineVal++;
            ring.SubmitSlice(offset, size, currentTimelineVal);
            activeRanges.Add(new LiveRange(offset, size, currentTimelineVal));
        }
    }
}
