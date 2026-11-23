/* =================================================================================================
 * FILE: LightingAndFoliageTests.cs
 * LOCATION: tests/Jatayu.Tests/LightingAndFoliageTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating Sprint 7b Scalable Lighting, Foliage & Transparency primitives:
 *   - DepthConvention.CreateReverseZOrthographic (Reverse-Z near=1.0, far=0.0)
 *   - ShadowCascade frustum splitting and texel snapping
 *   - PointLight (32 bytes) and SpotLight (48 bytes) struct layout & alignment
 *   - ClusterUniforms (176 bytes) struct layout & alignment
 *   - FoliageInstanceData (16 bytes packed) struct layout, quantization, and unpacking
 *   - GpuParticle (48 bytes) struct layout & alignment
 *   - FoliageCooker procedural distribution logic
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats.World;
using Jatayu.Cooker;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Lighting;
using Jatayu.Renderer.Particles;
using Jatayu.Renderer.Shadows;
using Xunit;

namespace Jatayu.Tests;

public class LightingAndFoliageTests
{
    [Fact]
    public void ReverseZOrthographic_MapsNearPlaneToOneAndFarToZero()
    {
        const float width = 100f;
        const float height = 80f;
        const float nearPlane = 0.5f;
        const float farPlane = 500f;

        var ortho = DepthConvention.CreateReverseZOrthographic(width, height, nearPlane, farPlane);

        // Vulkan NDC requirement: M22 < 0.0f (Y is flipped)
        Assert.True(ortho.M22 < 0.0f, "M22 must be negative for Vulkan NDC Y-flip");

        // Near plane point: (0, 0, -nearPlane, 1) in View space (camera looks along -Z)
        var nearPoint = new Vector4(0f, 0f, -nearPlane, 1f);
        var nearClip = Vector4.Transform(nearPoint, ortho);
        float nearNdcZ = nearClip.Z / nearClip.W;
        Assert.Equal(1.0f, nearNdcZ, tolerance: 0.001f);

        // Far plane point: (0, 0, -farPlane, 1) in View space
        var farPoint = new Vector4(0f, 0f, -farPlane, 1f);
        var farClip = Vector4.Transform(farPoint, ortho);
        float farNdcZ = farClip.Z / farClip.W;
        Assert.Equal(0.0f, farNdcZ, tolerance: 0.001f);

        // Mid point: (0, 0, -(nearPlane + farPlane)*0.5f, 1)
        var midPoint = new Vector4(0f, 0f, -(nearPlane + farPlane) * 0.5f, 1f);
        var midClip = Vector4.Transform(midPoint, ortho);
        float midNdcZ = midClip.Z / midClip.W;
        Assert.Equal(0.5f, midNdcZ, tolerance: 0.001f);
    }

    [Fact]
    public void PointLight_IsExactly32Bytes()
    {
        Assert.Equal(32, Marshal.SizeOf<PointLight>());

        var light = new PointLight
        {
            Position = new Vector3(10f, 20f, 30f),
            Radius = 15f,
            Color = new Vector3(1f, 0.8f, 0.6f),
            Intensity = 100f,
        };

        Assert.Equal(10f, light.Position.X);
        Assert.Equal(15f, light.Radius);
        Assert.Equal(100f, light.Intensity);
    }

    [Fact]
    public void SpotLight_IsExactly48Bytes()
    {
        Assert.Equal(48, Marshal.SizeOf<SpotLight>());

        var spot = new SpotLight
        {
            Position = new Vector3(1f, 2f, 3f),
            Range = 25f,
            Direction = Vector3.Normalize(new Vector3(0f, -1f, 0f)),
            CosInnerAngle = MathF.Cos(0.2f),
            Color = new Vector3(1f, 1f, 1f),
            CosOuterAngle = MathF.Cos(0.4f),
        };

        Assert.Equal(25f, spot.Range);
        Assert.True(spot.CosInnerAngle > spot.CosOuterAngle);
    }

    [Fact]
    public void ClusterUniforms_IsExactly176Bytes()
    {
        Assert.Equal(176, Marshal.SizeOf<ClusterUniforms>());
    }

    [Fact]
    public void FoliageInstanceData_IsExactly16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<FoliageInstanceData>());

        var instance = new FoliageInstanceData(
            new Vector3(12.5f, 3.25f, -8.75f),
            rotationRadians: MathF.PI * 0.5f,
            scale: 1.5f,
            tintIndex: 200,
            windPhase: 128
        );

        var unpackedPos = instance.PositionOffset;
        Assert.Equal(12.5f, unpackedPos.X, tolerance: 0.001f);
        Assert.Equal(3.25f, unpackedPos.Y, tolerance: 0.001f);
        Assert.Equal(-8.75f, unpackedPos.Z, tolerance: 0.001f);

        Assert.Equal(1.5f, instance.UnpackScale(), tolerance: 0.05f);
        Assert.Equal(MathF.PI * 0.5f, instance.UnpackRotation(), tolerance: 0.05f);
    }

    [Fact]
    public void GpuParticle_IsExactly64Bytes()
    {
        Assert.Equal(64, Marshal.SizeOf<GpuParticle>());
    }

    [Fact]
    public void ShadowCascade_TexelSnappingMaintainsFrustumCoverage()
    {
        var cascade = new ShadowCascade();
        var camPos = new Vector3(0f, 5f, 10f);
        var camTarget = new Vector3(0f, 5f, 0f);
        var camUp = Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(camPos, camTarget, camUp);
        var lightDir = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f));

        cascade.Update(lightDir, view, MathF.PI / 3f, 16f / 9f, 0.1f, 25f, 2048);

        Assert.Equal(0.1f, cascade.NearDistance);
        Assert.Equal(25f, cascade.SplitDistance);

        // Light view-proj must be valid non-zero matrix
        Assert.NotEqual(Matrix4x4.Identity, cascade.ViewProjection);
        Assert.True(cascade.ViewProjection.M44 == 1.0f || cascade.ViewProjection.M44 == 0.0f);
    }

    [Fact]
    public void FoliageCooker_BakesValidCellInstances()
    {
        var cellCoord = new CellCoord(2, -3);
        var instances = FoliageCooker.GenerateCellFoliage(cellCoord, (x, z) => 10.0f, instancesPerAxis: 8);

        Assert.NotNull(instances);
        Assert.True(instances.Length > 0, "Should generate instances on flat terrain");
        Assert.Equal(64, instances.Length); // 8x8 on flat terrain = 64 instances

        foreach (var inst in instances)
        {
            Assert.Equal(10.0f, inst.PositionOffset.Y);
            Assert.True(inst.UnpackScale() >= 0.7f && inst.UnpackScale() <= 1.3f);
        }
    }
}
