// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: RigidBodyComponent.cs
// Role: Physics component binding an entity to a Jolt simulation body handle and motion configuration.
// ==================================================================================================

namespace Jatayu.Scene.Components;

/// <summary>
/// Physics motion type matching Jolt motion qualities.
/// </summary>
public enum BodyMotionType
{
    Static = 0,
    Kinematic = 1,
    Dynamic = 2
}

/// <summary>
/// Component binding an entity to an unmanaged Jolt physics body.
/// </summary>
public struct RigidBodyComponent
{
    /// <summary>
    /// Jolt Body ID handle (0 if not yet spawned in physics simulation).
    /// </summary>
    public ulong BodyId;

    /// <summary>
    /// Motion type of this body.
    /// </summary>
    public BodyMotionType MotionType;

    /// <summary>
    /// Mass in kilograms (ignored for Static bodies).
    /// </summary>
    public float Mass;

    /// <summary>
    /// Surface friction coefficient (0.0 to 1.0).
    /// </summary>
    public float Friction;

    /// <summary>
    /// Bounciness / restitution coefficient (0.0 to 1.0).
    /// </summary>
    public float Restitution;

    /// <summary>
    /// Whether this body is a trigger / sensor.
    /// </summary>
    public bool IsSensor;

    /// <summary>
    /// Constructs a rigid body component.
    /// </summary>
    public RigidBodyComponent(BodyMotionType motionType = BodyMotionType.Dynamic, float mass = 1.0f)
    {
        BodyId = 0;
        MotionType = motionType;
        Mass = mass;
        Friction = 0.5f;
        Restitution = 0.0f;
        IsSensor = false;
    }
}
