/* =================================================================================================
 * FILE: DepthConventionTests.cs
 * LOCATION: tests/Jatayu.Tests/DepthConventionTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating canonical Reverse-Z depth constants and perspective projection matrices:
 *   - Near plane maps to Z_ndc = 1.0f
 *   - Far plane maps to Z_ndc = 0.0f
 *   - Infinite far plane projection asymptotic behavior
 *   - Vulkan Y-flipped NDC coordinates (M22 < 0)
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.VulkanApi;
using Jatayu.Renderer.Camera;
using Xunit;

namespace Jatayu.Tests;

public class DepthConventionTests
{
    [Fact]
    public void CanonicalConstants_MatchReverseZSpecification()
    {
        Assert.Equal(0.0f, DepthConvention.ClearDepth);
        Assert.Equal(1.0f, DepthConvention.NearZ);
        Assert.Equal(0.0f, DepthConvention.FarZ);
        Assert.Equal(VkCompareOp.GreaterOrEqual, DepthConvention.DepthCompareOp);
        Assert.True(DepthConvention.IsReverseZ);
    }

    [Fact]
    public void ReverseZInfiniteProjection_MapsNearPlaneToOneAndFarToZero()
    {
        const float fov = MathF.PI / 3f; // 60 degrees
        const float aspect = 16f / 9f;
        const float near = 0.1f;

        var proj = DepthConvention.CreateReverseZInfiniteProjection(fov, aspect, near);

        // Vulkan NDC requirement: Y-axis is inverted (+Y down in NDC, +Y up in world)
        Assert.True(proj.M22 < 0.0f, "M22 must be negative for Vulkan NDC Y-flip");

        // Transform Near Plane point: (0, 0, -near, 1) in View space (camera looks along -Z)
        var nearPoint = new Vector4(0f, 0f, -near, 1f);
        var nearClip = Vector4.Transform(nearPoint, proj);
        float nearNdcZ = nearClip.Z / nearClip.W;
        Assert.Equal(1.0f, nearNdcZ, tolerance: 0.0001f);

        // Transform Very Far point: (0, 0, -100000, 1)
        var farPoint = new Vector4(0f, 0f, -100_000f, 1f);
        var farClip = Vector4.Transform(farPoint, proj);
        float farNdcZ = farClip.Z / farClip.W;
        Assert.True(farNdcZ >= 0.0f && farNdcZ < 0.001f, "Far NDC depth must approach 0.0f");
    }

    [Fact]
    public void ReverseZFiniteProjection_MapsNearToOneAndFarToZero()
    {
        const float fov = MathF.PI / 3f;
        const float aspect = 16f / 9f;
        const float near = 0.1f;
        const float far = 1000.0f;

        var proj = DepthConvention.CreateReverseZFiniteProjection(fov, aspect, near, far);

        // Near plane point: (0, 0, -near, 1)
        var nearClip = Vector4.Transform(new Vector4(0f, 0f, -near, 1f), proj);
        float nearNdcZ = nearClip.Z / nearClip.W;
        Assert.Equal(1.0f, nearNdcZ, tolerance: 0.0001f);

        // Far plane point: (0, 0, -far, 1)
        var farClip = Vector4.Transform(new Vector4(0f, 0f, -far, 1f), proj);
        float farNdcZ = farClip.Z / farClip.W;
        Assert.Equal(0.0f, farNdcZ, tolerance: 0.0001f);
    }

    [Fact]
    public void DirectionalLight_ReverseZMapping_CloserObjectsHaveGreaterDepth()
    {
        var sun = new Jatayu.Renderer.Lighting.DirectionalLight
        {
            Direction = Vector3.Normalize(new Vector3(-0.6f, -1.0f, -0.4f))
        };
        var lightVp = sun.ComputeLightViewProjection(Vector3.Zero);

        Vector3 camPos = new Vector3(0f, 2.0f, 4.5f);
        Vector3 cubePos = new Vector3(0f, 0.5f, 0f);
        Vector3 groundPos = new Vector3(0f, -2.0f, 0f);

        Vector3 cubeRelPos = cubePos - camPos;
        Vector3 groundRelPos = groundPos - camPos;

        var cubeClip = Vector4.Transform(new Vector4(cubeRelPos, 1.0f), lightVp);
        var groundClip = Vector4.Transform(new Vector4(groundRelPos, 1.0f), lightVp);

        float cubeZ = cubeClip.Z / cubeClip.W;
        float groundZ = groundClip.Z / groundClip.W;

        // In Reverse-Z, closer object (cube) must have GREATER depth than further object (ground)
        Assert.True(cubeZ > groundZ, $"Cube ({cubeZ:F4}) must have greater depth than ground ({groundZ:F4}) in Reverse-Z");
        Assert.True(cubeZ > 0.0f && cubeZ < 1.0f, "Cube depth must fall inside [0, 1] shadow frustum");
        Assert.True(groundZ > 0.0f && groundZ < 1.0f, "Ground depth must fall inside [0, 1] shadow frustum");
    }
}
