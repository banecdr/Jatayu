/* =================================================================================================
 * FILE: CapsuleCollider.cs
 * LOCATION: src/Physics/Colliders/CapsuleCollider.cs
 * 
 * WHAT THIS FILE DOES:
 *   Capsule collision geometry defined by half-height of cylinder section and spherical cap radius.
 *   Standard collision shape for characters, players, and upright dynamic entities.
 * ================================================================================================= */

using Brutal.JoltApi;

namespace Jatayu.Physics.Colliders;

/// <summary>
/// Capsule collision geometry composed of a central cylinder and hemispherical caps.
/// Total height equals (2 * HalfHeightOfCylinder) + (2 * Radius).
/// </summary>
public sealed class CapsuleCollider : Collider
{
    /// <summary>
    /// Gets the half-height of the central cylindrical section in meters.
    /// </summary>
    public float HalfHeight { get; }

    /// <summary>
    /// Gets the radius of the capsule and hemispherical caps in meters.
    /// </summary>
    public float Radius { get; }

    /// <summary>
    /// Total vertical height of the capsule in meters.
    /// </summary>
    public float TotalHeight => (HalfHeight * 2.0f) + (Radius * 2.0f);

    /// <summary>
    /// Constructs a capsule collider.
    /// </summary>
    /// <param name="halfHeight">Half height of the cylindrical section.</param>
    /// <param name="radius">Radius of the caps and cylinder.</param>
    public CapsuleCollider(float halfHeight, float radius)
    {
        HalfHeight = halfHeight;
        Radius = radius;
        var settings = CapsuleShapeSettings.Create(halfHeight, radius);
        SetShape(settings.CreateShape());
        settings.Dispose();
    }

    /// <summary>
    /// Constructs a capsule collider from total character height and radius.
    /// </summary>
    public static CapsuleCollider FromTotalHeight(float totalHeight, float radius)
    {
        float cylinderHalfHeight = System.Math.Max(0.01f, (totalHeight - (2.0f * radius)) * 0.5f);
        return new CapsuleCollider(cylinderHalfHeight, radius);
    }
}
