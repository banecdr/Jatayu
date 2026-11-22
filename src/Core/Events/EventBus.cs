// ==================================================================================================
// Jatayu Engine - Core Platform
// Module: EventBus.cs
// Role: High-performance, low-latency publish/subscribe event dispatcher for engine-wide events.
// ==================================================================================================

using System.Runtime.CompilerServices;

namespace Jatayu.Core.Events;

/// <summary>
/// Delegate for handling strongly-typed events published via <see cref="EventBus"/>.
/// </summary>
/// <typeparam name="T">The event data type (recommended to be a struct).</typeparam>
public delegate void EventHandler<T>(in T evt);

/// <summary>
/// Fast publish/subscribe bus for decoupling engine subsystems (e.g. window resizing, cell loading, origin shifting).
/// Steady-state publishing produces 0 GC allocations.
/// </summary>
public static class EventBus
{
    private static class EventRegistry<T>
    {
        private static readonly object s_lock = new();
        private static EventHandler<T>?[] s_listeners = new EventHandler<T>?[16];
        private static int s_count = 0;

        public static void Subscribe(EventHandler<T> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (s_lock)
            {
                if (s_count >= s_listeners.Length)
                {
                    Array.Resize(ref s_listeners, s_listeners.Length * 2);
                }
                s_listeners[s_count++] = handler;
            }
        }

        public static void Unsubscribe(EventHandler<T> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (s_lock)
            {
                for (int i = 0; i < s_count; i++)
                {
                    if (s_listeners[i] == handler)
                    {
                        s_listeners[i] = s_listeners[s_count - 1];
                        s_listeners[s_count - 1] = null;
                        s_count--;
                        break;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Publish(in T evt)
        {
            // Snapshot count to avoid locking during invocation in hot paths
            int count = s_count;
            var listeners = s_listeners;
            for (int i = 0; i < count; i++)
            {
                listeners[i]?.Invoke(in evt);
            }
        }

        public static void Clear()
        {
            lock (s_lock)
            {
                Array.Clear(s_listeners, 0, s_count);
                s_count = 0;
            }
        }
    }

    /// <summary>
    /// Subscribes a listener callback to events of type <typeparamref name="T"/>.
    /// </summary>
    public static void Subscribe<T>(EventHandler<T> handler) => EventRegistry<T>.Subscribe(handler);

    /// <summary>
    /// Unsubscribes a listener callback from events of type <typeparamref name="T"/>.
    /// </summary>
    public static void Unsubscribe<T>(EventHandler<T> handler) => EventRegistry<T>.Unsubscribe(handler);

    /// <summary>
    /// Publishes an event to all registered listeners with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Publish<T>(in T evt) => EventRegistry<T>.Publish(in evt);

    /// <summary>
    /// Clears all event registrations for the specified type <typeparamref name="T"/>.
    /// </summary>
    public static void Clear<T>() => EventRegistry<T>.Clear();
}

/// <summary>
/// Event broadcast when the native operating system window changes dimensions.
/// </summary>
public readonly struct WindowResizedEvent
{
    public readonly int Width;
    public readonly int Height;

    public WindowResizedEvent(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>
/// Event broadcast when the simulation origin is rebased to avoid floating point precision degradation.
/// </summary>
public readonly struct OriginShiftedEvent
{
    public readonly double DeltaX;
    public readonly double DeltaY;
    public readonly double DeltaZ;

    public OriginShiftedEvent(double deltaX, double deltaY, double deltaZ)
    {
        DeltaX = deltaX;
        DeltaY = deltaY;
        DeltaZ = deltaZ;
    }
}
