// ==================================================================================================
// Jatayu Engine - Core Large-World Math
// Module: WorldPosition.cs
// Role: High-precision double 3D vector and camera-relative float transformation primitives (Section 1.2).
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Jatayu.Core.Mathematics;

/// <summary>
/// High-precision 64-bit double floating-point 3D world coordinate.
/// Prevents millimeter-level vertex wobble and floating-point precision collapse
/// in massive coordinates out to 50,000 meters and beyond.
/// </summary>
public struct WorldPosition : IEquatable<WorldPosition>
{
    public double X;
    public double Y;
    public double Z;

    public WorldPosition(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public WorldPosition(Vector3 local)
    {
        X = local.X;
        Y = local.Y;
        Z = local.Z;
    }

    /// <summary>
    /// Computes the camera-relative float3 vector: P_rel = P_obj - P_cam (Section 1.2).
    /// Safe to pass to GPU vertex buffers and shader push constants without precision loss.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 ToCameraRelative(in WorldPosition cameraPosition)
    {
        return new Vector3(
            (float)(X - cameraPosition.X),
            (float)(Y - cameraPosition.Y),
            (float)(Z - cameraPosition.Z)
        );
    }

    /// <summary>
    /// Computes Euclidean distance to another world position in double precision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double DistanceTo(in WorldPosition other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        double dz = Z - other.Z;
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Computes squared Euclidean distance in double precision (avoids square root).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double DistanceSquaredTo(in WorldPosition other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        double dz = Z - other.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// Translates this world position by a double-precision offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Translate(double dx, double dy, double dz)
    {
        X += dx;
        Y += dy;
        Z += dz;
    }

    /// <summary>
    /// Casts this coordinate to standard 32-bit float vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

    public static WorldPosition operator +(in WorldPosition a, in WorldPosition b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static WorldPosition operator -(in WorldPosition a, in WorldPosition b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static WorldPosition operator +(in WorldPosition a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static WorldPosition operator -(in WorldPosition a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public readonly bool Equals(WorldPosition other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override readonly bool Equals(object? obj) => obj is WorldPosition other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(in WorldPosition left, in WorldPosition right) => left.Equals(right);
    public static bool operator !=(in WorldPosition left, in WorldPosition right) => !left.Equals(right);

    public override readonly string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";

    public static WorldPosition Zero => new(0.0, 0.0, 0.0);
}
