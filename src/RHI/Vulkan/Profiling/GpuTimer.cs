/* =================================================================================================
 * FILE: GpuTimer.cs
 * LOCATION: src/RHI/Vulkan/Profiling/GpuTimer.cs
 * 
 * WHAT THIS FILE DOES:
 *   Wraps Vulkan timestamp query pools to measure exact GPU hardware execution times per pass.
 *   Uses double-buffered latency readback so GPU time queries never stall rendering.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Profiling;

/// <summary>
/// Hardware timestamp query manager for measuring per-pass GPU execution duration in milliseconds.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    public readonly struct GpuPassTiming
    {
        public string Name { get; }
        public float DurationMs { get; }

        public GpuPassTiming(string name, float durationMs)
        {
            Name = name;
            DurationMs = durationMs;
        }
    }

    private readonly VulkanContext _context;
    private readonly VkQueryPool[] _queryPools;
    private readonly int _maxQueriesPerFrame;
    private readonly int _framesInFlight;
    private readonly float _timestampPeriod; // nanoseconds per tick
    private readonly Dictionary<string, (uint StartIdx, uint EndIdx)>[] _passQueries;
    private readonly List<GpuPassTiming> _publishedTimings = new(32);
    private readonly ulong[] _resultsBuffer;
    private int _currentFrame;
    private uint _queryCounter;
    private bool _disposed;

    /// <summary>
    /// Gets the most recent completed frame's GPU pass timings in milliseconds.
    /// </summary>
    public IReadOnlyList<GpuPassTiming> LatestSnapshot => _publishedTimings;

    /// <summary>
    /// Initializes a multi-buffered GPU query pool for timestamp profiling.
    /// </summary>
    public unsafe GpuTimer(VulkanContext context, int maxQueriesPerFrame = 64, int framesInFlight = 2)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _maxQueriesPerFrame = maxQueriesPerFrame;
        _framesInFlight = Math.Max(1, framesInFlight);

        // Query GPU timestamp period (nanoseconds per counter tick)
        var props = new VkPhysicalDeviceProperties();
        context.Instance.PInvoke.vkGetPhysicalDeviceProperties.Invoke(context.PhysicalDevice.Handle, &props);
        _timestampPeriod = props.Limits.TimestampPeriod;

        _queryPools = new VkQueryPool[_framesInFlight];
        _passQueries = new Dictionary<string, (uint, uint)>[_framesInFlight];
        _resultsBuffer = new ulong[_maxQueriesPerFrame];

        for (int i = 0; i < _framesInFlight; i++)
        {
            _passQueries[i] = new Dictionary<string, (uint, uint)>(maxQueriesPerFrame / 2);

            var poolInfo = new VkQueryPoolCreateInfo
            {
                QueryType = VkQueryType.Timestamp,
                QueryCount = maxQueriesPerFrame
            };

            _queryPools[i] = _context.Device.CreateQueryPool(poolInfo, null);
        }
    }

    /// <summary>
    /// Resets and prepares the query pool for the current frame, and reads back results from the previous cycle.
    /// </summary>
    public unsafe void BeginFrame(CommandBuffer cmd, int frameIndex)
    {
        ThrowIfDisposed();

        _currentFrame = frameIndex % _framesInFlight;
        _queryCounter = 0;

        var pool = _queryPools[_currentFrame];

        // 1. Read back results from this frame slot (written on previous iteration)
        var passes = _passQueries[_currentFrame];
        if (passes.Count > 0)
        {
            uint maxQueryIndex = 0;
            foreach (var (_, (startIdx, endIdx)) in passes)
            {
                if (startIdx > maxQueryIndex) maxQueryIndex = startIdx;
                if (endIdx > maxQueryIndex) maxQueryIndex = endIdx;
            }
            uint queryCount = Math.Min(maxQueryIndex + 1, (uint)_resultsBuffer.Length);

            var results = _resultsBuffer;
            fixed (ulong* pResults = results)
            {
                var res = _context.Device.PInvoke.vkGetQueryPoolResults.Invoke(
                    _context.Device.Handle,
                    pool,
                    0,
                    (int)queryCount,
                    (nuint)(queryCount * sizeof(ulong)),
                    pResults,
                    new ByteSize64(sizeof(ulong)),
                    VkQueryResultFlags._64Bit
                );

                if (res == VkResult.Success)
                {
                    _publishedTimings.Clear();
                    foreach (var (name, (startIdx, endIdx)) in passes)
                    {
                        if (startIdx < queryCount && endIdx < queryCount && endIdx >= startIdx)
                        {
                            ulong delta = results[endIdx] - results[startIdx];
                            float ms = (float)(delta * _timestampPeriod / 1_000_000.0);
                            _publishedTimings.Add(new GpuPassTiming(name, Math.Max(0f, ms)));
                        }
                    }
                }
            }
        }

        // 2. Reset query pool for new recordings
        passes.Clear();
        _context.Device.PInvoke.vkCmdResetQueryPool.Invoke(cmd.Handle, pool, 0, _maxQueriesPerFrame);
    }

    /// <summary>
    /// Begins measuring a named GPU pass by writing a top-of-pipe timestamp.
    /// </summary>
    public unsafe void BeginPass(CommandBuffer cmd, string passName)
    {
        ThrowIfDisposed();

        if (_queryCounter + 2 > (uint)_maxQueriesPerFrame)
            return;

        uint startIdx = _queryCounter++;
        _context.Device.PInvoke.vkCmdWriteTimestamp.Invoke(
            cmd.Handle,
            VkPipelineStageFlags.TopOfPipeBit,
            _queryPools[_currentFrame],
            (int)startIdx
        );

        _passQueries[_currentFrame][passName] = (startIdx, 0);
    }

    /// <summary>
    /// Ends measuring a named GPU pass by writing a bottom-of-pipe timestamp.
    /// </summary>
    public unsafe void EndPass(CommandBuffer cmd, string passName)
    {
        ThrowIfDisposed();

        if (_queryCounter >= (uint)_maxQueriesPerFrame)
            return;

        if (_passQueries[_currentFrame].TryGetValue(passName, out var indices))
        {
            uint endIdx = _queryCounter++;
            _context.Device.PInvoke.vkCmdWriteTimestamp.Invoke(
                cmd.Handle,
                VkPipelineStageFlags.BottomOfPipeBit,
                _queryPools[_currentFrame],
                (int)endIdx
            );

            _passQueries[_currentFrame][passName] = (indices.StartIdx, endIdx);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _framesInFlight; i++)
        {
            if (_queryPools[i].VkHandle != 0)
            {
                _context.Device.DestroyQueryPool(_queryPools[i], null);
            }
        }
    }
}
