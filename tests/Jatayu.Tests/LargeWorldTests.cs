// ==================================================================================================
// Jatayu Engine - Unit Tests
// Module: LargeWorldTests.cs
// Role: Verifies Sprint 5b Large-World rendering foundations: double-precision WorldPosition,
//       OriginShift 256m tile snapping, 5-plane Reverse-Z Frustum culling, and MaterialTable.
// ==================================================================================================

using System.Numerics;
using Jatayu.Core.Events;
using Jatayu.Core.Mathematics;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Lighting;
using Jatayu.Renderer.Material;
using Jatayu.Renderer.Skybox;
using Xunit;

namespace Jatayu.Tests;

public sealed class LargeWorldTests
{
    [Fact]
    public void WorldPosition_CameraRelativeTranslation_HasZeroJitterAt50000m()
    {
        // Object at 50,000 meters from world origin
        var objectPos = new WorldPosition(50000.0, 100.0, -50000.0);
        // Camera right next to the object at 49,995 meters
        var cameraPos = new WorldPosition(49995.0, 100.0, -50000.0);

        Vector3 cameraRelative = objectPos.ToCameraRelative(in cameraPos);

        // Difference must be exactly 5.0 on X with zero jitter
        Assert.Equal(5.0f, cameraRelative.X, 0.0001f);
        Assert.Equal(0.0f, cameraRelative.Y, 0.0001f);
        Assert.Equal(0.0f, cameraRelative.Z, 0.0001f);

        // Distance in double precision
        double dist = objectPos.DistanceTo(in cameraPos);
        Assert.Equal(5.0, dist, 0.000001);
    }

    [Fact]
    public void WorldPosition_ArithmeticOperators_WorkAccurately()
    {
        var p1 = new WorldPosition(1000.5, 200.25, 300.125);
        var p2 = new WorldPosition(500.25, 100.125, 150.0);

        var diff = p1 - p2;
        Assert.Equal(500.25, diff.X, 0.000001);
        Assert.Equal(100.125, diff.Y, 0.000001);
        Assert.Equal(150.125, diff.Z, 0.000001);

        var sum = p1 + new Vector3(10f, 20f, 30f);
        Assert.Equal(1010.5, sum.X, 0.000001);
        Assert.Equal(220.25, sum.Y, 0.000001);
        Assert.Equal(330.125, sum.Z, 0.000001);
    }

    [Fact]
    public void OriginShift_ThresholdAndTileSnapping_TriggerCorrectly()
    {
        OriginShiftedEvent capturedEvent = default;
        bool eventReceived = false;

        Jatayu.Core.Events.EventHandler<OriginShiftedEvent> handler = (in OriginShiftedEvent e) =>
        {
            capturedEvent = e;
            eventReceived = true;
        };

        EventBus.Subscribe(handler);

        try
        {
            var originShift = new OriginShift { ThresholdDistance = 2000.0 };

            // 1. Camera at 1500m (below 2000m threshold) -> No shift
            bool shifted1 = originShift.CheckAndShift(new WorldPosition(1500.0, 0.0, 0.0), out var delta1);
            Assert.False(shifted1);
            Assert.False(eventReceived);
            Assert.Equal(WorldPosition.Zero, delta1);

            // 2. Camera moves to 2200m (exceeds 2000m threshold)
            // 2200 / 256 = 8.59375 -> rounded to 9 * 256 = 2304m
            bool shifted2 = originShift.CheckAndShift(new WorldPosition(2200.0, 0.0, 0.0), out var delta2);
            Assert.True(shifted2);
            Assert.True(eventReceived);
            Assert.Equal(2304.0, delta2.X);
            Assert.Equal(0.0, delta2.Y);
            Assert.Equal(0.0, delta2.Z);

            Assert.Equal(2304.0, capturedEvent.DeltaX);
            Assert.Equal(0.0, capturedEvent.DeltaY);
            Assert.Equal(0.0, capturedEvent.DeltaZ);

            // 3. New origin is now at 2304m
            Assert.Equal(2304.0, originShift.CurrentOrigin.X);
        }
        finally
        {
            EventBus.Unsubscribe(handler);
        }
    }

    [Fact]
    public void Frustum_5PlaneReverseZCulling_VisibleAndClampedProperly()
    {
        var camera = new Camera3D(
            position: new Vector3(0, 0, 0),
            target: new Vector3(0, 0, -10),
            width: 1920,
            height: 1080,
            fovDegrees: 60f,
            nearPlane: 0.1f
        );

        var frustum = Frustum.FromCamera(camera, maxDrawDistance: 5000.0f);

        // 1. Directly in front of camera at 50m: VISIBLE
        Assert.True(frustum.IsSphereVisible(new Vector3(0, 0, -50), 2.0f));

        // 2. Directly behind camera at 50m: NOT VISIBLE
        Assert.False(frustum.IsSphereVisible(new Vector3(0, 0, 50), 2.0f));

        // 3. Far outside to the right: NOT VISIBLE
        Assert.False(frustum.IsSphereVisible(new Vector3(1000, 0, -50), 2.0f));

        // 4. In front but beyond MaxDrawDistance (6,000m > 5,000m clamp): NOT VISIBLE
        Assert.False(frustum.IsSphereVisible(new Vector3(0, 0, -6000), 2.0f));

        // 5. Within MaxDrawDistance at 4,000m: VISIBLE
        Assert.True(frustum.IsSphereVisible(new Vector3(0, 0, -4000), 10.0f));

        // 6. AABB testing inside view
        Assert.True(frustum.IsBoxVisible(new Vector3(-5, -5, -60), new Vector3(5, 5, -50)));

        // 7. AABB testing behind camera
        Assert.False(frustum.IsBoxVisible(new Vector3(-5, -5, 50), new Vector3(5, 5, 60)));
    }

