// ==================================================================================================
// Jatayu Engine - 3D Lighting & Shadows
// Module: DirectionalLight.cs
// Role: Sunlight definition and orthographic shadow projection builder.
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Directional light representing celestial sunlight or moonlight.
/// </summary>
public sealed class DirectionalLight
{
    private Vector3 _direction = Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.3f));

    /// <summary>
    /// Gets or sets the light direction vector (normalized).
    /// Points in the direction photons travel.
    /// </summary>
    public Vector3 Direction
    {
        get => _direction;
        set => _direction = Vector3.Normalize(value);
    }

    /// <summary>
    /// Gets or sets the linear RGB color of the sunlight.
    /// </summary>
    public Vector3 Color { get; set; } = new(1.0f, 0.98f, 0.95f);

    /// <summary>
    /// Gets or sets the illuminance in Lux (default: 100,000 Lux for clear midday sun).
    /// </summary>
    public float Intensity { get; set; } = 100000.0f;

    /// <summary>
    /// Whether this directional light casts real-time shadows.
    /// </summary>
    public bool CastsShadows { get; set; } = true;

    /// <summary>
    /// Computes an orthographic light view-projection matrix focused on a center position.
    /// Uses Reverse-Z depth convention (near plane at 1.0, far plane at 0.0).
    /// </summary>
    public Matrix4x4 ComputeLightViewProjection(in Vector3 targetCenter, float orthoExtents = 100.0f, float nearPlane = 0.0f, float farPlane = 300.0f)
    {
        Vector3 lightPos = targetCenter - Direction * 150.0f;
        Vector3 up = MathF.Abs(Vector3.Dot(Direction, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;

        Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, targetCenter, up);

        // Vulkan Reverse-Z Orthographic projection:
        // In view space (right-handed), the camera looks down -Z.
        // Near distance maps to z_clip = 1.0f, Far distance maps to z_clip = 0.0f.
        // z_clip = z * M33 + M43
        // At z = -nearPlane: -nearPlane * M33 + M43 = 1.0f
        // At z = -farPlane:  -farPlane * M33 + M43 = 0.0f
        // -> M33 = 1.0f / (farPlane - nearPlane)
        // -> M43 = farPlane / (farPlane - nearPlane)
        float fRange = farPlane - nearPlane;

        Matrix4x4 proj = new(
            1.0f / orthoExtents, 0.0f, 0.0f, 0.0f,
            0.0f, -1.0f / orthoExtents, 0.0f, 0.0f, // Vulkan NDC Y-flip
            0.0f, 0.0f, 1.0f / fRange, 0.0f,
            0.0f, 0.0f, farPlane / fRange, 1.0f
        );

        return view * proj;
    }
}
