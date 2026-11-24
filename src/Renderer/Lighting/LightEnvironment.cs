// ==================================================================================================
// Jatayu Engine - 3D Lighting & Shadows
// Module: LightEnvironment.cs
// Role: Scene lighting environment holding celestial sun light and hemispherical ambient sky tones.
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Renderer.Lighting;

public enum ShadowDebugMode
{
    Off,
    Cascades,
    Depth,
    Bias,
}

/// <summary>
/// GPU-packed uniform buffer representation of the active lighting environment and 4-cascade shadow matrices.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 432)]
public struct GpuLightEnvironmentData
{
    public Vector3 SunDirection;       // 12 bytes: Normalized light direction
    public float SunIntensity;         // 4 bytes: Sun intensity in lux
    public Vector3 SunColor;           // 12 bytes: Linear RGB sun color
    public uint CastsShadows;          // 4 bytes: low bits select shadow mode; high bit enables environment IBL
    public Vector3 AmbientSkyColor;    // 12 bytes: Hemispherical zenith color
    public float AmbientIntensity;     // 4 bytes: Ambient intensity factor
    public Vector3 AmbientGroundColor; // 12 bytes: Hemispherical nadir ground reflection
    public float ShadowBias;           // 4 bytes: Constant depth bias
    public Matrix4x4 LightViewProj;    // 64 bytes: Default / Cascade 0 light view-projection matrix
    public Vector4 CascadeSplits;      // 16 bytes: View-space depth splits for cascades 0..3 (X, Y, Z, W)
    public Matrix4x4 CascadeViewProj0; // 64 bytes: Cascade 0 light VP
    public Matrix4x4 CascadeViewProj1; // 64 bytes: Cascade 1 light VP
    public Matrix4x4 CascadeViewProj2; // 64 bytes: Cascade 2 light VP
    public Matrix4x4 CascadeViewProj3; // 64 bytes: Cascade 3 light VP
    public Vector4 ShadowParams;       // 16 bytes: X=resolution, Y=invResolution, Z=pcfRadius, W=debugMode
    public Vector4 ClusterParams;      // 16 bytes: X=Width, Y=Height, Z=NearZ, W=ClusteredLightingEnabled
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
    public float ShadowBias { get; set; } = 0.00012f;
    public float ShadowOrthoExtents { get; set; } = 80.0f;
    public bool HighQualityShadows { get; set; } = true;
    public float ShadowFilterRadius { get; set; } = 1.4f;
    public ShadowDebugMode ShadowDebugMode { get; set; }

    /// <summary>
    /// Packs current environment parameters into a GPU-ready struct using either a single shadow map or CascadedShadowMap.
    /// </summary>
    public GpuLightEnvironmentData ToGpuData(
        in Vector3 cameraPosition,
        Shadows.CascadedShadowMap? csm = null,
        float screenWidth = 1280f,
        float screenHeight = 720f,
        float nearZ = 0.1f,
        bool clusteredLightingEnabled = false)
    {
        float extents = HighQualityShadows ? 28.0f : ShadowOrthoExtents;
        Matrix4x4 lightVp = Sun.CastsShadows ? Sun.ComputeLightViewProjection(cameraPosition, extents) : Matrix4x4.Identity;

        uint shadowQuality = 0u;
        if (Sun.CastsShadows)
        {
            shadowQuality = HighQualityShadows ? 2u : 1u;
        }

        Vector4 clusterParams = clusteredLightingEnabled ? new Vector4(screenWidth, screenHeight, nearZ, 1.0f) : Vector4.Zero;

        if (csm != null && Sun.CastsShadows)
        {
            var cascades = csm.Cascades;
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
                LightViewProj = cascades[0].ViewProjection,
                CascadeSplits = csm.CascadeSplits,
                CascadeViewProj0 = cascades[0].ViewProjection,
                CascadeViewProj1 = cascades[1].ViewProjection,
                CascadeViewProj2 = cascades[2].ViewProjection,
                CascadeViewProj3 = cascades[3].ViewProjection,
                ShadowParams = new Vector4(
                    csm.Resolution,
                    1.0f / csm.Resolution,
                    HighQualityShadows ? ShadowFilterRadius : 1.0f,
                    (float)ShadowDebugMode),
                ClusterParams = clusterParams,
            };
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
            CascadeSplits = new Vector4(1000f, 1000f, 1000f, 1000f),
            CascadeViewProj0 = lightVp,
            CascadeViewProj1 = lightVp,
            CascadeViewProj2 = lightVp,
            CascadeViewProj3 = lightVp,
            ShadowParams = new Vector4(
                2048f,
                1.0f / 2048f,
                HighQualityShadows ? ShadowFilterRadius : 1.0f,
                (float)ShadowDebugMode),
            ClusterParams = clusterParams,
        };
    }
}
