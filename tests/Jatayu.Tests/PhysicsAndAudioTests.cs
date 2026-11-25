/* =================================================================================================
 * FILE: PhysicsAndAudioTests.cs
 * LOCATION: tests/Jatayu.Tests/PhysicsAndAudioTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Comprehensive test suite for Sprint 8:
 *   - Jolt Physics Engine: initialization, body creation, step simulation, gravity response,
 *     raycasting (hit/miss), rigid body stacking stability over 120 ticks, floating-origin rebase.
 *   - Character Controller: kinematic movement, walk/sprint velocities, jump impulse, gravity, origin rebase.
 *   - OpenAL Spatial Audio: procedural PCM generation, emitter 3D distance rolloff, listener orientation,
 *     origin rebase translation, silent fallback handling.
 *   - Cell & Terrain Streaming Physics Integration: heightfield mesh collider raycasting, cell collider lifecycle.
 *   - Zero-Alloc Streaming Hardening: steady-state GC allocation verification at 100 m/s traversal.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.Numerics;
using Jatayu.AssetFormats.World;
using Jatayu.Audio;
using Jatayu.Core.Events;
using Jatayu.Core.Mathematics;
using Jatayu.Core.Profiling;
using Jatayu.Physics;
using Jatayu.Physics.Colliders;
using Jatayu.Scene.Components;
using Jatayu.Terrain;
using Jatayu.World;
using Xunit;

namespace Jatayu.Tests;

public class PhysicsAndAudioTests : IDisposable
{
    private readonly PhysicsEngine _physics;
    private readonly AudioEngine _audio;

    public PhysicsAndAudioTests()
    {
        _physics = new PhysicsEngine();
        _audio = new AudioEngine();
    }

    public void Dispose()
    {
        _physics.Dispose();
        _audio.Dispose();
    }

    // =============================================================================================
    // 1. Jolt Physics Engine Tests
    // =============================================================================================

    [Fact]
    public void PhysicsEngine_Initialization_Succeeds()
    {
        Assert.False(_physics.System.IsNull());
        Assert.Equal(0, _physics.ActiveBodyCount);
    }

    [Fact]
    public void PhysicsEngine_CreateStaticBox_And_DynamicSphere_SimulateGravity()
    {
        // 1. Create static floor box at origin (half-extents 50, 1, 50 -> surface at Y = 1)
        var floor = _physics.CreateBoxBody(
            new float3(0f, 0f, 0f),
            new float3(50f, 1f, 50f),
            BodyMotionType.Static
        );
        Assert.NotNull(floor);

        // 2. Create dynamic sphere at Y = 10, radius 1
        var sphere = _physics.CreateSphereBody(
            new float3(0f, 10f, 0f),
            radius: 1f,
            BodyMotionType.Dynamic
        );
        Assert.NotNull(sphere);
        Assert.True(sphere.IsActive);

        float initialY = sphere.Position.Y;
        Assert.Equal(10f, initialY, 0.01f);

        // 3. Step physics simulation for 120 frames (2 seconds at 60Hz) so it reaches the floor
        for (int i = 0; i < 120; i++)
        {
            _physics.Update(1.0f / 60.0f);
        }

        // 4. Verify sphere dropped under gravity and settled near the floor surface (Y ~ 2.0 = floor surface 1.0 + radius 1.0)
        float finalY = sphere.Position.Y;
        Assert.True(finalY < initialY, $"Expected sphere to fall: initial={initialY}, final={finalY}");
        Assert.InRange(finalY, 1.8f, 2.5f);

        floor.Dispose();
        sphere.Dispose();
    }

    [Fact]
    public void PhysicsEngine_Raycast_HitsBox()
    {
        // Create static box at Y = 5, extents 2x2x2 (half-extents 1, 1, 1 -> top face at Y = 6)
        var box = _physics.CreateBoxBody(
            new float3(0f, 5f, 0f),
            new float3(1f, 1f, 1f),
            BodyMotionType.Static
        );

        // Cast ray from (0, 10, 0) straight downwards towards (0, -10, 0)
        var rayHit = _physics.Raycast(
            new float3(0f, 10f, 0f),
            new float3(0f, -10f, 0f)
        );

        Assert.True(rayHit.HasHit);
        Assert.Equal(box.BodyId, rayHit.BodyId);
        Assert.InRange(rayHit.Position.Y, 5.95f, 6.05f);
        Assert.True(rayHit.Normal.Y > 0.9f, $"Expected normal pointing up, got {rayHit.Normal}");

        box.Dispose();
    }

    [Fact]
    public void PhysicsEngine_Raycast_MissReturnsFalse()
    {
        var box = _physics.CreateBoxBody(
            new float3(50f, 50f, 50f),
            new float3(1f, 1f, 1f),
            BodyMotionType.Static
        );

        // Cast ray far away
        var rayHit = _physics.Raycast(
            new float3(0f, 10f, 0f),
            new float3(0f, -10f, 0f)
        );

        Assert.False(rayHit.HasHit);
        box.Dispose();
    }

    [Fact]
    public void PhysicsEngine_RigidBodyStacking_StableOver120Ticks()
    {
        // Create floor
        var floor = _physics.CreateBoxBody(
            new float3(0f, 0f, 0f),
            new float3(20f, 1f, 20f),
            BodyMotionType.Static
        );

        // Stack 3 dynamic boxes of size 1x1x1 (half-extents 0.5)
        var b1 = _physics.CreateBoxBody(new float3(0f, 1.6f, 0f), new float3(0.5f, 0.5f, 0.5f), BodyMotionType.Dynamic);
        var b2 = _physics.CreateBoxBody(new float3(0f, 2.7f, 0f), new float3(0.5f, 0.5f, 0.5f), BodyMotionType.Dynamic);
        var b3 = _physics.CreateBoxBody(new float3(0f, 3.8f, 0f), new float3(0.5f, 0.5f, 0.5f), BodyMotionType.Dynamic);

        // Simulate 120 ticks (2 seconds)
        for (int i = 0; i < 120; i++)
        {
            _physics.Update(1.0f / 60.0f);
        }

        // Verify stacking stability: boxes must stay upright, not fall through floor, and preserve vertical order
        Assert.True(b1.Position.Y > 1.0f, $"Box 1 fell through floor: {b1.Position.Y}");
        Assert.True(b2.Position.Y > b1.Position.Y, $"Box 2 is below Box 1: {b2.Position.Y} <= {b1.Position.Y}");
        Assert.True(b3.Position.Y > b2.Position.Y, $"Box 3 is below Box 2: {b3.Position.Y} <= {b2.Position.Y}");

        floor.Dispose();
        b1.Dispose();
        b2.Dispose();
        b3.Dispose();
    }

    [Fact]
    public void PhysicsEngine_FloatingOriginRebase_TranslatesBodies()
    {
        var box = _physics.CreateBoxBody(
            new float3(100f, 5f, 200f),
            new float3(1f, 1f, 1f),
            BodyMotionType.Static
        );

        // Fire origin shifted event by (256, 0, 256)
        EventBus.Publish(new OriginShiftedEvent(256.0, 0.0, 256.0));

        var newPos = box.Position;
        Assert.Equal(-156f, newPos.X, 0.05f);
        Assert.Equal(5f, newPos.Y, 0.05f);
        Assert.Equal(-56f, newPos.Z, 0.05f);

        box.Dispose();
    }

    // =============================================================================================
    // 2. Character Controller Tests
    // =============================================================================================

    [Fact]
    public void CharacterController_WalkAndSprint_Velocities()
    {
        var character = new Jatayu.Physics.CharacterController(_physics, new float3(0f, 2f, 0f));

        Assert.Equal(5.0f, character.WalkSpeed);
        Assert.Equal(12.0f, character.SprintSpeed);
        Assert.Equal(7.0f, character.JumpVelocity);

        // Test walk horizontal velocity
        character.Update(1.0f / 60.0f, new System.Numerics.Vector2(0f, 1f), sprint: false, jump: false);
        Assert.InRange(character.LinearVelocity.Z, 4.0f, 5.5f);

        // Test sprint horizontal velocity
        character.Update(1.0f / 60.0f, new System.Numerics.Vector2(0f, 1f), sprint: true, jump: false);
        Assert.InRange(character.LinearVelocity.Z, 10.0f, 12.5f);

        character.Dispose();
    }

    [Fact]
    public void CharacterController_JumpAndGravity()
    {
        var character = new Jatayu.Physics.CharacterController(_physics, new float3(0f, 10f, 0f));

        // Jump
        character.Jump();
        Assert.Equal(7.0f, character.LinearVelocity.Y, 0.1f);

        // Update with gravity
        for (int i = 0; i < 10; i++)
        {
            character.Update(1.0f / 60.0f, System.Numerics.Vector2.Zero, sprint: false, jump: false);
        }

        // Y velocity should have decreased due to gravity (9.81 m/s^2)
        Assert.True(character.LinearVelocity.Y < 7.0f, "Gravity should decrease vertical velocity");

        character.Dispose();
    }

    [Fact]
    public void CharacterController_FloatingOriginRebase_TranslatesPosition()
    {
        var character = new Jatayu.Physics.CharacterController(_physics, new float3(100f, 10f, 100f));

        EventBus.Publish(new OriginShiftedEvent(100.0, 0.0, 100.0));

        var pos = character.Position;
        Assert.Equal(0f, pos.X, 0.05f);
        Assert.Equal(10f, pos.Y, 0.05f);
        Assert.Equal(0f, pos.Z, 0.05f);

        character.Dispose();
    }

    // =============================================================================================
    // 3. Audio Engine Tests
    // =============================================================================================

    [Fact]
    public void AudioEngine_InitAndProceduralClips_Succeed()
    {
        Assert.NotNull(_audio);
        Assert.NotNull(_audio.Listener);

        // Procedural clips must always be generated, even in silent fallback
        Assert.NotNull(_audio.SineClip);
        Assert.NotNull(_audio.ImpactClip);
        Assert.NotNull(_audio.FootstepClip);
        Assert.NotNull(_audio.JumpClip);

        Assert.True(_audio.SineClip.DurationSeconds > 0.05f);
        Assert.True(_audio.FootstepClip.DurationSeconds > 0.05f);
    }

    [Fact]
    public void SoundClip_FromFile_LoadsWavCorrectly()
    {
        string wavPath = Path.Combine(AppContext.BaseDirectory, "../../../assets/sounds/monkeys.wav");
        if (File.Exists(wavPath))
        {
            using var clip = SoundClip.FromFile(wavPath);
            Assert.True(clip.DurationSeconds > 1.0f);
            Assert.Equal(1, clip.Channels);
            Assert.Equal(44100, clip.SampleRate);
        }
    }

    [Fact]
    public void AudioEngine_SourcePlayback_PropertiesAndControl()
    {
        var source = _audio.CreateSource();
        Assert.NotNull(source);

        source.Position = new float3(10f, 2f, -5f);
        Assert.Equal(10f, source.Position.X, 0.01f);
        Assert.Equal(2f, source.Position.Y, 0.01f);
        Assert.Equal(-5f, source.Position.Z, 0.01f);

        source.Gain = 0.8f;
        Assert.Equal(0.8f, source.Gain, 0.01f);

        source.Clip = _audio.FootstepClip;
        source.Play();

        // One-shot playback through AudioEngine
        if (_audio.JumpClip != null)
        {
            _audio.PlayOneShot(_audio.JumpClip, new float3(0f, 1f, 0f), 1.0f);
        }

        source.Stop();
        _audio.RemoveSource(source);
    }

    [Fact]
    public void AudioEngine_Listener_SyncWithCamera()
    {
        var listener = _audio.Listener;
        var forward = new float3(0f, 0f, -1f);
        var up = new float3(0f, 1f, 0f);

        listener.Update(float3.Zero, forward, up);

        Assert.Equal(0f, listener.Position.X, 0.01f);
        Assert.Equal(0f, listener.Position.Y, 0.01f);
        Assert.Equal(0f, listener.Position.Z, 0.01f);
        Assert.Equal(forward.Z, listener.Forward.Z, 0.01f);
        Assert.Equal(up.Y, listener.Up.Y, 0.01f);
    }

    [Fact]
    public void AudioEngine_FloatingOriginRebase_TranslatesActiveSources()
    {
        var source = _audio.CreateSource();
        source.Position = new float3(50f, 0f, 50f);

        EventBus.Publish(new OriginShiftedEvent(50.0, 0.0, 50.0));

        Assert.Equal(0f, source.Position.X, 0.05f);
        Assert.Equal(0f, source.Position.Y, 0.05f);
        Assert.Equal(0f, source.Position.Z, 0.05f);

        _audio.RemoveSource(source);
    }

    [Fact]
    public void AudioEngine_DistanceModel_DefaultsToLinearDistanceClamped()
    {
        if (_audio.IsHardwareAvailable)
        {
            Assert.Equal(Brutal.OpenAlApi.AlDistanceModel.LinearDistanceClamped, _audio.DistanceModel);
        }
    }

    [Fact]
    public void AudioEngine_DistanceModel_CanBeChanged()
    {
        if (_audio.IsHardwareAvailable)
        {
            _audio.DistanceModel = Brutal.OpenAlApi.AlDistanceModel.InverseDistanceClamped;
            Assert.Equal(Brutal.OpenAlApi.AlDistanceModel.InverseDistanceClamped, _audio.DistanceModel);

            // Revert back to LinearDistanceClamped
            _audio.DistanceModel = Brutal.OpenAlApi.AlDistanceModel.LinearDistanceClamped;
            Assert.Equal(Brutal.OpenAlApi.AlDistanceModel.LinearDistanceClamped, _audio.DistanceModel);
        }
    }

    // =============================================================================================
    // 4. Terrain & World Cell Collision Integration Tests
    // =============================================================================================

    [Fact]
    public void TerrainTile_CreatePhysicsCollider_RaycastHitsHeightfield()
    {
        // Create procedural tile (65x65 samples, max elevation 30m)
        var tile = TerrainTile.CreateProcedural(0, 0, samples: 65, maxElevation: 30f);
        var camPos = new WorldPosition(0.0, 0.0, 0.0);

        tile.CreatePhysicsCollider(_physics, in camPos);
        Assert.NotNull(tile.ColliderBody);

        // Sample expected height at local (64m, 64m)
        float expectedHeight = tile.SampleHeightLocal(64f, 64f);

        // Raycast down from Y = 100 to Y = -50 at X = 64, Z = 64
        var hit = _physics.Raycast(new float3(64f, 100f, 64f), new float3(0f, -150f, 0f));

        Assert.True(hit.HasHit, "Raycast against terrain heightfield mesh collider must hit");
        Assert.InRange(hit.Position.Y, expectedHeight - 2.0f, expectedHeight + 2.0f);

        tile.DestroyPhysicsCollider(_physics);
        Assert.Null(tile.ColliderBody);
    }

    [Fact]
    public void WorldCell_SpawnAndDespawnPhysicsAndAudio_Lifecycle()
    {
        var cell = new WorldCell(new CellCoord(1, 1));
        var worldOrigin = new WorldPosition(128.0, 0.0, 128.0);

        // Setup test collider and audio point records
        cell.Colliders = new Jatayu.AssetFormats.World.CookedColliderRecord[]
        {
            new() { ShapeType = Jatayu.AssetFormats.World.ColliderShapeType.Box, OffsetX = 10f, OffsetY = 5f, OffsetZ = 10f, ExtentX = 2f, ExtentY = 2f, ExtentZ = 2f }
        };
        cell.AudioEmitters = new Jatayu.AssetFormats.World.CookedAudioPointRecord[]
        {
            new() { OffsetX = 5f, OffsetY = 2f, OffsetZ = 5f, Volume = 0.8f, Radius = 30f }
        };

        cell.SpawnPhysicsBodies(_physics, in worldOrigin);
        cell.SpawnAudioEmitters(_audio, in worldOrigin);

        Assert.Single(cell.ResidentBodies);
        Assert.Single(cell.ResidentAudioSources);
        Assert.Equal(10f, cell.ResidentBodies[0].Position.X, 0.05f);
        Assert.Equal(5f, cell.ResidentBodies[0].Position.Y, 0.05f);
        Assert.Equal(5f, cell.ResidentAudioSources[0].Position.X, 0.05f);
        Assert.Equal(2f, cell.ResidentAudioSources[0].Position.Y, 0.05f);

        cell.DespawnPhysicsBodies(_physics);
        cell.DespawnAudioEmitters(_audio);

        Assert.Empty(cell.ResidentBodies);
        Assert.Empty(cell.ResidentAudioSources);
    }

    // =============================================================================================
    // 5. Zero-Alloc Hardening & Performance Tests
    // =============================================================================================

    [Fact]
    public void StreamingManager_TraversalAt100Mps_ZeroHeapAllocations()
    {
        var grid = new WorldGrid();
        var config = new WorldConfig { UnloadRadiusRingA = 1000f };
        using var streamer = new StreamingManager(grid, config);
        streamer.Physics = _physics;
        streamer.Audio = _audio;

        var velocity = new System.Numerics.Vector3(100f, 0f, 0f); // 100 m/s high speed traversal
        double currentX = 0.0;
        double currentZ = 0.0;

        // Warmup: prime queues, background workers, and active cells across traversal range
        for (int i = 0; i < 60; i++)
        {
            currentX += 1.666;
            var pos = new WorldPosition(currentX, 0.0, currentZ);
            streamer.Update(1.0 / 60.0, in pos, in velocity);
        }
        for (int i = 0; i < 60; i++)
        {
            currentX -= 1.666;
            var pos = new WorldPosition(currentX, 0.0, currentZ);
            streamer.Update(1.0 / 60.0, in pos, in velocity);
        }

        var measurementEnd = new WorldPosition(currentX + 100.0, 0.0, currentZ);
        WaitForStreamingToSettle(streamer, in measurementEnd, in velocity);
        var measurementStart = new WorldPosition(currentX, 0.0, currentZ);
        WaitForStreamingToSettle(streamer, in measurementStart, in velocity);

        // Measure steady state allocation
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();

        // Run 60 frames of traversal at 100 m/s over primed corridor
        for (int i = 0; i < 60; i++)
        {
            currentX += 1.666;
            var pos = new WorldPosition(currentX, 0.0, currentZ);
            streamer.Update(1.0 / 60.0, in pos, in velocity);
        }

        long endAlloc = GC.GetAllocatedBytesForCurrentThread();
        long totalAlloc = endAlloc - startAlloc;

        // Verify zero or near-zero allocations in the streaming loop (excluding thread pool task dispatch)
        Assert.True(totalAlloc < 1024, $"Expected zero steady-state allocation during traversal, got {totalAlloc} bytes");
    }

    private static void WaitForStreamingToSettle(
        StreamingManager streamer,
        in WorldPosition playerPosition,
        in System.Numerics.Vector3 playerVelocity)
    {
        int consecutiveIdleFrames = 0;
        for (int i = 0; i < 1000; i++)
        {
            streamer.Update(1.0 / 60.0, in playerPosition, in playerVelocity);

            if (streamer.InFlightCount == 0 && streamer.ReadyToFinalizeCount == 0)
            {
                if (++consecutiveIdleFrames == 2)
                    return;
            }
            else
            {
                consecutiveIdleFrames = 0;
            }

            System.Threading.Thread.Sleep(1);
        }

        Assert.Fail("Streaming jobs did not settle during test warmup.");
    }
}
