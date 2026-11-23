/* =================================================================================================
 * FILE: EventBusTests.cs
 * LOCATION: tests/Jatayu.Tests/EventBusTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating EventBus:
 *   - Verifies zero GC allocation during steady-state publishing
 *   - Verifies subscribe, unsubscribe, and Clear functionality
 * ================================================================================================= */

using System;
using Jatayu.Core.Events;
using Xunit;

namespace Jatayu.Tests;

public class EventBusTests
{
    private readonly struct TestEvent
    {
        public readonly int Value;
        public TestEvent(int val) => Value = val;
    }

    [Fact]
    public void EventBus_SubscribeAndPublish_DispatchesCorrectly()
    {
        EventBus.Clear<TestEvent>();
        int received = 0;

        Jatayu.Core.Events.EventHandler<TestEvent> handler = (in TestEvent e) => received += e.Value;
        EventBus.Subscribe(handler);

        var evt = new TestEvent(42);
        EventBus.Publish(in evt);
        Assert.Equal(42, received);

        EventBus.Unsubscribe(handler);
        EventBus.Publish(in evt);
        Assert.Equal(42, received); // Did not increase
    }

    [Fact]
    public void EventBus_Publish_ZeroGcAllocationsInSteadyState()
    {
        EventBus.Clear<TestEvent>();
        int count = 0;
        Jatayu.Core.Events.EventHandler<TestEvent> handler = (in TestEvent _) => count++;
        EventBus.Subscribe(handler);

        // Warm up JIT
        var evt = new TestEvent(1);
        EventBus.Publish(in evt);

        long memBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            EventBus.Publish(in evt);
        }
        long memAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, memAfter - memBefore);
        Assert.Equal(1001, count);

        EventBus.Clear<TestEvent>();
    }
}
