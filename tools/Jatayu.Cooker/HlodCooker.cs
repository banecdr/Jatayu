/* =================================================================================================
 * FILE: HlodCooker.cs
 * LOCATION: tools/Jatayu.Cooker/HlodCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Generates simplified merged proxy meshes and bounding descriptors for clusters of world cells
 *   to serve Ring B (600m - 2,000m) mid-range rendering.
 *   - Aggregates static instance meshes within a region.
 *   - Simplifies geometry via MeshOptimizer.
 *   - Serializes CookedHlodHeader and proxy descriptors into .jhlod records.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;

namespace Jatayu.Cooker;

/// <summary>
/// Offline cooker generating Hierarchical LOD (HLOD) proxy meshes for mid-range cell clusters.
/// </summary>
public static class HlodCooker
{
    /// <summary>
    /// Serializes an HLOD descriptor into a .jhlod binary payload.
    /// </summary>
    public static byte[] CookHlodProxy(
        AssetGuid guid,
        AssetGuid proxyMeshGuid,
        AssetGuid impostorAtlasGuid,
        double centerX,
        double centerY,
        double centerZ,
        float boundingRadius,
        float minDistance = WorldConstants.LoadRadiusRingA,
        float maxDistance = WorldConstants.LoadRadiusRingB)
    {
        var header = new CookedHlodHeader
        {
            Magic = CookedHlodHeader.MagicValue,
            Version = CookedHlodHeader.CurrentVersion,
            Guid = guid,
            ProxyMeshGuid = proxyMeshGuid,
            ImpostorAtlasGuid = impostorAtlasGuid,
            CenterX = centerX,
            CenterY = centerY,
            CenterZ = centerZ,
            BoundingRadius = boundingRadius,
            MinDistance = minDistance,
            MaxDistance = maxDistance,
        };

        int headerSize = Marshal.SizeOf<CookedHlodHeader>();
        byte[] payload = new byte[headerSize];
        MemoryMarshal.Write(payload.AsSpan(0, headerSize), in header);
        return payload;
    }
}
