/* =================================================================================================
 * FILE: JobHandle.cs
 * LOCATION: src/Core/Jobs/JobHandle.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a lightweight, copyable dependency and completion token for a scheduled job.
 *   Provides IsCompleted query, blocking Complete() synchronization, and Combine() operators
 *   to establish Directed Acyclic Graphs (DAGs) of asynchronous work.
 * ================================================================================================= */

using System;

namespace Jatayu.Core.Jobs;

/// <summary>
/// A lightweight token representing an in-flight or completed job in the JobSystem.
/// </summary>
public readonly struct JobHandle : IEquatable<JobHandle>
{
    /// <summary>
    /// Represents an empty or already-completed job handle.
    /// </summary>
    public static readonly JobHandle None = default;

    internal readonly JobSystem? Owner;
    internal readonly int SlotIndex;
    internal readonly int Version;

    internal JobHandle(JobSystem? owner, int slotIndex, int version)
    {
        Owner = owner;
        SlotIndex = slotIndex;
        Version = version;
    }

    /// <summary>
    /// Gets whether this handle refers to a valid job entry.
    /// </summary>
    public bool IsValid => Owner != null;

    /// <summary>
    /// Gets whether the underlying job has finished executing.
    /// </summary>
    public bool IsCompleted => Owner == null || Owner.IsJobCompleted(SlotIndex, Version);

    /// <summary>
    /// Blocks until the associated job and all its dependencies have completed.
    /// While waiting, the calling thread will assist in executing pending jobs to prevent deadlocks.
    /// </summary>
    public void Complete()
    {
        Owner?.WaitForJob(SlotIndex, Version);
    }

    /// <summary>
    /// Combines two job handles into a single barrier dependency.
    /// </summary>
    public static JobHandle Combine(JobHandle a, JobHandle b)
    {
        if (!a.IsValid && !b.IsValid) return None;
        if (!a.IsValid) return b;
        if (!b.IsValid) return a;
        if (a.Equals(b)) return a;

        var owner = a.Owner ?? b.Owner!;
        return owner.Combine(a, b);
    }

    /// <summary>
    /// Combines multiple job handles into a single barrier dependency.
    /// </summary>
    public static JobHandle Combine(ReadOnlySpan<JobHandle> handles)
    {
        if (handles.IsEmpty) return None;

        JobSystem? owner = null;
        int validCount = 0;
        JobHandle lastValid = None;

        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i].IsValid && !handles[i].IsCompleted)
            {
                owner ??= handles[i].Owner;
                lastValid = handles[i];
                validCount++;
            }
        }

        if (validCount == 0) return None;
        if (validCount == 1) return lastValid;

        return owner!.Combine(handles);
    }

    public bool Equals(JobHandle other) =>
        ReferenceEquals(Owner, other.Owner) &&
        SlotIndex == other.SlotIndex &&
        Version == other.Version;

    public override bool Equals(object? obj) => obj is JobHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Owner, SlotIndex, Version);

    public static bool operator ==(JobHandle left, JobHandle right) => left.Equals(right);
    public static bool operator !=(JobHandle left, JobHandle right) => !left.Equals(right);
}
