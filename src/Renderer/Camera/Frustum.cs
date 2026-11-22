// ==================================================================================================
// Jatayu Engine - 3D Camera & Visibility
// Module: Frustum.cs
// Role: 5-plane view frustum culler with configurable MaxDrawDistance clamp for Reverse-Z (Section 1.2).
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Jatayu.Renderer.Camera;

/// <summary>
/// Representation of a single 3D geometric clipping plane in Hessian normal form (Ax + By + Cz + D = 0).
/// </summary>
public struct Plane
{
    public Vector3 Normal;
    public float Distance;

    public Plane(float a, float b, float c, float d)
    {
        float length = MathF.Sqrt(a * a + b * b + c * c);
        if (length > 1e-6f)
        {
            float inv = 1.0f / length;
            Normal = new Vector3(a * inv, b * inv, c * inv);
            Distance = d * inv;
        }
        else
        {
            Normal = Vector3.UnitY;
            Distance = 0.0f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float DotCoordinate(in Vector3 point)
    {
        return Vector3.Dot(Normal, point) + Distance;
    }
}

/// <summary>
/// 5-plane frustum culler tailored for Reverse-Z infinite projections with MaxDrawDistance clamp (CL-05).
/// Extracts Left, Right, Bottom, Top, and Near planes from view-projection matrix.
/// </summary>
public sealed class Frustum
{
    public enum PlaneIndex
    {
        Left = 0,
        Right = 1,
        Bottom = 2,
        Top = 3,
        Near = 4,
        Count = 5
    }

    private readonly Plane[] _planes = new Plane[(int)PlaneIndex.Count];
    private float _maxDrawDistance = 8000.0f;
    private float _maxDrawDistanceSq = 8000.0f * 8000.0f;
    private Vector3 _cameraPosition;

    /// <summary>
    /// Gets or sets the maximum draw distance clamp in meters (default: 8,000m).
    /// </summary>
    public float MaxDrawDistance
    {
        get => _maxDrawDistance;
        set
        {
            _maxDrawDistance = MathF.Max(1.0f, value);
            _maxDrawDistanceSq = _maxDrawDistance * _maxDrawDistance;
        }
    }

    /// <summary>
    /// Constructs a Frustum from a Camera3D instance.
    /// </summary>
    public static Frustum FromCamera(Camera3D camera, float maxDrawDistance = 8000.0f)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var frustum = new Frustum { MaxDrawDistance = maxDrawDistance };
        frustum.Update(camera.ViewProjection, camera.Position);
        return frustum;
    }

    /// <summary>
    /// Updates the 5 frustum clipping planes from the combined View-Projection matrix using Gribb-Hartmann.
    /// Under Reverse-Z infinite projection, Near plane is extracted from M3 - M2 (or M2).
    /// </summary>
    public void Update(in Matrix4x4 viewProjection, in Vector3 cameraPosition)
    {
        _cameraPosition = cameraPosition;

        // Left plane: row 4 + row 1
        _planes[(int)PlaneIndex.Left] = new Plane(
            viewProjection.M14 + viewProjection.M11,
            viewProjection.M24 + viewProjection.M21,
            viewProjection.M34 + viewProjection.M31,
            viewProjection.M44 + viewProjection.M41
        );

        // Right plane: row 4 - row 1
        _planes[(int)PlaneIndex.Right] = new Plane(
            viewProjection.M14 - viewProjection.M11,
            viewProjection.M24 - viewProjection.M21,
            viewProjection.M34 - viewProjection.M31,
            viewProjection.M44 - viewProjection.M41
        );

        // Bottom plane: row 4 + row 2
        _planes[(int)PlaneIndex.Bottom] = new Plane(
            viewProjection.M14 + viewProjection.M12,
            viewProjection.M24 + viewProjection.M22,
            viewProjection.M34 + viewProjection.M32,
            viewProjection.M44 + viewProjection.M42
        );

        // Top plane: row 4 - row 2
        _planes[(int)PlaneIndex.Top] = new Plane(
            viewProjection.M14 - viewProjection.M12,
            viewProjection.M24 - viewProjection.M22,
            viewProjection.M34 - viewProjection.M32,
            viewProjection.M44 - viewProjection.M42
        );

        // Near plane: Under Vulkan Reverse-Z [0, 1] infinite projection (where near is 1.0):
        // Vulkan clip space 0 <= z <= w. Near plane is at z_ndc = 1.0 -> w - z >= 0 (row 4 - row 3)
        _planes[(int)PlaneIndex.Near] = new Plane(
            viewProjection.M14 - viewProjection.M13,
            viewProjection.M24 - viewProjection.M23,
            viewProjection.M34 - viewProjection.M33,
            viewProjection.M44 - viewProjection.M43
        );
    }

    /// <summary>
    /// Tests a bounding sphere against the 5 frustum planes and the MaxDrawDistance clamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IntersectsSphere(in Vector3 center, float radius)
    {
        // 1. Far distance clamp
        float distSq = Vector3.DistanceSquared(center, _cameraPosition);
        float maxDistWithRadius = _maxDrawDistance + radius;
        if (distSq > maxDistWithRadius * maxDistWithRadius)
        {
            return false;
        }

        // 2. 5-plane rejection
        for (int i = 0; i < (int)PlaneIndex.Count; i++)
        {
            if (_planes[i].DotCoordinate(center) < -radius)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tests an axis-aligned bounding box (AABB) against the 5 frustum planes and the MaxDrawDistance clamp.
    /// </summary>
    public bool IntersectsAABB(in Vector3 min, in Vector3 max)
    {
        // 1. Far distance clamp on box center
        Vector3 center = (min + max) * 0.5f;
        Vector3 extents = (max - min) * 0.5f;
        float radius = extents.Length();

        float distSq = Vector3.DistanceSquared(center, _cameraPosition);
        float maxDistWithRadius = _maxDrawDistance + radius;
        if (distSq > maxDistWithRadius * maxDistWithRadius)
        {
            return false;
        }

        // 2. 5-plane AABB testing
        for (int i = 0; i < (int)PlaneIndex.Count; i++)
        {
            ref readonly var plane = ref _planes[i];
            Vector3 positiveVertex = new(
                plane.Normal.X >= 0 ? max.X : min.X,
                plane.Normal.Y >= 0 ? max.Y : min.Y,
                plane.Normal.Z >= 0 ? max.Z : min.Z
            );

            if (plane.DotCoordinate(positiveVertex) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a bounding sphere is visible inside the frustum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSphereVisible(in Vector3 center, float radius) => IntersectsSphere(center, radius);

    /// <summary>
    /// Checks if an axis-aligned bounding box is visible inside the frustum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsBoxVisible(in Vector3 min, in Vector3 max) => IntersectsAABB(min, max);
}