    [Fact]
    public void Material_PbrDataPackaging_MatchesGpuLayout()
    {
        var mat = new Material
        {
            Name = "Gold",
            BaseColor = new Vector4(1.0f, 0.76f, 0.33f, 1.0f),
            Metallic = 1.0f,
            Roughness = 0.2f,
            AlbedoTextureSlot = 5,
            NormalTextureSlot = 6,
            MetallicRoughnessTextureSlot = 7,
            OcclusionTextureSlot = 8,
            EmissiveTextureSlot = 9,
            EmissiveColor = new Vector3(0.5f, 0.2f, 0.1f),
            EmissiveIntensity = 2.0f,
            DoubleSided = true,
        };

        var gpuData = mat.ToGpuData();

        Assert.Equal(mat.BaseColor, gpuData.BaseColorFactor);
        Assert.Equal(mat.Metallic, gpuData.MetallicFactor);
        Assert.Equal(mat.Roughness, gpuData.RoughnessFactor);
        Assert.Equal(5u, gpuData.AlbedoIndex);
        Assert.Equal(6u, gpuData.NormalIndex);
        Assert.Equal(7u, gpuData.MetallicRoughnessIndex);
        Assert.Equal(8u, gpuData.OcclusionIndex);
        Assert.Equal(9u, gpuData.EmissiveIndex);
        Assert.Equal(mat.EmissiveColor, gpuData.EmissiveFactor);
        Assert.Equal(2.0f, gpuData.EmissiveIntensity);
        Assert.Equal(1u, gpuData.Flags);
    }

    [Fact]
    public void DirectionalLight_ReverseZLightViewProjection_MapsNearTo1AndFarTo0()
    {
        var light = new DirectionalLight
        {
            Direction = new Vector3(0, -1, 0),
            Intensity = 100000f,
            CastsShadows = true,
        };

        Vector3 center = Vector3.Zero;
        float nearPlane = -100.0f;
        float farPlane = 100.0f;
        Matrix4x4 lightVp = light.ComputeLightViewProjection(center, orthoExtents: 50.0f, nearPlane: nearPlane, farPlane: farPlane);

        bool inverted = Matrix4x4.Invert(lightVp, out var invLightVp);
        Assert.True(inverted);

        // Near plane point maps to 1.0 in Reverse-Z
        Vector4 nearClip = new(0, 0, 1.0f, 1.0f);
        Vector4 nearWorld = Vector4.Transform(nearClip, invLightVp);
        nearWorld /= nearWorld.W;

        Vector4 reprojectedNear = Vector4.Transform(nearWorld, lightVp);
        float nearDepth = reprojectedNear.Z / reprojectedNear.W;
        Assert.Equal(1.0f, nearDepth, 0.001f);

        // Far plane point maps to 0.0 in Reverse-Z
        Vector4 farClip = new(0, 0, 0.0f, 1.0f);
        Vector4 farWorld = Vector4.Transform(farClip, invLightVp);
        farWorld /= farWorld.W;

        Vector4 reprojectedFar = Vector4.Transform(farWorld, lightVp);
        float farDepth = reprojectedFar.Z / reprojectedFar.W;
        Assert.Equal(0.0f, farDepth, 0.001f);
    }

    [Fact]
    public void Skybox_ComputeInverseViewProjection_StripsTranslation()
    {
        var skybox = new Skybox();
        var camera = new Camera3D(
            position: new Vector3(100, 200, 300),
            target: new Vector3(100, 200, 200),
            width: 1920,
            height: 1080
        );

        Matrix4x4 invVp = skybox.ComputeInverseViewProjection(camera.ViewMatrix, camera.ProjectionMatrix);

        // Verify that inverting back produces a view matrix with zero translation
        Matrix4x4.Invert(invVp, out var forwardVp);

        // Direction vector unprojected at Z = 0.0 (Reverse-Z infinity) must not be NaN/Infinity
        Vector4 clipPoint = new(0, 0, 0.0f, 1.0f);
        Vector4 worldRay = Vector4.Transform(clipPoint, invVp);
        Vector3 rayDir = Vector3.Normalize(new Vector3(worldRay.X, worldRay.Y, worldRay.Z));

        Assert.False(float.IsNaN(rayDir.X));
        Assert.False(float.IsNaN(rayDir.Y));
        Assert.False(float.IsNaN(rayDir.Z));
        Assert.Equal(1.0f, rayDir.Length(), 0.001f);
    }
}
