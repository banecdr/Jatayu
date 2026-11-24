/* =================================================================================================
 * FILE: PhysicsEngine.cs
 * LOCATION: src/Physics/PhysicsEngine.cs
 * 
 * WHAT THIS FILE DOES:
 *   Master Jolt 3D physics engine coordinator:
 *   - Initializes native Jolt physics subsystem (D9 single precision, broadphase layers, object filters).
 *   - Manages static, kinematic, and dynamic rigid bodies.
 *   - Coordinates fixed-timestep physics simulation with multi-threaded or single-threaded jobs.
 *   - Coordinates deterministic floating-origin shifts (snapped to 256m terrain tile multiple per D16).
 *   - Executes high-performance raycast queries across static and dynamic collision geometries.
 *   - Streams static cell colliders and terrain heightfield shapes.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;
using Jatayu.Core.Events;
using Jatayu.Core.Logging;
using Jatayu.Physics.Colliders;
using Jatayu.Scene.Components;

namespace Jatayu.Physics;

/// <summary>
/// Physics object layers distinguishing static, kinematic, and dynamic geometries.
/// </summary>
public static class PhysicsLayers
{
    public const ushort NonMoving = 0; // Static terrain, cell static instances, buildings
    public const ushort Moving = 1;    // Dynamic rigid bodies, kinematic platforms, debris
    public const ushort Character = 2; // Virtual character and player
    public const ushort NumLayers = 3;
}

/// <summary>
/// Physics broad-phase layers.
/// </summary>
public static class BroadPhaseLayers
{
    public const byte NonMoving = 0;
    public const byte Moving = 1;
    public const byte NumLayers = 2;
}

/// <summary>
/// Master coordinator for the Jolt 3D physics simulation engine.
/// </summary>
public sealed class PhysicsEngine : IDisposable
{
    private static bool s_joltInitialized;
    private static readonly object s_initLock = new();

    private PhysicsSystem _physicsSystem;
    private TempAllocatorImplWithMallocFallback _tempAllocator;
    private JobSystemThreadPool _jobThreadPool;
    private BroadPhaseLayerInterfaceTable _broadPhaseLayerTable;
    private ObjectLayerPairFilterTable _objectLayerPairFilter;
    private ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhaseFilter;

    private readonly List<PhysicsBody> _bodies = new(1024);
    private readonly Dictionary<uint, PhysicsBody> _bodiesById = new(1024);
    private readonly object _bodiesLock = new();

    private readonly Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private float3 _gravity = new(0.0f, -9.81f, 0.0f);
    private bool _disposed;

    /// <summary>
    /// Gets the underlying Jolt BodyInterface for low-level access.
    /// </summary>
    public BodyInterface BodyInterface => _physicsSystem.GetBodyInterface();

    /// <summary>
    /// Gets the underlying Jolt PhysicsSystem.
    /// </summary>
    public PhysicsSystem System => _physicsSystem;

    /// <summary>
    /// Gets the total number of managed physics bodies currently registered.
    /// </summary>
    public int BodyCount
    {
        get
        {
            lock (_bodiesLock)
                return _bodies.Count;
        }
    }

    /// <summary>
    /// Gets the total number of managed physics bodies currently active/registered.
    /// </summary>
    public int ActiveBodyCount => BodyCount;

    /// <summary>
    /// Gets or sets simulation gravity.
    /// </summary>
    public float3 Gravity
    {
        get => _gravity;
        set
        {
            _gravity = value;
            _physicsSystem.SetGravity(value);
        }
    }

    public PhysicsEngine(uint maxBodies = 16384, uint maxBodyPairs = 16384, uint maxContactConstraints = 16384)
    {
        lock (s_initLock)
        {
            if (!s_joltInitialized)
            {
                Jolt.RegisterDefaultAllocator();
                if (Factory.Instance.IsNull())
                {
                    Factory.Instance = Factory.Create();
                }
                Jolt.RegisterTypes();
                s_joltInitialized = true;
            }
        }

        // 1. Allocate 16MB temp allocator with malloc fallback
        _tempAllocator = TempAllocatorImplWithMallocFallback.Create(16 * 1024 * 1024);

        // 2. Worker threads for physics jobs
        int workerThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        _jobThreadPool = JobSystemThreadPool.Create(2048, 8, workerThreads);

        // 3. Configure BroadPhase layer mapping table
        _broadPhaseLayerTable = BroadPhaseLayerInterfaceTable.Create(PhysicsLayers.NumLayers, BroadPhaseLayers.NumLayers);
        _broadPhaseLayerTable.MapObjectToBroadPhaseLayer(PhysicsLayers.NonMoving, BroadPhaseLayers.NonMoving);
        _broadPhaseLayerTable.MapObjectToBroadPhaseLayer(PhysicsLayers.Moving, BroadPhaseLayers.Moving);
        _broadPhaseLayerTable.MapObjectToBroadPhaseLayer(PhysicsLayers.Character, BroadPhaseLayers.Moving);

        // 4. Configure Object layer collision table
        _objectLayerPairFilter = ObjectLayerPairFilterTable.Create(PhysicsLayers.NumLayers);
        // NonMoving vs Moving collides
        _objectLayerPairFilter.EnableCollision(PhysicsLayers.NonMoving, PhysicsLayers.Moving);
        _objectLayerPairFilter.EnableCollision(PhysicsLayers.NonMoving, PhysicsLayers.Character);
        // Moving vs Moving collides
        _objectLayerPairFilter.EnableCollision(PhysicsLayers.Moving, PhysicsLayers.Moving);
        _objectLayerPairFilter.EnableCollision(PhysicsLayers.Moving, PhysicsLayers.Character);
        // NonMoving vs NonMoving does NOT collide (terrain does not collide with static rocks)
        _objectLayerPairFilter.DisableCollision(PhysicsLayers.NonMoving, PhysicsLayers.NonMoving);

        // 5. Configure Object vs BroadPhase filter table
        _objectVsBroadPhaseFilter = ObjectVsBroadPhaseLayerFilterTable.Create(
            _broadPhaseLayerTable,
            BroadPhaseLayers.NumLayers,
            _objectLayerPairFilter,
            PhysicsLayers.NumLayers
        );

        // 6. Initialize PhysicsSystem
        _physicsSystem = PhysicsSystem.Create();
        _physicsSystem.Init(
            maxBodies,
            numBodyMutexes: Math.Max(32u, maxBodies / 1024u),
            maxBodyPairs,
            maxContactConstraints,
            _broadPhaseLayerTable,
            _objectVsBroadPhaseFilter,
            _objectLayerPairFilter
        );
        _physicsSystem.SetGravity(_gravity);

        // Subscribe to floating-origin shifts (Section 1.3, D16)
        _originShiftHandler = (in OriginShiftedEvent e) => ShiftOrigin(e.DeltaX, e.DeltaY, e.DeltaZ);
        EventBus.Subscribe<OriginShiftedEvent>(_originShiftHandler);

        Log.Info($"[PhysicsEngine] Initialized Jolt Physics System (MaxBodies={maxBodies}, Workers={workerThreads})");
    }

    /// <summary>
    /// Steps the physics simulation by the given delta time.
    /// </summary>
    /// <param name="deltaTime">Delta time in seconds (e.g. 1/60f).</param>
    /// <param name="collisionSteps">Number of collision sub-steps (default 1).</param>
    public void Update(float deltaTime, int collisionSteps = 1)
    {
        if (_disposed) return;
        _physicsSystem.Update(deltaTime, collisionSteps, _tempAllocator, _jobThreadPool);
    }

    /// <summary>
    /// Creates and adds a physics body with the specified collider and motion settings.
    /// </summary>
    public PhysicsBody CreateBody(
        Collider collider,
        in float3 position,
        in floatQuat rotation,
        BodyMotionType motionType,
        float mass = 1.0f,
        float linearDamping = 0.05f,
        float angularDamping = 0.05f)
    {
        if (collider == null || !collider.IsValid)
            throw new ArgumentException("Collider must have a valid Jolt shape.", nameof(collider));

        MotionType joltMotionType = motionType switch
        {
            BodyMotionType.Static => MotionType.Static,
            BodyMotionType.Kinematic => MotionType.Kinematic,
            _ => MotionType.Dynamic
        };

        ushort layer = motionType == BodyMotionType.Static ? PhysicsLayers.NonMoving : PhysicsLayers.Moving;

        var creationSettings = BodyCreationSettings.Create(
            collider.Shape,
            position,
            rotation,
            joltMotionType,
            layer
        );

        if (motionType == BodyMotionType.Dynamic)
        {
            creationSettings.SetOverrideMassProperties(OverrideMassProperties.CalculateInertia);
            var massProps = creationSettings.GetMassProperties();
            massProps.SetMass(mass > 0 ? mass : 1.0f);
            creationSettings.SetMassPropertiesOverride(massProps);
            creationSettings.SetLinearDamping(linearDamping);
            creationSettings.SetAngularDamping(angularDamping);
        }

        var bodyInterface = BodyInterface;
        var bodyId = bodyInterface.CreateAndAddBody(creationSettings, motionType == BodyMotionType.Static ? Activation.DontActivate : Activation.Activate);
        creationSettings.Dispose();

        var body = new PhysicsBody(this, bodyId, motionType, collider);
        lock (_bodiesLock)
        {
            _bodies.Add(body);
            _bodiesById[bodyId] = body;
        }

        return body;
    }

    /// <summary>
    /// Creates a dynamic or static box body.
    /// </summary>
    public PhysicsBody CreateBoxBody(
        in float3 position,
        in float3 halfExtents,
        BodyMotionType motionType = BodyMotionType.Dynamic,
        float mass = 1.0f,
        float friction = 0.5f,
        float restitution = 0.0f,
        float linearDamping = 0.05f,
        float angularDamping = 0.05f)
    {
        var collider = new BoxCollider(halfExtents);
        var body = CreateBody(collider, position, floatQuat.Identity, motionType, mass, linearDamping, angularDamping);
        body.Friction = friction;
        body.Restitution = restitution;
        return body;
    }

    /// <summary>
    /// Creates a dynamic or static sphere body.
    /// </summary>
    public PhysicsBody CreateSphereBody(
        in float3 position,
        float radius,
        BodyMotionType motionType = BodyMotionType.Dynamic,
        float mass = 1.0f,
        float friction = 0.5f,
        float restitution = 0.0f,
        float linearDamping = 0.15f,
        float angularDamping = 0.5f)
    {
        var collider = new SphereCollider(radius);
        var body = CreateBody(collider, position, floatQuat.Identity, motionType, mass, linearDamping, angularDamping);
        body.Friction = friction;
        body.Restitution = restitution;
        return body;
    }

    /// <summary>
    /// Creates a static mesh collider body (used for static cells and terrain).
    /// </summary>
    public PhysicsBody CreateMeshBody(in float3 position, ReadOnlySpan<float3> vertices, ReadOnlySpan<uint> indices)
    {
        var collider = new MeshCollider(vertices, indices);
        return CreateBody(collider, position, floatQuat.Identity, BodyMotionType.Static, 0.0f);
    }

    /// <summary>
    /// Removes a body from the simulation.
    /// </summary>
    internal void RemoveBody(PhysicsBody body)
    {
        lock (_bodiesLock)
        {
            if (_bodiesById.Remove(body.BodyId))
            {
                _bodies.Remove(body);
                BodyInterface.RemoveBody(body.BodyId);
                BodyInterface.DestroyBody(body.BodyId);
            }
        }
    }

    /// <summary>
    /// Translates all bodies in the simulation by the negative delta offset during a floating-origin rebase (Section 1.3, D16).
    /// </summary>
    /// <param name="deltaOffset">Offset applied to the camera in double precision.</param>
    public void ShiftOrigin(double deltaX, double deltaY, double deltaZ)
    {
        float3 offset = new(-(float)deltaX, -(float)deltaY, -(float)deltaZ);
        lock (_bodiesLock)
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                _bodies[i].Translate(offset);
            }
        }
    }

    /// <summary>
    /// Casts a ray into the physics world and retrieves the closest hit point.
    /// </summary>
    public RaycastHit Raycast(in float3 origin, in float3 direction, float maxDistance = 1000.0f)
    {
        Raycast(origin, direction, maxDistance, out var hit);
        return hit;
    }

    /// <summary>
    /// Casts a ray into the physics world and retrieves the closest hit point.
    /// </summary>
    public bool Raycast(in float3 origin, in float3 direction, float maxDistance, out RaycastHit hit)
    {
        hit = RaycastHit.Miss;
        if (_disposed) return false;

        float3 normDir = direction;
        float lenSq = (normDir.X * normDir.X) + (normDir.Y * normDir.Y) + (normDir.Z * normDir.Z);
        if (lenSq > 0.000001f)
        {
            float invLen = 1.0f / MathF.Sqrt(lenSq);
            normDir = new float3(normDir.X * invLen, normDir.Y * invLen, normDir.Z * invLen);
        }

        float3 rayVector = new(normDir.X * maxDistance, normDir.Y * maxDistance, normDir.Z * maxDistance);
        var ray = new RRayCast { Origin = origin, Direction = rayVector };
        var raySettings = RayCastSettings.Create();
        var hitResult = new RayCastResult();

        var query = _physicsSystem.GetNarrowPhaseQuery();
        bool hasHit = query.CastRayClosest(
            in ray,
            raySettings,
            out hitResult,
            default,
            default,
            default,
            default
        );
        raySettings.Dispose();

        if (hasHit && hitResult.Fraction <= 1.0f)
        {
            float hitFraction = hitResult.Fraction;
            float dist = maxDistance * hitFraction;
            float3 hitPos = new(
                origin.X + normDir.X * dist,
                origin.Y + normDir.Y * dist,
                origin.Z + normDir.Z * dist
            );

            uint hitBodyId = hitResult.BodyID;
            float3 hitNormal = new(0, 1, 0);
            if (hitBodyId != 0)
            {
                var bodyPos = BodyInterface.GetPosition(hitBodyId);
                float3 diff = new(hitPos.X - bodyPos.X, hitPos.Y - bodyPos.Y, hitPos.Z - bodyPos.Z);
                float diffLen = MathF.Sqrt(diff.X * diff.X + diff.Y * diff.Y + diff.Z * diff.Z);
                if (diffLen > 0.001f)
                    hitNormal = new float3(diff.X / diffLen, diff.Y / diffLen, diff.Z / diffLen);
            }

            hit = new RaycastHit(true, hitPos, hitNormal, dist, hitBodyId);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_bodiesLock)
            {
                var bi = BodyInterface;
                for (int i = 0; i < _bodies.Count; i++)
                {
                    bi.RemoveBody(_bodies[i].BodyId);
                    bi.DestroyBody(_bodies[i].BodyId);
                }
                _bodies.Clear();
                _bodiesById.Clear();
            }

            _physicsSystem.Dispose();
            _objectVsBroadPhaseFilter.Dispose();
            _objectLayerPairFilter.Dispose();
            _broadPhaseLayerTable.Dispose();
            _jobThreadPool.Dispose();
            _tempAllocator.Dispose();
            EventBus.Unsubscribe<OriginShiftedEvent>(_originShiftHandler);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
