/* =================================================================================================
 * FILE: AssetGuid.cs
 * LOCATION: src/Jatayu.AssetFormats/AssetGuid.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-speed, 64-bit identifier (AssetGuid) for assets, textures, models, materials, and world cells.
 *   Provides ultra-fast FNV-1a 64-bit hashing from normalized asset paths, string parsing,
 *   formatting as 16-character hex strings, and zero-allocation hash comparisons.
 * ================================================================================================= */

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Jatayu.AssetFormats;

/// <summary>
/// A compact 64-bit unique identifier for cooked assets, materials, and world chunks.
/// </summary>
public readonly struct AssetGuid : IEquatable<AssetGuid>, IComparable<AssetGuid>
{
    /// <summary>
    /// Represents an unassigned or empty asset identifier (Value = 0).
    /// </summary>
    public static readonly AssetGuid Empty = default;

    /// <summary>
    /// The 64-bit integer payload representing this asset identifier.
    /// </summary>
    public readonly ulong Value;

    /// <summary>
    /// Initializes an AssetGuid from a 64-bit integer value.
    /// </summary>
    public AssetGuid(ulong value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets whether this GUID is valid (non-zero).
    /// </summary>
    public bool IsValid => Value != 0;

    /// <summary>
    /// Gets whether this GUID is empty (zero).
    /// </summary>
    public bool IsEmpty => Value == 0;

    /// <summary>
    /// Computes a deterministic 64-bit FNV-1a AssetGuid from a virtual asset path or identifier.
    /// Normalizes path separators to '/' and converts to lower-case.
    /// </summary>
    public static AssetGuid FromPath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return Empty;

        string normalized = assetPath.Replace('\\', '/').Trim().ToLowerInvariant();
        int byteCount = Encoding.UTF8.GetByteCount(normalized);

        Span<byte> utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(normalized, utf8);

        return FromSpan(utf8);
    }

    /// <summary>
    /// Computes a 64-bit FNV-1a hash directly from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AssetGuid FromSpan(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;

        ulong hash = fnvOffsetBasis;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= fnvPrime;
        }

        return new AssetGuid(hash == 0 ? 1UL : hash);
    }

    /// <summary>
    /// Derives an AssetGuid from a standard 128-bit System.Guid by XOR-folding high and low 64-bit words.
    /// </summary>
    public static AssetGuid FromGuid(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);

        ulong high = BitConverter.ToUInt64(bytes[..8]);
        ulong low = BitConverter.ToUInt64(bytes[8..]);

        ulong val = high ^ low;
        return new AssetGuid(val == 0 ? 1UL : val);
    }

    /// <summary>
    /// Parses a 16-character hexadecimal string into an AssetGuid.
    /// </summary>
    public static AssetGuid Parse(string hexString)
    {
        if (TryParse(hexString, out var guid))
            return guid;

        throw new FormatException($"Invalid AssetGuid hexadecimal string: '{hexString}'");
    }

    /// <summary>
    /// Attempts to parse a hexadecimal string into an AssetGuid.
    /// </summary>
    public static bool TryParse(string? hexString, out AssetGuid guid)
    {
        guid = Empty;
        if (string.IsNullOrWhiteSpace(hexString))
            return false;

        ReadOnlySpan<char> span = hexString.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];

        if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            guid = new AssetGuid(value);
            return true;
        }

        return false;
    }

    public bool Equals(AssetGuid other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is AssetGuid other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(AssetGuid other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("x16");

    public static implicit operator ulong(AssetGuid guid) => guid.Value;
    public static explicit operator AssetGuid(ulong value) => new(value);

    public static bool operator ==(AssetGuid left, AssetGuid right) => left.Value == right.Value;
    public static bool operator !=(AssetGuid left, AssetGuid right) => left.Value != right.Value;
    public static bool operator <(AssetGuid left, AssetGuid right) => left.Value < right.Value;
    public static bool operator <=(AssetGuid left, AssetGuid right) => left.Value <= right.Value;
    public static bool operator >(AssetGuid left, AssetGuid right) => left.Value > right.Value;
    public static bool operator >=(AssetGuid left, AssetGuid right) => left.Value >= right.Value;
}
