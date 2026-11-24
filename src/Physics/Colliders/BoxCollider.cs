/* =================================================================================================
 * FILE: BoxCollider.cs
 * LOCATION: src/Physics/Colliders/BoxCollider.cs
 * 
 * WHAT THIS FILE DOES:
 *   Axis-aligned / oriented box collision geometry defined by half-extents.
 * ================================================================================================= */

using System.Numerics;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace Jatayu.Physics.Colliders;

/// <summary>
/// Box collision geometry defined by half-extents along the X, Y, and Z axes.
/// </summary>
public sealed class BoxCollider : Collider
{
    /// <summary>
    /// Gets the half-extents of the box.
    /// </summary>
    public float3 HalfExtents { get; }

    /// <summary>
    /// Constructs a box collider from half-extents.
    /// </summary>
    /// <param name="halfExtents">Half dimensions of the box along each axis.</param>
    /// <param name="convexRadius">Convex radius for edge rounding to prevent snagging (default 0.05m).</param>
    public BoxCollider(float3 halfExtents, float convexRadius = 0.05f)
    {
        HalfExtents = halfExtents;
        var settings = BoxShapeSettings.Create(halfExtents, convexRadius);
        SetShape(settings.CreateShape());
        settings.Dispose();
    }

    /// <summary>
    /// Constructs a box collider from full size dimensions.
    /// </summary>
    public static BoxCollider FromSize(float sizeX, float sizeY, float sizeZ, float convexRadius = 0.05f)
    {
        return new BoxCollider(new float3(sizeX * 0.5f, sizeY * 0.5f, sizeZ * 0.5f), convexRadius);
    }

    /// <summary>
    /// Constructs a box collider from System.Numerics.Vector3 half extents.
    /// </summary>
    public BoxCollider(Vector3 halfExtents, float convexRadius = 0.05f)
        : this(new float3(halfExtents.X, halfExtents.Y, halfExtents.Z), convexRadius)
    {
    }
}
