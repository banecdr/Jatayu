// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: CameraComponent.cs
// Role: Viewport camera component driving projection and view matrices under Reverse-Z conventions.
// ==================================================================================================

using System.Numerics;
using Jatayu.Renderer.Camera;

namespace Jatayu.Scene.Components;

/// <summary>
/// Camera component attached to an entity representing an active viewpoint or viewport.
/// Uses canonical Reverse-Z depth conventions (near plane at 1.0f, far plane at 0.0f).
/// </summary>
public struct CameraComponent
{
    public float FieldOfView;
    public float NearPlane;
    public float FarPlane;
    public float AspectRatio;
    public bool IsActive;
    public bool IsOrthographic;

    /// <summary>
    /// Constructs a camera component with Reverse-Z infinite far-plane projection defaults.
    /// </summary>
    public CameraComponent(float fovDegrees = 60.0f, float aspectRatio = 16.0f / 9.0f, float nearPlane = 0.1f)
    {
        FieldOfView = fovDegrees;
        AspectRatio = aspectRatio;
        NearPlane = nearPlane;
        FarPlane = 8000.0f;
        IsActive = true;
        IsOrthographic = false;
    }

    /// <summary>
    /// Computes the Reverse-Z projection matrix for this camera.
    /// </summary>
    public readonly Matrix4x4 ComputeProjection()
    {
        if (IsOrthographic)
        {
            return Matrix4x4.CreateOrthographic(1920.0f, 1080.0f, NearPlane, FarPlane);
        }

        // Canonical Reverse-Z Infinite Perspective Projection (Section 1.1)
        return DepthConvention.CreateReverseZInfiniteProjection(FieldOfView, AspectRatio, NearPlane);
    }
}
