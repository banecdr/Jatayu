/* =================================================================================================
 * FILE: DeferredDeletionQueueTests.cs
 * LOCATION: tests/Jatayu.Tests/DeferredDeletionQueueTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating the DeferredDeletionQueue:
 *   - Defers resource disposal until the target GPU frame retires
 *   - Correctly disposes Action callbacks and IDisposable instances
 *   - FlushAll() disposes all pending items on shutdown
 * ================================================================================================= */

using System;
using Jatayu.RHI.Vulkan.Memory;
using Xunit;

namespace Jatayu.Tests;

public class DeferredDeletionQueueTests
{
    private sealed class DummyDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    [Fact]
    public void Enqueue_DefersExecutionUntilTargetFrameFlushed()
    {
        using var queue = new DeferredDeletionQueue();

        bool actionFired = false;
        var dummy = new DummyDisposable();

        // Enqueue to be deleted when frame 3 completes
        queue.Enqueue(() => actionFired = true, frameIndex: 3);
        queue.Enqueue(dummy, frameIndex: 3);

        Assert.Equal(2, queue.PendingCount);

        // Flush frame 1: should NOT trigger frame 3 items
        queue.Flush(1);
        Assert.False(actionFired);
        Assert.False(dummy.IsDisposed);
        Assert.Equal(2, queue.PendingCount);

        // Flush frame 2: still should NOT trigger
        queue.Flush(2);
        Assert.False(actionFired);
        Assert.False(dummy.IsDisposed);
        Assert.Equal(2, queue.PendingCount);

        // Flush frame 3: NOW both should trigger!
        queue.Flush(3);
        Assert.True(actionFired);
        Assert.True(dummy.IsDisposed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void FlushAll_ExecutesAllPendingImmediately()
    {
        using var queue = new DeferredDeletionQueue();

        int executedCount = 0;
        queue.Enqueue(() => executedCount++, frameIndex: 10);
        queue.Enqueue(() => executedCount++, frameIndex: 20);
        queue.Enqueue(() => executedCount++, frameIndex: 30);

        Assert.Equal(3, queue.PendingCount);

        queue.FlushAll();

        Assert.Equal(3, executedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Dispose_AutomaticallyFlushesRemainingItems()
    {
        int executedCount = 0;
        {
            using var queue = new DeferredDeletionQueue();
            queue.Enqueue(() => executedCount++, frameIndex: 100);
            Assert.Equal(1, queue.PendingCount);
        } // Dispose called here

        Assert.Equal(1, executedCount);
    }
}
