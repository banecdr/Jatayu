/* =================================================================================================
 * FILE: ProfilerTests.cs
 * LOCATION: tests/Jatayu.Tests/ProfilerTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating the CPU Profiler:
 *   - Measuring manual zones (BeginZone / EndZone)
 *   - Measuring scoped zones (using Profiler.Scope)
 *   - Snapshot publishing at frame boundaries (BeginFrame / EndFrame)
 * ================================================================================================= */

using System;
using System.Threading;
using Jatayu.Core.Profiling;
using Xunit;

namespace Jatayu.Tests;

public class ProfilerTests
{
    [Fact]
    public void Profiler_RecordsAndPublishesZoneTimings()
    {
        Profiler.BeginFrame();

        Profiler.BeginZone("TestManualZone");
        Thread.Sleep(5);
        Profiler.EndZone("TestManualZone");

        using (Profiler.Scope("TestScopedZone"))
        {
            Thread.Sleep(5);
        }

        // Before EndFrame, snapshot is not yet updated for current frame
        Profiler.EndFrame();

        var snapshot = Profiler.LatestSnapshot;
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Count >= 2);

        bool foundManual = false;
        bool foundScoped = false;

        foreach (var zone in snapshot)
        {
            if (zone.Name == "TestManualZone")
            {
                foundManual = true;
                Assert.True(zone.DurationMs > 0f);
            }
            else if (zone.Name == "TestScopedZone")
            {
                foundScoped = true;
                Assert.True(zone.DurationMs > 0f);
            }
        }

        Assert.True(foundManual, "TestManualZone must be present in snapshot");
        Assert.True(foundScoped, "TestScopedZone must be present in snapshot");
    }

    [Fact]
    public void Profiler_BeginFrameClearsPreviousFrameZones()
    {
        Profiler.BeginFrame();
        Profiler.BeginZone("Frame1Zone");
        Profiler.EndZone("Frame1Zone");
        Profiler.EndFrame();

        Assert.Contains(Profiler.LatestSnapshot, z => z.Name == "Frame1Zone");

        Profiler.BeginFrame();
        Profiler.BeginZone("Frame2Zone");
        Profiler.EndZone("Frame2Zone");
        Profiler.EndFrame();

        Assert.Contains(Profiler.LatestSnapshot, z => z.Name == "Frame2Zone");
        Assert.DoesNotContain(Profiler.LatestSnapshot, z => z.Name == "Frame1Zone");
    }
}
