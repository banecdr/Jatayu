/* =================================================================================================
 * FILE: MeshCooker.cs
 * LOCATION: tools/Jatayu.Cooker/MeshCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Processes 3D meshes using Ahwoo.BRUTAL.MeshOptimizer:
 *   - Optimizes vertex cache (FIFO hardware transform cache reuse).
 *   - Optimizes overdraw (minimizes pixel shader overdraw costs).
 *   - Optimizes vertex fetch (re-indexes vertices in memory to match cache access order).
 *   - Computes multiresolution LOD hierarchies using edge collapse simplification.
 *   - Calculates tight bounding spheres and axis-aligned bounding boxes (AABB).
 *   - Serializes into high-speed .jmesh binary blobs.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.MeshOptimizer;
using Brutal.Pointers;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

/// <summary>
/// Vertex format representing cooked geometry vertices.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CookedVertex
{
    public float PosX, PosY, PosZ;
    public float ColorR, ColorG, ColorB;
    public float NormalX, NormalY, NormalZ;
    public float TexCoordU, TexCoordV;

    public CookedVertex(float x, float y, float z, float u = 0, float v = 0, float nx = 0, float ny = 1, float nz = 0)
    {
        PosX = x; PosY = y; PosZ = z;
        ColorR = 1; ColorG = 1; ColorB = 1;
        NormalX = nx; NormalY = ny; NormalZ = nz;
        TexCoordU = u; TexCoordV = v;
    }
}

/// <summary>
/// Optimizes and bakes 3D meshes into cooked .jmesh binaries.
/// </summary>
public static class MeshCooker
{
    /// <summary>
    /// Cooks raw vertices and indices into an optimized .jmesh binary blob with LOD hierarchy.
    /// </summary>
    public static byte[] CookRawSubmesh(Jatayu.AssetFormats.Loaders.RawSubmesh submesh, AssetGuid guid, int lodLevels = 3)
    {
        var cookedVertices = new CookedVertex[submesh.Vertices.Length];
        for (int i = 0; i < submesh.Vertices.Length; i++)
        {
            ref readonly var v = ref submesh.Vertices[i];
            cookedVertices[i] = new CookedVertex
            {
                PosX = v.Position.X,
                PosY = v.Position.Y,
                PosZ = v.Position.Z,
                ColorR = v.Color.X,
                ColorG = v.Color.Y,
                ColorB = v.Color.Z,
                NormalX = v.Normal.X,
                NormalY = v.Normal.Y,
                NormalZ = v.Normal.Z,
                TexCoordU = v.TexCoord.X,
                TexCoordV = v.TexCoord.Y,
            };
        }
        return CookMesh(guid, cookedVertices, submesh.Indices, lodLevels);
    }

    /// <summary>
    /// Cooks raw vertices and indices into an optimized .jmesh binary blob with LOD hierarchy.
    /// </summary>
    public static unsafe byte[] CookMesh(
        AssetGuid guid,
        ReadOnlySpan<CookedVertex> vertices,
        ReadOnlySpan<uint> indices,
        int lodLevels = 3)
    {
        if (vertices.IsEmpty || indices.IsEmpty)
            throw new ArgumentException("Vertices and indices cannot be empty.");

        int vertexCount = vertices.Length;
        int indexCount = indices.Length;

        // 1. Copy raw indices into int[] for MeshOptimizer
        var optIndices = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
            optIndices[i] = (int)indices[i];

        var optVertices = new CookedVertex[vertexCount];
        vertices.CopyTo(optVertices);

        // 2. Vertex Cache Optimization
        var remapIndices = new int[indexCount];
        fixed (int* pRemap = remapIndices)
        fixed (int* pOptIdx = optIndices)
        {
            MeshOptimizer.OptimizeVertexCache(
                new Ptr<int>(pRemap),
                new Ptr<int>(pOptIdx),
                indexCount,
                vertexCount
            );
        }

        // 3. Overdraw Optimization
        var overdrawIndices = new int[indexCount];
        fixed (int* pOverdraw = overdrawIndices)
        fixed (int* pRemap = remapIndices)
        fixed (CookedVertex* pVerts = optVertices)
        {
            MeshOptimizer.OptimizeOverdraw(
                new Ptr<int>(pOverdraw),
                new Ptr<int>(pRemap),
                indexCount,
                new Ptr<float>((float*)pVerts),
                vertexCount,
                (ByteSize64)sizeof(CookedVertex),
                1.05f
            );
        }

        // 4. Vertex Fetch Optimization
        var finalVertices = new CookedVertex[vertexCount];
        fixed (CookedVertex* pFinalVerts = finalVertices)
        fixed (int* pOverdraw = overdrawIndices)
        fixed (CookedVertex* pVerts = optVertices)
        {
            MeshOptimizer.OptimizeVertexFetch(
                new Ptr<CookedVertex>(pFinalVerts),
                new Ptr<int>(pOverdraw),
                indexCount,
                new Ptr<CookedVertex>(pVerts),
                vertexCount,
                (ByteSize64)sizeof(CookedVertex)
            );
        }

        // 5. Generate LOD Hierarchy
        lodLevels = Math.Clamp(lodLevels, 1, 4);
        var lodList = new List<(int[] Indices, float Error)>(lodLevels)
        {
            (overdrawIndices, 0.0f) // LOD 0 (100% detail)
        };

        for (int lod = 1; lod < lodLevels; lod++)
        {
            float reductionFactor = lod switch
            {
                1 => 0.50f, // 50% triangles
                2 => 0.25f, // 25% triangles
                _ => 0.12f, // 12% triangles
            };

            int targetIndexCount = Math.Max(12, (int)(indexCount * reductionFactor));
            var lodIndices = new int[indexCount];
            float resultError = 0;

            fixed (int* pDest = lodIndices)
            fixed (int* pSrc = overdrawIndices)
            fixed (CookedVertex* pVerts = finalVertices)
            {
                var simplifiedCount = (long)MeshOptimizer.Simplify(
                    new Ptr<int>(pDest),
                    new Ptr<int>(pSrc),
                    indexCount,
                    new Ptr<float>((float*)pVerts),
                    vertexCount,
                    (ByteSize64)sizeof(CookedVertex),
                    targetIndexCount,
                    0.05f * lod,
                    0,
                    &resultError
                );

                if (simplifiedCount > 0 && simplifiedCount <= indexCount)
                {
                    var trimmed = new int[simplifiedCount];
                    Array.Copy(lodIndices, trimmed, simplifiedCount);
                    lodList.Add((trimmed, resultError));
                }
            }
        }

        // 6. Compute Bounds (AABB + Bounding Sphere)
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (int i = 0; i < vertexCount; i++)
        {
            var v = finalVertices[i];
            if (v.PosX < minX) minX = v.PosX;
            if (v.PosY < minY) minY = v.PosY;
            if (v.PosZ < minZ) minZ = v.PosZ;

            if (v.PosX > maxX) maxX = v.PosX;
            if (v.PosY > maxY) maxY = v.PosY;
            if (v.PosZ > maxZ) maxZ = v.PosZ;
        }

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;

        float maxRadiusSq = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            var v = finalVertices[i];
            float dx = v.PosX - centerX;
            float dy = v.PosY - centerY;
            float dz = v.PosZ - centerZ;
            float rSq = dx * dx + dy * dy + dz * dz;
            if (rSq > maxRadiusSq) maxRadiusSq = rSq;
        }
        float boundingRadius = MathF.Sqrt(maxRadiusSq);

        // 7. Calculate total buffer sizes and offsets
        uint totalIndices = 0;
        foreach (var lod in lodList)
            totalIndices += (uint)lod.Indices.Length;

        var header = new CookedMeshHeader
        {
            Magic = CookedMeshHeader.MagicValue,
            Version = CookedMeshHeader.CurrentVersion,
            Guid = guid,
            LodCount = (uint)lodList.Count,
            TotalVertexCount = (uint)vertexCount,
            TotalIndexCount = totalIndices,
            BoundingRadius = boundingRadius,
            BoundingMinX = minX,
            BoundingMinY = minY,
            BoundingMinZ = minZ,
            BoundingMaxX = maxX,
            BoundingMaxY = maxY,
            BoundingMaxZ = maxZ,
        };

        // 8. Serialize into memory stream
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write Header
        int headerSize = Marshal.SizeOf<CookedMeshHeader>();
        Span<byte> headerBytes = stackalloc byte[headerSize];
        MemoryMarshal.Write(headerBytes, in header);
        writer.Write(headerBytes);

        // Write LOD records
        int lodRecordSize = Marshal.SizeOf<CookedMeshLod>();
        uint currentIndexOffset = 0;
        Span<byte> lodBytes = stackalloc byte[lodRecordSize];
        for (int i = 0; i < lodList.Count; i++)
        {
            var lodData = lodList[i];
            var lodRecord = new CookedMeshLod
            {
                LodIndex = (uint)i,
                VertexOffset = 0,
                VertexCount = (uint)vertexCount,
                IndexOffset = currentIndexOffset,
                IndexCount = (uint)lodData.Indices.Length,
                GeometricError = lodData.Error,
            };

            MemoryMarshal.Write(lodBytes, in lodRecord);
            writer.Write(lodBytes);

            currentIndexOffset += (uint)lodData.Indices.Length;
        }

        // Write Vertex Data
        var vertexBytes = MemoryMarshal.AsBytes<CookedVertex>(finalVertices);
        writer.Write(vertexBytes);

        // Write Index Data
        foreach (var lod in lodList)
        {
            var indexBytes = MemoryMarshal.AsBytes<int>(lod.Indices);
            writer.Write(indexBytes);
        }

        return ms.ToArray();
    }
}
