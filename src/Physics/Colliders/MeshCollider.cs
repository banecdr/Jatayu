/* =================================================================================================
 * FILE: MeshCollider.cs
 * LOCATION: src/Physics/Colliders/MeshCollider.cs
 * 
 * WHAT THIS FILE DOES:
 *   Arbitrary triangle mesh collision geometry optimized for static environment meshes,
 *   world cell static instances, and cooked terrain structures.
 * ================================================================================================= */

using System;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace Jatayu.Physics.Colliders;

/// <summary>
/// Static triangle mesh collider backed by Jolt MeshShape.
/// Designed for static geometry (Ring A static cell colliders, terrain, buildings).
/// </summary>
public sealed class MeshCollider : Collider
{
    /// <summary>
    /// Total number of triangles in the collision mesh.
    /// </summary>
    public int TriangleCount { get; }

    /// <summary>
    /// Total number of vertices in the collision mesh.
    /// </summary>
    public int VertexCount { get; }

    /// <summary>
    /// Constructs a static mesh collider from vertex coordinates and triangle index triplets.
    /// </summary>
    public MeshCollider(ReadOnlySpan<float3> vertices, ReadOnlySpan<uint> indices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Index count must be a multiple of 3", nameof(indices));

        VertexCount = vertices.Length;
        TriangleCount = indices.Length / 3;

        var triangles = new IndexedTriangle[TriangleCount];
        unsafe
        {
            fixed (IndexedTriangle* pTriangles = triangles)
            {
                for (int i = 0; i < TriangleCount; i++)
                {
                    uint i0 = indices[i * 3 + 0];
                    uint i1 = indices[i * 3 + 1];
                    uint i2 = indices[i * 3 + 2];

                    pTriangles[i].MaterialIndex = 0;
                    pTriangles[i].UserData = 0;
                    uint* pIdx = (uint*)&pTriangles[i].Idx;
                    pIdx[0] = i0;
                    pIdx[1] = i1;
                    pIdx[2] = i2;
                }
            }
        }

        var settings = MeshShapeSettings.Create(vertices, triangles, ReadOnlySpan<PhysicsMaterial>.Empty);
        SetShape(settings.CreateShape());
        settings.Dispose();
    }

    /// <summary>
    /// Constructs a static mesh collider from pre-indexed triangles and vertices.
    /// </summary>
    public MeshCollider(ReadOnlySpan<float3> vertices, ReadOnlySpan<IndexedTriangle> triangles)
    {
        VertexCount = vertices.Length;
        TriangleCount = triangles.Length;

        var settings = MeshShapeSettings.Create(vertices, triangles, ReadOnlySpan<PhysicsMaterial>.Empty);
        SetShape(settings.CreateShape());
        settings.Dispose();
    }
}
