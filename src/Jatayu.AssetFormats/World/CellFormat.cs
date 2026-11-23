/* =================================================================================================
 * FILE: CellFormat.cs
 * LOCATION: src/Jatayu.AssetFormats/World/CellFormat.cs
 * 
 * WHAT THIS FILE DOES:
 *   Canonical unmanaged binary structures for world cell records stored in .jpak packages:
 *   - CookedCellHeader: Header describing bounds, counts, and sub-chunk offsets for a 128m cell.
 *   - CookedStaticInstance: 64-byte packed static instance transform and asset bindings.
 *   - CookedColliderRecord: Cell physics collider descriptor (Box, Sphere, Capsule, Mesh).
 *   - CookedAudioPointRecord: Cell 3D spatial audio emitter descriptor.
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;

namespace Jatayu.AssetFormats.World;

/// <summary>
/// Bitwise flags describing cell state and runtime hints.
/// </summary>
[Flags]
public enum CellFlags : uint
{
    None = 0,
    HasTerrain = 1 << 0,
    HasWater = 1 << 1,
    HasFoliage = 1 << 2,
    HasNavMesh = 1 << 3,
    HasAudio = 1 << 4,
}

/// <summary>
/// Bitwise flags for static instances inside a cell.
/// </summary>
[Flags]
public enum StaticInstanceFlags : uint
{
    None = 0,
    CastShadow = 1 << 0,
    ReceiveDecals = 1 << 1,
    Occluder = 1 << 2,
    HlodProxy = 1 << 3,
}

/// <summary>
/// Identifies the physics collider geometric shape.
/// </summary>
public enum ColliderShapeType : byte
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    Mesh = 3,
}

/// <summary>
/// Master binary header for a world cell record (.jcell payload inside .jpak).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedCellHeader
{
    public const uint MagicValue = 0x4C45434A; // "JCEL"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public CellCoord Coord;
    public AssetGuid CellGuid;

    // Local bounding box relative to cell origin
    public float BoundingMinX;
    public float BoundingMinY;
    public float BoundingMinZ;
    public float BoundingMaxX;
    public float BoundingMaxY;
    public float BoundingMaxZ;

    // Entity and data counts
    public uint StaticInstanceCount;
    public uint ColliderCount;
    public uint AudioEmitterCount;
    public CellFlags Flags;

    // Byte offsets relative to beginning of CookedCellHeader
    public ulong StaticInstancesOffset;
    public ulong CollidersOffset;
    public ulong AudioEmittersOffset;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;

    public static CookedCellHeader Create(
        CellCoord coord,
        AssetGuid cellGuid,
        uint instanceCount,
        uint colliderCount,
        uint audioCount,
        CellFlags flags,
        float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ)
    {
        uint headerSize = (uint)Marshal.SizeOf<CookedCellHeader>();
        ulong currentOffset = (ulong)headerSize;

        ulong instancesOffset = currentOffset;
        currentOffset += (ulong)(instanceCount * Marshal.SizeOf<CookedStaticInstance>());

        ulong collidersOffset = currentOffset;
        currentOffset += (ulong)(colliderCount * Marshal.SizeOf<CookedColliderRecord>());

        ulong audioOffset = currentOffset;

        return new CookedCellHeader
        {
            Magic = MagicValue,
            Version = CurrentVersion,
            Coord = coord,
            CellGuid = cellGuid,
            BoundingMinX = minX,
            BoundingMinY = minY,
            BoundingMinZ = minZ,
            BoundingMaxX = maxX,
            BoundingMaxY = maxY,
            BoundingMaxZ = maxZ,
            StaticInstanceCount = instanceCount,
            ColliderCount = colliderCount,
            AudioEmitterCount = audioCount,
            Flags = flags,
            StaticInstancesOffset = instancesOffset,
            CollidersOffset = collidersOffset,
            AudioEmittersOffset = audioOffset,
        };
    }
}

/// <summary>
/// Packed binary representation of a single static mesh instance inside a world cell.
/// Positions are stored relative to cell origin in single-precision floats (0..128m).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedStaticInstance
{
    // Cell-local position (float3)
    public float PosX;
    public float PosY;
    public float PosZ;

    // Rotation quaternion (float4)
    public float RotX;
    public float RotY;
    public float RotZ;
    public float RotW;

    // Scale (float3)
    public float ScaleX;
    public float ScaleY;
    public float ScaleZ;

    // Asset and material references
    public AssetGuid MeshGuid;
    public AssetGuid MaterialGuid;
    public uint MaterialId;

    // Culling and diagnostics
    public float BoundingRadius;
    public StaticInstanceFlags Flags;
}

/// <summary>
/// Packed binary representation of a static physics collider streamed with a cell.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedColliderRecord
{
    public ColliderShapeType ShapeType;
    public byte Reserved1;
    public ushort Reserved2;

    // Offset relative to cell origin
    public float OffsetX;
    public float OffsetY;
    public float OffsetZ;

    // Shape half-extents or radius/height
    public float ExtentX;
    public float ExtentY;
    public float ExtentZ;

    // Mesh asset GUID for shape type Mesh
    public AssetGuid MeshGuid;
}

/// <summary>
/// Packed binary representation of a spatial audio emitter streamed with a cell.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedAudioPointRecord
{
    public float OffsetX;
    public float OffsetY;
    public float OffsetZ;

    public AssetGuid SoundClipGuid;
    public float Volume;
    public float Radius;
    public uint Flags;
}
