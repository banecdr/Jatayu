/* =================================================================================================
 * FILE: DepthConvention.cs
 * LOCATION: src/Renderer/Camera/DepthConvention.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines the canonical Reverse-Z depth convention and matrix construction for Jatayu.
 *   
 * WHY REVERSE-Z:
 *   In standard perspective projection, floating-point depth precision is non-linear and heavily
 *   clustered near the near clipping plane (e.g. 50% of all precision bits are wasted in the first
 *   1-2 meters). For open worlds extending 8 km to 50 km+, this causes catastrophic Z-fighting.
 *   
 *   Reverse-Z reverses the depth mapping so Near plane = 1.0f and Far plane = 0.0f.
 *   Combined with IEEE 754 floating-point format (which has exponentially more mantissa precision
 *   near 0.0), precision is distributed almost uniformly across the entire view frustum!
 *
 * CANONICAL RULES:
 *   - Depth buffer format: D32_SFLOAT
 *   - Clear value: 0.0f
 *   - Near plane depth: 1.0f
 *   - Far plane depth: 0.0f
 *   - Depth comparison: VK_COMPARE_OP_GREATER_OR_EQUAL
 *   - Hi-Z Occlusion: MIN reduction filter (VK_EXT_sampler_filter_minmax)
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.VulkanApi;

namespace Jatayu.Renderer.Camera;

/// <summary>
/// Canonical Reverse-Z depth constants and perspective projection builders.
/// </summary>
public static class DepthConvention
{
    /// <summary>
    /// Depth buffer clear value for Reverse-Z (0.0f represents infinite distance).
    /// </summary>
    public const float ClearDepth = 0.0f;

    /// <summary>
    /// Normalized device coordinate (NDC) depth value at the near clipping plane.
    /// </summary>
    public const float NearZ = 1.0f;

    /// <summary>
    /// Normalized device coordinate (NDC) depth value at the far clipping plane / infinity.
    /// </summary>
    public const float FarZ = 0.0f;

    /// <summary>
    /// Vulkan depth comparison operator for Reverse-Z: closer objects have GREATER depth values.
    /// </summary>
    public const VkCompareOp DepthCompareOp = VkCompareOp.GreaterOrEqual;

    /// <summary>
    /// Indicates whether Reverse-Z is globally active.
    /// </summary>
    public const bool IsReverseZ = true;

    /// <summary>
    /// Constructs a Vulkan-tailored Reverse-Z perspective projection matrix with an infinite far plane.
    /// Near plane maps to 1.0f, far plane at infinity maps to 0.0f.
    /// Automatically applies Vulkan NDC Y-axis flipping (proj.M22 = -proj.M22).
    /// </summary>
    /// <param name="fovRadians">Vertical field of view in radians.</param>
    /// <param name="aspect">Aspect ratio (width / height).</param>
    /// <param name="nearPlane">Near clipping plane distance (e.g. 0.1f).</param>
    /// <returns>Reverse-Z infinite perspective projection matrix.</returns>
    public static Matrix4x4 CreateReverseZInfiniteProjection(float fovRadians, float aspect, float nearPlane)
    {
        if (nearPlane <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Near plane must be greater than zero.");

        float tanHalfFov = MathF.Tan(fovRadians * 0.5f);

        // System.Numerics.Matrix4x4 uses row-vector convention (v * M):
        // x' = x * M11
        // y' = y * M22  (negative to flip Y for Vulkan NDC)
        // z' = nearPlane
        // w' = -z
        // -> z_ndc = z' / w' = nearPlane / (-z)
        // At z = -nearPlane: z_ndc = nearPlane / nearPlane = 1.0f
        // At z -> -infinity: z_ndc = nearPlane / infinity = 0.0f
        return new Matrix4x4
        {
            M11 = 1.0f / (aspect * tanHalfFov),
            M12 = 0.0f,
            M13 = 0.0f,
            M14 = 0.0f,

            M21 = 0.0f,
            M22 = -1.0f / tanHalfFov, // Vulkan NDC Y-axis flip (+Y is Up in 3D world)
            M23 = 0.0f,
            M24 = 0.0f,

            M31 = 0.0f,
            M32 = 0.0f,
            M33 = 0.0f,               // Infinite far plane (no z-dependent term in z')
            M34 = -1.0f,              // Perspective divide: w' = -z

            M41 = 0.0f,
            M42 = 0.0f,
            M43 = nearPlane,          // Constant offset produces nearPlane / (-z)
            M44 = 0.0f,
        };
    }

    /// <summary>
    /// Constructs a Vulkan-tailored Reverse-Z perspective projection matrix with a finite far plane.
    /// Near plane maps to 1.0f, far plane maps to 0.0f.
    /// Automatically applies Vulkan NDC Y-axis flipping.
    /// </summary>
    public static Matrix4x4 CreateReverseZFiniteProjection(float fovRadians, float aspect, float nearPlane, float farPlane)
    {
        if (nearPlane <= 0.0f || farPlane <= nearPlane)
            throw new ArgumentException("Invalid near/far plane combination.");

        float tanHalfFov = MathF.Tan(fovRadians * 0.5f);
        float fRange = farPlane - nearPlane;

        return new Matrix4x4
        {
            M11 = 1.0f / (aspect * tanHalfFov),
            M12 = 0.0f,
            M13 = 0.0f,
            M14 = 0.0f,

            M21 = 0.0f,
            M22 = -1.0f / tanHalfFov,
            M23 = 0.0f,
            M24 = 0.0f,

            M31 = 0.0f,
            M32 = 0.0f,
            M33 = nearPlane / fRange,
            M34 = -1.0f,

            M41 = 0.0f,
            M42 = 0.0f,
            M43 = (nearPlane * farPlane) / fRange,
            M44 = 0.0f,
        };
    }
}
