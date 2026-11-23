/* =================================================================================================
 * FILE: WorldCell.cs
 * LOCATION: src/World/WorldCell.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a single 128m x 128m streaming cell in the world grid.
 *   Implements the cell lifecycle state machine (Unloaded -> LoadingIO -> LoadingDecode ->
 *   ReadyToFinalize -> Active -> Unloading), manages static instance arrays, physics colliders,
 *   audio emitters, bounding volumes, and double-precision origin rebasing.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;

namespace Jatayu.World;

/// <summary>
/// Lifecycle state of a streaming world cell.
/// </summary>
public enum CellState : byte
{
    Unloaded = 0,
    LoadingIO = 1,
    LoadingDecode = 2,
    ReadyToFinalize = 3,
    Active = 4,
    Unloading = 5,
}

/// <summary>
/// Distance ring tier assigned to an active or loaded cell.
/// </summary>
public enum StreamingRing : byte
{
    None = 0,
    RingA = 1, // Near (0..600m): Static meshes, dynamic entities, colliders, foliage
    RingB = 2, // Mid (600..2000m): HLOD proxies, tree impostors, no colliders
    RingC = 3, // Far (2000..8000m): Terrain clipmap & sky only
}

/// <summary>
/// Encapsulates a single 128m world grid streaming cell and its resident data.
/// </summary>
public sealed class WorldCell
{
    public CellCoord Coord { get; }
    public AssetGuid CellGuid { get; }

    /// <summary>
    /// Double-precision world origin coordinate (minimum corner) of this cell.
    /// Invariant to static instance arrays during floating-origin shifts.
    /// </summary>
    public WorldPosition Origin { get; private set; }

    /// <summary>
    /// Current lifecycle state.
    /// </summary>
    public CellState State { get; internal set; } = CellState.Unloaded;

    /// <summary>
    /// Current distance ring tier.
    /// </summary>
    public StreamingRing Ring { get; internal set; } = StreamingRing.None;

    /// <summary>
    /// Raw cooked header parsed from .jpak payload.
    /// </summary>
    public CookedCellHeader Header { get; internal set; }

    /// <summary>
    /// Static mesh instances owned by this cell.
    /// Transforms are cell-local (0..128m), eliminating VRAM matrix updates on origin shifts.
    /// </summary>
    public CookedStaticInstance[] StaticInstances { get; internal set; } = Array.Empty<CookedStaticInstance>();

    /// <summary>
    /// Physics collider descriptors streamed with this cell.
    /// </summary>
    public CookedColliderRecord[] Colliders { get; internal set; } = Array.Empty<CookedColliderRecord>();

    /// <summary>
    /// Spatial audio emitters streamed with this cell.
    /// </summary>
    public CookedAudioPointRecord[] AudioEmitters { get; internal set; } = Array.Empty<CookedAudioPointRecord>();

    /// <summary>
    /// World-space center position of this cell in double precision.
    /// </summary>
    public WorldPosition Center => new(
        Origin.X + (WorldConstants.CellSize * 0.5),
        Origin.Y + (Header.BoundingMaxY + Header.BoundingMinY) * 0.5,
        Origin.Z + (WorldConstants.CellSize * 0.5)
    );

    /// <summary>
    /// Estimated memory usage in bytes of this cell's CPU data.
    /// </summary>
    public int MemorySizeBytes =>
        (StaticInstances.Length * Unsafe.SizeOf<CookedStaticInstance>()) +
        (Colliders.Length * Unsafe.SizeOf<CookedColliderRecord>()) +
        (AudioEmitters.Length * Unsafe.SizeOf<CookedAudioPointRecord>());

    public WorldCell(CellCoord coord, float cellSize = WorldConstants.CellSize)
    {
        Coord = coord;
        CellGuid = coord.ToAssetGuid();
        coord.GetWorldOrigin(cellSize, out double ox, out double oz);
        Origin = new WorldPosition(ox, 0.0, oz);
    }

    /// <summary>
    /// Updates the double-precision origin during a floating-origin rebase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ShiftOrigin(double deltaX, double deltaY, double deltaZ)
    {
        Origin = new WorldPosition(Origin.X + deltaX, Origin.Y + deltaY, Origin.Z + deltaZ);
    }

    /// <summary>
    /// Computes the camera-relative bounding sphere for frustum culling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetCameraRelativeBounds(
        in WorldPosition cameraPos,
        out Vector3 relCenter,
        out float radius)
    {
        double cx = Origin.X + ((Header.BoundingMinX + Header.BoundingMaxX) * 0.5);
        double cy = Origin.Y + ((Header.BoundingMinY + Header.BoundingMaxY) * 0.5);
        double cz = Origin.Z + ((Header.BoundingMinZ + Header.BoundingMaxZ) * 0.5);

        relCenter = new Vector3(
            (float)(cx - cameraPos.X),
            (float)(cy - cameraPos.Y),
            (float)(cz - cameraPos.Z)
        );

        float dx = (Header.BoundingMaxX - Header.BoundingMinX) * 0.5f;
        float dy = (Header.BoundingMaxY - Header.BoundingMinY) * 0.5f;
        float dz = (Header.BoundingMaxZ - Header.BoundingMinZ) * 0.5f;
        radius = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Re-evaluates distance to camera and assigns the active streaming ring.
    /// </summary>
    public StreamingRing EvaluateRing(double distanceMeters, in WorldConfig config)
    {
        if (distanceMeters <= config.LoadRadiusRingA)
            return StreamingRing.RingA;
        if (distanceMeters <= config.LoadRadiusRingB)
            return StreamingRing.RingB;
        if (distanceMeters <= config.MaxDrawDistance)
            return StreamingRing.RingC;

        return StreamingRing.None;
    }

    /// <summary>
    /// Cleans up CPU instance arrays when cell is unloaded.
    /// </summary>
    public void Unload()
    {
        State = CellState.Unloaded;
        Ring = StreamingRing.None;
        StaticInstances = Array.Empty<CookedStaticInstance>();
        Colliders = Array.Empty<CookedColliderRecord>();
        AudioEmitters = Array.Empty<CookedAudioPointRecord>();
        Header = default;
    }
}
