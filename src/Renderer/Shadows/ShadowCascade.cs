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
        int resolution,
        double camWorldX = 0.0,
        double camWorldY = 0.0,
        double camWorldZ = 0.0)
    {
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Shadow map resolution must be positive.");
        }

        CascadeIndex = Math.Max(0, CascadeIndex);
        NearDistance = nearDist;
        SplitDistance = farDist;

        // 1. Analytical bounding sphere of the frustum slice [near, far] centered at slice midpoint.
        //    k2 = squared distance of a plane's corner from the axis, per unit of depth.
        float tanHalfFov = MathF.Tan(fovRadians * 0.5f);
        float k2 = tanHalfFov * tanHalfFov * (1.0f + aspect * aspect);

        float centerDist = 0.5f * (farDist + nearDist);
        float dz = 0.5f * (farDist - nearDist);
        float radius = MathF.Sqrt(dz * dz + farDist * farDist * k2);

        // Camera forward in camera-relative world space. For a rotation-only view
        // matrix (row-vector convention), view-space -Z maps to -(M13, M23, M33).
        Vector3 camForward = new Vector3(-cameraView.M13, -cameraView.M23, -cameraView.M33);
        Vector3 center = camForward * centerDist;

        // 2. Texel size from the (rotation-invariant) radius.
        float worldUnitsPerTexel = (radius * 2.0f) / resolution;
        BoundingRadius = radius;
        TexelSize = worldUnitsPerTexel;

        // 3. Light basis (identical to what CreateLookAt would produce).
        Vector3 lightDir = Vector3.Normalize(lightDirection);
        Vector3 up = MathF.Abs(lightDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 zAxis = -lightDir;
        Vector3 xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

        float pullBack = MathF.Max(radius * 2.5f, 150.0f);

        // 4. Snap the window center to the shadow-map texel grid in light space.
        //    The grid must be anchored in WORLD space, but `center` is camera-relative,
        //    so offset by the camera's world position projected onto the light axes.
        double t = worldUnitsPerTexel;
        float centerLx = Vector3.Dot(center, xAxis);
        float centerLy = Vector3.Dot(center, yAxis);
        float centerLz = Vector3.Dot(center, zAxis);

        bool worldAnchored = camWorldX != 0.0 || camWorldY != 0.0 || camWorldZ != 0.0;
        double snappedX;
        double snappedY;

        if (worldAnchored)
        {
            double originX = camWorldX * xAxis.X + camWorldY * xAxis.Y + camWorldZ * xAxis.Z;
            double originY = camWorldX * yAxis.X + camWorldY * yAxis.Y + camWorldZ * yAxis.Z;
            snappedX = Math.Round((centerLx + originX) / t) * t - originX;
            snappedY = Math.Round((centerLy + originY) / t) * t - originY;
        }
        else
        {
            snappedX = centerLx;
            snappedY = centerLy;
        }

        // Window center after snapping, and the light camera position.
        Vector3 snappedCenter = center
            + xAxis * (float)(snappedX - centerLx)
            + yAxis * (float)(snappedY - centerLy);
        LightPosition = snappedCenter - (lightDir * pullBack);

        // 5. Light view matrix built directly from the basis (row-vector convention).
        Matrix4x4 lightView = new Matrix4x4(
            xAxis.X, yAxis.X, zAxis.X, 0.0f,
            xAxis.Y, yAxis.Y, zAxis.Y, 0.0f,
            xAxis.Z, yAxis.Z, zAxis.Z, 0.0f,
            -(float)snappedX, -(float)snappedY, -(centerLz + pullBack), 1.0f);

        // 6. Symmetrical Reverse-Z orthographic projection
        float orthoExtent = radius * 2.0f;
        float nearZ = 0.1f;
        float farZ = pullBack + radius + 150.0f;
        Matrix4x4 lightProj = DepthConvention.CreateReverseZOrthographic(orthoExtent, orthoExtent, nearZ, farZ);

        ViewProjection = lightView * lightProj;
    }
}
