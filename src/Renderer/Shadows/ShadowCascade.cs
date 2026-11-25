/* =================================================================================================
 * FILE: ShadowCascade.cs
 * LOCATION: src/Renderer/Shadows/ShadowCascade.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a single cascade partition in a 4-split Cascaded Shadow Map (CSM).
 *   Calculates tight (on-axis) sub-frustum bounding spheres, light view-projection matrices, and applies
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
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Shadow map resolution must be positive.");
        }

        CascadeIndex = Math.Max(0, CascadeIndex);
        NearDistance = nearDist;
        SplitDistance = farDist;

        // Zero out camera position translation so cascade sub-frustums and bounding spheres
        // are strictly calculated in camera-relative coordinate space (0, 0, 0),
        // matching the large-world camera-relative model and world positions in shaders.
        Matrix4x4 camRelativeView = cameraView;
        camRelativeView.M41 = 0.0f;
        camRelativeView.M42 = 0.0f;
        camRelativeView.M43 = 0.0f;

        if (!Matrix4x4.Invert(camRelativeView, out var invCamView))
        {
            invCamView = Matrix4x4.Identity;
        }

        // 1. Tight bounding sphere of the sub-frustum (Dimitrov, "Cascaded Shadow Maps").
        //    The optimal sphere centre lies on the view axis; compared with the average of the eight
        //    corners this shrinks the fitted radius, which directly raises effective shadow resolution.
        //    The sphere depends only on (near, far, fov, aspect), so it is rotation invariant and the
        //    texel snapping below only has to handle translation.
        float tanHalfFov = MathF.Tan(fovRadians * 0.5f);
        float k2 = tanHalfFov * tanHalfFov * (1.0f + aspect * aspect);
        float centerDepth = 0.5f * (nearDist + farDist) * (1.0f + k2);
        if (centerDepth > farDist)
        {
            centerDepth = farDist;
        }

        float farHalfY = tanHalfFov * farDist;
        float farHalfX = farHalfY * aspect;
        float nearHalfY = tanHalfFov * nearDist;
        float nearHalfX = nearHalfY * aspect;
        float farGap = farDist - centerDepth;
        float nearGap = centerDepth - nearDist;
        float radius = MathF.Sqrt(MathF.Max(
            (farHalfX * farHalfX) + (farHalfY * farHalfY) + (farGap * farGap),
            (nearHalfX * nearHalfX) + (nearHalfY * nearHalfY) + (nearGap * nearGap)));

        // The view looks down -Z in camera-relative space; bring the sphere centre into that space.
        Vector3 center = Vector3.Transform(new Vector3(0.0f, 0.0f, -centerDepth), invCamView);

        // 2. Quantize the radius upward (1/16 m steps) so tiny FOV / aspect changes never resize the
        //    cascade every frame, then derive the exact texel footprint from the final radius.
        radius = MathF.Ceiling(radius * 16.0f) / 16.0f;
        float worldUnitsPerTexel = (radius * 2.0f) / resolution;
        BoundingRadius = radius;
        TexelSize = worldUnitsPerTexel;

        // 4. Position virtual light camera pulled back far enough to encompass all potential shadow casters
        Vector3 lightDir = Vector3.Normalize(lightDirection);
        float pullBack = MathF.Max(radius * 2.5f, 150.0f);
        Vector3 lightPos = center - (lightDir * pullBack);
        LightPosition = lightPos;

        Vector3 up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPos, center, up);

        // 5. Snap the fitted frustum center, not the light-space origin, to a shadow texel.
        Vector3 centerLight = Vector3.Transform(center, lightView);
        float snappedX = MathF.Round(centerLight.X / worldUnitsPerTexel) * worldUnitsPerTexel;
        float snappedY = MathF.Round(centerLight.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
        lightView.M41 += snappedX - centerLight.X;
        lightView.M42 += snappedY - centerLight.Y;

        // 6. Symmetrical Reverse-Z Orthographic projection with extended light depth range
        float orthoExtent = radius * 2.0f;
        float nearZ = 0.1f;
        float farZ = pullBack + radius + 150.0f;
        Matrix4x4 lightProj = DepthConvention.CreateReverseZOrthographic(orthoExtent, orthoExtent, nearZ, farZ);

        ViewProjection = lightView * lightProj;
    }
}
