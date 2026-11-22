// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: MeshRenderer.cs
// Role: Render component referencing geometry mesh and material assets for dynamic entity draws.
// ==================================================================================================

using Jatayu.AssetFormats;
using Jatayu.Renderer.Mesh;

namespace Jatayu.Scene.Components;

/// <summary>
/// Component attached to renderable entities.
/// Specifies the mesh geometry, material identifier, and rendering flags.
/// </summary>
public struct MeshRenderer
{
    /// <summary>
    /// Asset GUID of the cooked mesh.
    /// </summary>
    public AssetGuid MeshGuid;

    /// <summary>
    /// Asset GUID of the cooked PBR material.
    /// </summary>
    public AssetGuid MaterialGuid;

    /// <summary>
    /// Direct runtime reference to the GPU mesh container, if loaded.
    /// </summary>
    public Mesh? Mesh;

    /// <summary>
    /// Whether this renderer casts shadows into cascaded shadow maps.
    /// </summary>
    public bool CastShadows;

    /// <summary>
    /// Whether this renderer is drawn in the depth pre-pass.
    /// </summary>
    public bool DepthPrePass;

    /// <summary>
    /// Whether this entity is currently visible.
    /// </summary>
    public bool IsVisible;

    /// <summary>
    /// Constructs a mesh renderer component.
    /// </summary>
    public MeshRenderer(Mesh mesh, bool castShadows = true, bool depthPrePass = true)
    {
        MeshGuid = AssetGuid.Empty;
        MaterialGuid = AssetGuid.Empty;
        Mesh = mesh;
        CastShadows = castShadows;
        DepthPrePass = depthPrePass;
        IsVisible = true;
    }

    /// <summary>
    /// Constructs a mesh renderer component by asset GUIDs.
    /// </summary>
    public MeshRenderer(AssetGuid meshGuid, AssetGuid materialGuid, bool castShadows = true, bool depthPrePass = true)
    {
        MeshGuid = meshGuid;
        MaterialGuid = materialGuid;
        Mesh = null;
        CastShadows = castShadows;
        DepthPrePass = depthPrePass;
        IsVisible = true;
    }
}
