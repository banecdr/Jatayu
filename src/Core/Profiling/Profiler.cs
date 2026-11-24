/* =================================================================================================
 * FILE: Profiler.cs
 * LOCATION: src/Core/Profiling/Profiler.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-precision, zero-allocation CPU zone profiler.
 *   Provides microsecond timing of render graph passes, simulation ticks, and streaming jobs.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Jatayu.Core.Profiling;

/// <summary>
/// Lightweight CPU zone profiler with microsecond precision and zero heap allocations.
/// </summary>
public static class Profiler
{
    public readonly struct ZoneTiming
    {
        public string Name { get; }
        public float DurationMs { get; }

        public ZoneTiming(string name, float durationMs)
        {
            Name = name;
            DurationMs = durationMs;
        }
    }

    private static readonly Dictionary<string, long> s_zoneStartTicks = new(64);
    private static readonly List<ZoneTiming> s_completedZones = new(64);
    private static readonly List<ZoneTiming> s_publishedSnapshot = new(64);
    private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;
    private static long s_frameStartTicks;
    private static float s_frameDurationMs;

    /// <summary>
    /// Gets the most recent completed frame's zone timings in milliseconds.
    /// </summary>
    public static IReadOnlyList<ZoneTiming> LatestSnapshot => s_publishedSnapshot;

    public static float FrameDurationMs => s_frameDurationMs;

    public static float MeasuredCpuDurationMs
    {
        get
        {
            float total = 0f;
            foreach (var zone in s_publishedSnapshot)
                total += zone.DurationMs;
            return total;
        }
    }

    /// <summary>
    /// Marks the start of a CPU frame.
    /// </summary>
    public static void BeginFrame()
    {
        s_zoneStartTicks.Clear();
        s_completedZones.Clear();
        s_frameStartTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Marks the end of a CPU frame and publishes the snapshot for HUD/DebugUI rendering.
    /// </summary>
    public static void EndFrame()
    {
        long elapsed = Stopwatch.GetTimestamp() - s_frameStartTicks;
        s_frameDurationMs = (float)(elapsed * s_tickToMs);
        s_publishedSnapshot.Clear();
        s_publishedSnapshot.AddRange(s_completedZones);
    }

    /// <summary>
    /// Starts measuring a named zone.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginZone(string name)
    {
        s_zoneStartTicks[name] = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Ends measuring a named zone.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndZone(string name)
    {
        if (s_zoneStartTicks.TryGetValue(name, out var start))
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            float ms = (float)(elapsed * s_tickToMs);
            s_completedZones.Add(new ZoneTiming(name, ms));
        }
    }

    /// <summary>
    /// Measures an execution scope using C# 'using' syntax with zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfileScope Scope(string name)
    {
        BeginZone(name);
        return new ProfileScope(name);
    }

    /// <summary>
    /// Ref struct wrapping a profile zone to guarantee zero heap allocations.
    /// </summary>
    public readonly ref struct ProfileScope
    {
        private readonly string _name;

        public ProfileScope(string name)
        {
            _name = name;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            EndZone(_name);
        }
    }
}
