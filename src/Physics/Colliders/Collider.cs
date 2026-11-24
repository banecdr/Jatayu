/* =================================================================================================
 * FILE: Collider.cs
 * LOCATION: src/Physics/Colliders/Collider.cs
 * 
 * WHAT THIS FILE DOES:
 *   Abstract base class for all physics collision shapes. Holds the underlying Jolt Shape handle,
 *   local offset, rotation, and lifecycle management.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace Jatayu.Physics.Colliders;

/// <summary>
/// Abstract base class for physics collision geometries.
/// </summary>
public abstract class Collider : IDisposable
{
    private Shape _shape;
    private bool _disposed;

    /// <summary>
    /// Gets the compiled native Jolt shape handle.
    /// </summary>
    public Shape Shape => _shape;

    /// <summary>
    /// Gets or sets the local center-of-mass offset of this collider relative to the parent body.
    /// </summary>
    public float3 Offset { get; set; } = float3.Zero;

    /// <summary>
    /// Gets or sets the local rotation of this collider relative to the parent body.
    /// </summary>
    public floatQuat Rotation { get; set; } = floatQuat.Identity;

    /// <summary>
    /// True if the underlying shape has been successfully created.
    /// </summary>
    public bool IsValid => !_shape.IsNull();

    protected Collider()
    {
    }

    protected void SetShape(Shape shape)
    {
        _shape = shape;
    }

    public virtual void Dispose()
    {
        if (!_disposed)
        {
            if (!_shape.IsNull())
            {
                _shape.Dispose();
                _shape = default;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
