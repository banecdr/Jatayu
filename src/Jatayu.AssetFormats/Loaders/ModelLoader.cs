/* =================================================================================================
 * FILE: ModelLoader.cs
 * LOCATION: src/Jatayu.AssetFormats/Loaders/ModelLoader.cs
 * 
 * WHAT THIS FILE DOES:
 *   Loads raw 3D mesh geometry, hierarchies, and vertex attributes from glTF 2.0 / GLB files
 *   using Ahwoo.BRUTAL.Gltf (GltfLoader). Extracts positions, normals, colors, texture coordinates,
 *   and indices decoupled from graphics API runtime for cooking and editing pipelines.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Brutal.GltfApi;
using Brutal.Numerics;

namespace Jatayu.AssetFormats.Loaders;

/// <summary>
/// Intermediate raw vertex structure extracted from glTF files before cooking/optimization.
/// </summary>
public struct RawVertex
{
    public float3 Position;
    public float3 Color;
    public float3 Normal;
    public float2 TexCoord;

    public RawVertex(float3 pos, float3 normal, float2 uv, float3 color)
    {
        Position = pos;
        Normal = normal;
        TexCoord = uv;
        Color = color;
    }
}

/// <summary>
/// Represents raw mesh geometry extracted from a glTF model primitive.
/// </summary>
public sealed class RawSubmesh
{
    public string Name { get; }
    public RawVertex[] Vertices { get; }
    public uint[] Indices { get; }
    public int MaterialIndex { get; }

    public RawSubmesh(string name, RawVertex[] vertices, uint[] indices, int materialIndex = -1)
    {
        Name = name;
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        MaterialIndex = materialIndex;
    }
}

/// <summary>
/// Intermediate model container representing all submeshes and materials in a glTF asset.
/// </summary>
public sealed class RawModelData
{
    public string SourcePath { get; }
    public IReadOnlyList<RawSubmesh> Submeshes { get; }

    public RawModelData(string sourcePath, IReadOnlyList<RawSubmesh> submeshes)
    {
        SourcePath = sourcePath;
        Submeshes = submeshes ?? throw new ArgumentNullException(nameof(submeshes));
    }
}

/// <summary>
/// Importer for glTF 2.0 (.gltf / .glb) 3D assets using Brutal.Gltf.
/// </summary>
public static class ModelLoader
{
    /// <summary>
    /// Loads and parses a glTF 2.0 file from disk.
    /// </summary>
    public static unsafe RawModelData LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"glTF model file not found: {filePath}", filePath);

        using var loader = new GltfLoader(filePath);
        var gltf = loader.GltfJson;

        var submeshes = new List<RawSubmesh>();

        if (gltf.Meshes == null || gltf.Meshes.Length == 0)
            return new RawModelData(filePath, submeshes);

        for (int meshIdx = 0; meshIdx < gltf.Meshes.Length; meshIdx++)
        {
            var mesh = gltf.Meshes[meshIdx];
            string meshName = mesh.Name ?? $"Mesh_{meshIdx}";

            if (mesh.Primitives == null || mesh.Primitives.Length == 0) continue;

            for (int primIdx = 0; primIdx < mesh.Primitives.Length; primIdx++)
            {
                var prim = mesh.Primitives[primIdx];
                var attrs = prim.Attributes;

                // Extract POSITION (mandatory)
                if (attrs == null || !attrs.TryGetValue("POSITION", out int posAccessorIdx))
                    continue;

                var posSpan = loader.GetAccessorBufferSpan(posAccessorIdx);
                var positions = MemoryMarshal.Cast<byte, float3>(posSpan);
                int vertexCount = positions.Length;

                // Extract NORMAL (optional)
                ReadOnlySpan<float3> normals = default;
                if (attrs.TryGetValue("NORMAL", out int normAccessorIdx))
                {
                    var normSpan = loader.GetAccessorBufferSpan(normAccessorIdx);
                    normals = MemoryMarshal.Cast<byte, float3>(normSpan);
                }

                // Extract TEXCOORD_0 (optional)
                ReadOnlySpan<float2> uvs = default;
                if (attrs.TryGetValue("TEXCOORD_0", out int uvAccessorIdx))
                {
                    var uvSpan = loader.GetAccessorBufferSpan(uvAccessorIdx);
                    uvs = MemoryMarshal.Cast<byte, float2>(uvSpan);
                }

                // Extract COLOR_0 (optional)
                ReadOnlySpan<float3> colors = default;
                if (attrs.TryGetValue("COLOR_0", out int colorAccessorIdx))
                {
                    var colorSpan = loader.GetAccessorBufferSpan(colorAccessorIdx);
                    colors = MemoryMarshal.Cast<byte, float3>(colorSpan);
                }

                var vertices = new RawVertex[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    float3 pos = positions[v];
                    float3 norm = (v < normals.Length) ? normals[v] : new float3(0, 1, 0);
                    float2 uv = (v < uvs.Length) ? uvs[v] : new float2(0, 0);
                    float3 col = (v < colors.Length) ? colors[v] : new float3(1, 1, 1);

                    vertices[v] = new RawVertex(pos, norm, uv, col);
                }

                // Extract INDICES
                uint[] indices;
                if (prim.Indices.HasValue)
                {
                    int idxAccessor = prim.Indices.Value;
                    var idxSpan = loader.GetAccessorBufferSpan(idxAccessor);
                    var accessor = gltf.Accessors![idxAccessor];

                    indices = new uint[accessor.Count];
                    if (accessor.ComponentType == Accessor.ComponentTypeEnum.UnsignedShort)
                    {
                        var u16Span = MemoryMarshal.Cast<byte, ushort>(idxSpan);
                        for (int i = 0; i < u16Span.Length; i++)
                            indices[i] = u16Span[i];
                    }
                    else if (accessor.ComponentType == Accessor.ComponentTypeEnum.UnsignedInt)
                    {
                        var u32Span = MemoryMarshal.Cast<byte, uint>(idxSpan);
                        u32Span.CopyTo(indices);
                    }
                    else if (accessor.ComponentType == Accessor.ComponentTypeEnum.UnsignedByte)
                    {
                        for (int i = 0; i < idxSpan.Length; i++)
                            indices[i] = idxSpan[i];
                    }
                }
                else
                {
                    // Generate sequential indices if non-indexed
                    indices = new uint[vertexCount];
                    for (uint i = 0; i < vertexCount; i++)
                        indices[i] = i;
                }

                int matIndex = prim.Material ?? -1;
                string submeshName = $"{meshName}_Prim_{primIdx}";
                submeshes.Add(new RawSubmesh(submeshName, vertices, indices, matIndex));
            }
        }

        return new RawModelData(filePath, submeshes);
    }
}
