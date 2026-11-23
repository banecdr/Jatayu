/* =================================================================================================
 * FILE: SpotLight.cs
 * LOCATION: src/Renderer/Lighting/SpotLight.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents a directional conical spot light source for clustered forward shading.
 *   Packed to 48 bytes for direct GPU SSBO / UBO mapping.
 * ================================================================================================= */

using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Conical spot light source packed to 48 bytes for GPU storage buffers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct SpotLight
{
    /// <summary>
    /// Camera-relative 3D position in meters.
    /// </summary>
    public Vector3 Position;

    /// <summary>
    /// Maximum attenuation distance in meters.
    /// </summary>
    public float Range;

    /// <summary>
    /// Normalized direction vector of the cone axis.
    /// </summary>
    public Vector3 Direction;

    /// <summary>
    /// Cosine of the inner cone angle (full intensity).
    /// </summary>
    public float CosInnerAngle;

    /// <summary>
    /// Linear RGB color.
    /// </summary>
    public Vector3 Color;

    /// <summary>
    /// Cosine of the outer cone angle (attenuates to 0).
    /// </summary>
    public float CosOuterAngle;

    public SpotLight(Vector3 position, float range, Vector3 direction, float innerAngleRad, float outerAngleRad, Vector3 color)
    {
        Position = position;
        Range = range;
        Direction = Vector3.Normalize(direction);
        CosInnerAngle = MathF.Cos(innerAngleRad);
        CosOuterAngle = MathF.Cos(outerAngleRad);
        Color = color;
    }
}
