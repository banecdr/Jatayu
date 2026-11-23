/* =================================================================================================
 * FILE: CellCooker.cs
 * LOCATION: tools/Jatayu.Cooker/CellCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Bakes world cells into binary chunk records (.jcell payloads) inside .jpak archives.
 *   Serializes CookedCellHeader, static instance transform arrays, physics colliders, and
 *   spatial audio points into contiguous, memory-mapped binary payloads.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;

namespace Jatayu.Cooker;

/// <summary>
/// Serializes cell records into binary chunk payloads for .jpak packages.
/// </summary>
public static class CellCooker
{
    /// <summary>
    /// Bakes cell metadata, static instance array, colliders, and audio points into a contiguous binary payload.
    /// </summary>
    public static byte[] CookCell(
        CellCoord coord,
        AssetGuid cellGuid,
        ReadOnlySpan<CookedStaticInstance> instances,
        ReadOnlySpan<CookedColliderRecord> colliders,
        ReadOnlySpan<CookedAudioPointRecord> audioPoints,
        CellFlags flags,
        float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ)
    {
        var header = CookedCellHeader.Create(
            coord,
            cellGuid,
            (uint)instances.Length,
            (uint)colliders.Length,
            (uint)audioPoints.Length,
            flags,
            minX, minY, minZ,
            maxX, maxY, maxZ
        );

        int headerSize = Marshal.SizeOf<CookedCellHeader>();
        int instanceSize = Marshal.SizeOf<CookedStaticInstance>();
        int colliderSize = Marshal.SizeOf<CookedColliderRecord>();
        int audioSize = Marshal.SizeOf<CookedAudioPointRecord>();

        int totalSize = headerSize +
                        (instances.Length * instanceSize) +
                        (colliders.Length * colliderSize) +
                        (audioPoints.Length * audioSize);

        byte[] payload = new byte[totalSize];

        // 1. Write Header
        Span<byte> headerBytes = payload.AsSpan(0, headerSize);
        MemoryMarshal.Write(headerBytes, in header);

        // 2. Write Static Instances
        if (instances.Length > 0)
        {
            Span<byte> instanceDst = payload.AsSpan((int)header.StaticInstancesOffset, instances.Length * instanceSize);
            MemoryMarshal.AsBytes(instances).CopyTo(instanceDst);
        }

        // 3. Write Colliders
        if (colliders.Length > 0)
        {
            Span<byte> colliderDst = payload.AsSpan((int)header.CollidersOffset, colliders.Length * colliderSize);
            MemoryMarshal.AsBytes(colliders).CopyTo(colliderDst);
        }

        // 4. Write Audio Points
        if (audioPoints.Length > 0)
        {
            Span<byte> audioDst = payload.AsSpan((int)header.AudioEmittersOffset, audioPoints.Length * audioSize);
            MemoryMarshal.AsBytes(audioPoints).CopyTo(audioDst);
        }

        return payload;
    }

    /// <summary>
    /// Automatically computes bounding box extents and bakes a cell with static instances.
    /// </summary>
    public static byte[] CookCellWithInstances(
        CellCoord coord,
        ReadOnlySpan<CookedStaticInstance> instances,
        ReadOnlySpan<CookedColliderRecord> colliders = default,
        ReadOnlySpan<CookedAudioPointRecord> audioPoints = default,
        CellFlags flags = CellFlags.None)
    {
        AssetGuid cellGuid = coord.ToAssetGuid();

        float minX = 0f, minY = 0f, minZ = 0f;
        float maxX = WorldConstants.CellSize;
        float maxY = 50f;
        float maxZ = WorldConstants.CellSize;

        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var inst = ref instances[i];
            float r = inst.BoundingRadius;
            minX = MathF.Min(minX, inst.PosX - r);
            minY = MathF.Min(minY, inst.PosY - r);
            minZ = MathF.Min(minZ, inst.PosZ - r);
            maxX = MathF.Max(maxX, inst.PosX + r);
            maxY = MathF.Max(maxY, inst.PosY + r);
            maxZ = MathF.Max(maxZ, inst.PosZ + r);
        }

        return CookCell(coord, cellGuid, instances, colliders, audioPoints, flags, minX, minY, minZ, maxX, maxY, maxZ);
    }
}
