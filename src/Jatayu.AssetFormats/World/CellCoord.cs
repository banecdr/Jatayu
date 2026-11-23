/* =================================================================================================
 * FILE: CellCoord.cs
 * LOCATION: src/Jatayu.AssetFormats/World/CellCoord.cs
 * 
 * WHAT THIS FILE DOES:
 *   Represents an integer 2D coordinate pair (X, Z) identifying a 128m world streaming cell.
 *   Provides fast 64-bit packing, FNV-1a deterministic AssetGuid derivation, Chebyshev/Manhattan/
 *   Euclidean distance metrics, 4-way and 8-way neighbor enumeration, and conversions between
 *   double-precision world coordinates and discrete cell coordinates.
 * ================================================================================================= */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Jatayu.AssetFormats.World;

/// <summary>
/// A 2D integer coordinate pair (X, Z) representing a streaming world cell.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct CellCoord : IEquatable<CellCoord>, IComparable<CellCoord>
{
    /// <summary>
    /// Discrete X grid index.
    /// </summary>
    public readonly int X;

    /// <summary>
    /// Discrete Z grid index.
    /// </summary>
    public readonly int Z;

    /// <summary>
    /// Origin coordinate (0, 0).
    /// </summary>
    public static readonly CellCoord Zero = new(0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CellCoord(int x, int z)
    {
        X = x;
        Z = z;
    }

    /// <summary>
    /// Packs this coordinate into a single 64-bit unsigned integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ToPackedUlong()
    {
        return ((ulong)(uint)X << 32) | (uint)Z;
    }

    /// <summary>
    /// Unpacks a 64-bit unsigned integer into a CellCoord.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CellCoord FromPackedUlong(ulong packed)
    {
        int x = (int)(packed >> 32);
        int z = (int)(packed & 0xFFFFFFFF);
        return new CellCoord(x, z);
    }

    /// <summary>
    /// Derives a deterministic 64-bit AssetGuid for this cell coordinate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AssetGuid ToAssetGuid()
    {
        // FNV-1a 64-bit hash over the packed coordinate bytes
        ulong hash = 14695981039346656037UL;
        ulong packed = ToPackedUlong();

        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)(packed >> (i * 8));
            hash ^= b;
            hash *= 1099511628211UL;
        }

        // Reserve high bit or avoid 0 to guarantee non-empty GUID
        if (hash == 0) hash = 1;
        return new AssetGuid(hash);
    }

    /// <summary>
    /// Computes the CellCoord containing the given continuous double-precision world position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CellCoord FromWorld(double worldX, double worldZ, float cellSize = 128.0f)
    {
        int cx = (int)Math.Floor(worldX / cellSize);
        int cz = (int)Math.Floor(worldZ / cellSize);
        return new CellCoord(cx, cz);
    }

    /// <summary>
    /// Computes the minimum (corner) world coordinates for this cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetWorldOrigin(float cellSize, out double originX, out double originZ)
    {
        originX = (double)X * cellSize;
        originZ = (double)Z * cellSize;
    }

    /// <summary>
    /// Computes the center world coordinates for this cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetWorldCenter(float cellSize, out double centerX, out double centerZ)
    {
        centerX = ((double)X + 0.5) * cellSize;
        centerZ = ((double)Z + 0.5) * cellSize;
    }

    /// <summary>
    /// Computes Chebyshev distance (maximum coordinate difference) to another cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ChebyshevDistance(in CellCoord other)
    {
        return Math.Max(Math.Abs(X - other.X), Math.Abs(Z - other.Z));
    }

    /// <summary>
    /// Computes Manhattan distance (|ΔX| + |ΔZ|) to another cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ManhattanDistance(in CellCoord other)
    {
        return Math.Abs(X - other.X) + Math.Abs(Z - other.Z);
    }

    /// <summary>
    /// Computes squared Euclidean distance in grid space.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DistanceSquared(in CellCoord other)
    {
        int dx = X - other.X;
        int dz = Z - other.Z;
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// Computes Euclidean distance in grid space.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Distance(in CellCoord other)
    {
        return MathF.Sqrt(DistanceSquared(in other));
    }

    /// <summary>
    /// Computes Euclidean distance in meters from the center of this cell to continuous world coordinates.
    /// </summary>
    public double DistanceToWorld(double worldX, double worldZ, float cellSize = 128.0f)
    {
        GetWorldCenter(cellSize, out double cx, out double cz);
        double dx = cx - worldX;
        double dz = cz - worldZ;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Computes squared Euclidean distance in meters from the center of this cell to continuous world coordinates.
    /// </summary>
    public double DistanceSquaredToWorld(double worldX, double worldZ, float cellSize = 128.0f)
    {
        GetWorldCenter(cellSize, out double cx, out double cz);
        double dx = cx - worldX;
        double dz = cz - worldZ;
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// Returns the 8 adjacent neighbor coordinates without heap allocation.
    /// </summary>
    public NeighborEnumerator GetNeighbors() => new(this);

    public struct NeighborEnumerator
    {
        private readonly CellCoord _center;
        private int _index;
        private CellCoord _current;

        public NeighborEnumerator(in CellCoord center)
        {
            _center = center;
            _index = -1;
            _current = default;
        }

        public readonly CellCoord Current => _current;

        public bool MoveNext()
        {
            _index++;
            if (_index == 4) // Skip center (0, 0)
                _index++;

            if (_index >= 9)
                return false;

            int dx = (_index % 3) - 1;
            int dz = (_index / 3) - 1;
            _current = new CellCoord(_center.X + dx, _center.Z + dz);
            return true;
        }

        public readonly NeighborEnumerator GetEnumerator() => this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(CellCoord other) => X == other.X && Z == other.Z;

    public override bool Equals(object? obj) => obj is CellCoord other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => unchecked((X * 73856093) ^ (Z * 19349663));

    public int CompareTo(CellCoord other)
    {
        int cmp = Z.CompareTo(other.Z);
        return cmp != 0 ? cmp : X.CompareTo(other.X);
    }

    public override string ToString() => $"({X}, {Z})";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(CellCoord left, CellCoord right) => left.Equals(right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(CellCoord left, CellCoord right) => !left.Equals(right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CellCoord operator +(CellCoord left, CellCoord right) => new(left.X + right.X, left.Z + right.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CellCoord operator -(CellCoord left, CellCoord right) => new(left.X - right.X, left.Z - right.Z);
}
