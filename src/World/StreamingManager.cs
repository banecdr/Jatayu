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
    private readonly PrioritizedLoadRequest[] _topK;

    // Reusable query buffers (zero heap allocation in update loop)
    private readonly List<CellCoord> _candidateCoords = new(256);
    private readonly List<WorldCell> _cellsToUnload = new(64);

    private readonly object _stateLock = new();
    private readonly Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private bool _disposed;

    // Subsystems
    public Physics.PhysicsEngine? Physics { get; set; }
    public Audio.AudioEngine? Audio { get; set; }
    private WorldPosition _lastPlayerPos;

    // Worker pool for zero GC allocation background jobs
    private readonly ConcurrentQueue<CellLoadWorker> _workerPool = new();

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
        _topK = new PrioritizedLoadRequest[Math.Max(1, _config.MaxConcurrentLoadingJobs)];

        EffectiveLoadRadius = _config.LoadRadiusRingA;

        // Pre-allocate worker pool (64 reusable workers with cached delegates)
        for (int i = 0; i < 64; i++)
        {
            _workerPool.Enqueue(new CellLoadWorker(this));
        }

        // Subscribe to floating-origin rebase events
        _originShiftHandler = OnOriginShifted;
        EventBus.Subscribe<OriginShiftedEvent>(_originShiftHandler);
    }

    private void OnOriginShifted(in OriginShiftedEvent e)
    {
        _grid.ShiftOrigin(e.DeltaX, e.DeltaY, e.DeltaZ);
        _lastPlayerPos = new WorldPosition(_lastPlayerPos.X + e.DeltaX, _lastPlayerPos.Y + e.DeltaY, _lastPlayerPos.Z + e.DeltaZ);
    }

    /// <summary>
    /// Evaluates player movement and updates streaming queues.
    /// Executes each frame within CPU budget (< 1.2 ms).
    /// </summary>
    public void Update(double deltaTime, in WorldPosition playerPos, in Vector3 playerVelocity)
    {
        long startTicks = Stopwatch.GetTimestamp();
        _lastPlayerPos = playerPos;

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
            int availableSlots = Math.Min(
                _config.MaxConcurrentLoadingJobs - _inFlightLoads.Count,
                _topK.Length);

            // Early-out: if every loading slot is busy, nothing can be dispatched this
            // frame, so skip the candidate scan entirely.
            if (availableSlots > 0)
            {
                var top = _topK;              // sorted ascending by Priority (best first)
                int count = 0;

                // Hoist loop invariants
                float cellSize = _config.CellSize;
                double px = playerPos.X;
                double pz = playerPos.Z;
                float vx = velDir.X;
                float vz = velDir.Y;

                for (int i = 0; i < _candidateCoords.Count; i++)
                {
                    var coord = _candidateCoords[i];

                    // Priority is pure math, so compute it before any hash lookups.
                    // Subtract in double first, then narrow, to keep precision far
                    // from the world origin (same as the original).
                    coord.GetWorldCenter(cellSize, out double cx, out double cz);
                    float dx = (float)(cx - px);
                    float dz = (float)(cz - pz);

                    // Directional bias: cells along the travel direction get a discount.
                    float priority = MathF.Sqrt(dx * dx + dz * dz)
                                   - 1.5f * (vx * dx + vz * dz);

                    // Cheap reject: can't beat the worst of the K we already hold.
                    if (count == availableSlots && priority >= top[count - 1].Priority)
                        continue;

                    // Only candidates that would make the cut pay for the lookups.
                    if (_inFlightLoads.Contains(coord))
                        continue;

                    if (_grid.TryGetCell(coord, out var existing) && existing.State != CellState.Unloaded)
                        continue;

                    // Insert into the small sorted array. When full, the slot at
                    // count-1 (the current worst) is overwritten/dropped.
                    int pos = count < availableSlots ? count++ : count - 1;
                    while (pos > 0 && top[pos - 1].Priority > priority)
                    {
                        top[pos] = top[pos - 1];
                        pos--;
                    }
                    top[pos] = new PrioritizedLoadRequest(coord, priority);
                }

                // 4. Dispatch background loading jobs (best priority first).
                for (int i = 0; i < count; i++)
                {
                    var next = top[i];
                    var cell = _grid.GetOrCreateCell(next.Coord);
                    cell.State = CellState.LoadingIO;
                    _inFlightLoads.Add(next.Coord);

                    DispatchLoadJob(cell);
                }
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
        if (!_workerPool.TryDequeue(out var worker))
        {
            worker = new CellLoadWorker(this);
        }

        worker.TargetCell = cell;

        if (_jobSystem != null)
        {
            _jobSystem.Schedule(worker.ExecuteAction, JobPriority.Normal);
        }
        else
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(worker, preferLocal: false);
        }
    }

    private void ReturnWorker(CellLoadWorker worker)
    {
        if (!Volatile.Read(ref _disposed))
        {
            _workerPool.Enqueue(worker);
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

                if (Physics != null)
                {
                    cell.SpawnPhysicsBodies(Physics, _lastPlayerPos);
                }
                if (Audio != null)
                {
                    cell.SpawnAudioEmitters(Audio, _lastPlayerPos);
                }
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
            cell.DespawnPhysicsBodies(Physics);
            cell.DespawnAudioEmitters(Audio);
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

            if (Physics != null)
            {
                cell.SpawnPhysicsBodies(Physics, _lastPlayerPos);
            }
            if (Audio != null)
            {
                cell.SpawnAudioEmitters(Audio, _lastPlayerPos);
            }
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
            Array.Clear(_topK, 0, _topK.Length);
            _inFlightLoads.Clear();
            while (_readyToFinalize.TryDequeue(out _)) { }
            while (_workerPool.TryDequeue(out _)) { }

            var active = _grid.ActiveCells;
            for (int i = 0; i < active.Count; i++)
            {
                active[i].DespawnPhysicsBodies(Physics);
                active[i].DespawnAudioEmitters(Audio);
            }
        }
    }

    private sealed class CellLoadWorker : System.Threading.IThreadPoolWorkItem
    {
        private readonly StreamingManager _manager;
        public WorldCell? TargetCell;
        public readonly Action ExecuteAction;

        public CellLoadWorker(StreamingManager manager)
        {
            _manager = manager;
            ExecuteAction = Execute;
        }

        void System.Threading.IThreadPoolWorkItem.Execute() => Execute();

        public void Execute()
        {
            var cell = TargetCell;
            TargetCell = null;
            if (cell == null || Volatile.Read(ref _manager._disposed))
            {
                _manager.ReturnWorker(this);
                return;
            }

            var coord = cell.Coord;
            var reader = _manager._packReader;
            DecodedCellPayload? payload = null;

            if (reader != null)
            {
                CellLoader.TryLoadCellFromPack(reader, coord, out payload);
            }

            if (payload == null)
            {
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

            _manager._readyToFinalize.Enqueue(payload);
            _manager.ReturnWorker(this);
        }
    }
}

