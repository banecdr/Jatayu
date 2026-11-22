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
    public Matrix4x4 ComputeLightViewProjection(in Vector3 targetCenter, float orthoExtents = 100.0f, float nearPlane = -150.0f, float farPlane = 150.0f)
    {
        Vector3 lightPos = targetCenter - Direction * 50.0f;
        Vector3 up = MathF.Abs(Vector3.Dot(Direction, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;

        Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, targetCenter, up);

        // Vulkan Reverse-Z Orthographic projection:
        // Maps near plane to 1.0f and far plane to 0.0f, with Y-inverted NDC
        float r = orthoExtents;
        float l = -orthoExtents;
        float t = -orthoExtents; // Inverted for Vulkan
        float b = orthoExtents;
        float n = nearPlane;
        float f = farPlane;

        Matrix4x4 proj = new(
            2.0f / (r - l), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (t - b), 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f / (n - f), 0.0f,
            -(r + l) / (r - l), -(t + b) / (t - b), -f / (n - f), 1.0f
        );

        return view * proj;
    }
}
