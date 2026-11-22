/* =================================================================================================
 * FILE: ParallelFor.cs
 * LOCATION: src/Core/Jobs/ParallelFor.cs
 * 
 * WHAT THIS FILE DOES:
 *   Batched parallel-for loop partitioner for data-parallel algorithms.
 *   Divides total iteration count into configurable batch chunks and schedules them across
 *   worker threads in the JobSystem, returning a combined JobHandle for synchronization.
 * ================================================================================================= */

using System;
using System.Buffers;

namespace Jatayu.Core.Jobs;

/// <summary>
/// Utility for scheduling data-parallel work ranges across the JobSystem.
/// </summary>
public static class ParallelFor
{
    private struct BatchJob<TJob> : IJob where TJob : struct, IJobParallelForBatch
    {
        private TJob _job;
        private readonly int _startIndex;
        private readonly int _count;

        public BatchJob(TJob job, int startIndex, int count)
        {
            _job = job;
            _startIndex = startIndex;
            _count = count;
        }

        public void Execute()
        {
            _job.Execute(_startIndex, _count);
        }
    }

    /// <summary>
    /// Schedules a zero-allocation struct parallel-for batch job.
    /// </summary>
    public static JobHandle Schedule<TJob>(
        JobSystem jobSystem,
        TJob job,
        int totalIterations,
        int batchSize,
        JobPriority priority = JobPriority.Normal,
        JobHandle dependency = default) where TJob : struct, IJobParallelForBatch
    {
        ArgumentNullException.ThrowIfNull(jobSystem);
        if (totalIterations <= 0) return JobHandle.None;
        if (batchSize <= 0) batchSize = 64;

        int batchCount = (totalIterations + batchSize - 1) / batchSize;
        if (batchCount == 1)
        {
            return jobSystem.Schedule(new BatchJob<TJob>(job, 0, totalIterations), priority, dependency);
        }

        var rented = ArrayPool<JobHandle>.Shared.Rent(batchCount);
        try
        {
            for (int i = 0; i < batchCount; i++)
            {
                int start = i * batchSize;
                int count = Math.Min(batchSize, totalIterations - start);
                rented[i] = jobSystem.Schedule(new BatchJob<TJob>(job, start, count), priority, dependency);
            }

            return jobSystem.Combine(new ReadOnlySpan<JobHandle>(rented, 0, batchCount));
        }
        finally
        {
            ArrayPool<JobHandle>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Schedules a delegate-based parallel-for batch job.
    /// </summary>
    public static JobHandle Schedule(
        JobSystem jobSystem,
        int totalIterations,
        int batchSize,
        Action<int, int> executeBatch,
        JobPriority priority = JobPriority.Normal,
        JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(jobSystem);
        ArgumentNullException.ThrowIfNull(executeBatch);
        if (totalIterations <= 0) return JobHandle.None;
        if (batchSize <= 0) batchSize = 64;

        int batchCount = (totalIterations + batchSize - 1) / batchSize;
        if (batchCount == 1)
        {
            return jobSystem.Schedule(() => executeBatch(0, totalIterations), priority, dependency);
        }

        var rented = ArrayPool<JobHandle>.Shared.Rent(batchCount);
        try
        {
            for (int i = 0; i < batchCount; i++)
            {
                int start = i * batchSize;
                int count = Math.Min(batchSize, totalIterations - start);
                rented[i] = jobSystem.Schedule(() => executeBatch(start, count), priority, dependency);
            }

            return jobSystem.Combine(new ReadOnlySpan<JobHandle>(rented, 0, batchCount));
        }
        finally
        {
            ArrayPool<JobHandle>.Shared.Return(rented);
        }
    }
}
