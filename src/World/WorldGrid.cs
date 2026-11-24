/* =================================================================================================
 * FILE: WorldGrid.cs
 * LOCATION: src/World/WorldGrid.cs
 * 
 * WHAT THIS FILE DOES:
 *   Sparse 2D spatial grid storing active and dormant world cells.
 *   Provides sub-microsecond spatial lookups by WorldPosition and CellCoord, radius queries,
 *   active cell enumeration, and coordinates deterministic floating-origin shifts (snapped to 256m).
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;

namespace Jatayu.World;

/// <summary>
/// Sparse 2D hash grid indexing world cells across massive world coordinates.
/// </summary>
public sealed class WorldGrid
{
    private readonly Dictionary<CellCoord, WorldCell> _cells;
    private readonly List<WorldCell> _activeCells;
    private WorldCell[] _activeCellsSnapshot;
    private readonly WorldConfig _config;
    private readonly object _sync = new();

    /// <summary>
    /// Gets the number of active cells currently loaded in the world.
    /// </summary>
    public int ActiveCellCount { get { lock (_sync) { return _activeCells.Count; } } }

    /// <summary>
    /// Gets the total number of tracked cells in the grid (loaded, loading, and dormant).
    /// </summary>
    public int TotalCellCount { get { lock (_sync) { return _cells.Count; } } }

    /// <summary>
    /// Gets a read-only list of all currently active cells.
    /// </summary>
    public IReadOnlyList<WorldCell> ActiveCells
    {
        get
        {
            lock (_sync)
            {
                return _activeCellsSnapshot;
            }
        }
    }

    public WorldGrid(WorldConfig? config = null, int initialCapacity = 512)
    {
        _config = config ?? new WorldConfig();
        _cells = new Dictionary<CellCoord, WorldCell>(initialCapacity);
        _activeCells = new List<WorldCell>(initialCapacity);
        _activeCellsSnapshot = Array.Empty<WorldCell>();
    }

    /// <summary>
    /// Retrieves or registers a WorldCell at the given discrete coordinate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorldCell GetOrCreateCell(in CellCoord coord)
    {
        lock (_sync)
        {
            if (_cells.TryGetValue(coord, out var existing))
                return existing;

            var cell = new WorldCell(coord, _config.CellSize);
            _cells[coord] = cell;
            return cell;
        }
    }

    /// <summary>
    /// Attempts to retrieve a registered WorldCell at the given coordinate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCell(in CellCoord coord, out WorldCell cell)
    {
        lock (_sync)
        {
            return _cells.TryGetValue(coord, out cell!);
        }
    }

    /// <summary>
    /// Retrieves or registers the WorldCell enclosing continuous double-precision world coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WorldCell GetCellAtWorld(double worldX, double worldZ)
    {
        var coord = CellCoord.FromWorld(worldX, worldZ, _config.CellSize);
        return GetOrCreateCell(in coord);
    }

    /// <summary>
    /// Marks a cell as active and registers it in the active list.
    /// </summary>
    public void MarkActive(WorldCell cell)
    {
        lock (_sync)
        {
            cell.State = CellState.Active;
            if (!_activeCells.Contains(cell))
            {
                _activeCells.Add(cell);
                _activeCellsSnapshot = _activeCells.ToArray();
            }
        }
    }

    /// <summary>
    /// Marks a cell as unloaded and removes it from the active list.
    /// </summary>
    public void MarkUnloaded(WorldCell cell)
    {
        lock (_sync)
        {
            cell.Unload();
            if (_activeCells.Remove(cell))
            {
                _activeCellsSnapshot = _activeCells.ToArray();
            }
        }
    }

    /// <summary>
    /// Collects all discrete CellCoord indices located within a Euclidean radius in meters
    /// of continuous double-precision center coordinates.
    /// Zero heap allocation when reusing destination list.
    /// </summary>
    public void GetCellCoordsInRadius(
        in WorldPosition center,
        float radiusMeters,
        List<CellCoord> destination)
    {
        destination.Clear();

        float cellSize = _config.CellSize;
        int minCx = (int)Math.Floor((center.X - radiusMeters) / cellSize);
        int maxCx = (int)Math.Floor((center.X + radiusMeters) / cellSize);
        int minCz = (int)Math.Floor((center.Z - radiusMeters) / cellSize);
        int maxCz = (int)Math.Floor((center.Z + radiusMeters) / cellSize);

        double radiusSq = radiusMeters * radiusMeters;

        for (int cz = minCz; cz <= maxCz; cz++)
        {
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                var coord = new CellCoord(cx, cz);
                coord.GetWorldCenter(cellSize, out double cellCenterX, out double cellCenterZ);

                double dx = cellCenterX - center.X;
                double dz = cellCenterZ - center.Z;
                if ((dx * dx + dz * dz) <= radiusSq)
                {
                    destination.Add(coord);
                }
            }
        }
    }

    /// <summary>
    /// Collects active cells located within a bounding radius.
    /// Zero heap allocation when reusing destination list.
    /// </summary>
    public void GetActiveCellsInRadius(
        in WorldPosition center,
        float radiusMeters,
        List<WorldCell> destination)
    {
        destination.Clear();
        double radiusSq = radiusMeters * radiusMeters;

        lock (_sync)
        {
            for (int i = 0; i < _activeCells.Count; i++)
            {
                var cell = _activeCells[i];
                double distSq = cell.Center.DistanceSquaredTo(center);
                if (distSq <= radiusSq)
                {
                    destination.Add(cell);
                }
            }
        }
    }

    /// <summary>
    /// Translates all cell double-precision origin coordinates during a floating-origin shift.
    /// Invariant: static instance buffers in VRAM remain untouched (Section 1.3).
    /// </summary>
    public void ShiftOrigin(double deltaX, double deltaY, double deltaZ)
    {
        lock (_sync)
        {
            foreach (var cell in _cells.Values)
            {
                cell.ShiftOrigin(deltaX, deltaY, deltaZ);
            }
        }
    }

    /// <summary>
    /// Clears all cells and active references.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            foreach (var cell in _cells.Values)
            {
                cell.Unload();
            }
            _cells.Clear();
            _activeCells.Clear();
            _activeCellsSnapshot = Array.Empty<WorldCell>();
        }
    }
}
