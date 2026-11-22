/* =================================================================================================
 * FILE: JobSystem.cs
 * LOCATION: src/Core/Jobs/JobSystem.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-performance, work-stealing multithreaded job scheduler sized to physical CPU cores.
 *   Provides zero-allocation struct job scheduling, priority queues (High, Normal, Background),
 *   dependency resolution DAGs, and cooperative work-stealing while waiting on JobHandles.
 * ================================================================================================= */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Jatayu.Core.Jobs;

/// <summary>
/// Multithreaded job scheduler that coordinates parallel task execution across dedicated worker threads.
/// </summary>
public sealed class JobSystem : IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly struct JobSlot
    {
        public readonly int Index;
        public readonly ManualResetEventSlim CompletionEvent;
        public readonly List<int> Dependents;
        public readonly object Lock;

        public JobSlot(int index)
        {
            Index = index;
            CompletionEvent = new ManualResetEventSlim(true);
            Dependents = new List<int>(4);
            Lock = new object();
        }
    }

    private sealed class SlotState
    {
        public int Version = 1;
        public Action? Action;
        public IJob? StructJob;
        public JobPriority Priority = JobPriority.Normal;
        public int RemainingDependencies;
        public volatile bool IsCompleted = true;
    }

    private readonly Thread[] _workers;
    private readonly ConcurrentQueue<int>[] _workerQueues;
    private readonly ConcurrentQueue<int> _highQueue = new();
    private readonly ConcurrentQueue<int> _normalQueue = new();
    private readonly ConcurrentQueue<int> _backgroundQueue = new();

    private readonly JobSlot[] _slots;
    private readonly SlotState[] _slotStates;
    private readonly ConcurrentQueue<int> _freeSlots = new();

    private readonly SemaphoreSlim _workSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    [ThreadStatic]
    private static int t_workerIndex = -1;

    /// <summary>
    /// Gets the number of worker threads executing jobs.
    /// </summary>
    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Initializes a new JobSystem. Defaults worker count to Environment.ProcessorCount - 2 (minimum 1).
    /// </summary>
    /// <param name="workerCount">Optional override for worker thread count.</param>
    /// <param name="capacity">Maximum concurrent in-flight job capacity.</param>
    public JobSystem(int? workerCount = null, int capacity = DefaultCapacity)
    {
        int count = workerCount ?? Math.Max(1, Environment.ProcessorCount - 2);
        _workers = new Thread[count];
        _workerQueues = new ConcurrentQueue<int>[count];

        for (int i = 0; i < count; i++)
        {
            _workerQueues[i] = new ConcurrentQueue<int>();
        }

        _slots = new JobSlot[capacity];
        _slotStates = new SlotState[capacity];

        for (int i = 0; i < capacity; i++)
        {
            _slots[i] = new JobSlot(i);
            _slotStates[i] = new SlotState();
            _freeSlots.Enqueue(i);
        }

        for (int i = 0; i < count; i++)
        {
            int index = i;
            _workers[i] = new Thread(() => WorkerLoop(index))
            {
                Name = $"Jatayu Worker #{index}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _workers[i].Start();
        }
    }

    /// <summary>
    /// Schedules a delegate action for execution on the job system.
    /// </summary>
    public JobHandle Schedule(
        Action action,
        JobPriority priority = JobPriority.Normal,
        JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return AllocateAndSchedule(action, null, priority, dependency);
    }

    /// <summary>
    /// Schedules a zero-allocation struct job for execution on the job system.
    /// </summary>
    public JobHandle Schedule<TJob>(
        TJob job,
        JobPriority priority = JobPriority.Normal,
        JobHandle dependency = default) where TJob : struct, IJob
    {
        return AllocateAndSchedule(null, job, priority, dependency);
    }

    /// <summary>
    /// Creates a barrier dependency handle waiting on two prerequisite jobs.
    /// </summary>
    public JobHandle Combine(JobHandle a, JobHandle b)
    {
        if (!a.IsValid && !b.IsValid) return JobHandle.None;
        if (!a.IsValid) return b;
        if (!b.IsValid) return a;
        if (a.IsCompleted && b.IsCompleted) return JobHandle.None;
        if (a.IsCompleted) return b;
        if (b.IsCompleted) return a;

        int slotIndex = AcquireSlot();
        var slot = _slots[slotIndex];
        var state = _slotStates[slotIndex];

        state.Action = null;
        state.StructJob = null;
        state.Priority = JobPriority.High;
        state.RemainingDependencies = 2;
        state.IsCompleted = false;
        slot.CompletionEvent.Reset();

        int version = state.Version;
        var handle = new JobHandle(this, slotIndex, version);

        RegisterDependent(a.SlotIndex, a.Version, slotIndex);
        RegisterDependent(b.SlotIndex, b.Version, slotIndex);

        return handle;
    }

    /// <summary>
    /// Creates a barrier dependency handle waiting on multiple prerequisite jobs.
    /// </summary>
    public JobHandle Combine(ReadOnlySpan<JobHandle> handles)
    {
        int validCount = 0;
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i].IsValid && !handles[i].IsCompleted)
                validCount++;
        }

        if (validCount == 0)
            return JobHandle.None;

        int slotIndex = AcquireSlot();
        var slot = _slots[slotIndex];
        var state = _slotStates[slotIndex];

        state.Action = null;
        state.StructJob = null;
        state.Priority = JobPriority.High;
        state.RemainingDependencies = validCount;
        state.IsCompleted = false;
        slot.CompletionEvent.Reset();

        int version = state.Version;
        var handle = new JobHandle(this, slotIndex, version);

        for (int i = 0; i < handles.Length; i++)
        {
            var dep = handles[i];
            if (!dep.IsValid || dep.IsCompleted)
                continue;

            RegisterDependent(dep.SlotIndex, dep.Version, slotIndex);
        }

        return handle;
    }

    internal bool IsJobCompleted(int slotIndex, int version)
    {
        if ((uint)slotIndex >= (uint)_slotStates.Length) return true;
        var state = _slotStates[slotIndex];
        if (state.Version != version) return true;
        return state.IsCompleted;
    }

    internal void WaitForJob(int slotIndex, int version)
    {
        if ((uint)slotIndex >= (uint)_slotStates.Length) return;
        var slot = _slots[slotIndex];
        var state = _slotStates[slotIndex];

        while (state.Version == version && !state.IsCompleted)
        {
            // Cooperatively execute other available jobs while waiting
            if (TryExecuteOneJob())
                continue;

            // Wait on the completion event with a brief timeout before checking again
            slot.CompletionEvent.Wait(1, _cts.Token);
        }
    }

    private JobHandle AllocateAndSchedule(
        Action? action,
        IJob? structJob,
        JobPriority priority,
        JobHandle dependency)
    {
        int slotIndex = AcquireSlot();
        var slot = _slots[slotIndex];
        var state = _slotStates[slotIndex];

        state.Action = action;
        state.StructJob = structJob;
        state.Priority = priority;
        state.RemainingDependencies = (dependency.IsValid && !dependency.IsCompleted) ? 1 : 0;
        state.IsCompleted = false;
        slot.CompletionEvent.Reset();

        int version = state.Version;
        var handle = new JobHandle(this, slotIndex, version);

        if (state.RemainingDependencies > 0)
        {
            RegisterDependent(dependency.SlotIndex, dependency.Version, slotIndex);
        }
        else
        {
            EnqueueReadyJob(slotIndex, priority);
        }

        return handle;
    }

    private void RegisterDependent(int prerequisiteSlot, int prerequisiteVersion, int dependentSlot)
    {
        var prereqSlot = _slots[prerequisiteSlot];
        var prereqState = _slotStates[prerequisiteSlot];

        bool alreadyDone = false;
        lock (prereqSlot.Lock)
        {
            if (prereqState.Version == prerequisiteVersion && !prereqState.IsCompleted)
            {
                prereqSlot.Dependents.Add(dependentSlot);
            }
            else
            {
                alreadyDone = true;
            }
        }

        if (alreadyDone)
        {
            // Dependency completed before registration; decrement dependency counter
            OnPrerequisiteCompleted(dependentSlot);
        }
    }

    private void OnPrerequisiteCompleted(int dependentSlot)
    {
        var depState = _slotStates[dependentSlot];
        if (Interlocked.Decrement(ref depState.RemainingDependencies) == 0)
        {
            EnqueueReadyJob(dependentSlot, depState.Priority);
        }
    }

    private void EnqueueReadyJob(int slotIndex, JobPriority priority)
    {
        switch (priority)
        {
            case JobPriority.High:
                _highQueue.Enqueue(slotIndex);
                break;
            case JobPriority.Normal:
                int worker = t_workerIndex;
                if (worker >= 0 && worker < _workerQueues.Length)
                    _workerQueues[worker].Enqueue(slotIndex);
                else
                    _normalQueue.Enqueue(slotIndex);
                break;
            case JobPriority.Background:
                _backgroundQueue.Enqueue(slotIndex);
                break;
        }

        _workSignal.Release();
    }

    private int AcquireSlot()
    {
        int index;
        while (!_freeSlots.TryDequeue(out index))
        {
            // All slots exhausted; assist in executing jobs to free capacity
            if (!TryExecuteOneJob())
            {
                Thread.Yield();
            }
        }
        return index;
    }

    private void ReleaseSlot(int index)
    {
        var state = _slotStates[index];
        state.Action = null;
        state.StructJob = null;
        Interlocked.Increment(ref state.Version);

        _freeSlots.Enqueue(index);
    }

    private void WorkerLoop(int workerIndex)
    {
        t_workerIndex = workerIndex;

        while (!_cts.IsCancellationRequested)
        {
            if (TryExecuteOneJob())
                continue;

            try
            {
                _workSignal.Wait(50, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool TryExecuteOneJob()
    {
        // 1. Try High Priority global queue
        if (_highQueue.TryDequeue(out int slotIndex))
        {
            ExecuteSlot(slotIndex);
            return true;
        }

        // 2. Try current worker's local queue
        int worker = t_workerIndex;
        if (worker >= 0 && worker < _workerQueues.Length)
        {
            if (_workerQueues[worker].TryDequeue(out slotIndex))
            {
                ExecuteSlot(slotIndex);
                return true;
            }
        }

        // 3. Try Normal Priority global queue
        if (_normalQueue.TryDequeue(out slotIndex))
        {
            ExecuteSlot(slotIndex);
            return true;
        }

        // 4. Try work-stealing from another worker's queue
        if (_workerQueues.Length > 1)
        {
            int start = worker >= 0 ? (worker + 1) % _workerQueues.Length : 0;
            for (int i = 0; i < _workerQueues.Length; i++)
            {
                int target = (start + i) % _workerQueues.Length;
                if (target != worker && _workerQueues[target].TryDequeue(out slotIndex))
                {
                    ExecuteSlot(slotIndex);
                    return true;
                }
            }
        }

        // 5. Try Background Priority queue
        if (_backgroundQueue.TryDequeue(out slotIndex))
        {
            ExecuteSlot(slotIndex);
            return true;
        }

        return false;
    }

    private void ExecuteSlot(int slotIndex)
    {
        var slot = _slots[slotIndex];
        var state = _slotStates[slotIndex];

        try
        {
            state.StructJob?.Execute();
            state.Action?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JobSystem] Unhandled exception in job slot {slotIndex}: {ex}");
        }
        finally
        {
            // Mark completed and notify waiting handles
            state.IsCompleted = true;
            slot.CompletionEvent.Set();

            // Wake up dependent jobs
            List<int>? deps = null;
            lock (slot.Lock)
            {
                if (slot.Dependents.Count > 0)
                {
                    deps = new List<int>(slot.Dependents);
                    slot.Dependents.Clear();
                }
            }

            if (deps != null)
            {
                for (int i = 0; i < deps.Count; i++)
                {
                    OnPrerequisiteCompleted(deps[i]);
                }
            }

            ReleaseSlot(slotIndex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _workSignal.Release(_workers.Length * 2);

        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].Join(200);
        }

        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].CompletionEvent.Dispose();
        }

        _workSignal.Dispose();
        _cts.Dispose();
    }
}
