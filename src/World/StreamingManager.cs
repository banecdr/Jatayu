/* =================================================================================================
 * FILE: StreamingManager.cs
 * LOCATION: src/World/StreamingManager.cs
 * 
 * WHAT THIS FILE DOES:
 *   Central controller orchestrating asynchronous open-world cell streaming:
 *   - Velocity-scaled lookahead radius prediction (D19) capped at 900m (D23).
 *   - 150m hysteresis unloading band to prevent border thrashing (D4).
 *   - Directional loading queue prioritization along player velocity vector.
 *   - Multithreaded background IO/decoding via JobSystem / ThreadPool.
 *   - Strict per-frame CPU finalization time budgeting (1.0 ms) to guarantee 0 hitches > 2 ms.
 *   - Dynamic VRAM pressure throttling via VramBudget.
 * ================================================================================================= */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Assets;
using Jatayu.Core.Events;
using Jatayu.Core.Jobs;
using Jatayu.Core.Mathematics;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.World;

/// <summary>
/// Prioritized loading queue entry sorted by directional distance to player.
/// </summary>
public readonly struct PrioritizedLoadRequest : IComparable<PrioritizedLoadRequest>
{
    public readonly CellCoord Coord;
    public readonly float Priority; // Lower value = higher priority (closer to camera along velocity)

    public PrioritizedLoadRequest(CellCoord coord, float priority)
    {
        Coord = coord;
        Priority = priority;
    }

    public int CompareTo(PrioritizedLoadRequest other) => Priority.CompareTo(other.Priority);
}

/// <summary>
/// Coordinates asynchronous world cell streaming, velocity prefetching, and main-thread finalization.
/// </summary>
public sealed class StreamingManager : IDisposable
{
    private readonly WorldGrid _grid;
    private readonly WorldConfig _config;
    private readonly PackReader? _packReader;
    private readonly JobSystem? _jobSystem;
    private readonly VramBudget? _vramBudget;

    // Concurrency and state tracking
    private readonly HashSet<CellCoord> _inFlightLoads = new();
    private readonly ConcurrentQueue<DecodedCellPayload> _readyToFinalize = new();
    private readonly List<PrioritizedLoadRequest> _pendingRequests = new(256);

    // Reusable query buffers (zero heap allocation in update loop)
    private readonly List<CellCoord> _candidateCoords = new(256);
    private readonly List<WorldCell> _cellsToUnload = new(64);

    private readonly object _stateLock = new();
    private readonly Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private bool _disposed;

    // Telemetry & Diagnostics
    public int InFlightCount => _inFlightLoads.Count;
    public int ReadyToFinalizeCount => _readyToFinalize.Count;
    public int ActiveCellCount => _grid.ActiveCellCount;
    public float EffectiveLoadRadius { get; private set; }
    public float CurrentSpeedMps { get; private set; }
    public float LastStreamingTimeMs { get; private set; }

    public WorldGrid Grid => _grid;
    public WorldConfig Config => _config;

    public StreamingManager(
        WorldGrid grid,
        WorldConfig? config = null,
        PackReader? packReader = null,
        JobSystem? jobSystem = null,
        VramBudget? vramBudget = null)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _config = config ?? new WorldConfig();
        _packReader = packReader;
        _jobSystem = jobSystem;
        _vramBudget = vramBudget;

        EffectiveLoadRadius = _config.LoadRadiusRingA;

