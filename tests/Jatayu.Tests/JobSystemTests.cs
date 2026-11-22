/* =================================================================================================
 * FILE: JobSystemTests.cs
 * LOCATION: tests/Jatayu.Tests/JobSystemTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Validates the Jatayu Multithreaded Job System (Sprint 4.1):
 *   - Basic delegate and struct job scheduling and completion.
 *   - Dependency chaining (DAG execution order guarantees).
 *   - JobHandle.Combine multi-dependency barriers.
 *   - ParallelFor batch range partitioning.
 *   - MainThreadDispatcher enqueue, time budgeting, and drain.
 * ================================================================================================= */

using System;
using System.Threading;
using Jatayu.Core.Jobs;
using Xunit;

namespace Jatayu.Tests;

public class JobSystemTests
{
    private struct IncrementJob : IJob
    {
        public int[] Target;
        public int Index;

        public void Execute()
        {
            Interlocked.Increment(ref Target[Index]);
        }
    }

    private struct ParallelIncrementJob : IJobParallelForBatch
    {
        public int[] Target;

        public void Execute(int startIndex, int count)
        {
            for (int i = startIndex; i < startIndex + count; i++)
            {
                Target[i] += 10;
            }
        }
    }

    [Fact]
    public void Schedule_SingleActionJob_CompletesSuccessfully()
    {
        using var jobSystem = new JobSystem(workerCount: 2);
        int value = 0;

        var handle = jobSystem.Schedule(() =>
        {
            Thread.Sleep(10);
            value = 42;
        });

        Assert.False(handle.IsCompleted);
        handle.Complete();
        Assert.True(handle.IsCompleted);
        Assert.Equal(42, value);
    }

    [Fact]
    public void Schedule_StructJob_ExecutesCorrectly()
    {
        using var jobSystem = new JobSystem(workerCount: 2);
        var array = new int[4];

        var handle = jobSystem.Schedule(new IncrementJob
        {
            Target = array,
            Index = 2,
        });

        handle.Complete();
        Assert.Equal(1, array[2]);
    }

    [Fact]
    public void Schedule_DependencyChain_ExecutesInOrder()
    {
        using var jobSystem = new JobSystem(workerCount: 2);
        int step = 0;

        var handle1 = jobSystem.Schedule(() =>
        {
            Thread.Sleep(15);
            step = 1;
        });

        var handle2 = jobSystem.Schedule(() =>
        {
            Assert.Equal(1, step);
            step = 2;
        }, dependency: handle1);

        handle2.Complete();
        Assert.Equal(2, step);
    }

    [Fact]
    public void Combine_MultipleDependencies_WaitsForAll()
    {
        using var jobSystem = new JobSystem(workerCount: 4);
        int a = 0, b = 0, c = 0;

        var hA = jobSystem.Schedule(() => { Thread.Sleep(10); a = 1; });
        var hB = jobSystem.Schedule(() => { Thread.Sleep(15); b = 2; });
        var hC = jobSystem.Schedule(() => { Thread.Sleep(20); c = 3; });

        var combined = JobHandle.Combine(new[] { hA, hB, hC });
        var finalHandle = jobSystem.Schedule(() =>
        {
            Assert.Equal(1, a);
            Assert.Equal(2, b);
            Assert.Equal(3, c);
        }, dependency: combined);

        finalHandle.Complete();
        Assert.True(finalHandle.IsCompleted);
    }

    [Fact]
    public void ParallelFor_PartitionsBatchesAcrossWorkers()
    {
        using var jobSystem = new JobSystem(workerCount: 4);
        const int count = 1000;
        var data = new int[count];

        var handle = ParallelFor.Schedule(jobSystem, new ParallelIncrementJob { Target = data }, count, batchSize: 64);
        handle.Complete();

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(10, data[i]);
        }
    }

    [Fact]
    public void MainThreadDispatcher_DrainsWithTimeBudget()
    {
        var dispatcher = new MainThreadDispatcher();
        int counter = 0;

        for (int i = 0; i < 20; i++)
        {
            dispatcher.Enqueue(() =>
            {
                Thread.Sleep(1);
                Interlocked.Increment(ref counter);
            });
        }

        Assert.Equal(20, dispatcher.PendingCount);

        // Drain with a tight 5ms budget
        int executed = dispatcher.Drain(maxTimeMilliseconds: 5.0);
        Assert.True(executed > 0);
        Assert.True(dispatcher.PendingCount < 20);

        // Drain the rest unconditionally
        dispatcher.DrainAll();
        Assert.Equal(0, dispatcher.PendingCount);
        Assert.Equal(20, counter);
    }
}
