// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: Transform.cs
// Role: Spatial transform component holding local float position/rotation/scale and double-precision world anchor.
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Jatayu.Scene;

/// <summary>
/// Transform component for spatial entities.
/// Stores local float coordinates (Position, Rotation, Scale) along with double-precision coordinates
/// for seamless integration with large-world floating-origin rebasing.
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    // Double precision absolute coordinates (Sprint 5b large-world simulation)
    public double WorldX;
    public double WorldY;
    public double WorldZ;

    /// <summary>
    /// Constructs a standard transform with default orientation and unit scale.
    /// </summary>
    public Transform(Vector3 position)
    {
        Position = position;
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
        WorldX = position.X;
        WorldY = position.Y;
        WorldZ = position.Z;
    }

    /// <summary>
    /// Constructs a transform with specified position, rotation, and scale.
    /// </summary>
    public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
        WorldX = position.X;
        WorldY = position.Y;
        WorldZ = position.Z;
    }

    /// <summary>
    /// Constructs a transform with double precision coordinates for large-world positions.
    /// </summary>
    public Transform(double worldX, double worldY, double worldZ)
    {
        WorldX = worldX;
        WorldY = worldY;
        WorldZ = worldZ;
        Position = new Vector3((float)worldX, (float)worldY, (float)worldZ);
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
    }

    /// <summary>
    /// Constructs a transform with double precision coordinates, rotation, and scale.
    /// </summary>
    public Transform(double worldX, double worldY, double worldZ, Quaternion rotation, Vector3 scale)
    {
        WorldX = worldX;
        WorldY = worldY;
        WorldZ = worldZ;
        Position = new Vector3((float)worldX, (float)worldY, (float)worldZ);
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>
    /// Computes the 4x4 affine local transformation matrix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Matrix4x4 ComputeMatrix()
    {
        return Matrix4x4.CreateScale(Scale) *
               Matrix4x4.CreateFromQuaternion(Rotation) *
               Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>
    /// Computes the forward unit vector along the local -Z axis (standard Vulkan/OpenGL forward).
    /// </summary>
    public readonly Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);

    /// <summary>
    /// Computes the up unit vector along the local +Y axis.
    /// </summary>
    public readonly Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>
    /// Computes the right unit vector along the local +X axis.
    /// </summary>
    public readonly Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);

    /// <summary>
    /// Default identity transform.
    /// </summary>
    public static Transform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}
