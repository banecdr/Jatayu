/* =================================================================================================
 * FILE: TerrainTile.cs
 * LOCATION: src/Terrain/TerrainTile.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a 256m x 256m streamed terrain chunk covering 2x2 cells (keyed by TileCoord = CellCoord / 2)
 *   per architectural decision D15.
 *   - Owns high-resolution elevation height data (e.g. 65x65 samples = 4m quad spacing).
 *   - Computes double-precision world origins invariant across floating-origin shifts.
 *   - Computes local surface normals and bilinear height interpolation for ground clamping.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;

namespace Jatayu.Terrain;

/// <summary>
/// 2D integer tile coordinate identifying a 256m terrain chunk.
/// </summary>
public readonly struct TileCoord : IEquatable<TileCoord>, IComparable<TileCoord>
{
    public readonly int X;
    public readonly int Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TileCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TileCoord FromCell(in CellCoord cell)
    {
        var (tx, tz) = WorldConstants.GetTileCoordForCell(in cell);
        return new TileCoord(tx, tz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TileCoord FromWorld(double worldX, double worldZ, float tileSize = WorldConstants.TileSize)
    {
        int tx = (int)Math.Floor(worldX / tileSize);
        int tz = (int)Math.Floor(worldZ / tileSize);
        return new TileCoord(tx, tz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetWorldOrigin(float tileSize, out double ox, out double oz)
    {
        ox = (double)X * tileSize;
        oz = (double)Z * tileSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(TileCoord other) => X == other.X && Z == other.Z;

    public override bool Equals(object? obj) => obj is TileCoord other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => unchecked((X * 73856093) ^ (Z * 19349663));

    public int CompareTo(TileCoord other)
    {
        int cmp = Z.CompareTo(other.Z);
        return cmp != 0 ? cmp : X.CompareTo(other.X);
    }

    public override string ToString() => $"Tile({X}, {Z})";

    public static bool operator ==(TileCoord left, TileCoord right) => left.Equals(right);
    public static bool operator !=(TileCoord left, TileCoord right) => !left.Equals(right);
}

/// <summary>
/// Encapsulates a 256m streamed terrain chunk and its resident elevation dataset.
/// </summary>
public sealed class TerrainTile
{
    public TileCoord Coord { get; }
    public AssetGuid TileGuid { get; }

    /// <summary>
    /// Double-precision world origin coordinate (minimum corner) of this tile.
    /// </summary>
    public WorldPosition Origin { get; private set; }

    /// <summary>
    /// Raw elevation sample array (SamplesX * SamplesZ).
    /// </summary>
    public float[] Heights { get; }

    public uint SamplesX { get; }
    public uint SamplesZ { get; }

    public float MinHeight { get; }
    public float MaxHeight { get; }

    public AssetGuid NormalMapGuid { get; }
    public AssetGuid SplatMapGuid { get; }

    public TerrainTile(
        TileCoord coord,
        uint samplesX,
        uint samplesZ,
        float[] heights,
        float minHeight,
        float maxHeight,
        AssetGuid normalMapGuid = default,
        AssetGuid splatMapGuid = default,
        float tileSize = WorldConstants.TileSize)
    {
        Coord = coord;
        ulong packedTile = ((ulong)(uint)coord.X << 32) | (uint)coord.Z;
        TileGuid = new AssetGuid(packedTile ^ 0x54494C455F54494CUL);

        coord.GetWorldOrigin(tileSize, out double ox, out double oz);
        Origin = new WorldPosition(ox, 0.0, oz);

        SamplesX = samplesX;
        SamplesZ = samplesZ;
        Heights = heights ?? throw new ArgumentNullException(nameof(heights));
        MinHeight = minHeight;
        MaxHeight = maxHeight;
        NormalMapGuid = normalMapGuid;
        SplatMapGuid = splatMapGuid;
    }

    /// <summary>
    /// Updates double-precision origin during a floating-origin rebase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ShiftOrigin(double deltaX, double deltaY, double deltaZ)
    {
        Origin = new WorldPosition(Origin.X + deltaX, Origin.Y + deltaY, Origin.Z + deltaZ);
    }

    /// <summary>
    /// Evaluates bilinearly interpolated height at tile-local coordinates (0..256m).
    /// </summary>
    public float SampleHeightLocal(float localX, float localZ, float tileSize = WorldConstants.TileSize)
    {
        float clampedX = Math.Clamp(localX, 0f, tileSize);
        float clampedZ = Math.Clamp(localZ, 0f, tileSize);

        float fx = (clampedX / tileSize) * (SamplesX - 1);
        float fz = (clampedZ / tileSize) * (SamplesZ - 1);

        int x0 = (int)MathF.Floor(fx);
        int z0 = (int)MathF.Floor(fz);
        int x1 = Math.Min(x0 + 1, (int)SamplesX - 1);
        int z1 = Math.Min(z0 + 1, (int)SamplesZ - 1);

        float tx = fx - x0;
        float tz = fz - z0;

        float h00 = Heights[z0 * SamplesX + x0];
        float h10 = Heights[z0 * SamplesX + x1];
        float h01 = Heights[z1 * SamplesX + x0];
        float h11 = Heights[z1 * SamplesX + x1];

        float h0 = MathF.FusedMultiplyAdd(h10 - h00, tx, h00);
        float h1 = MathF.FusedMultiplyAdd(h11 - h01, tx, h01);
        return MathF.FusedMultiplyAdd(h1 - h0, tz, h0);
    }

    /// <summary>
    /// Decodes a CookedTerrainTileHeader and height array from a raw byte span (.jtile).
    /// </summary>
    public static bool TryDecodeTile(ReadOnlySpan<byte> data, out TerrainTile? tile)
    {
        tile = null;
        int headerSize = Marshal.SizeOf<CookedTerrainTileHeader>();
        if (data.Length < headerSize)
            return false;

        var header = MemoryMarshal.Read<CookedTerrainTileHeader>(data[..headerSize]);
        if (!header.IsValid)
            return false;

        ulong sampleCount = (ulong)header.HeightSamplesX * header.HeightSamplesZ;
        if (sampleCount > int.MaxValue / sizeof(float))
            return false;

        int expectedBytes = checked((int)sampleCount * sizeof(float));
        if (header.HeightDataOffset > (ulong)data.Length ||
            expectedBytes > data.Length ||
            (ulong)expectedBytes > (ulong)data.Length - header.HeightDataOffset)
            return false;

        var heightSpan = MemoryMarshal.Cast<byte, float>(data.Slice((int)header.HeightDataOffset, expectedBytes));
        var heights = heightSpan.ToArray();

        var coord = new TileCoord(header.TileX, header.TileZ);
        tile = new TerrainTile(
            coord,
            header.HeightSamplesX,
            header.HeightSamplesZ,
            heights,
            header.MinHeight,
            header.MaxHeight,
            header.NormalMapGuid,
            header.SplatMapGuid
        );

        return true;
    }

    /// <summary>
    /// Generates a procedural terrain tile matching the terrain.vert multi-frequency elevation formula.
    /// </summary>
    public static TerrainTile CreateProcedural(int tileX, int tileZ, uint samples = 65, float maxElevation = 45.0f, float tileSize = WorldConstants.TileSize)
    {
        var coord = new TileCoord(tileX, tileZ);
        coord.GetWorldOrigin(tileSize, out double ox, out double oz);

        var heights = new float[samples * samples];
        float minH = float.MaxValue;
        float maxH = float.MinValue;
        float step = tileSize / (samples - 1);

        for (int z = 0; z < samples; z++)
        {
            double wz = oz + z * step;
            for (int x = 0; x < samples; x++)
            {
                double wx = ox + x * step;
                float fx = (float)wx;
                float fz = (float)wz;

                float h1 = MathF.Sin(fx * 0.005f) * MathF.Cos(fz * 0.005f) * maxElevation;
                float h2 = MathF.Sin(fx * 0.015f + 1.2f) * MathF.Sin(fz * 0.015f + 0.8f) * (maxElevation * 0.35f);
                float h3 = MathF.Cos(fx * 0.04f - 0.5f) * MathF.Sin(fz * 0.04f + 1.7f) * (maxElevation * 0.1f);

                // Flat clearing around origin (radius 200m) for testing arena and rotating cubes
                float dist = MathF.Sqrt(fx * fx + fz * fz);
                float t = Math.Clamp((dist - 200.0f) / 60.0f, 0.0f, 1.0f);
                float flatFactor = t * t * (3.0f - 2.0f * t);
                float h = (h1 + h2 + h3) * flatFactor;

                heights[z * samples + x] = h;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
        }

        return new TerrainTile(coord, samples, samples, heights, minH, maxH, tileSize: tileSize);
    }

    /// <summary>
    /// Jolt rigid body collider representing this tile's terrain surface in Ring A.
    /// </summary>
    public Physics.PhysicsBody? ColliderBody { get; internal set; }

    /// <summary>
    /// Spawns a static mesh collision shape in the physics engine for this tile.
    /// </summary>
    public void CreatePhysicsCollider(Physics.PhysicsEngine physicsEngine, in WorldPosition cameraPos, float tileSize = WorldConstants.TileSize)
    {
        if (physicsEngine == null) return;
        DestroyPhysicsCollider(physicsEngine);

        // Subsample elevation grid (17x17 grid = 16x16 quads = 16m resolution) for fast, robust collision
        int gridX = 17;
        int gridZ = 17;

        int vertCount = gridX * gridZ;
        var vertices = new Brutal.Numerics.float3[vertCount];

        float relOriginX = (float)(Origin.X - cameraPos.X);
        float relOriginZ = (float)(Origin.Z - cameraPos.Z);

        float dx = tileSize / (gridX - 1);
        float dz = tileSize / (gridZ - 1);

        for (int gz = 0; gz < gridZ; gz++)
        {
            float localZ = gz * dz;
            for (int gx = 0; gx < gridX; gx++)
            {
                float localX = gx * dx;
                float height = SampleHeightLocal(localX, localZ, tileSize);
                vertices[gz * gridX + gx] = new Brutal.Numerics.float3(relOriginX + localX, height, relOriginZ + localZ);
            }
        }

        int quadCount = (gridX - 1) * (gridZ - 1);
        var indices = new uint[quadCount * 6];
        int idx = 0;
        for (int gz = 0; gz < gridZ - 1; gz++)
        {
            for (int gx = 0; gx < gridX - 1; gx++)
            {
                uint i0 = (uint)(gz * gridX + gx);
                uint i1 = (uint)(gz * gridX + gx + 1);
                uint i2 = (uint)((gz + 1) * gridX + gx);
                uint i3 = (uint)((gz + 1) * gridX + gx + 1);

                indices[idx++] = i0;
                indices[idx++] = i2;
                indices[idx++] = i1;

                indices[idx++] = i1;
                indices[idx++] = i2;
                indices[idx++] = i3;
            }
        }

        ColliderBody = physicsEngine.CreateMeshBody(Brutal.Numerics.float3.Zero, vertices, indices);
    }

    /// <summary>
    /// Destroys the physics collider when tile leaves Ring A or unloads.
    /// </summary>
    public void DestroyPhysicsCollider(Physics.PhysicsEngine? physicsEngine)
    {
        if (ColliderBody != null)
        {
            ColliderBody.Dispose();
            ColliderBody = null;
        }
    }
}
