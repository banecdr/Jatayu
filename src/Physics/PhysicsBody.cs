/* =================================================================================================
 * FILE: PhysicsBody.cs
 * LOCATION: src/Physics/PhysicsBody.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-level object-oriented wrapper for a Jolt 3D simulation rigid body (Static, Kinematic, Dynamic).
 *   Encapsulates body ID, velocity manipulation, force/impulse application, restitution, friction,
 *   activation state, and floating-origin translation.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;
using Jatayu.Physics.Colliders;
using Jatayu.Scene.Components;

namespace Jatayu.Physics;

/// <summary>
/// Managed wrapper for a Jolt 3D rigid body in the physics simulation.
/// </summary>
public sealed class PhysicsBody : IDisposable
{
    private readonly PhysicsEngine _engine;
    private readonly uint _bodyId;
    private readonly BodyMotionType _motionType;
    private Collider? _collider;
    private bool _disposed;
    private object? _userData;

    /// <summary>
    /// Gets the unique native Jolt Body ID.
    /// </summary>
    public uint BodyId => _bodyId;

    /// <summary>
    /// Gets the motion type (Static, Kinematic, Dynamic).
    /// </summary>
    public BodyMotionType MotionType => _motionType;

    /// <summary>
    /// Gets or sets user data associated with this body.
    /// </summary>
    public object? UserData
    {
        get => _userData;
        set => _userData = value;
    }

    /// <summary>
    /// Gets or sets the attached collider shape.
    /// </summary>
    public Collider? Collider => _collider;

    /// <summary>
    /// Gets or sets the camera-relative position of the body in simulation space.
    /// </summary>
    public float3 Position
    {
        get => _engine.BodyInterface.GetPosition(_bodyId);
        set => _engine.BodyInterface.SetPosition(_bodyId, value, Activation.Activate);
    }

    /// <summary>
    /// Gets or sets the camera-relative position as System.Numerics.Vector3.
    /// </summary>
    public Vector3 PositionVector3
    {
        get
        {
            var p = Position;
            return new Vector3(p.X, p.Y, p.Z);
        }
        set => Position = new float3(value.X, value.Y, value.Z);
    }

    /// <summary>
    /// Gets or sets the orientation rotation of the body.
    /// </summary>
    public floatQuat Rotation
    {
        get
        {
            _engine.BodyInterface.GetPositionAndRotation(_bodyId, out _, out var rot);
            return rot;
        }
        set
        {
            var pos = Position;
            _engine.BodyInterface.SetPositionAndRotation(_bodyId, pos, value, Activation.Activate);
        }
    }

    /// <summary>
    /// Gets or sets the linear velocity in meters per second.
    /// </summary>
    public float3 LinearVelocity
    {
        get => _engine.BodyInterface.GetLinearVelocity(_bodyId);
        set => _engine.BodyInterface.SetLinearVelocity(_bodyId, value);
    }

    /// <summary>
    /// Gets or sets the linear velocity as System.Numerics.Vector3.
    /// </summary>
    public Vector3 LinearVelocityVector3
    {
        get
        {
            var v = LinearVelocity;
            return new Vector3(v.X, v.Y, v.Z);
        }
        set => LinearVelocity = new float3(value.X, value.Y, value.Z);
    }

    /// <summary>
    /// Gets or sets the angular velocity in radians per second.
    /// </summary>
    public float3 AngularVelocity
    {
        get => _engine.BodyInterface.GetAngularVelocity(_bodyId);
        set => _engine.BodyInterface.SetAngularVelocity(_bodyId, value);
    }

    /// <summary>
    /// Gets or sets surface friction (0.0 to 1.0).
    /// </summary>
    public float Friction
    {
        get => _engine.BodyInterface.GetFriction(_bodyId);
        set => _engine.BodyInterface.SetFriction(_bodyId, value);
    }

    /// <summary>
    /// Gets or sets surface bounciness / restitution (0.0 to 1.0).
    /// </summary>
    public float Restitution
    {
        get => _engine.BodyInterface.GetRestitution(_bodyId);
        set => _engine.BodyInterface.SetRestitution(_bodyId, value);
    }

    /// <summary>
    /// Gets whether this body is active and participating in collision resolution.
    /// </summary>
    public bool IsActive => _engine.BodyInterface.IsActive(_bodyId);

    internal PhysicsBody(PhysicsEngine engine, uint bodyId, BodyMotionType motionType, Collider? collider)
    {
        _engine = engine;
        _bodyId = bodyId;
        _motionType = motionType;
        _collider = collider;
    }

    /// <summary>
    /// Activates the body so it simulates actively.
    /// </summary>
    public void Activate()
    {
        _engine.BodyInterface.ActivateBody(_bodyId);
    }

    /// <summary>
    /// Puts the body to sleep to conserve CPU time when at rest.
    /// </summary>
    public void Deactivate()
    {
        _engine.BodyInterface.DeactivateBody(_bodyId);
    }

    /// <summary>
    /// Adds an instantaneous linear velocity impulse to the body.
    /// </summary>
    public void AddImpulse(float3 impulse)
    {
        _engine.BodyInterface.AddLinearVelocity(_bodyId, impulse);
    }

    /// <summary>
    /// Adds an instantaneous linear velocity impulse (System.Numerics.Vector3).
    /// </summary>
    public void AddImpulse(Vector3 impulse)
    {
        AddImpulse(new float3(impulse.X, impulse.Y, impulse.Z));
    }

    /// <summary>
    /// Translates the body by the specified offset (used during floating-origin rebasing).
    /// </summary>
    public void Translate(float3 deltaOffset)
    {
        _engine.BodyInterface.GetPositionAndRotation(_bodyId, out var pos, out var rot);
        pos = new float3(pos.X + deltaOffset.X, pos.Y + deltaOffset.Y, pos.Z + deltaOffset.Z);
        _engine.BodyInterface.SetPositionAndRotationWhenChanged(_bodyId, pos, rot, Activation.DontActivate);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _engine.RemoveBody(this);
            _collider?.Dispose();
            _collider = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