        // Subscribe to floating-origin rebase events
        _originShiftHandler = OnOriginShifted;
        EventBus.Subscribe<OriginShiftedEvent>(_originShiftHandler);
    }

    private void OnOriginShifted(in OriginShiftedEvent e)
    {
        _grid.ShiftOrigin(e.DeltaX, e.DeltaY, e.DeltaZ);
    }

    /// <summary>
    /// Evaluates player movement and updates streaming queues.
    /// Executes each frame within CPU budget (< 1.2 ms).
    /// </summary>
    public void Update(double deltaTime, in WorldPosition playerPos, in Vector3 playerVelocity)
    {
        long startTicks = Stopwatch.GetTimestamp();

        // 1. Compute velocity and effective prefetch radius (D19, D23)
        float speed = MathF.Sqrt((playerVelocity.X * playerVelocity.X) + (playerVelocity.Z * playerVelocity.Z));
        CurrentSpeedMps = speed;

        float effectiveRadius = WorldConstants.ComputeVelocityScaledRadius(
            _config.LoadRadiusRingA,
            speed,
            _config.LeadSeconds,
            _config.PrefetchRadiusCap
        );

        // VRAM pressure throttling: if memory pressure exceeds threshold, clamp radius to base Ring A
        if (_vramBudget != null && _vramBudget.QueryStats().BudgetUsagePercent > _config.VramPressureThreshold)
        {
            effectiveRadius = _config.LoadRadiusRingA;
        }

        EffectiveLoadRadius = effectiveRadius;

        // 2. Query cells within effective radius
        _grid.GetCellCoordsInRadius(playerPos, effectiveRadius, _candidateCoords);

        // Normalize velocity direction for priority sorting
        Vector2 velDir = speed > 0.01f
            ? new Vector2(playerVelocity.X / speed, playerVelocity.Z / speed)
            : Vector2.Zero;

        // 3. Populate and prioritize load requests
        lock (_stateLock)
        {
            _pendingRequests.Clear();

            for (int i = 0; i < _candidateCoords.Count; i++)
            {
                var coord = _candidateCoords[i];
                var cell = _grid.GetOrCreateCell(coord);

                if (cell.State == CellState.Unloaded && !_inFlightLoads.Contains(coord))
                {
                    coord.GetWorldCenter(_config.CellSize, out double cx, out double cz);
                    float dx = (float)(cx - playerPos.X);
                    float dz = (float)(cz - playerPos.Z);
                    float dist = MathF.Sqrt(dx * dx + dz * dz);

                    // Directional dot product: cells along travel direction get a substantial priority discount
                    float forwardProjection = (velDir.X * dx) + (velDir.Y * dz);
                    float priority = dist - (forwardProjection * 1.5f);

                    _pendingRequests.Add(new PrioritizedLoadRequest(coord, priority));
                }
            }

            _pendingRequests.Sort();

            // 4. Dispatch background loading jobs
            while (_pendingRequests.Count > 0 && _inFlightLoads.Count < _config.MaxConcurrentLoadingJobs)
            {
                var next = _pendingRequests[0];
                _pendingRequests.RemoveAt(0);

                var cell = _grid.GetOrCreateCell(next.Coord);
                cell.State = CellState.LoadingIO;
                _inFlightLoads.Add(next.Coord);

                DispatchLoadJob(cell);
            }
        }

        // 5. Main-Thread Finalization within CPU Time Budget
        FinalizeCompletedCells();

        // 6. Unload distant cells outside hysteresis radius (D4)
        EvaluateUnloads(playerPos);

        long endTicks = Stopwatch.GetTimestamp();
        LastStreamingTimeMs = (float)((endTicks - startTicks) * 1000.0 / Stopwatch.Frequency);
    }

    private void DispatchLoadJob(WorldCell cell)
    {
        var coord = cell.Coord;
        var reader = _packReader;

        void JobAction()
        {
            if (Volatile.Read(ref _disposed))
                return;

            DecodedCellPayload? payload = null;

            if (reader != null)
            {
                CellLoader.TryLoadCellFromPack(reader, coord, out payload);
            }

            if (payload == null)
            {
                // Fallback: empty cell header
                var header = CookedCellHeader.Create(
                    coord,
                    coord.ToAssetGuid(),
                    0, 0, 0,
                    CellFlags.None,
                    0, 0, 0,
                    WorldConstants.CellSize, 10, WorldConstants.CellSize
                );
                payload = new DecodedCellPayload(coord, in header, Array.Empty<CookedStaticInstance>(), Array.Empty<CookedColliderRecord>(), Array.Empty<CookedAudioPointRecord>());
            }

            _readyToFinalize.Enqueue(payload);
        }

        if (_jobSystem != null)
        {
            _jobSystem.Schedule(JobAction, JobPriority.Normal);
        }
        else
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ => JobAction());
        }
    }

    private void FinalizeCompletedCells()
    {
        long budgetTicks = (long)(_config.FinalizationBudgetMs * Stopwatch.Frequency / 1000.0);
        long startTicks = Stopwatch.GetTimestamp();

        while (_readyToFinalize.TryDequeue(out var payload))
        {
            lock (_stateLock)
            {
                _inFlightLoads.Remove(payload.Coord);
            }

            if (_grid.TryGetCell(payload.Coord, out var cell))
            {
                CellLoader.ApplyPayloadToCell(cell, payload);
                _grid.MarkActive(cell);
            }

            // Enforce time budgeting
            if ((Stopwatch.GetTimestamp() - startTicks) >= budgetTicks)
            {
                break;
            }
        }
    }

    private void EvaluateUnloads(in WorldPosition playerPos)
    {
        _cellsToUnload.Clear();
        float unloadRadiusSq = _config.UnloadRadiusRingA * _config.UnloadRadiusRingA;

        var activeCells = _grid.ActiveCells;
        for (int i = 0; i < activeCells.Count; i++)
        {
            var cell = activeCells[i];
            double distSq = cell.Center.DistanceSquaredTo(playerPos);
            if (distSq > unloadRadiusSq)
            {
                _cellsToUnload.Add(cell);
            }
        }

        for (int i = 0; i < _cellsToUnload.Count; i++)
        {
            var cell = _cellsToUnload[i];
            _grid.MarkUnloaded(cell);
        }
    }

    /// <summary>
    /// Synchronously loads a cell immediately (blocking until finished). Useful for tests.
    /// </summary>
    public bool ForceSyncLoad(CellCoord coord)
    {
        var cell = _grid.GetOrCreateCell(coord);
        DecodedCellPayload? payload = null;

        if (_packReader != null)
        {
            CellLoader.TryLoadCellFromPack(_packReader, coord, out payload);
        }

        if (payload != null)
        {
            CellLoader.ApplyPayloadToCell(cell, payload);
            _grid.MarkActive(cell);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            EventBus.Unsubscribe<OriginShiftedEvent>(_originShiftHandler);
            _pendingRequests.Clear();
            _inFlightLoads.Clear();
            while (_readyToFinalize.TryDequeue(out _)) { }
        }
    }
}
