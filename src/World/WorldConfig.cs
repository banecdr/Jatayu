/* =================================================================================================
 * FILE: WorldConfig.cs
 * LOCATION: src/World/WorldConfig.cs
 * 
 * WHAT THIS FILE DOES:
 *   Configuration record governing open-world dimensions, distance streaming rings,
 *   velocity-scaled lookahead, hysteresis thresholds, and time/memory budgets.
 * ================================================================================================= */

using Jatayu.AssetFormats.World;

namespace Jatayu.World;

/// <summary>
/// Configuration parameters governing world cell streaming and grid management.
/// </summary>
public sealed record WorldConfig
{
    /// <summary>
    /// Dimension of a single world cell in meters (128m).
    /// </summary>
    public float CellSize { get; init; } = WorldConstants.CellSize;

    /// <summary>
    /// Dimension of a terrain tile in meters (256m = 2x2 cells).
    /// </summary>
    public float TileSize { get; init; } = WorldConstants.TileSize;

    /// <summary>
    /// Ring A (Near) streaming load radius in meters (600m).
    /// Full detail static meshes, colliders, audio, and dense foliage are active.
    /// </summary>
    public float LoadRadiusRingA { get; init; } = WorldConstants.LoadRadiusRingA;

    /// <summary>
    /// Ring A (Near) streaming unload radius in meters (750m).
    /// Provides a 150m hysteresis band to prevent loading/unloading thrashing.
    /// </summary>
    public float UnloadRadiusRingA { get; init; } = WorldConstants.UnloadRadiusRingA;

    /// <summary>
    /// Ring B (Mid) streaming load radius in meters (2,000m).
    /// HLOD proxies and tree impostors active; physics colliders disabled.
    /// </summary>
    public float LoadRadiusRingB { get; init; } = WorldConstants.LoadRadiusRingB;

    /// <summary>
    /// Ring B (Mid) streaming unload radius in meters (2,200m).
    /// </summary>
    public float UnloadRadiusRingB { get; init; } = WorldConstants.UnloadRadiusRingB;

    /// <summary>
    /// Ring C (Far) maximum view distance in meters (8,000m / 8 km).
    /// Clipmap terrain and atmospheric sky only.
    /// </summary>
    public float MaxDrawDistance { get; init; } = WorldConstants.MaxDrawDistance;

    /// <summary>
    /// Velocity lookahead prediction time in seconds for streaming prefetch (D19).
    /// </summary>
    public float LeadSeconds { get; init; } = WorldConstants.DefaultLeadSeconds;

    /// <summary>
    /// Maximum prefetch radius cap in meters (900m) to throttle extreme velocity queues (D23).
    /// </summary>
    public float PrefetchRadiusCap { get; init; } = WorldConstants.PrefetchRadiusCap;

    /// <summary>
    /// Maximum time in milliseconds allotted per frame for main-thread cell finalization (1.0 ms).
    /// Guarantees that cell uploads/scene registration never causes a frame hitch > 2 ms.
    /// </summary>
    public float FinalizationBudgetMs { get; init; } = 1.0f;

    /// <summary>
    /// Maximum number of asynchronous cell loading jobs executing concurrently in JobSystem.
    /// </summary>
    public int MaxConcurrentLoadingJobs { get; init; } = 4;

    /// <summary>
    /// Maximum number of active resident cells in memory before aggressive eviction.
    /// </summary>
    public int MaxActiveCells { get; init; } = 256;

    /// <summary>
    /// VRAM usage threshold ratio (0.0 to 1.0) above which prefetch streaming is deferred.
    /// </summary>
    public float VramPressureThreshold { get; init; } = 0.85f;
}
