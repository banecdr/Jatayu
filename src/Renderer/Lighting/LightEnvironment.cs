// ==================================================================================================
// Jatayu Engine - 3D Lighting & Shadows
// Module: LightEnvironment.cs
// Role: Scene lighting environment holding celestial sun light and hemispherical ambient sky tones.
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// GPU-packed uniform buffer representation of the active lighting environment.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 128)]
public struct GpuLightEnvironmentData
{
    public Vector3 SunDirection;       // 12 bytes: Normalized light direction
    public float SunIntensity;         // 4 bytes: Sun intensity in lux
    public Vector3 SunColor;           // 12 bytes: Linear RGB sun color
    public uint CastsShadows;          // 4 bytes: 1 if shadow pass active, 0 otherwise
    public Vector3 AmbientSkyColor;    // 12 bytes: Hemispherical zenith color
    public float AmbientIntensity;     // 4 bytes: Ambient intensity factor
    public Vector3 AmbientGroundColor; // 12 bytes: Hemispherical nadir ground reflection
    public float ShadowBias;           // 4 bytes: Constant depth bias
    public Matrix4x4 LightViewProj;    // 64 bytes: Light view-projection matrix for shadow sampling
}

/// <summary>
/// Scene-wide lighting environment container.
/// </summary>
public sealed class LightEnvironment
{
    public DirectionalLight Sun { get; set; } = new();

    public Vector3 AmbientSkyColor { get; set; } = new(0.2f, 0.35f, 0.6f);
    public Vector3 AmbientGroundColor { get; set; } = new(0.1f, 0.08f, 0.05f);
    public float AmbientIntensity { get; set; } = 0.4f;
    public float ShadowBias { get; set; } = 0.0005f;
    public float ShadowOrthoExtents { get; set; } = 80.0f;
    public bool HighQualityShadows { get; set; } = false;

    /// <summary>
    /// Packs current environment parameters into a GPU-ready struct.
    /// </summary>
    public GpuLightEnvironmentData ToGpuData(in Vector3 cameraPosition)
    {
        float extents = HighQualityShadows ? 28.0f : ShadowOrthoExtents;
        Matrix4x4 lightVp = Sun.CastsShadows ? Sun.ComputeLightViewProjection(cameraPosition, extents) : Matrix4x4.Identity;

        uint shadowQuality = 0u;
        if (Sun.CastsShadows)
        {
            shadowQuality = HighQualityShadows ? 2u : 1u;
        }

        return new GpuLightEnvironmentData
        {
            SunDirection = Sun.Direction,
            SunIntensity = Sun.Intensity,
            SunColor = Sun.Color,
            CastsShadows = shadowQuality,
            AmbientSkyColor = AmbientSkyColor,
            AmbientIntensity = AmbientIntensity,
            AmbientGroundColor = AmbientGroundColor,
            ShadowBias = ShadowBias,
            LightViewProj = lightVp,
        };
    }
}
