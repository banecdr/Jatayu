/* =================================================================================================
 * FILE: AudioListener.cs
 * LOCATION: src/Audio/AudioListener.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents the 3D spatial audio receptor (ears) in the game world.
 *   Synchronizes listener position and orientation vectors (forward, up) with the active camera,
 *   supports master gain scaling, and handles floating-origin coordinate shifts.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.Numerics;
using Brutal.OpenAlApi;
using Jatayu.Renderer.Camera;

namespace Jatayu.Audio;

/// <summary>
/// Singleton/central audio receptor representing the player's ears in 3D space.
/// </summary>
public sealed class AudioListener
{
    private float3 _position = float3.Zero;
    private float3 _velocity = float3.Zero;
    private float3 _forward = new(0, 0, -1);
    private float3 _up = new(0, 1, 0);
    private float _gain = 1.0f;

    /// <summary>
    /// Gets or sets the camera-relative position of the listener.
    /// </summary>
    public float3 Position
    {
        get => _position;
        set
        {
            _position = value;
            Al.Listener.Position = value;
        }
    }

    /// <summary>
    /// Gets or sets the position as System.Numerics.Vector3.
    /// </summary>
    public Vector3 PositionVector3
    {
        get => new(_position.X, _position.Y, _position.Z);
        set => Position = new float3(value.X, value.Y, value.Z);
    }

    /// <summary>
    /// Gets or sets the velocity of the listener for Doppler shift calculations.
    /// </summary>
    public float3 Velocity
    {
        get => _velocity;
        set
        {
            _velocity = value;
            Al.Listener.Velocity = value;
        }
    }

    /// <summary>
    /// Forward orientation vector.
    /// </summary>
    public float3 Forward => _forward;

    /// <summary>
    /// Up orientation vector.
    /// </summary>
    public float3 Up => _up;

    /// <summary>
    /// Gets or sets the master listener volume gain.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            _gain = MathF.Max(0.0f, value);
            Al.Listener.Gain = _gain;
        }
    }

    /// <summary>
    /// Updates listener position and orientation vectors.
    /// </summary>
    public void Update(in float3 position, in float3 forward, in float3 up)
    {
        Position = position;
        SetOrientation(forward, up);
    }

    /// <summary>
    /// Constructs the audio listener and initializes OpenAL listener defaults.
    /// </summary>
    public AudioListener()
    {
        Al.Listener.Gain = 1.0f;
        Al.Listener.Position = float3.Zero;
        Al.Listener.Velocity = float3.Zero;
        Al.Listener.Orientation = (_forward, _up);
    }

    /// <summary>
    /// Updates the listener orientation from 3D forward and up vectors.
    /// </summary>
    public void SetOrientation(in float3 forward, in float3 up)
    {
        _forward = forward;
        _up = up;
        Al.Listener.Orientation = (_forward, _up);
    }

    /// <summary>
    /// Synchronizes listener position and orientation with the active 3D camera.
    /// </summary>
    public void UpdateFromCamera(Camera3D camera)
    {
        if (camera == null) return;

        // Camera position in camera-relative rendering space is origin (0, 0, 0)
        Position = float3.Zero;

        // Camera forward and up direction vectors
        var fwd = camera.Forward;
        var up = camera.Up;

        SetOrientation(
            new float3(fwd.X, fwd.Y, fwd.Z),
            new float3(up.X, up.Y, up.Z)
        );
    }

    /// <summary>
    /// Translates the listener position during a floating-origin rebase (Section 1.3).
    /// </summary>
    public void Translate(in float3 deltaOffset)
    {
        Position = new float3(_position.X + deltaOffset.X, _position.Y + deltaOffset.Y, _position.Z + deltaOffset.Z);
    }
}
