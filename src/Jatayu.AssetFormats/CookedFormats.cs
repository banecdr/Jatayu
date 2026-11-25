/* =================================================================================================
 * FILE: CookedFormats.cs
 * LOCATION: src/Jatayu.AssetFormats/CookedFormats.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines the shared, unmanaged binary disk structures for Jatayu cooked assets:
 *   - .jpak archive package headers and Table of Contents (TOC) entries.
 *   - .jmesh cooked mesh headers and LOD descriptors (MeshOptimizer processed).
 *   - .jtex cooked texture headers, resident mip tail offsets, and streaming mip records.
 *   - .jmat cooked PBR material parameters and texture GUID bindings.
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;

namespace Jatayu.AssetFormats;

/// <summary>
/// Identifies the category of a cooked asset stored in a .jpak package.
/// </summary>
public enum AssetType : byte
{
    Unknown = 0,
    Mesh = 1,
    Texture = 2,
    Material = 3,
    WorldCell = 4,
    Shader = 5,
    Audio = 6,
    TerrainTile = 7,
    HeightPyramid = 8,
    HlodProxy = 9,
}

/// <summary>
/// GPU texture compression format used in cooked texture payloads.
/// </summary>
public enum CookedTextureFormat : byte
{
    Rgba8Unorm = 0,
    R8Unorm = 1,
    Rg8Unorm = 2,
    Bc1Rgba = 3,
    Bc3Rgba = 4,
    Bc4R = 5,
    Bc5Rg = 6,
    Bc7Rgba = 7,
    BasisUniversal = 8,
    Rgba16Float = 9,
    Rgba32Float = 10,
}

[Flags]
public enum TextureFlags : uint
{
    None = 0,
    Srgb = 1 << 0,
    NormalMap = 1 << 1,
    HasMipmaps = 1 << 2,
    Cubemap = 1 << 3,
}

[Flags]
public enum MaterialFlags : uint
{
    None = 0,
    DoubleSided = 1 << 0,
    AlphaBlend = 1 << 1,
    AlphaMask = 1 << 2,
    Unlit = 1 << 3,
}

/// <summary>
/// Master binary header at byte offset 0 of every .jpak asset package.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct JpakHeader
{
    public const uint MagicValue = 0x4B41504A; // "JPAK"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public uint EntryCount;
    public uint HeaderSize;
    public ulong TocOffset;
    public ulong DataOffset;

    public static JpakHeader Create(uint entryCount, ulong tocOffset, ulong dataOffset) => new()
    {
        Magic = MagicValue,
        Version = CurrentVersion,
        EntryCount = entryCount,
        HeaderSize = (uint)Marshal.SizeOf<JpakHeader>(),
        TocOffset = tocOffset,
        DataOffset = dataOffset,
    };

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}

/// <summary>
/// Table of Contents (TOC) entry inside a .jpak file indexing an asset by its 64-bit GUID.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct JpakEntry
{
    public AssetGuid Guid;
    public AssetType Type;
    public byte Reserved1;
    public ushort Reserved2;
    public uint Flags;
    public ulong Offset;
    public ulong CompressedSize;
    public ulong UncompressedSize;
}

/// <summary>
/// Binary header for cooked meshes (.jmesh) containing LOD hierarchies and bounding geometry.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedMeshHeader
{
    public const uint MagicValue = 0x48534D4A; // "JMSH"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public AssetGuid Guid;
    public uint LodCount;
    public uint TotalVertexCount;
    public uint TotalIndexCount;
    public float BoundingRadius;
    public float BoundingMinX;
    public float BoundingMinY;
    public float BoundingMinZ;
    public float BoundingMaxX;
    public float BoundingMaxY;
    public float BoundingMaxZ;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}

/// <summary>
/// Describes a single Level of Detail (LOD) level within a cooked mesh.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedMeshLod
{
    public uint LodIndex;
    public uint VertexOffset;
    public uint VertexCount;
    public uint IndexOffset;
    public uint IndexCount;
    public float GeometricError;
}

/// <summary>
/// Binary header for cooked textures (.jtex) supporting resident mip tails and streamed mips.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedTextureHeader
{
    public const uint MagicValue = 0x5845544A; // "JTEX"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public AssetGuid Guid;
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ResidentMips;
    public CookedTextureFormat Format;
    public byte Reserved1;
    public ushort Reserved2;
    public TextureFlags Flags;
    public ulong ResidentTailOffset;
    public uint ResidentTailSize;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}

/// <summary>
/// Describes a single mip level in a cooked texture stream.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedTextureMip
{
    public uint MipIndex;
    public uint Width;
    public uint Height;
    public ulong Offset;
    public uint Size;
}

/// <summary>
/// Binary record for cooked PBR materials (.jmat) referencing textures by AssetGuid.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedMaterialRecord
{
    public const uint MagicValue = 0x54414D4A; // "JMAT"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public AssetGuid Guid;
    public AssetGuid AlbedoTexture;
    public AssetGuid NormalTexture;
    public AssetGuid MetallicRoughnessTexture;
    public AssetGuid OcclusionTexture;
    public AssetGuid EmissiveTexture;
    public float RoughnessFactor;
    public float MetallicFactor;
    public float AlphaCutoff;
    public MaterialFlags Flags;
    public float BaseColorR;
    public float BaseColorG;
    public float BaseColorB;
    public float BaseColorA;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}

/// <summary>
/// Binary header for a 256m terrain tile (.jtile) covering 2x2 cells (TileCoord = CellCoord / 2).
/// Contains height sample counts, min/max heights, normal map GUID, and splat map GUID.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedTerrainTileHeader
{
    public const uint MagicValue = 0x544C4954; // "TILT" ("TIL" + 'T')
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public AssetGuid Guid;
    public int TileX;
    public int TileZ;

    // Grid resolution (e.g. 65x65 height samples across 256m tile)
    public uint HeightSamplesX;
    public uint HeightSamplesZ;

    // Elevation range for rapid AABB culling and quantization
    public float MinHeight;
    public float MaxHeight;

    // Associated streaming textures
    public AssetGuid NormalMapGuid;
    public AssetGuid SplatMapGuid;

    // Byte offsets relative to header start
    public ulong HeightDataOffset;
    public uint HeightDataSize;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}

/// <summary>
/// Binary header for an HLOD proxy mesh cluster (.jhlod) covering distant world cells in Ring B.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedHlodHeader
{
    public const uint MagicValue = 0x444F4C48; // "HLOD"
    public const uint CurrentVersion = 1;

    public uint Magic;
    public uint Version;
    public AssetGuid Guid;
    public AssetGuid ProxyMeshGuid;
    public AssetGuid ImpostorAtlasGuid;

    // Center world coordinates in double precision
    public double CenterX;
    public double CenterY;
    public double CenterZ;
    public float BoundingRadius;

    // LOD transition distances
    public float MinDistance;
    public float MaxDistance;

    public readonly bool IsValid => Magic == MagicValue && Version == CurrentVersion;
}
