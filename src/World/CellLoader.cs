/* =================================================================================================
 * FILE: CellLoader.cs
 * LOCATION: src/World/CellLoader.cs
 * 
 * WHAT THIS FILE DOES:
 *   Asynchronous worker responsible for decoding cooked world cell records from .jpak archives.
 *   Extracts CookedCellHeader, static mesh instance transforms, physics colliders, and audio points
 *   directly from memory-mapped zero-copy spans.
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Assets;

namespace Jatayu.World;

/// <summary>
/// Intermediate decoded cell data payload produced by background loading jobs.
/// </summary>
public sealed class DecodedCellPayload
{
    public CellCoord Coord { get; }
    public CookedCellHeader Header { get; }
    public CookedStaticInstance[] Instances { get; }
    public CookedColliderRecord[] Colliders { get; }
    public CookedAudioPointRecord[] AudioEmitters { get; }

    public DecodedCellPayload(
        CellCoord coord,
        in CookedCellHeader header,
        CookedStaticInstance[] instances,
        CookedColliderRecord[] colliders,
        CookedAudioPointRecord[] audioEmitters)
    {
        Coord = coord;
        Header = header;
        Instances = instances;
        Colliders = colliders;
        AudioEmitters = audioEmitters;
    }
}

/// <summary>
/// High-speed decoder extracting world cell records from .jpak packages.
/// </summary>
public static class CellLoader
{
    /// <summary>
    /// Parses a raw byte span containing a .jcell payload into a DecodedCellPayload.
    /// </summary>
    public static bool TryDecodeCell(ReadOnlySpan<byte> data, out DecodedCellPayload? payload)
    {
        payload = null;
        int headerSize = Marshal.SizeOf<CookedCellHeader>();
        if (data.Length < headerSize)
            return false;

        var header = MemoryMarshal.Read<CookedCellHeader>(data[..headerSize]);
        if (!header.IsValid)
            return false;

        // Parse Static Instances
        CookedStaticInstance[] instances = Array.Empty<CookedStaticInstance>();
        if (header.StaticInstanceCount > 0)
        {
            int instanceSize = Marshal.SizeOf<CookedStaticInstance>();
            int offset = (int)header.StaticInstancesOffset;
            int totalBytes = (int)header.StaticInstanceCount * instanceSize;

            if (offset + totalBytes <= data.Length)
            {
                var instanceSpan = MemoryMarshal.Cast<byte, CookedStaticInstance>(data.Slice(offset, totalBytes));
                instances = instanceSpan.ToArray();
            }
        }

        // Parse Colliders
        CookedColliderRecord[] colliders = Array.Empty<CookedColliderRecord>();
        if (header.ColliderCount > 0)
        {
            int colliderSize = Marshal.SizeOf<CookedColliderRecord>();
            int offset = (int)header.CollidersOffset;
            int totalBytes = (int)header.ColliderCount * colliderSize;

            if (offset + totalBytes <= data.Length)
            {
                var colliderSpan = MemoryMarshal.Cast<byte, CookedColliderRecord>(data.Slice(offset, totalBytes));
                colliders = colliderSpan.ToArray();
            }
        }

        // Parse Audio Emitters
        CookedAudioPointRecord[] audioEmitters = Array.Empty<CookedAudioPointRecord>();
        if (header.AudioEmitterCount > 0)
        {
            int audioSize = Marshal.SizeOf<CookedAudioPointRecord>();
            int offset = (int)header.AudioEmittersOffset;
            int totalBytes = (int)header.AudioEmitterCount * audioSize;

            if (offset + totalBytes <= data.Length)
            {
                var audioSpan = MemoryMarshal.Cast<byte, CookedAudioPointRecord>(data.Slice(offset, totalBytes));
                audioEmitters = audioSpan.ToArray();
            }
        }

        payload = new DecodedCellPayload(header.Coord, in header, instances, colliders, audioEmitters);
        return true;
    }

    /// <summary>
    /// Decodes a cell record from a memory-mapped PackReader archive.
    /// </summary>
    public static bool TryLoadCellFromPack(PackReader packReader, CellCoord coord, out DecodedCellPayload? payload)
    {
        payload = null;
        var guid = coord.ToAssetGuid();
        if (!packReader.TryGetEntry(guid, out _))
            return false;

        var span = packReader.ReadAssetBytes(guid);
        return TryDecodeCell(span, out payload);
    }

    /// <summary>
    /// Applies a decoded cell payload onto an existing WorldCell instance.
    /// </summary>
    public static void ApplyPayloadToCell(WorldCell cell, DecodedCellPayload payload)
    {
        cell.Header = payload.Header;
        cell.StaticInstances = payload.Instances;
        cell.Colliders = payload.Colliders;
        cell.AudioEmitters = payload.AudioEmitters;
        cell.State = CellState.Active;
    }
}
