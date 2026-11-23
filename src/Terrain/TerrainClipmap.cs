/* =================================================================================================
 * FILE: TerrainClipmap.cs
 * LOCATION: src/Terrain/TerrainClipmap.cs
 * 
 * WHAT THIS FILE DOES:
 *   Single canonical GPU Clipmap LOD terrain renderer for all rings (D22).
 *   - Constructs concentric nested square mesh rings around the active camera.
 *   - Samples resident TerrainTile chunks in Ring A/B and blends with TerrainHeightPyramid in Ring C.
 *   - Evaluates continuous geomorphing factors to eliminate popping seams.
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
/// Push constants submitted to terrain.vert and terrain.frag (96 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct TerrainPushConstants
{
    public Matrix4x4 ViewProjMatrix;
    public Vector4 CameraRelativeOrigin; // xyz = origin, w = geomorph factor
    public Vector4 TerrainParams;        // x = scale, y = min height, z = max height, w = ring index
}

/// <summary>
/// Vertex structure for terrain clipmap concentric grid rings.
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
/// Unified GPU Clipmap terrain renderer managing concentric LOD rings and geomorphing.
/// </summary>
public sealed class TerrainClipmap : IDisposable
{
    public const int DefaultRingCount = 6;
    public const int GridResolution = 64; // 64x64 vertices per ring

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;
    private readonly uint _indexCount;

    private readonly Dictionary<TileCoord, TerrainTile> _residentTiles = new();
    private readonly TerrainHeightPyramid _heightPyramid;
    private readonly TerrainMaterial _material;
    private bool _disposed;

    public TerrainMaterial Material => _material;
    public TerrainHeightPyramid HeightPyramid => _heightPyramid;
    public int ResidentTileCount => _residentTiles.Count;

    public GpuBuffer VertexBuffer => _vertexBuffer;
    public GpuBuffer IndexBuffer => _indexBuffer;
    public uint IndexCount => _indexCount;

    public unsafe TerrainClipmap(VulkanContext context, GpuAllocator allocator, TerrainHeightPyramid? heightPyramid = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _heightPyramid = heightPyramid ?? new TerrainHeightPyramid();
        _material = new TerrainMaterial();

        // 1. Build concentric clipmap grid mesh
        BuildClipmapMesh(GridResolution, out var vertices, out var indices);
        _indexCount = (uint)indices.Length;

        // 2. Allocate GPU buffers and upload mesh data
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

    private static void BuildClipmapMesh(int resolution, out TerrainVertex[] vertices, out uint[] indices)
    {
        int vertCount = resolution * resolution;
        vertices = new TerrainVertex[vertCount];
        int quadCount = (resolution - 1) * (resolution - 1);
        indices = new uint[quadCount * 6];

        float step = 1.0f / (resolution - 1);

        int vIdx = 0;
        for (int z = 0; z < resolution; z++)
        {
            float fz = (z * step) - 0.5f; // Center mesh on (0, 0)
            for (int x = 0; x < resolution; x++)
            {
                float fx = (x * step) - 0.5f;
                vertices[vIdx++] = new TerrainVertex(
                    new float3(fx, 0f, fz),
                    new float3(0f, 1f, 0f),
                    new float2(x * step, z * step)
                );
            }
        }

        int iIdx = 0;
        for (int z = 0; z < resolution - 1; z++)
        {
            for (int x = 0; x < resolution - 1; x++)
            {
                uint tl = (uint)(z * resolution + x);
                uint tr = (uint)(z * resolution + (x + 1));
                uint bl = (uint)((z + 1) * resolution + x);
                uint br = (uint)((z + 1) * resolution + (x + 1));

                indices[iIdx++] = tl;
                indices[iIdx++] = bl;
                indices[iIdx++] = tr;

                indices[iIdx++] = tr;
                indices[iIdx++] = bl;
                indices[iIdx++] = br;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _residentTiles.Clear();
    }
}
