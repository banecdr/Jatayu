/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: tools/Jatayu.WorldGen/Program.cs
 * 
 * WHAT THIS FILE DOES:
 *   Procedural World Generator for the Jatayu 3D Engine.
 *   - Generates procedural placeholder glTF 2.0 (.glb) models (rocks, buildings, trees, grass).
 *   - Generates procedural 16 km x 16 km world grid cells (up to 128x128 = 16,384 cells)
 *     with deterministic pseudo-random scatter tables, static mesh instances, and bounds.
 *   - Outputs cooked .jcell binary chunk records ready for packing into .jpak archives.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Brutal.Numerics;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;

namespace Jatayu.WorldGen;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("             JATAYU PROCEDURAL WORLD GENERATOR (SPRINT 6A)                    ");
        Console.WriteLine("================================================================================");

        string outputDir = "assets/generated";
        int gridDim = 16; // Default to 16x16 cells (2 km x 2 km), can be set up to 128 for 16 km x 16 km

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                outputDir = args[++i];
            }
            else if (args[i] == "--grid" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out gridDim) ||
                    gridDim < 1 || gridDim > 128 || (gridDim & 1) != 0)
                {
                    Console.Error.WriteLine("[WorldGen] --grid must be an even integer between 2 and 128.");
                    return 2;
                }
            }
        }

        Directory.CreateDirectory(outputDir);
        string modelsDir = Path.Combine(outputDir, "models");
        string cellsDir = Path.Combine(outputDir, "cells");
        Directory.CreateDirectory(modelsDir);
        Directory.CreateDirectory(cellsDir);

        Console.WriteLine($"[WorldGen] Output Directory: {outputDir}");
        Console.WriteLine($"[WorldGen] Generating placeholder .glb models...");

        // 1. Generate Models
        string rockPath = Path.Combine(modelsDir, "rock.glb");
        string buildingPath = Path.Combine(modelsDir, "building.glb");
        string treePath = Path.Combine(modelsDir, "tree.glb");
        string grassPath = Path.Combine(modelsDir, "grass.glb");

        GenerateRockModel(rockPath);
        GenerateBuildingModel(buildingPath);
        GenerateTreeModel(treePath);
        GenerateGrassModel(grassPath);

        Console.WriteLine($"  - {rockPath}");
        Console.WriteLine($"  - {buildingPath}");
        Console.WriteLine($"  - {treePath}");
        Console.WriteLine($"  - {grassPath}");

        // 2. Generate Cell Scatter Records
        Console.WriteLine($"[WorldGen] Generating procedural world grid ({gridDim}x{gridDim} = {gridDim * gridDim} cells)...");

        var rockGuid = AssetGuid.FromPath("rock");
        var buildingGuid = AssetGuid.FromPath("building");
        var treeGuid = AssetGuid.FromPath("tree");

        int totalInstances = 0;
        int halfGrid = gridDim / 2;

        for (int cz = -halfGrid; cz < halfGrid; cz++)
        {
            for (int cx = -halfGrid; cx < halfGrid; cx++)
            {
                var coord = new CellCoord(cx, cz);
                var instances = GenerateCellInstances(coord, rockGuid, buildingGuid, treeGuid);
                totalInstances += instances.Length;

                byte[] cellBytes = SerializeCell(coord, instances);
                string cellFileName = $"{coord.ToAssetGuid().Value:x16}.jcell";
                string cellFilePath = Path.Combine(cellsDir, cellFileName);
                File.WriteAllBytes(cellFilePath, cellBytes);
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[WorldGen] Successfully generated {gridDim * gridDim} cells ({totalInstances:N0} static instances) in '{cellsDir}'");
        Console.ResetColor();

        return 0;
    }

    private static float EvaluateTerrainElevation(double worldX, double worldZ)
    {
        const float maxElevation = 45.0f;
        float h1 = MathF.Sin((float)worldX * 0.005f) * MathF.Cos((float)worldZ * 0.005f) * maxElevation;
        float h2 = MathF.Sin((float)worldX * 0.015f + 1.2f) * MathF.Sin((float)worldZ * 0.015f + 0.8f) * (maxElevation * 0.35f);
        float h3 = MathF.Cos((float)worldX * 0.04f - 0.5f) * MathF.Sin((float)worldZ * 0.04f + 1.7f) * (maxElevation * 0.1f);

        float dist = MathF.Sqrt((float)(worldX * worldX + worldZ * worldZ));
        float flatFactor = Math.Clamp((dist - 200.0f) / 60.0f, 0.0f, 1.0f);
        flatFactor = flatFactor * flatFactor * (3.0f - 2.0f * flatFactor); // smoothstep
        return (h1 + h2 + h3) * flatFactor;
    }

    private static CookedStaticInstance[] GenerateCellInstances(
        CellCoord coord,
        AssetGuid rockGuid,
        AssetGuid buildingGuid,
        AssetGuid treeGuid)
    {
        // Deterministic pseudo-random seed per cell
        int seed = unchecked((coord.X * 73856093) ^ (coord.Z * 19349663) ^ 0x5bd1e995);
        var rng = new Random(seed);

        // Density: 10 to 30 instances per 128m cell
        int candidateCount = rng.Next(12, 28);
        var list = new List<CookedStaticInstance>(candidateCount);

        for (int i = 0; i < candidateCount; i++)
        {
            float localX = (float)rng.NextDouble() * (WorldConstants.CellSize - 16f) + 8f;
            float localZ = (float)rng.NextDouble() * (WorldConstants.CellSize - 16f) + 8f;

            double worldX = (coord.X * WorldConstants.CellSize) + localX;
            double worldZ = (coord.Z * WorldConstants.CellSize) + localZ;

            // Exclusion zone around exhibition pavilion plinth
            if (Math.Abs(worldX) < 100.0 && Math.Abs(worldZ) < 100.0)
            {
                continue;
            }

            float localY = EvaluateTerrainElevation(worldX, worldZ);

            // Random yaw rotation around Y axis
            float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);
            float sinHalf = MathF.Sin(yaw * 0.5f);
            float cosHalf = MathF.Cos(yaw * 0.5f);

            int typeRoll = rng.Next(100);
            AssetGuid meshGuid;
            float scale;
            float radius;

            if (typeRoll < 20)
            {
                // Building
                meshGuid = buildingGuid;
                scale = (float)rng.NextDouble() * 1.5f + 1.0f;
                radius = 8.0f * scale;
            }
            else if (typeRoll < 60)
            {
                // Tree
                meshGuid = treeGuid;
                scale = (float)rng.NextDouble() * 1.0f + 0.8f;
                radius = 4.0f * scale;
            }
            else
            {
                // Rock
                meshGuid = rockGuid;
                scale = (float)rng.NextDouble() * 1.2f + 0.5f;
                radius = 2.5f * scale;
            }

            list.Add(new CookedStaticInstance
            {
                PosX = localX,
                PosY = localY,
                PosZ = localZ,
                RotX = 0f,
                RotY = sinHalf,
                RotZ = 0f,
                RotW = cosHalf,
                ScaleX = scale,
                ScaleY = scale,
                ScaleZ = scale,
                MeshGuid = meshGuid,
                MaterialGuid = AssetGuid.Empty,
                MaterialId = 0,
                BoundingRadius = radius,
                Flags = StaticInstanceFlags.CastShadow,
            });
        }

        return list.ToArray();
    }

    private static byte[] SerializeCell(CellCoord coord, CookedStaticInstance[] instances)
    {
        AssetGuid cellGuid = coord.ToAssetGuid();
        float minX = 0f, minY = 0f, minZ = 0f;
        float maxX = WorldConstants.CellSize;
        float maxY = 40f;
        float maxZ = WorldConstants.CellSize;

        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var inst = ref instances[i];
            float r = inst.BoundingRadius;
            minX = MathF.Min(minX, inst.PosX - r);
            minY = MathF.Min(minY, inst.PosY - r);
            minZ = MathF.Min(minZ, inst.PosZ - r);
            maxX = MathF.Max(maxX, inst.PosX + r);
            maxY = MathF.Max(maxY, inst.PosY + r);
            maxZ = MathF.Max(maxZ, inst.PosZ + r);
        }

        var header = CookedCellHeader.Create(
            coord,
            cellGuid,
            (uint)instances.Length,
            0,
            0,
            CellFlags.None,
            minX, minY, minZ,
            maxX, maxY, maxZ
        );

        int headerSize = Marshal.SizeOf<CookedCellHeader>();
        int instanceSize = Marshal.SizeOf<CookedStaticInstance>();
        int totalSize = headerSize + (instances.Length * instanceSize);

        byte[] bytes = new byte[totalSize];
        MemoryMarshal.Write(bytes.AsSpan(0, headerSize), in header);

        if (instances.Length > 0)
        {
            Span<byte> dst = bytes.AsSpan((int)header.StaticInstancesOffset, instances.Length * instanceSize);
            MemoryMarshal.AsBytes<CookedStaticInstance>(instances).CopyTo(dst);
        }

        return bytes;
    }

    #region Procedural GLB Mesh Generators

    private static void GenerateRockModel(string path)
    {
        // Perturbed box for low-poly rock
        var (verts, indices) = CreateBoxMesh(new float3(2f, 1.5f, 2f), new float3(0.55f, 0.52f, 0.50f));
        WriteGlb(path, verts, indices);
    }

    private static void GenerateBuildingModel(string path)
    {
        // Simple rectangular building
        var (verts, indices) = CreateBoxMesh(new float3(8f, 12f, 10f), new float3(0.72f, 0.70f, 0.68f));
        WriteGlb(path, verts, indices);
    }

    private static void GenerateTreeModel(string path)
    {
        // Simple tree: trunk box + canopy box
        var (trunkV, trunkI) = CreateBoxMesh(new float3(0.6f, 3.0f, 0.6f), new float3(0.40f, 0.25f, 0.15f), new float3(0, 1.5f, 0));
        var (canopyV, canopyI) = CreateBoxMesh(new float3(3.0f, 4.0f, 3.0f), new float3(0.18f, 0.55f, 0.22f), new float3(0, 4.5f, 0));

        var combinedV = new List<GlbVertex>(trunkV.Length + canopyV.Length);
        var combinedI = new List<uint>(trunkI.Length + canopyI.Length);

        combinedV.AddRange(trunkV);
        combinedI.AddRange(trunkI);

        uint offset = (uint)trunkV.Length;
        combinedV.AddRange(canopyV);
        for (int i = 0; i < canopyI.Length; i++)
            combinedI.Add(canopyI[i] + offset);

        WriteGlb(path, combinedV.ToArray(), combinedI.ToArray());
    }

    private static void GenerateGrassModel(string path)
    {
        // Cross quad cards for grass
        var verts = new GlbVertex[8];
        var indices = new uint[]
        {
            0, 1, 2, 0, 2, 3, // Quad 1
            4, 5, 6, 4, 6, 7  // Quad 2
        };

        float3 green = new(0.25f, 0.65f, 0.20f);
        float h = 1.0f;
        float w = 0.5f;

        // Quad 1: along X axis
        verts[0] = new GlbVertex(new float3(-w, 0, 0), new float3(0, 1, 0), new float2(0, 0), green);
        verts[1] = new GlbVertex(new float3(w, 0, 0), new float3(0, 1, 0), new float2(1, 0), green);
        verts[2] = new GlbVertex(new float3(w, h, 0), new float3(0, 1, 0), new float2(1, 1), green);
        verts[3] = new GlbVertex(new float3(-w, h, 0), new float3(0, 1, 0), new float2(0, 1), green);

        // Quad 2: along Z axis
        verts[4] = new GlbVertex(new float3(0, 0, -w), new float3(0, 1, 0), new float2(0, 0), green);
        verts[5] = new GlbVertex(new float3(0, 0, w), new float3(0, 1, 0), new float2(1, 0), green);
        verts[6] = new GlbVertex(new float3(0, h, w), new float3(0, 1, 0), new float2(1, 1), green);
        verts[7] = new GlbVertex(new float3(0, h, -w), new float3(0, 1, 0), new float2(0, 1), green);

        WriteGlb(path, verts, indices);
    }

    private struct GlbVertex
    {
        public float3 Position;
        public float3 Normal;
        public float2 TexCoord;
        public float3 Color;

        public GlbVertex(float3 pos, float3 normal, float2 uv, float3 color)
        {
            Position = pos;
            Normal = normal;
            TexCoord = uv;
            Color = color;
        }
    }

    private static (GlbVertex[] Vertices, uint[] Indices) CreateBoxMesh(
        float3 size,
        float3 color,
        float3 center = default)
    {
        float hx = size.X * 0.5f;
        float hy = size.Y * 0.5f;
        float hz = size.Z * 0.5f;

        // 6 faces * 4 vertices = 24 vertices
        var vertices = new GlbVertex[24];
        var indices = new uint[36];

        float3[] faceNormals =
        {
            new(0, 0, 1),   // +Z Front
            new(0, 0, -1),  // -Z Back
            new(-1, 0, 0),  // -X Left
            new(1, 0, 0),   // +X Right
            new(0, 1, 0),   // +Y Top
            new(0, -1, 0),  // -Y Bottom
        };

        float3[][] facePositions =
        {
            // Front (+Z)
            new float3[] { new(-hx, -hy, hz), new(hx, -hy, hz), new(hx, hy, hz), new(-hx, hy, hz) },
            // Back (-Z)
            new float3[] { new(hx, -hy, -hz), new(-hx, -hy, -hz), new(-hx, hy, -hz), new(hx, hy, -hz) },
            // Left (-X)
            new float3[] { new(-hx, -hy, -hz), new(-hx, -hy, hz), new(-hx, hy, hz), new(-hx, hy, -hz) },
            // Right (+X)
            new float3[] { new(hx, -hy, hz), new(hx, -hy, -hz), new(hx, hy, -hz), new(hx, hy, hz) },
            // Top (+Y)
            new float3[] { new(-hx, hy, hz), new(hx, hy, hz), new(hx, hy, -hz), new(-hx, hy, -hz) },
            // Bottom (-Y)
            new float3[] { new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz) },
        };

        int vIdx = 0;
        int iIdx = 0;

        for (int face = 0; face < 6; face++)
        {
            uint baseV = (uint)vIdx;
            var n = faceNormals[face];
            var corners = facePositions[face];

            vertices[vIdx++] = new GlbVertex(corners[0] + center, n, new float2(0, 0), color);
            vertices[vIdx++] = new GlbVertex(corners[1] + center, n, new float2(1, 0), color);
            vertices[vIdx++] = new GlbVertex(corners[2] + center, n, new float2(1, 1), color);
            vertices[vIdx++] = new GlbVertex(corners[3] + center, n, new float2(0, 1), color);

            indices[iIdx++] = baseV;
            indices[iIdx++] = baseV + 1;
            indices[iIdx++] = baseV + 2;
            indices[iIdx++] = baseV;
            indices[iIdx++] = baseV + 2;
            indices[iIdx++] = baseV + 3;
        }

        return (vertices, indices);
    }

    private static void WriteGlb(string path, GlbVertex[] vertices, uint[] indices)
    {
        // Build binary buffer: Indices (uint32) then Positions (float3) then Normals (float3) then TexCoords (float2) then Colors (float3)
        int indexBytes = indices.Length * sizeof(uint);
        int posBytes = vertices.Length * sizeof(float) * 3;
        int normBytes = vertices.Length * sizeof(float) * 3;
        int uvBytes = vertices.Length * sizeof(float) * 2;
        int colBytes = vertices.Length * sizeof(float) * 3;

        int totalBinBytes = indexBytes + posBytes + normBytes + uvBytes + colBytes;
        // Pad to 4-byte boundary
        int binPadding = (4 - (totalBinBytes % 4)) % 4;
        byte[] binData = new byte[totalBinBytes + binPadding];

        using (var ms = new MemoryStream(binData))
        using (var bw = new BinaryWriter(ms))
        {
            // Indices
            for (int i = 0; i < indices.Length; i++) bw.Write(indices[i]);
            // Positions
            for (int i = 0; i < vertices.Length; i++) { bw.Write(vertices[i].Position.X); bw.Write(vertices[i].Position.Y); bw.Write(vertices[i].Position.Z); }
            // Normals
            for (int i = 0; i < vertices.Length; i++) { bw.Write(vertices[i].Normal.X); bw.Write(vertices[i].Normal.Y); bw.Write(vertices[i].Normal.Z); }
            // TexCoords
            for (int i = 0; i < vertices.Length; i++) { bw.Write(vertices[i].TexCoord.X); bw.Write(vertices[i].TexCoord.Y); }
            // Colors
            for (int i = 0; i < vertices.Length; i++) { bw.Write(vertices[i].Color.X); bw.Write(vertices[i].Color.Y); bw.Write(vertices[i].Color.Z); }
        }

        int offsetIndices = 0;
        int offsetPos = indexBytes;
        int offsetNorm = offsetPos + posBytes;
        int offsetUv = offsetNorm + normBytes;
        int offsetCol = offsetUv + uvBytes;

        // Compute Bounding Box
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < vertices.Length; i++)
        {
            minX = MathF.Min(minX, vertices[i].Position.X);
            minY = MathF.Min(minY, vertices[i].Position.Y);
            minZ = MathF.Min(minZ, vertices[i].Position.Z);
            maxX = MathF.Max(maxX, vertices[i].Position.X);
            maxY = MathF.Max(maxY, vertices[i].Position.Y);
            maxZ = MathF.Max(maxZ, vertices[i].Position.Z);
        }

        // Construct standard glTF 2.0 JSON
        var jsonObj = new
        {
            asset = new { version = "2.0", generator = "Jatayu.WorldGen" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[]
            {
                new
                {
                    primitives = new[]
                    {
                        new
                        {
                            attributes = new Dictionary<string, int>
                            {
                                { "POSITION", 1 },
                                { "NORMAL", 2 },
                                { "TEXCOORD_0", 3 },
                                { "COLOR_0", 4 }
                            },
                            indices = 0,
                            mode = 4 // TRIANGLES
                        }
                    }
                }
            },
            accessors = new object[]
            {
                // 0: INDICES
                new { bufferView = 0, byteOffset = 0, componentType = 5125, count = indices.Length, type = "SCALAR" },
                // 1: POSITION
                new { bufferView = 1, byteOffset = 0, componentType = 5126, count = vertices.Length, type = "VEC3", min = new[] { minX, minY, minZ }, max = new[] { maxX, maxY, maxZ } },
                // 2: NORMAL
                new { bufferView = 2, byteOffset = 0, componentType = 5126, count = vertices.Length, type = "VEC3" },
                // 3: TEXCOORD_0
                new { bufferView = 3, byteOffset = 0, componentType = 5126, count = vertices.Length, type = "VEC2" },
                // 4: COLOR_0
                new { bufferView = 4, byteOffset = 0, componentType = 5126, count = vertices.Length, type = "VEC3" }
            },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = offsetIndices, byteLength = indexBytes, target = 34963 }, // ELEMENT_ARRAY_BUFFER
                new { buffer = 0, byteOffset = offsetPos, byteLength = posBytes, target = 34962 }, // ARRAY_BUFFER
                new { buffer = 0, byteOffset = offsetNorm, byteLength = normBytes, target = 34962 },
                new { buffer = 0, byteOffset = offsetUv, byteLength = uvBytes, target = 34962 },
                new { buffer = 0, byteOffset = offsetCol, byteLength = colBytes, target = 34962 }
            },
            buffers = new[]
            {
                new { byteLength = binData.Length }
            }
        };

        string jsonString = JsonSerializer.Serialize(jsonObj);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonString);
        int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;

        int totalGlbLength = 12 + 8 + jsonBytes.Length + jsonPadding + 8 + binData.Length;

        using var fs = File.Create(path);
        using var fileWriter = new BinaryWriter(fs);

        // GLB Header
        fileWriter.Write((uint)0x46546C67); // "glTF"
        fileWriter.Write((uint)2);          // version
        fileWriter.Write((uint)totalGlbLength);

        // JSON Chunk
        fileWriter.Write((uint)(jsonBytes.Length + jsonPadding));
        fileWriter.Write((uint)0x4E4F534A); // "JSON"
        fileWriter.Write(jsonBytes);
        for (int p = 0; p < jsonPadding; p++) fileWriter.Write((byte)0x20); // space padding

        // BIN Chunk
        fileWriter.Write((uint)binData.Length);
        fileWriter.Write((uint)0x004E4942); // "BIN\0"
        fileWriter.Write(binData);
    }

    #endregion
}
