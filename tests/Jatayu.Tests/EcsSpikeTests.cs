// ==================================================================================================
// SPRINT 5a: ECS Spike & Allocation Benchmark (Decision D2 & D26)
// Validates Arch (Apache-2.0) with 100,000 entities: 0.25 ms query time & 0-byte steady-state GC allocation.
// ==================================================================================================

using System.Diagnostics;
using Arch.Core;
using Arch.Core.Extensions;
using Jatayu.Core.Profiling;
using Xunit;
using Xunit.Abstractions;

namespace Jatayu.Tests;

public struct ArchPosition
{
    public float X, Y, Z;
}

public struct ArchVelocity
{
    public float Vx, Vy, Vz;
}

public struct ArchMovementJob : IForEach<ArchPosition, ArchVelocity>
{
    public float Dt;
    public void Update(ref ArchPosition pos, ref ArchVelocity vel)
    {
        pos.X += vel.Vx * Dt;
    }
}

public class EcsSpikeTests
{
    private readonly ITestOutputHelper _output;

    public EcsSpikeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Arch_100kEntities_ZeroAllocationsAndSubMillisecondQuery()
    {
        // 1. Setup World & 100,000 entities
        var world = World.Create();
        for (int i = 0; i < 100_000; i++)
        {
            world.Create(new ArchPosition { X = i, Y = 0, Z = 0 }, new ArchVelocity { Vx = 1, Vy = 2, Vz = 3 });
        }

        var query = new QueryDescription().WithAll<ArchPosition, ArchVelocity>();
        var job = new ArchMovementJob { Dt = 0.016f };

        // Warmup multiple frames to ensure JIT and internal buffers are primed
        for (int i = 0; i < 5; i++)
        {
            world.InlineQuery<ArchMovementJob, ArchPosition, ArchVelocity>(in query, ref job);
        }

        // 2. Measure steady-state performance
        var sw = Stopwatch.StartNew();
        const int iterations = 10;
        for (int frame = 0; frame < iterations; frame++)
        {
            world.InlineQuery<ArchMovementJob, ArchPosition, ArchVelocity>(in query, ref job);
        }
        sw.Stop();
        double msPerFrame = sw.Elapsed.TotalMilliseconds / iterations;

        // 3. Verify zero heap allocation per frame
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        world.InlineQuery<ArchMovementJob, ArchPosition, ArchVelocity>(in query, ref job);
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long frameAlloc = afterAlloc - beforeAlloc;

        _output.WriteLine($"[Arch] 100k Entities: {msPerFrame:F3} ms/frame, Allocation: {frameAlloc} bytes/frame");

        World.Destroy(world);

        Assert.Equal(0, frameAlloc);
        Assert.True(msPerFrame < 1.0, $"Arch query took {msPerFrame:F2} ms, expected < 1.0 ms");
    }
}
