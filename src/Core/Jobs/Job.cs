/* =================================================================================================
 * FILE: Job.cs
 * LOCATION: src/Core/Jobs/Job.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines the core job interfaces and priority enumerations for the Jatayu Job System.
 *   Enables both delegate-based tasks and zero-allocation struct jobs for high-throughput
 *   parallel algorithms (frustum culling, animation, asset decompression, terrain generation).
 * ================================================================================================= */

using System;

namespace Jatayu.Core.Jobs;

/// <summary>
/// Execution priority for scheduled jobs in the JobSystem.
/// </summary>
public enum JobPriority
{
    /// <summary>
    /// Urgent tasks required for the current frame (e.g. frustum culling, barrier preparation).
    /// </summary>
    High = 0,

    /// <summary>
    /// Standard game simulation tasks (e.g. physics updates, animation sampling).
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Background streaming, asset decompression, and asynchronous cooking tasks.
    /// </summary>
    Background = 2,
}

/// <summary>
/// Interface for zero-allocation struct jobs executed once on a worker thread.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the unit of work.
    /// </summary>
    void Execute();
}

/// <summary>
/// Interface for batched parallel jobs processing contiguous sub-ranges.
/// </summary>
public interface IJobParallelForBatch
{
    /// <summary>
    /// Executes work for the specified sub-range [startIndex, startIndex + count).
    /// </summary>
    /// <param name="startIndex">The first index in this batch.</param>
    /// <param name="count">The number of items to process in this batch.</param>
    void Execute(int startIndex, int count);
}
