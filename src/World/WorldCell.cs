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
    /// Active physics rigid bodies spawned for this cell while resident in Ring A.
    /// </summary>
    public List<Physics.PhysicsBody> ResidentBodies { get; } = new(16);

    /// <summary>
    /// Active 3D spatial audio emitters spawned for this cell while resident in Ring A.
    /// </summary>
    public List<Audio.AudioSource> ResidentAudioSources { get; } = new(8);

    /// <summary>
    /// Spawns static physics colliders in the physics engine for this cell in Ring A.
    /// </summary>
    public void SpawnPhysicsBodies(Physics.PhysicsEngine physicsEngine, in WorldPosition worldOrigin)
    {
        if (physicsEngine == null || Colliders.Length == 0) return;
        DespawnPhysicsBodies(physicsEngine);

        for (int i = 0; i < Colliders.Length; i++)
        {
            ref readonly var col = ref Colliders[i];
            double worldX = Origin.X + col.OffsetX;
            double worldY = Origin.Y + col.OffsetY;
            double worldZ = Origin.Z + col.OffsetZ;

            float relX = (float)(worldX - worldOrigin.X);
            float relY = (float)(worldY - worldOrigin.Y);
            float relZ = (float)(worldZ - worldOrigin.Z);
            var pos = new Brutal.Numerics.float3(relX, relY, relZ);

            Physics.PhysicsBody? body = null;
            switch (col.ShapeType)
            {
                case ColliderShapeType.Box:
                    body = physicsEngine.CreateBoxBody(pos, new Brutal.Numerics.float3(col.ExtentX, col.ExtentY, col.ExtentZ), Scene.Components.BodyMotionType.Static);
                    break;
                case ColliderShapeType.Sphere:
                    body = physicsEngine.CreateSphereBody(pos, col.ExtentX, Scene.Components.BodyMotionType.Static);
                    break;
                case ColliderShapeType.Capsule:
                    var capsule = new Physics.Colliders.CapsuleCollider(col.ExtentX, col.ExtentY);
                    body = physicsEngine.CreateBody(capsule, pos, Brutal.Numerics.floatQuat.Identity, Scene.Components.BodyMotionType.Static);
                    break;
                case ColliderShapeType.Mesh:
                    float ex = col.ExtentX > 0 ? col.ExtentX : 2.0f;
                    float ey = col.ExtentY > 0 ? col.ExtentY : 2.0f;
                    float ez = col.ExtentZ > 0 ? col.ExtentZ : 2.0f;
                    body = physicsEngine.CreateBoxBody(pos, new Brutal.Numerics.float3(ex, ey, ez), Scene.Components.BodyMotionType.Static);
                    break;
            }

            if (body != null)
            {
                ResidentBodies.Add(body);
            }
        }
    }

    /// <summary>
    /// Removes and disposes static physics colliders owned by this cell.
    /// </summary>
    public void DespawnPhysicsBodies(Physics.PhysicsEngine? physicsEngine)
    {
        for (int i = 0; i < ResidentBodies.Count; i++)
        {
            ResidentBodies[i].Dispose();
        }
        ResidentBodies.Clear();
    }

    /// <summary>
    /// Spawns 3D spatial audio emitters for this cell in Ring A.
    /// </summary>
    public void SpawnAudioEmitters(Audio.AudioEngine audioEngine, in WorldPosition worldOrigin)
    {
        if (audioEngine == null || !audioEngine.IsHardwareAvailable || AudioEmitters.Length == 0) return;
        DespawnAudioEmitters(audioEngine);

        for (int i = 0; i < AudioEmitters.Length; i++)
        {
            ref readonly var emitter = ref AudioEmitters[i];
            double worldX = Origin.X + emitter.OffsetX;
            double worldY = Origin.Y + emitter.OffsetY;
            double worldZ = Origin.Z + emitter.OffsetZ;

            float relX = (float)(worldX - worldOrigin.X);
            float relY = (float)(worldY - worldOrigin.Y);
            float relZ = (float)(worldZ - worldOrigin.Z);

            var source = audioEngine.CreateSource();
            if (source != null)
            {
                source.Position = new Brutal.Numerics.float3(relX, relY, relZ);
                source.Gain = emitter.Volume > 0 ? emitter.Volume : 0.5f;
                source.MaxDistance = emitter.Radius > 0 ? emitter.Radius : 50.0f;
                source.ReferenceDistance = 2.0f;
                source.IsLooping = true;
                if (audioEngine.FootstepClip != null)
                {
                    source.Clip = audioEngine.FootstepClip;
                    source.Play();
                }
                ResidentAudioSources.Add(source);
            }
        }
    }

    /// <summary>
    /// Removes and disposes 3D audio emitters owned by this cell.
    /// </summary>
    public void DespawnAudioEmitters(Audio.AudioEngine? audioEngine)
    {
        for (int i = 0; i < ResidentAudioSources.Count; i++)
        {
            audioEngine?.RemoveSource(ResidentAudioSources[i]);
        }
        ResidentAudioSources.Clear();
    }

    /// <summary>
    /// Cleans up CPU instance arrays when cell is unloaded.
    /// </summary>
    public void Unload()
    {
        DespawnPhysicsBodies(null);
        DespawnAudioEmitters(null);
        State = CellState.Unloaded;
        Ring = StreamingRing.None;
        StaticInstances = Array.Empty<CookedStaticInstance>();
        Colliders = Array.Empty<CookedColliderRecord>();
        AudioEmitters = Array.Empty<CookedAudioPointRecord>();
        Header = default;
    }
}
