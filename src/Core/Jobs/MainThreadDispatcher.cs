/* =================================================================================================
 * FILE: MainThreadDispatcher.cs
 * LOCATION: src/Core/Jobs/MainThreadDispatcher.cs
 * 
 * WHAT THIS FILE DOES:
 *   Thread-safe queue for job results requiring main-thread execution (GPU resource finalization,
 *   scene mutations, GLFW window callbacks). Provides time-budgeted draining (e.g. max 2.0 ms)
 *   to ensure background asset completions never cause frame hitches.
 * ================================================================================================= */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Jatayu.Core.Jobs;

/// <summary>
/// Dispatches asynchronous worker thread callbacks and completion actions to the main game loop thread.
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    /// <summary>
    /// Gets the number of pending actions currently waiting in the dispatcher queue.
    /// </summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Enqueues an action to be executed on the main game thread.
    /// </summary>
    /// <param name="action">The delegate to execute on the main thread.</param>
    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Enqueue(action);
    }

    /// <summary>
    /// Drains actions from the queue on the main thread until the queue is empty or the time budget expires.
    /// </summary>
    /// <param name="maxTimeMilliseconds">Maximum allowed CPU execution time in milliseconds before returning.</param>
    /// <returns>The number of actions successfully executed.</returns>
    public int Drain(double maxTimeMilliseconds = 2.0)
    {
        if (_queue.IsEmpty) return 0;

        long start = Stopwatch.GetTimestamp();
        double maxTicks = maxTimeMilliseconds * (Stopwatch.Frequency / 1000.0);
        int executed = 0;

        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
                executed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MainThreadDispatcher] Unhandled exception in dispatched action: {ex}");
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            if (elapsed >= maxTicks)
                break;
        }

        return executed;
    }

    /// <summary>
    /// Drains and executes all pending actions unconditionally without time slicing.
    /// Typically called during scene transitions or shutdown.
    /// </summary>
    /// <returns>The number of actions successfully executed.</returns>
    public int DrainAll()
    {
        int executed = 0;
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
                executed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MainThreadDispatcher] Unhandled exception in dispatched action: {ex}");
            }
        }
        return executed;
    }
}
