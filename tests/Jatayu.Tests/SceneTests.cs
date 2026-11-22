// ==================================================================================================
// SPRINT 5a: Scene & ECS Architecture Unit Tests
// Tests Entity lifecycle, component attachment, active camera discovery, and zero-allocation query execution.
// ==================================================================================================

using System.Numerics;
using Arch.Core;
using Jatayu.Renderer.Camera;
using Jatayu.Scene;
using Jatayu.Scene.Components;
using Xunit;

namespace Jatayu.Tests;

public struct TestRotateJob : IForEach<Transform>
{
    public float AngleDelta;
    public void Update(ref Transform transform)
    {
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, AngleDelta);
    }
}

public class SceneTests
{
    [Fact]
    public void Scene_CreateAndDestroyEntity_ManagesLifecycleCorrectly()
    {
        using var scene = new Scene.Scene();

        var entity = scene.CreateEntity(new Transform(new Vector3(10, 20, 30)));
        Assert.True(entity.IsAlive);
        Assert.True(entity.Has<Transform>());

        ref var transform = ref entity.Get<Transform>();
        Assert.Equal(10f, transform.Position.X);
        Assert.Equal(20f, transform.Position.Y);
        Assert.Equal(30f, transform.Position.Z);

        scene.DestroyEntity(entity);
        Assert.False(entity.IsAlive);
    }

    [Fact]
    public void Scene_ComponentAttachment_SupportsMultipleComponents()
    {
        using var scene = new Scene.Scene();

        var entity = scene.CreateEntity(new Transform(Vector3.Zero));
        entity.Add(new CameraComponent(fovDegrees: 75.0f));
        entity.Add(LightComponent.CreateDirectional(Vector3.One, 50000.0f));
        entity.Add(new RigidBodyComponent(BodyMotionType.Dynamic, mass: 10.0f));

        Assert.True(entity.Has<Transform>());
        Assert.True(entity.Has<CameraComponent>());
        Assert.True(entity.Has<LightComponent>());
        Assert.True(entity.Has<RigidBodyComponent>());

        ref var camera = ref entity.Get<CameraComponent>();
        Assert.Equal(75.0f, camera.FieldOfView);

        ref var light = ref entity.Get<LightComponent>();
        Assert.Equal(LightType.Directional, light.Type);
        Assert.Equal(50000.0f, light.Intensity);

        ref var rb = ref entity.Get<RigidBodyComponent>();
        Assert.Equal(10.0f, rb.Mass);
        Assert.Equal(BodyMotionType.Dynamic, rb.MotionType);
    }

    [Fact]
    public void Scene_TryGetActiveCamera_FindsPrimaryCamera()
    {
        using var scene = new Scene.Scene();

        var camEntity = scene.CreateEntity(new Transform(new Vector3(0, 5, -10)));
        camEntity.Add(new CameraComponent(fovDegrees: 90.0f) { IsActive = true });

        bool found = scene.TryGetActiveCamera(out var camera, out var transform);
        Assert.True(found);
        Assert.Equal(90.0f, camera.FieldOfView);
        Assert.Equal(5.0f, transform.Position.Y);
        Assert.Equal(-10.0f, transform.Position.Z);
    }

    [Fact]
    public void Scene_Query_MutatesComponentsWithZeroAllocations()
    {
        using var scene = new Scene.Scene();

        for (int i = 0; i < 1000; i++)
        {
            scene.CreateEntity(new Transform(Vector3.Zero));
        }

        var query = new QueryDescription().WithAll<Transform>();
        var job = new TestRotateJob { AngleDelta = 0.5f };

        // Warmup
        scene.Query<TestRotateJob, Transform>(in query, ref job);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        scene.Query<TestRotateJob, Transform>(in query, ref job);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, allocAfter - allocBefore);
    }
}
