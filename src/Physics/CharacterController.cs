/* =================================================================================================
 * FILE: CharacterController.cs
 * LOCATION: src/Physics/CharacterController.cs
 * 
 * WHAT THIS FILE DOES:
 *   Kinematic virtual character controller wrapping Jolt's CharacterVirtual.
 *   Provides smooth player navigation, walking/sprinting velocities, gravity integration,
 *   jumping impulses, stair stepping, slope angle clamping, ground detection, and
 *   floating-origin coordinate translation.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;
using Jatayu.Core.Events;
using Jatayu.Physics.Colliders;

namespace Jatayu.Physics;

/// <summary>
/// Kinematic character controller powered by Jolt CharacterVirtual.
/// Provides stair stepping, slope limits, ground adherence, and jump physics.
/// </summary>
public sealed class CharacterController : IDisposable
{
    private readonly PhysicsEngine _engine;
    private readonly CharacterVirtual _character;
    private readonly CapsuleCollider _capsuleCollider;
    private TempAllocatorImpl _tempAllocator;
    private readonly Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private bool _disposed;

    // Movement configuration parameters
    public float WalkSpeed { get; set; } = 5.0f;
    public float SprintSpeed { get; set; } = 12.0f;
    public float JumpVelocity { get; set; } = 7.0f;
    public float MaxSlopeAngleDegrees { get; set; } = 50.0f;
    public float StepHeight { get; set; } = 0.35f;

    private float _verticalVelocity = 0.0f;
    private bool _jumpRequested = false;
    private bool _isJumping = false;

    /// <summary>
    /// Gets the current camera-relative position of the character controller.
    /// </summary>
    public float3 Position
    {
        get => _character.GetPosition();
        set => _character.SetPosition(value);
    }

    /// <summary>
    /// Gets the current position as System.Numerics.Vector3.
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
    /// Gets the orientation rotation of the character.
    /// </summary>
    public floatQuat Rotation
    {
        get => _character.GetRotation();
        set => _character.SetRotation(value);
    }

    /// <summary>
    /// Gets current linear velocity.
    /// </summary>
    public float3 LinearVelocity
    {
        get => _character.GetLinearVelocity();
        set => _character.SetLinearVelocity(value);
    }

    /// <summary>
    /// Gets current linear velocity as System.Numerics.Vector3.
    /// </summary>
    public Vector3 LinearVelocityVector3
    {
        get
        {
            var v = LinearVelocity;
            return new Vector3(v.X, v.Y, v.Z);
        }
    }

    /// <summary>
    /// True if the character is currently standing on a valid ground surface.
    /// </summary>
    public bool IsGrounded => _character.IsSupported();

    /// <summary>
    /// Gets the ground surface normal under the character.
    /// </summary>
    public float3 GroundNormal => _character.GetGroundNormal();

    /// <summary>
    /// Constructs a character controller at the specified world position.
    /// </summary>
    /// <param name="engine">The master physics engine.</param>
    /// <param name="initialPosition">Initial camera-relative position.</param>
    /// <param name="totalHeight">Total character height in meters (default 1.8m).</param>
    /// <param name="radius">Capsule radius in meters (default 0.3m).</param>
    public CharacterController(
        PhysicsEngine engine,
        in float3 initialPosition,
        float totalHeight = 1.8f,
        float radius = 0.3f)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        // Create character capsule
        _capsuleCollider = CapsuleCollider.FromTotalHeight(totalHeight, radius);

        // Temp allocator for character queries (1 MB)
        _tempAllocator = TempAllocatorImpl.Create((UIntPtr)(1024 * 1024));

        var settings = CharacterVirtualSettings.Create();
        settings.SetShape(_capsuleCollider.Shape);
        settings.SetMaxSlopeAngle(MaxSlopeAngleDegrees * (MathF.PI / 180.0f));
        settings.SetUp(new float3(0.0f, 1.0f, 0.0f));
        settings.SetMass(80.0f);
        settings.SetMaxStrength(2500.0f);
        settings.SetInnerBodyLayer(PhysicsLayers.Character);
        settings.SetPredictiveContactDistance(0.1f);
        settings.SetPenetrationRecoverySpeed(1.0f);

        _character = CharacterVirtual.Create(settings, initialPosition, floatQuat.Identity, _engine.System);
        settings.Dispose();

        _originShiftHandler = (in OriginShiftedEvent e) => Translate(new float3(-(float)e.DeltaX, -(float)e.DeltaY, -(float)e.DeltaZ));
        EventBus.Subscribe<OriginShiftedEvent>(_originShiftHandler);
    }

    /// <summary>
    /// Triggers an immediate jump impulse.
    /// </summary>
    public void Jump()
    {
        _jumpRequested = true;
        _isJumping = true;
        _verticalVelocity = JumpVelocity;
        var v = LinearVelocity;
        _character.SetLinearVelocity(new float3(v.X, _verticalVelocity, v.Z));
    }

    /// <summary>
    /// Updates the character controller movement and physics simulation step.
    /// </summary>
    /// <param name="deltaTime">Delta time in seconds.</param>
    /// <param name="moveInput">Normalized horizontal movement input vector (X=right, Z=forward).</param>
    /// <param name="sprint">Whether sprinting is active.</param>
    /// <param name="jump">Whether jump button was pressed this frame.</param>
    public void Update(float deltaTime, in Vector2 moveInput, bool sprint = false, bool jump = false)
    {
        if (_disposed || deltaTime <= 0.0f) return;

        bool grounded = _character.IsSupported();

        // 1. Horizontal movement velocity
        float speed = sprint ? SprintSpeed : WalkSpeed;
        float targetVx = moveInput.X * speed;
        float targetVz = moveInput.Y * speed;

        // 2. Vertical gravity and jumping
        float gravityY = _engine.Gravity.Y;
        if ((jump && grounded) || _jumpRequested)
        {
            _jumpRequested = false;
            _isJumping = true;
            _verticalVelocity = JumpVelocity;
        }
        else if (_isJumping)
        {
            _verticalVelocity += gravityY * deltaTime;
            // Landed back on ground after jump apex
            if (_verticalVelocity <= 0.0f && grounded)
            {
                _isJumping = false;
                _verticalVelocity = -1.0f;
            }
        }
        else if (grounded)
        {
            // Small downward force to stay glued to ground/steps
            _verticalVelocity = -1.0f;
        }
        else
        {
            _verticalVelocity += gravityY * deltaTime;
        }

        if (_verticalVelocity < -50.0f)
            _verticalVelocity = -50.0f; // Terminal velocity clamp

        float3 desiredVelocity = new(targetVx, _verticalVelocity, targetVz);
        _character.SetLinearVelocity(desiredVelocity);

        // 3. Update character physics
        _character.Update(
            deltaTime,
            _engine.Gravity,
            default,
            default,
            default,
            default,
            _tempAllocator
        );

        // 3b. Push dynamic rigid bodies contacted by virtual character
        var contacts = _character.GetActiveContacts();
        if (contacts.Length > 0)
        {
            var bi = _engine.BodyInterface;
            for (int i = 0; i < contacts.Length; i++)
            {
                ref readonly var c = ref contacts[i];
                if (c.MotionTypeB == MotionType.Dynamic)
                {
                    float pushMagnitude = MathF.Max(MathF.Abs(targetVx), MathF.Abs(targetVz)) * 30.0f;
                    if (pushMagnitude > 0.01f)
                    {
                        float3 pushImpulse = new(
                            -c.ContactNormal.X * pushMagnitude * deltaTime,
                            0f,
                            -c.ContactNormal.Z * pushMagnitude * deltaTime
                        );
                        bi.AddImpulse(c.BodyB, pushImpulse, c.Position);
                    }
                }
            }
        }

        // 4. Stair stepping & floor sticking (only when grounded and not airborne jumping)
        if (grounded && !_isJumping && (MathF.Abs(targetVx) > 0.01f || MathF.Abs(targetVz) > 0.01f))
        {
            float3 stepUp = new(0, StepHeight, 0);
            float3 stepForward = new(targetVx * deltaTime, 0, targetVz * deltaTime);
            float3 stepDownExtra = new(0, -StepHeight, 0);

            if (_character.CanWalkStairs(desiredVelocity))
            {
                _character.WalkStairs(
                    deltaTime,
                    stepUp,
                    stepForward,
                    stepForward,
                    stepDownExtra,
                    default,
                    default,
                    default,
                    default,
                    _tempAllocator
                );
            }

            _character.StickToFloor(
                new float3(0, -StepHeight * 1.5f, 0),
                default,
                default,
                default,
                default,
                _tempAllocator
            );
        }
    }

    /// <summary>
    /// Translates the character by the specified offset during a floating-origin rebase (Section 1.3).
    /// </summary>
    public void Translate(in float3 deltaOffset)
    {
        var pos = _character.GetPosition();
        pos = new float3(pos.X + deltaOffset.X, pos.Y + deltaOffset.Y, pos.Z + deltaOffset.Z);
        _character.SetPosition(pos);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _character.Dispose();
            _capsuleCollider.Dispose();
            _tempAllocator.Dispose();
            EventBus.Unsubscribe<OriginShiftedEvent>(_originShiftHandler);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
