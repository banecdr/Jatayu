// ==================================================================================================
// Jatayu Engine - 3D Atmosphere & Environment
// Module: Skybox.cs
// Role: Procedural atmospheric sky background rendered at Reverse-Z infinity (Z = 0.0f).
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Pipelines;

namespace Jatayu.Renderer.Skybox;

/// <summary>
/// Packed uniform parameters for procedural sky rendering.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct GpuSkyboxData
{
    public Matrix4x4 InverseViewProjection; // 64 bytes: Inverts clip space NDC to world ray directions
}

/// <summary>
/// Procedural atmospheric sky rendered at Reverse-Z infinity ($Z = 0.0$).
/// </summary>
public sealed class Skybox
{
    public Vector3 ZenithColor { get; set; } = new(0.15f, 0.35f, 0.75f);
    public Vector3 HorizonColor { get; set; } = new(0.7f, 0.8f, 0.95f);
    public Vector3 GroundColor { get; set; } = new(0.2f, 0.18f, 0.15f);

    /// <summary>
    /// Computes the inverse view-projection matrix used to reconstruct ray directions from fullscreen NDC.
    /// Translation is stripped from the view matrix so the sky remains infinitely distant.
    /// </summary>
    public Matrix4x4 ComputeInverseViewProjection(in Matrix4x4 view, in Matrix4x4 projection)
    {
        // Strip translation to keep sky at infinity
        Matrix4x4 rotationOnlyView = view;
        rotationOnlyView.M41 = 0;
        rotationOnlyView.M42 = 0;
        rotationOnlyView.M43 = 0;

        Matrix4x4 viewProj = rotationOnlyView * projection;
        Matrix4x4.Invert(viewProj, out var inv);
        return inv;
    }
}
