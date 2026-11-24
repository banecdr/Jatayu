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
 *   - GpuParticle (64 bytes) struct layout & alignment
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
using Jatayu.Renderer.Material;
using Jatayu.Renderer.Particles;
using Jatayu.Renderer.Shadows;
using Jatayu.RHI.Vulkan.Pipelines;
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
        Assert.Equal(1024, ClusteredLightCulling.MaxLights);
        Assert.Equal(65536, ClusteredLightCulling.MaxIndices);
        Assert.Equal((uint)ClusteredLightCulling.MaxIndices, new ClusterUniforms { MaxLightIndices = ClusteredLightCulling.MaxIndices }.MaxLightIndices);
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
        Assert.Equal(0, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Position)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Age)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Velocity)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Lifetime)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Color)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<GpuParticle>(nameof(GpuParticle.Size)).ToInt32());
    }

    [Fact]
    public void Material_EncodesAlphaTestFlagWithoutChangingDoubleSidedFlag()
    {
        var opaque = new Material { DoubleSided = true };
        var masked = new Material { DoubleSided = true, AlphaTest = true };

        Assert.Equal(1u, opaque.ToGpuData().Flags);
        Assert.Equal(3u, masked.ToGpuData().Flags);
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

        var cubeTop = Vector4.Transform(new Vector3(0f, 1f, 0f), cascade.ViewProjection);
        var ground = Vector4.Transform(new Vector3(0f, 0f, 0f), cascade.ViewProjection);
        var cubeBot = Vector4.Transform(new Vector3(0f, -1f, 0f), cascade.ViewProjection);

        float zTop = cubeTop.Z / cubeTop.W;
        float zGround = ground.Z / ground.W;
        float zBot = cubeBot.Z / cubeBot.W;

        // In Reverse-Z: closer to light MUST have LARGER depth!
        // Cube top is closer to light than ground, which is closer than cube bottom!
        Assert.True(zTop > zGround, $"zTop ({zTop}) must be > zGround ({zGround}) in Reverse-Z!");
        Assert.True(zGround > zBot, $"zGround ({zGround}) must be > zBot ({zBot}) in Reverse-Z!");

        Matrix4x4 relativeView = view;
        relativeView.M41 = relativeView.M42 = relativeView.M43 = 0.0f;
        Assert.True(Matrix4x4.Invert(relativeView, out var inverseRelativeView));
        float tanHalfFov = MathF.Tan(MathF.PI / 6f);
        float nearY = tanHalfFov * cascade.NearDistance;
        float nearX = nearY * 16f / 9f;
        float farY = tanHalfFov * cascade.SplitDistance;
        float farX = farY * 16f / 9f;
        Span<Vector3> viewCorners = stackalloc Vector3[8]
        {
            new(-nearX, nearY, -cascade.NearDistance),
            new(nearX, nearY, -cascade.NearDistance),
            new(nearX, -nearY, -cascade.NearDistance),
            new(-nearX, -nearY, -cascade.NearDistance),
            new(-farX, farY, -cascade.SplitDistance),
            new(farX, farY, -cascade.SplitDistance),
            new(farX, -farY, -cascade.SplitDistance),
            new(-farX, -farY, -cascade.SplitDistance),
        };
        foreach (Vector3 viewCorner in viewCorners)
        {
            Vector3 corner = Vector3.Transform(viewCorner, inverseRelativeView);
            Vector4 clip = Vector4.Transform(corner, cascade.ViewProjection);
            Assert.InRange(clip.X / clip.W, -1.001f, 1.001f);
            Assert.InRange(clip.Y / clip.W, -1.001f, 1.001f);
            Assert.InRange(clip.Z / clip.W, -0.001f, 1.001f);
        }

        Vector3 frustumCenter = Vector3.Transform(
            new Vector3(0f, 0f, -(cascade.NearDistance + cascade.SplitDistance) * 0.5f),
            inverseRelativeView);
        Vector4 centerClip = Vector4.Transform(frustumCenter, cascade.ViewProjection);
        float pixelX = (centerClip.X / centerClip.W * 0.5f + 0.5f) * 2048f;
        float pixelY = (centerClip.Y / centerClip.W * 0.5f + 0.5f) * 2048f;
        Assert.Equal(MathF.Round(pixelX), pixelX, precision: 3);
        Assert.Equal(MathF.Round(pixelY), pixelY, precision: 3);
    }

    [Fact]
    public void CascadedShadowMap_SplitsAreOrderedAndRespectDistribution()
    {
        Vector4 uniform = CascadedShadowMap.CalculateSplitDistances(0.1f, 100f, lambda: 0.0f);
        Vector4 logarithmic = CascadedShadowMap.CalculateSplitDistances(0.1f, 100f, lambda: 1.0f);

        Assert.True(uniform.X < uniform.Y && uniform.Y < uniform.Z && uniform.Z < uniform.W);
        Assert.Equal(100f, uniform.W, precision: 4);
        Assert.True(logarithmic.X < uniform.X);
        Assert.True(logarithmic.Y < uniform.Y);
        Assert.True(logarithmic.Z < uniform.Z);
        Assert.Equal(100f, logarithmic.W, precision: 4);
    }

    [Fact]
    public void CascadedShadowMap_RejectsInvalidSplitRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadedShadowMap.CalculateSplitDistances(0f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadedShadowMap.CalculateSplitDistances(1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadedShadowMap.CalculateSplitDistances(0.1f, 100f, lambda: 1.1f));
    }

    [Fact]
    public void CascadedShadowMap_SelectsExpectedCascadeAtEachSplit()
    {
        var splits = new Vector4(10f, 20f, 30f, 40f);

        Assert.Equal(0, CascadedShadowMap.SelectCascadeIndex(10f, in splits));
        Assert.Equal(1, CascadedShadowMap.SelectCascadeIndex(10.01f, in splits));
        Assert.Equal(2, CascadedShadowMap.SelectCascadeIndex(20.01f, in splits));
        Assert.Equal(3, CascadedShadowMap.SelectCascadeIndex(30.01f, in splits));
        Assert.Equal(3, CascadedShadowMap.SelectCascadeIndex(40f, in splits));
        Assert.Equal(-1, CascadedShadowMap.SelectCascadeIndex(40.01f, in splits));
    }

    [Fact]
    public void CascadedShadowMap_PacksShadowParametersInGpuOrder()
    {
        Vector4 packed = CascadedShadowMap.PackGpuShadowParams(1024, 0.0005f, 1.6f);

        Assert.Equal(1024f, packed.X);
        Assert.Equal(1f / 1024f, packed.Y);
        Assert.Equal(0.0005f, packed.Z);
        Assert.Equal(1.6f, packed.W);
        Assert.Throws<ArgumentOutOfRangeException>(() => CascadedShadowMap.PackGpuShadowParams(0, 0.001f, 1.0f));
    }

    [Fact]
    public void ShadowQualityPreset_MapsPersistedNamesToResolutionAndFiltering()
    {
        Assert.True(ShadowQualityPreset.TryParse("standard", out var standard));
        Assert.Equal(1024, standard.Resolution);
        Assert.False(standard.HighQualityFiltering);

        Assert.True(ShadowQualityPreset.TryParse("High", out var high));
        Assert.Equal(2048, high.Resolution);
        Assert.True(high.HighQualityFiltering);
        Assert.Equal(1.6f, high.FilterRadius);

        Assert.True(ShadowQualityPreset.TryParse("Ultra", out var ultra));
        Assert.Equal(2048, ultra.Resolution);
        Assert.True(ultra.FilterRadius > high.FilterRadius);
        Assert.False(ShadowQualityPreset.TryParse("unknown", out _));
    }

    [Fact]
    public void LightEnvironment_PacksShadowFilterAndDebugParameters()
    {
        var light = new LightEnvironment
        {
            HighQualityShadows = true,
            ShadowFilterRadius = 2.4f,
            ShadowDebugMode = ShadowDebugMode.Bias,
        };
        var data = light.ToGpuData(Vector3.Zero);

        Assert.Equal(2u, data.CastsShadows);
        Assert.Equal(2048f, data.ShadowParams.X);
        Assert.Equal(1f / 2048f, data.ShadowParams.Y);
        Assert.Equal(2.4f, data.ShadowParams.Z);
        Assert.Equal((float)ShadowDebugMode.Bias, data.ShadowParams.W);
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

    [Fact]
    public void IblProbe_ConstantsAndLayout_AreValid()
    {
        Assert.True(IblProbe.BrdfLutResolution >= 256);
        Assert.True(IblProbe.IrradianceResolution >= 32);
        Assert.True(IblProbe.PrefilterResolution >= 64);
        Assert.True(IblProbe.PrefilterMipLevels >= 4);
    }

    [Theory]
    [InlineData("ibl_environment.comp")]
    [InlineData("ibl_irradiance.comp")]
    [InlineData("ibl_prefilter.comp")]
    public void IblProbe_ResolvesPackagedComputeShaders(string shaderName)
    {
        string shaderAssetRoot = Path.Combine(Path.GetTempPath(), $"Jatayu-ibl-shaders-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(shaderAssetRoot, "assets", "shaders");
        Directory.CreateDirectory(shaderDirectory);

        try
        {
            string spirvPath = Path.Combine(shaderDirectory, $"{shaderName}.spv");
            File.WriteAllBytes(spirvPath, [0x03, 0x02, 0x23, 0x07]);

            string resolvedPath = IblProbe.ResolveShader(shaderName, shaderAssetRoot);

            Assert.Equal(spirvPath, resolvedPath);
        }
        finally
        {
            Directory.Delete(shaderAssetRoot, recursive: true);
        }
    }

    [Fact]
    public void CascadedShadowMap_ConstantsAndGpuData_AreValid()
    {
        Assert.Equal(4, CascadedShadowMap.CascadeCount);
        Assert.Equal(2048, CascadedShadowMap.DefaultResolution);
        Assert.Equal(288, Marshal.SizeOf<GpuCascadeData>());
    }

    [Fact]
    public void TransparentComposite_ShaderFiles_ExistAndNotEmpty()
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot, "Jatayu.csproj")))
            repositoryRoot = Directory.GetParent(repositoryRoot)?.FullName;
        Assert.NotNull(repositoryRoot);

        string shaderDirectory = Path.Combine(repositoryRoot, "assets", "shaders");

        string vertPath = ShaderCompiler.ResolveShaderFile(Path.Combine(shaderDirectory, "transparent_composite.vert"));
        string fragPath = ShaderCompiler.ResolveShaderFile(Path.Combine(shaderDirectory, "transparent_composite.frag"));

        Assert.True(File.Exists(vertPath), $"Missing vertex shader: {vertPath}");
        Assert.True(File.Exists(fragPath), $"Missing fragment shader: {fragPath}");
        Assert.True(new FileInfo(vertPath).Length > 0, "Vertex shader is empty");
        Assert.True(new FileInfo(fragPath).Length > 0, "Fragment shader is empty");
    }
}
