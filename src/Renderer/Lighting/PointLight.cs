/* =================================================================================================
 * FILE: PointLight.cs
 * LOCATION: src/Renderer/Lighting/PointLight.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents an omnidirectional point light source for clustered forward shading.
 *   Packed to 32 bytes for direct GPU SSBO / UBO mapping.
 * ================================================================================================= */

using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Omnidirectional point light source packed to 32 bytes for GPU storage buffers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct PointLight
{
    /// <summary>
    /// Camera-relative 3D position in meters.
    /// </summary>
    public Vector3 Position;

    /// <summary>
    /// Maximum attenuation cutoff radius in meters.
    /// </summary>
    public float Radius;

    /// <summary>
    /// Linear RGB color.
    /// </summary>
    public Vector3 Color;

    /// <summary>
    /// Light intensity / luminous power.
    /// </summary>
    public float Intensity;

    public PointLight(Vector3 position, float radius, Vector3 color, float intensity)
    {
        Position = position;
        Radius = radius;
        Color = color;
        Intensity = intensity;
    }
}
