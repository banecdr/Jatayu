/* =================================================================================================
 * FILE: TerrainClipmap.cs
 * LOCATION: src/Terrain/TerrainClipmap.cs
 *
 * WHAT THIS FILE DOES:
 *   Single canonical GPU Clipmap LOD terrain renderer for all rings (D22).
 *   Geometry layout, snapping and geomorph scheme ported from Terrain3D's Terrain3DMesher / main.glsl.
 *
 *   - Builds 10 reusable flat mesh PIECES once (tile, edge strips, fill strips, LOD0 trims).
 *   - Every frame (only when the target moves >= 1 vertex spacing) re-snaps N nested LOD rings
 *     around the camera and emits a list of ClipmapInstance (piece type + world origin + scale).
 *   - Each ring L has vertex spacing = VertexSpacing * 2^L and is snapped to 2^(L+1) * VertexSpacing,
 *     so every ring's vertices land exactly on every other vertex of the ring below it.
 *   - The vertex shader (terrain.vert) geomorphs the outer band of each ring onto the next
 *     coarser grid, so rings join without cracks or popping.
 *   - Samples resident TerrainTile chunks in Ring A/B and blends with TerrainHeightPyramid in Ring C
 *     (CPU side, SampleElevation).
 *
 * RING FOOTPRINT (m = MeshSize, s = VertexSpacing, ring L scale = s * 2^L):
 *   ring L covers  +-(2m + 2) * scale   (tiles + fills/trims)
 *   + a 2-cell "edge" moat of the same scale that stitches it to ring L+1's hole.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Graph;
using Jatayu.Renderer.Lighting;
using Jatayu.Renderer.Mesh;
using Jatayu.Renderer.Shadows;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;

namespace Jatayu.Terrain;

/// <summary>
/// Push constants submitted per clipmap piece to terrain.vert / terrain.frag (96 bytes).
///   CameraRelativeOrigin : xyz = piece origin relative to camera (y = -camera.Y), w = piece scale (metres per grid unit)
///   TerrainParams        : x = piece world origin X, y = piece world origin Z, z = meshSize (negative if debug rings active), w = LOD index
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct TerrainPushConstants
{
    public Matrix4x4 ViewProjMatrix;
    public Vector4 CameraRelativeOrigin;
    public Vector4 TerrainParams;
}

/// <summary>
/// Vertex structure for terrain clipmap pieces. Position is in integer grid units (x, 0, z),
/// the instance scale converts it to metres.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct TerrainVertex
{
    public float3 Position;
    public float3 Normal;
    public float2 TexCoord;

    public TerrainVertex(float3 pos, float3 normal, float2 uv)
    {
        Position = pos;
        Normal = normal;
        TexCoord = uv;
    }
}

/// <summary>
/// The 10 reusable flat meshes. Order matches Terrain3D's MeshType enum.
/// "Standard" variants use a uniform diagonal and are used by LOD0 only; the others use
/// alternating diagonals which line up with the LOD0 -> LOD1 geomorph.
/// </summary>
public enum ClipmapMeshType
{
    Tile = 0,           // m x m
    EdgeA,              // 2 x (4m + 8)  strips along Z
    EdgeB,              // (4m + 4) x 2  strips along X
    FillA,              // 4 x m         (LOD1+)
    FillB,              // m x 4         (LOD1+)
    StandardTrimA,      // 2 x (4m + 2)  (LOD0 only)
    StandardTrimB,      // (4m + 2) x 2  (LOD0 only)
    StandardTile,       // m x m         (LOD0 only)
    StandardEdgeA,      // 2 x (4m + 8)  (LOD0 only)
    StandardEdgeB,      // (4m + 4) x 2  (LOD0 only)
}

/// <summary>
/// Range inside the shared vertex/index buffers for one mesh piece.
/// Draw with: vkCmdDrawIndexed(IndexCount, 1, FirstIndex, VertexOffset, 0).
/// Indices are local to the piece (add VertexOffset as baseVertex).
/// </summary>
public readonly struct ClipmapMeshPiece
{
    public readonly int VertexOffset;
    public readonly uint FirstIndex;
    public readonly uint IndexCount;
    public readonly int SizeX; // grid units
    public readonly int SizeZ;

    public ClipmapMeshPiece(int vertexOffset, uint firstIndex, uint indexCount, int sizeX, int sizeZ)
    {
        VertexOffset = vertexOffset;
        FirstIndex = firstIndex;
        IndexCount = indexCount;
        SizeX = sizeX;
        SizeZ = sizeZ;
    }
}

/// <summary>
/// One placed piece of the clipmap. WorldX/WorldZ are double-precision world coordinates of the piece's
/// minimum corner so they stay exact under floating-origin rebases.
/// </summary>
public readonly struct ClipmapInstance
{
    public readonly ClipmapMeshType Type;
    public readonly int Lod;
    public readonly double WorldX;
    public readonly double WorldZ;
    public readonly float Scale;   // metres per grid unit = VertexSpacing * 2^Lod
    public readonly float ExtentX; // piece size in metres (for CPU culling)
    public readonly float ExtentZ;

    public ClipmapInstance(ClipmapMeshType type, int lod, double worldX, double worldZ, float scale, float extentX, float extentZ)
    {
        Type = type;
        Lod = lod;
        WorldX = worldX;
        WorldZ = worldZ;
        Scale = scale;
        ExtentX = extentX;
        ExtentZ = extentZ;
    }
}

/// <summary>
/// Unified GPU Clipmap terrain renderer managing concentric LOD rings and geomorphing.
/// </summary>
public sealed class TerrainClipmap : IDisposable
{
    /// <summary>Number of nested LOD rings. 6 rings @ 2 m / MeshSize 64 reach ~4.2 km (Ring C).</summary>
    public const int DefaultRingCount = 6;

    /// <summary>Quads per tile edge. Must be even, 8..256.</summary>
    public const int DefaultMeshSize = 32;

    /// <summary>Metres between vertices on LOD0.</summary>
    public const float DefaultVertexSpacing = 2.0f;

    /// <summary>Legacy name kept for callers; represents MeshSize.</summary>
    public const int GridResolution = DefaultMeshSize;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;
    private readonly uint _indexCount;

    private readonly int _meshSize;
    private readonly int _lodCount;
    private readonly double _vertexSpacing;
    private readonly ClipmapMeshPiece[] _pieces;

    // Piece offsets in grid units (before scaling), see Terrain3DMesher::_generate_offset_data
    private readonly (int X, int Z)[] _tileOffsetsLod0;
    private readonly (int X, int Z)[] _tileOffsets;
    private readonly (int X, int Z)[] _trimAOffsets;
    private readonly (int X, int Z)[] _trimBOffsets;
    private readonly (int X, int Z)[] _fillAOffsets;
    private readonly (int X, int Z)[] _fillBOffsets;
    private readonly int[][] _edgePos; // [instance][test index 0..2]
    private readonly int _offsetA;

    private readonly List<ClipmapInstance> _instances;
    private bool _hasLastTarget;
    private double _lastTargetX;
    private double _lastTargetZ;

    private readonly Dictionary<TileCoord, TerrainTile> _residentTiles = new();
    private readonly TerrainHeightPyramid _heightPyramid;
    private readonly TerrainMaterial _material;
    private bool _disposed;

    public TerrainMaterial Material => _material;
    public TerrainHeightPyramid HeightPyramid => _heightPyramid;
    public int ResidentTileCount => _residentTiles.Count;

    public GpuBuffer VertexBuffer => _vertexBuffer;
    public GpuBuffer IndexBuffer => _indexBuffer;
    /// <summary>Total indices across all pieces (pieces are packed back to back).</summary>
    public uint IndexCount => _indexCount;

    public int MeshSize => _meshSize;
    public int LodCount => _lodCount;
    public double VertexSpacing => _vertexSpacing;

    /// <summary>Distance from the target to the outer edge of the last ring's tiles/fills (metres).</summary>
    public double OuterHalfExtent => (2.0 * _meshSize + 2.0) * _vertexSpacing * (1L << (_lodCount - 1));

    /// <summary>Current set of pieces to draw. Valid until the next Update() that returns true.</summary>
    public ReadOnlySpan<ClipmapInstance> Instances => CollectionsMarshal.AsSpan(_instances);

    public ClipmapMeshPiece GetPiece(ClipmapMeshType type) => _pieces[(int)type];

    internal TerrainClipmap(
        int meshSize,
        int lodCount,
        float vertexSpacing,
        TerrainHeightPyramid? heightPyramid,
        out TerrainVertex[] vertices,
        out uint[] indices)
    {
        _context = null!;
        _allocator = null!;
        _heightPyramid = heightPyramid ?? new TerrainHeightPyramid();
        _material = new TerrainMaterial();

        _meshSize = Math.Clamp(meshSize & ~1, 8, 256); // must be even
        _lodCount = Math.Clamp(lodCount, 1, 10);
        _vertexSpacing = Math.Clamp(vertexSpacing, 0.25f, 100.0f);

        // 1. Build the 10 reusable pieces into shared vertex / index arrays
        _pieces = BuildMeshPieces(_meshSize, out vertices, out indices);
        _indexCount = (uint)indices.Length;

        // 2. Precompute instance offsets (grid units)
        BuildOffsetData(
            _meshSize,
            out _tileOffsetsLod0, out _tileOffsets,
            out _trimAOffsets, out _trimBOffsets,
            out _fillAOffsets, out _fillBOffsets,
            out _edgePos, out _offsetA);

        // 3. 24 instances for LOD0, 20 for every other ring
        _instances = new List<ClipmapInstance>(24 + 20 * (_lodCount - 1));
        _vertexBuffer = null!;
        _indexBuffer = null!;
    }

    internal TerrainClipmap(
        int meshSize = DefaultMeshSize,
        int lodCount = DefaultRingCount,
        float vertexSpacing = DefaultVertexSpacing,
        TerrainHeightPyramid? heightPyramid = null)
        : this(meshSize, lodCount, vertexSpacing, heightPyramid, out _, out _)
    {
    }

    public unsafe TerrainClipmap(
        VulkanContext context,
        GpuAllocator allocator,
        TerrainHeightPyramid? heightPyramid = null,
        int meshSize = DefaultMeshSize,
        int lodCount = DefaultRingCount,
        float vertexSpacing = DefaultVertexSpacing)
        : this(meshSize, lodCount, vertexSpacing, heightPyramid, out var vertices, out var indices)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        // 4. Allocate GPU buffers and upload mesh data
        _vertexBuffer = _allocator.CreateBufferWithData<TerrainVertex>(
            vertices,
            VkBufferUsageFlags.VertexBufferBit,
            VmaMemoryUsage.CpuToGpu);

        _indexBuffer = _allocator.CreateBufferWithData<uint>(
            indices,
            VkBufferUsageFlags.IndexBufferBit,
            VmaMemoryUsage.CpuToGpu);
    }

    /// <summary>
    /// Registers a resident 256m terrain tile.
    /// </summary>
    public void RegisterTile(TerrainTile tile)
    {
        _residentTiles[tile.Coord] = tile;
    }

    /// <summary>
    /// Evicts an unloaded terrain tile.
    /// </summary>
    public void UnregisterTile(in TileCoord coord)
    {
        _residentTiles.Remove(coord);
    }

    /// <summary>
    /// Evaluates continuous terrain height at arbitrary double-precision coordinates.
    /// Samples high-res resident tile if present, otherwise samples coarse height pyramid (D22).
    /// </summary>
    public float SampleElevation(double worldX, double worldZ)
    {
        var tileCoord = TileCoord.FromWorld(worldX, worldZ, WorldConstants.TileSize);
        if (_residentTiles.TryGetValue(tileCoord, out var tile))
        {
            tile.Coord.GetWorldOrigin(WorldConstants.TileSize, out double ox, out double oz);
            float localX = (float)(worldX - ox);
            float localZ = (float)(worldZ - oz);
            return tile.SampleHeightLocal(localX, localZ);
        }

        return _heightPyramid.SampleHeightWorld(worldX, worldZ);
    }

    /// <summary>
    /// Forces the next Update() to rebuild the instance list (call after changing MeshSize/spacing or on teleport).
    /// </summary>
    public void ResetTargetPosition() => _hasLastTarget = false;

    /// <summary>
    /// Re-centres every ring on the target (the active camera) in double-precision world space.
    /// Equivalent of Terrain3DMesher::snap(). Cheap: returns false immediately unless the target moved
    /// at least one LOD0 vertex spacing, in which case the instance list is rebuilt.
    /// </summary>
    /// <returns>True if the Instances list changed.</returns>
    public bool Update(double targetWorldX, double targetWorldZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double spacing = _vertexSpacing;

        if (_hasLastTarget &&
            Math.Max(Math.Abs(_lastTargetX - targetWorldX), Math.Abs(_lastTargetZ - targetWorldZ)) < spacing)
        {
            return false;
        }

        _hasLastTarget = true;
        _lastTargetX = targetWorldX;
        _lastTargetZ = targetWorldZ;
        _instances.Clear();

        double snappedX = Math.Floor(targetWorldX / spacing) * spacing;
        double snappedZ = Math.Floor(targetWorldZ / spacing) * spacing;

        for (int lod = 0; lod < _lodCount; lod++)
        {
            double lodMul = (double)(1L << lod);
            double scale = lodMul * spacing;
            double snapStep = lodMul * 2.0 * spacing;

            // Snap this ring to its own 2x grid so its vertices coincide with every other vertex of the ring below
            double posX = RoundAway(snappedX / snapStep) * snapStep;
            double posZ = RoundAway(snappedZ / snapStep) * snapStep;

            // Where this ring sits inside the next (coarser) ring decides where the 2-wide edge strips go:
            // 0 / 1 / 2 = both before / straddle / both after
            double nextStep = snapStep * 2.0;
            double nextX = RoundAway(snappedX / nextStep) * nextStep;
            double nextZ = RoundAway(snappedZ / nextStep) * nextStep;
            int testX = Math.Clamp((int)RoundAway((posX - nextX) / snapStep) + 1, 0, 2);
            int testZ = Math.Clamp((int)RoundAway((posZ - nextZ) / snapStep) + 1, 0, 2);

            bool lod0 = lod == 0;
            float fScale = (float)scale;

            // Tiles
            var tiles = lod0 ? _tileOffsetsLod0 : _tileOffsets;
            var tileType = lod0 ? ClipmapMeshType.StandardTile : ClipmapMeshType.Tile;
            for (int i = 0; i < tiles.Length; i++)
                Emit(tileType, lod, tiles[i].X, tiles[i].Z, posX, posZ, scale, fScale);

            // Edge strips (all LODs, two of each)
            var edgeAType = lod0 ? ClipmapMeshType.StandardEdgeA : ClipmapMeshType.EdgeA;
            var edgeBType = lod0 ? ClipmapMeshType.StandardEdgeB : ClipmapMeshType.EdgeB;
            for (int i = 0; i < 2; i++)
            {
                int ex = _edgePos[i][testX];
                int ez = -(_offsetA + testZ * 2);
                Emit(edgeAType, lod, ex, ez, posX, posZ, scale, fScale);
            }
            for (int i = 0; i < 2; i++)
            {
                int ex = -_offsetA;
                int ez = _edgePos[i][testZ];
                Emit(edgeBType, lod, ex, ez, posX, posZ, scale, fScale);
            }

            // LOD0: trims close the ring; LOD1+: fills close the ring
            if (lod0)
            {
                for (int i = 0; i < _trimAOffsets.Length; i++)
                    Emit(ClipmapMeshType.StandardTrimA, lod, _trimAOffsets[i].X, _trimAOffsets[i].Z, posX, posZ, scale, fScale);
                for (int i = 0; i < _trimBOffsets.Length; i++)
                    Emit(ClipmapMeshType.StandardTrimB, lod, _trimBOffsets[i].X, _trimBOffsets[i].Z, posX, posZ, scale, fScale);
            }
            else
            {
                for (int i = 0; i < _fillAOffsets.Length; i++)
                    Emit(ClipmapMeshType.FillA, lod, _fillAOffsets[i].X, _fillAOffsets[i].Z, posX, posZ, scale, fScale);
                for (int i = 0; i < _fillBOffsets.Length; i++)
                    Emit(ClipmapMeshType.FillB, lod, _fillBOffsets[i].X, _fillBOffsets[i].Z, posX, posZ, scale, fScale);
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the per-piece push constants. Origin is made camera-relative in double precision before
    /// being narrowed to float, so it is safe for any world size.
    /// TerrainParams.z carries meshSize, made negative when debugRings is enabled.
    /// </summary>
    public TerrainPushConstants MakePushConstants(
        in ClipmapInstance instance,
        in Matrix4x4 viewProj,
        double cameraWorldX, double cameraWorldY, double cameraWorldZ,
        bool debugRings = false)
    {
        return new TerrainPushConstants
        {
            ViewProjMatrix = viewProj,
            CameraRelativeOrigin = new Vector4(
                (float)(instance.WorldX - cameraWorldX),
                (float)(-cameraWorldY), // vertex shader adds the sampled height
                (float)(instance.WorldZ - cameraWorldZ),
                instance.Scale),
            TerrainParams = new Vector4(
                (float)instance.WorldX,
                (float)instance.WorldZ,
                debugRings ? -_meshSize : _meshSize,
                instance.Lod),
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    /// <summary>C/C++ round(): halves round away from zero. Must match the reference or edge tests desync.</summary>
    private static double RoundAway(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    private void Emit(ClipmapMeshType type, int lod, int offX, int offZ, double posX, double posZ, double scale, float fScale)
    {
        // Terrain3D: t.origin = offset; t = t.scaled(lod_scale); t.origin += pos  => origin = offset * scale + pos
        var piece = _pieces[(int)type];
        _instances.Add(new ClipmapInstance(
            type,
            lod,
            offX * scale + posX,
            offZ * scale + posZ,
            fScale,
            (float)(piece.SizeX * scale),
            (float)(piece.SizeZ * scale)));
    }

    /// <summary>
    /// All offsets are in grid units. Meshes grow along +X/+Z from their minimum corner.
    /// Direct port of Terrain3DMesher::_generate_offset_data.
    /// </summary>
    private static void BuildOffsetData(
        int m,
        out (int X, int Z)[] tileLod0, out (int X, int Z)[] tile,
        out (int X, int Z)[] trimA, out (int X, int Z)[] trimB,
        out (int X, int Z)[] fillA, out (int X, int Z)[] fillB,
        out int[][] edgePos, out int offsetA)
    {
        // LOD0: full 4x4 grid of tiles
        tileLod0 = new (int, int)[]
        {
            (0, m), (m, m), (m, 0), (m, -m), (m, -2 * m), (0, -2 * m),
            (-m, -2 * m), (-2 * m, -2 * m), (-2 * m, -m), (-2 * m, 0), (-2 * m, m), (-m, m),
            // inner tiles
            (0, 0), (-m, 0), (0, -m), (-m, -m),
        };

        // LOD0: fixed 2-wide trim around the tiles
        trimA = new (int, int)[] { (2 * m, -2 * m), (-2 * m - 2, -2 * m - 2) };
        trimB = new (int, int)[] { (-2 * m, -2 * m - 2), (-2 * m - 2, 2 * m) };

        // LOD1+: ring of 12 tiles with a 2-unit gap per axis for the fills
        tile = new (int, int)[]
        {
            (2, m + 2), (m + 2, m + 2), (m + 2, -2), (m + 2, -m - 2), (m + 2, -2 * m - 2), (-2, -2 * m - 2),
            (-m - 2, -2 * m - 2), (-2 * m - 2, -2 * m - 2), (-2 * m - 2, -m + 2), (-2 * m - 2, 2),
            (-2 * m - 2, m + 2), (-m + 2, m + 2),
        };

        // Edge strips: x (EdgeA) or z (EdgeB) is picked by test index 0/1/2 depending on where the
        // ring sits inside the next one
        offsetA = 2 * m + 2;
        int b = 2 * m + 4;
        int c = 2 * m + 6;
        edgePos = new[]
        {
            new[] { offsetA, offsetA, -b },
            new[] { b, -b, -c },
        };

        // Fills occupy the gaps between tiles on LOD1+
        fillA = new (int, int)[] { (m - 2, -2 * m - 2), (-m - 2, m + 2) };
        fillB = new (int, int)[] { (m + 2, m - 2), (-2 * m - 2, -m - 2) };
    }

    /// <summary>
    /// Builds all 10 piece meshes into shared arrays (same sizes as Terrain3DMesher::_generate_mesh_types).
    /// </summary>
    private static ClipmapMeshPiece[] BuildMeshPieces(int m, out TerrainVertex[] vertices, out uint[] indices)
    {
        var vertList = new List<TerrainVertex>();
        var idxList = new List<uint>();
        var pieces = new ClipmapMeshPiece[10];

        void Add(ClipmapMeshType type, int sizeX, int sizeZ, bool standardGrid)
        {
            int vertexOffset = vertList.Count;
            uint firstIndex = (uint)idxList.Count;
            int stride = sizeX + 1;

            for (int z = 0; z <= sizeZ; z++)
            {
                for (int x = 0; x <= sizeX; x++)
                {
                    vertList.Add(new TerrainVertex(
                        new float3(x, 0f, z),
                        new float3(0f, 1f, 0f),
                        new float2(x, z)));
                }
            }

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    uint tl = (uint)(z * stride + x);
                    uint tr = tl + 1;
                    uint bl = (uint)((z + 1) * stride + x);
                    uint br = bl + 1;

                    // Alternating diagonals (Terrain3D). Standard grid = always the "even" pattern.
                    // Winding matches the engine's existing (tl, bl, tr) convention.
                    if (((x + z) & 1) == 0 || standardGrid)
                    {
                        // diagonal tl - br
                        idxList.Add(tl); idxList.Add(bl); idxList.Add(br);
                        idxList.Add(tl); idxList.Add(br); idxList.Add(tr);
                    }
                    else
                    {
                        // diagonal bl - tr
                        idxList.Add(tl); idxList.Add(bl); idxList.Add(tr);
                        idxList.Add(tr); idxList.Add(bl); idxList.Add(br);
                    }
                }
            }

            pieces[(int)type] = new ClipmapMeshPiece(
                vertexOffset, firstIndex, (uint)(idxList.Count - (int)firstIndex), sizeX, sizeZ);
        }

        Add(ClipmapMeshType.Tile, m, m, false);
        Add(ClipmapMeshType.EdgeA, 2, m * 4 + 8, false);
        Add(ClipmapMeshType.EdgeB, m * 4 + 4, 2, false);
        Add(ClipmapMeshType.FillA, 4, m, false);
        Add(ClipmapMeshType.FillB, m, 4, false);
        Add(ClipmapMeshType.StandardTrimA, 2, m * 4 + 2, true);
        Add(ClipmapMeshType.StandardTrimB, m * 4 + 2, 2, true);
        Add(ClipmapMeshType.StandardTile, m, m, true);
        Add(ClipmapMeshType.StandardEdgeA, 2, m * 4 + 8, true);
        Add(ClipmapMeshType.StandardEdgeB, m * 4 + 4, 2, true);

        vertices = vertList.ToArray();
        indices = idxList.ToArray();
        return pieces;
    }

    public void Dispose()
    {
        lock (this)
        {
            if (_disposed) return;
        _disposed = true;
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _residentTiles.Clear();
            _instances.Clear();
        }
    }
}
