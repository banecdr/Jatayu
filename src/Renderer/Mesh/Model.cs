/* =================================================================================================
 * FILE: Model.cs
 * LOCATION: src/Renderer/Mesh/Model.cs
 * 
 * WHAT THIS FILE DOES:
 *   Multi-mesh 3D model container representing loaded glTF models or cooked asset hierarchies.
 *   Contains sub-meshes, material bindings, local transformation matrices, and node hierarchies.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.AssetFormats;

namespace Jatayu.Renderer.Mesh;

/// <summary>
/// Represents a submesh part of a 3D model, pairing a GPU mesh with local transform and material reference.
/// </summary>
public sealed class ModelSubmesh
{
    public string Name { get; set; } = string.Empty;
    public Mesh Mesh { get; set; }
    public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
    public AssetGuid MaterialGuid { get; set; }

    public ModelSubmesh(Mesh mesh, Matrix4x4 localTransform = default, AssetGuid materialGuid = default, string name = "")
    {
        Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        LocalTransform = localTransform == default ? Matrix4x4.Identity : localTransform;
        MaterialGuid = materialGuid;
        Name = name;
    }
}

/// <summary>
/// Node in a model scene hierarchy.
/// </summary>
public sealed class ModelNode
{
    public string Name { get; set; } = string.Empty;
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    public int SubmeshIndex { get; set; } = -1;
    public List<ModelNode> Children { get; } = [];
}

/// <summary>
/// Multi-mesh 3D model container holding sub-meshes, materials, and local node hierarchies.
/// </summary>
public sealed class Model : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the friendly name of this model.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the unique asset identifier for this model.
    /// </summary>
    public AssetGuid Guid { get; }

    /// <summary>
    /// Gets the sub-meshes comprising this model.
    /// </summary>
    public IReadOnlyList<ModelSubmesh> Submeshes { get; }

    /// <summary>
    /// Gets the root nodes of the model hierarchy.
    /// </summary>
    public IReadOnlyList<ModelNode> RootNodes { get; }

    /// <summary>
    /// Minimum point of the model's aggregate axis-aligned bounding box (AABB).
    /// </summary>
    public Vector3 BoundingMin { get; }

    /// <summary>
    /// Maximum point of the model's aggregate axis-aligned bounding box (AABB).
    /// </summary>
    public Vector3 BoundingMax { get; }

    /// <summary>
    /// Bounding sphere radius encompassing all submeshes.
    /// </summary>
    public float BoundingRadius { get; }

    public Model(
        string name,
        AssetGuid guid,
        IReadOnlyList<ModelSubmesh> submeshes,
        IReadOnlyList<ModelNode>? rootNodes = null)
    {
        Name = name ?? string.Empty;
        Guid = guid;
        Submeshes = submeshes ?? throw new ArgumentNullException(nameof(submeshes));
        RootNodes = rootNodes ?? [];

        // Compute aggregate bounding box and radius
        if (submeshes.Count > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int i = 0; i < submeshes.Count; i++)
            {
                var sub = submeshes[i];
                min = Vector3.Min(min, sub.Mesh.BoundingMin);
                max = Vector3.Max(max, sub.Mesh.BoundingMax);
            }
            BoundingMin = min;
            BoundingMax = max;
            BoundingRadius = Vector3.Distance(min, max) * 0.5f;
        }
    }

    /// <summary>
    /// Renders all submeshes in the model.
    /// </summary>
    public void Draw(CommandBuffer cmd, uint instanceCount = 1, uint firstInstance = 0)
    {
        for (int i = 0; i < Submeshes.Count; i++)
        {
            var sub = Submeshes[i];
            sub.Mesh.Bind(cmd);
            sub.Mesh.Draw(cmd, instanceCount, firstInstance);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        for (int i = 0; i < Submeshes.Count; i++)
        {
            Submeshes[i].Mesh.Dispose();
        }

        _disposed = true;
    }
}
