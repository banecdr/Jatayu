/* =================================================================================================
 * FILE: WorldConstants.cs
 * LOCATION: src/Jatayu.AssetFormats/World/WorldConstants.cs
 * 
 * WHAT THIS FILE DOES:
 *   Shared architectural constants and spatial helper formulas for the Jatayu open-world engine:
 *   - Cell dimensions (128m), Terrain tile dimensions (256m).
 *   - Streaming distance rings (Ring A: 600m/750m, Ring B: 2000m/2200m, Ring C: 8000m).
 *   - Velocity lead time (6.0s) and prefetch cap (900m) per decisions D18, D19, and D23.
 *   - Reference scene dimensions (16 km x 16 km, 128x128 grid cells).
 * ================================================================================================= */

using System;
using System.Runtime.CompilerServices;

namespace Jatayu.AssetFormats.World;

/// <summary>
/// Architectural constants and spatial formulas for world grid and streaming.
/// </summary>
public static class WorldConstants
{
    /// <summary>
    /// Dimension of a single world cell in meters (128m).
    /// </summary>
    public const float CellSize = 128.0f;

    /// <summary>
    /// Dimension of a terrain tile in meters (256m = 2x2 cells).
    /// </summary>
    public const float TileSize = 256.0f;

    /// <summary>
    /// Number of cells along one side of a terrain tile (2).
    /// </summary>
    public const int CellsPerTile = 2;

    /// <summary>
    /// Ring A (Near) streaming load radius in meters (600m).
    /// Dynamic entities, full-detail static meshes, physics colliders, and dense foliage are active.
    /// </summary>
    public const float LoadRadiusRingA = 600.0f;

    /// <summary>
    /// Ring A (Near) streaming unload radius in meters (750m).
    /// Implements a 150m hysteresis buffer to prevent thrashing at cell borders.
    /// </summary>
    public const float UnloadRadiusRingA = 750.0f;

    /// <summary>
    /// Ring B (Mid) streaming load radius in meters (2,000m).
    /// HLOD proxy meshes and tree impostors active; physics colliders disabled.
    /// </summary>
    public const float LoadRadiusRingB = 2000.0f;

    /// <summary>
    /// Ring B (Mid) streaming unload radius in meters (2,200m, 10% hysteresis).
    /// </summary>
    public const float UnloadRadiusRingB = 2200.0f;

    /// <summary>
    /// Ring C (Far) maximum view distance in meters (8,000m / 8 km).
    /// Clipmap terrain height pyramid and atmospheric sky only.
    /// </summary>
    public const float MaxDrawDistance = 8000.0f;

    /// <summary>
    /// Ring C (Far) streaming unload radius in meters (8,800m, 10% hysteresis).
    /// </summary>
    public const float UnloadRadiusRingC = 8800.0f;

    /// <summary>
    /// Lookahead prediction time in seconds for velocity-scaled streaming prefetch (D19).
    /// </summary>
    public const float DefaultLeadSeconds = 6.0f;

    /// <summary>
    /// Maximum allowable prefetch radius in meters under high velocity (D23).
    /// Prevents excessive loading queues at extreme speeds (e.g. 100 m/s).
    /// </summary>
    public const float PrefetchRadiusCap = 900.0f;

    /// <summary>
    /// Dimension of the reference test world in meters (16 km = 16,384m).
    /// </summary>
    public const double ReferenceWorldDimension = 16384.0;

    /// <summary>
    /// Total cell count along one dimension of the 16 km reference world (128 cells).
    /// </summary>
    public const int ReferenceGridCells = 128;

    /// <summary>
    /// Floating-origin shift snapping interval in meters (256m terrain tile multiple).
    /// </summary>
    public const double OriginShiftSnapMeters = 256.0;

    /// <summary>
    /// Computes the terrain tile coordinate (TileCoord = CellCoord / 2) covering a cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int TileX, int TileZ) GetTileCoordForCell(in CellCoord coord)
    {
        int tx = coord.X >= 0 ? coord.X / CellsPerTile : (coord.X - 1) / CellsPerTile;
        int tz = coord.Z >= 0 ? coord.Z / CellsPerTile : (coord.Z - 1) / CellsPerTile;
        return (tx, tz);
    }

    /// <summary>
    /// Computes the effective streaming radius scaling with velocity:
    /// R_effective = Min(baseRadius + speed * leadSeconds, capRadius).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeVelocityScaledRadius(
        float baseRadius,
        float speedMps,
        float leadSeconds = DefaultLeadSeconds,
        float capRadius = PrefetchRadiusCap)
    {
        float target = baseRadius + (speedMps * leadSeconds);
        return MathF.Min(target, capRadius);
    }
}
