// ==================================================================================================
// Jatayu Engine - Core Platform
// Module: EngineConfig.cs
// Role: Engine startup configuration record holding technical parameters and resource budgets.
// ==================================================================================================

namespace Jatayu.Core;

/// <summary>
/// Startup configuration parameters for initializing Jatayu Engine and its low-level subsystems.
/// </summary>
public sealed record EngineConfig
{
    // --- Window & Display ---
    public string WindowTitle { get; init; } = "Jatayu Engine - Vulkan 1.3";
    public int WindowWidth { get; init; } = 1920;
    public int WindowHeight { get; init; } = 1080;
    public bool VSync { get; init; } = false;
    public bool Fullscreen { get; init; } = false;

    // --- Vulkan & Memory ---
    public int StagingRingSizeMB { get; init; } = 128;
    public ulong TargetVramBudgetMB { get; init; } = 4096;
    public int MaxInFlightFrames { get; init; } = 2;

    // --- World & Streaming Disciplines ---
    public float CellSizeMeters { get; init; } = 128.0f;
    public float TerrainTileSizeMeters { get; init; } = 256.0f;
    public float MaxDrawDistanceMeters { get; init; } = 8000.0f;
    public float RingALoadRadiusMeters { get; init; } = 600.0f;
    public float RingAUnloadRadiusMeters { get; init; } = 750.0f;
    public float RingBLoadRadiusMeters { get; init; } = 2000.0f;
    public float RingCLoadRadiusMeters { get; init; } = 8000.0f;
    public float StreamingLeadSeconds { get; init; } = 6.0f;
    public float StreamingPrefetchCapMeters { get; init; } = 900.0f;

    // --- Lighting & Shadows ---
    public int ShadowCascadesCount { get; init; } = 4;
    public int ShadowMapResolution { get; init; } = 2048;

    // --- Asset Pipeline ---
    public string AssetDirectory { get; init; } = "assets";
    public string LogFilePath { get; init; } = "logs/jatayu.log";

    /// <summary>
    /// Default production configuration meeting reference architecture disciplines.
    /// </summary>
    public static EngineConfig Default => new();
}
