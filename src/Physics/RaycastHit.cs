/* =================================================================================================
 * FILE: RaycastHit.cs
 * LOCATION: src/Physics/RaycastHit.cs
 * 
 * WHAT THIS FILE DOES:
 *   Raycast query result structure storing hit state, impact point, surface normal,
 *   hit fraction/distance, and the Jolt body ID of the impacted collider.
 * ================================================================================================= */

using System.Numerics;
using Brutal.Numerics;

namespace Jatayu.Physics;

/// <summary>
/// Result of a physics raycast query.
/// </summary>
public readonly struct RaycastHit
{
    /// <summary>
    /// True if the ray intersected a physics collider; false otherwise.
    /// </summary>
    public readonly bool HasHit;

    /// <summary>
    /// World position of the raycast contact point in camera-relative coordinates.
    /// </summary>
    public readonly float3 Position;

    /// <summary>
    /// Surface normal vector at the impact location (normalized).
    /// </summary>
    public readonly float3 Normal;

    /// <summary>
    /// Distance along the ray trajectory from the origin to the contact point.
    /// </summary>
    public readonly float Distance;

    /// <summary>
    /// Jolt simulation Body ID of the struck body (0 if no hit).
    /// </summary>
    public readonly uint BodyId;

    public RaycastHit(bool hasHit, float3 position, float3 normal, float distance, uint bodyId)
    {
        HasHit = hasHit;
        Position = position;
        Normal = normal;
        Distance = distance;
        BodyId = bodyId;
    }

    /// <summary>
    /// Empty miss result.
    /// </summary>
    public static readonly RaycastHit Miss = new(false, float3.Zero, new float3(0, 1, 0), float.MaxValue, 0);

    /// <summary>
    /// Gets the hit position as System.Numerics.Vector3.
    /// </summary>
    public Vector3 PositionVector3 => new(Position.X, Position.Y, Position.Z);

    /// <summary>
    /// Gets the surface normal as System.Numerics.Vector3.
    /// </summary>
    public Vector3 NormalVector3 => new(Normal.X, Normal.Y, Normal.Z);
}
