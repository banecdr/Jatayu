/* =================================================================================================
 * FILE: SphereCollider.cs
 * LOCATION: src/Physics/Colliders/SphereCollider.cs
 * 
 * WHAT THIS FILE DOES:
 *   Spherical collision geometry defined by radius.
 * ================================================================================================= */

using Brutal.JoltApi;

namespace Jatayu.Physics.Colliders;

/// <summary>
/// Spherical collision geometry defined by a radius.
/// </summary>
public sealed class SphereCollider : Collider
{
    /// <summary>
    /// Gets the radius of the sphere in meters.
    /// </summary>
    public float Radius { get; }

    /// <summary>
    /// Constructs a sphere collider with the specified radius.
    /// </summary>
    public SphereCollider(float radius)
    {
        Radius = radius;
        var settings = SphereShapeSettings.Create(radius);
        SetShape(settings.CreateShape());
        settings.Dispose();
    }
}
