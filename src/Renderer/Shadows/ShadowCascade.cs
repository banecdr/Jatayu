/* =================================================================================================
 * FILE: ShadowCascade.cs
 * LOCATION: src/Renderer/Shadows/ShadowCascade.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a single cascade partition in a 4-split Cascaded Shadow Map (CSM).
 *   Calculates sub-frustum bounding spheres, light view-projection matrices, and applies
 *   texel snapping in light space to eliminate sub-pixel shadow edge shimmering during camera motion.
 * ================================================================================================= */

using System;
using System.Numerics;
using Jatayu.Renderer.Camera;

namespace Jatayu.Renderer.Shadows;

/// <summary>
/// A single cascade split in a 4-split Cascaded Shadow Map.
/// </summary>
public struct ShadowCascade
{
    public int CascadeIndex;
    public float NearDistance;
    public float SplitDistance;
    public Matrix4x4 ViewProjection;
    public Vector3 LightPosition;
    public float TexelSize;
    public float BoundingRadius;

    /// <summary>
    /// Computes the light view-projection matrix for this cascade partition with texel snapping.
    /// </summary>
    public void Update(
        Vector3 lightDirection,
        in Matrix4x4 cameraView,
        float fovRadians,
        float aspect,
        float nearDist,
        float farDist,
        int resolution)
    {
        CascadeIndex = Math.Max(0, CascadeIndex);
        NearDistance = nearDist;
        SplitDistance = farDist;

        if (!Matrix4x4.Invert(cameraView, out var invCamView))
        {
            invCamView = Matrix4x4.Identity;
        }

        // 1. Calculate the 8 sub-frustum corners in camera-relative space
        float tanHalfFov = MathF.Tan(fovRadians * 0.5f);
        float nearY = tanHalfFov * nearDist;
        float nearX = nearY * aspect;
        float farY = tanHalfFov * farDist;
        float farX = farY * aspect;

        Span<Vector3> viewCorners = stackalloc Vector3[8]
        {
            new(-nearX,  nearY, -nearDist),
            new( nearX,  nearY, -nearDist),
            new( nearX, -nearY, -nearDist),
            new(-nearX, -nearY, -nearDist),
            new(-farX,   farY,  -farDist),
            new( farX,   farY,  -farDist),
            new( farX,  -farY,  -farDist),
            new(-farX,  -farY,  -farDist),
        };

        Vector3 center = Vector3.Zero;
        Span<Vector3> worldCorners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            worldCorners[i] = Vector3.Transform(viewCorners[i], invCamView);
            center += worldCorners[i];
        }
        center /= 8.0f;

        // 2. Compute bounding sphere radius
        float radius = 0.0f;
        for (int i = 0; i < 8; i++)
        {
            float dist = Vector3.Distance(worldCorners[i], center);
            if (dist > radius) radius = dist;
        }

        // 3. Round radius to texel increment
        float worldUnitsPerTexel = (radius * 2.0f) / resolution;
        radius = MathF.Ceiling(radius / worldUnitsPerTexel) * worldUnitsPerTexel;
        BoundingRadius = radius;
        TexelSize = worldUnitsPerTexel;

        // 4. Position virtual light camera pulled back to capture shadow casters behind camera
        Vector3 lightDir = Vector3.Normalize(lightDirection);
        Vector3 lightPos = center - (lightDir * (radius * 2.0f));
        LightPosition = lightPos;

        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPos, center, up);

        // 5. Texel snapping in light view space
        Vector3 centerLight = Vector3.Transform(center, lightView);
        float snappedX = MathF.Floor(centerLight.X / worldUnitsPerTexel) * worldUnitsPerTexel;
        float snappedY = MathF.Floor(centerLight.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
        float diffX = snappedX - centerLight.X;
        float diffY = snappedY - centerLight.Y;

        lightView.M41 += diffX;
        lightView.M42 += diffY;

        // 6. Symmetrical Reverse-Z Orthographic projection with extended light depth range
        float orthoExtent = radius * 2.0f;
        float nearZ = 0.1f;
        float farZ = radius * 4.0f;
        Matrix4x4 lightProj = DepthConvention.CreateReverseZOrthographic(orthoExtent, orthoExtent, nearZ, farZ);

        ViewProjection = lightView * lightProj;
    }
}
