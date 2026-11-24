/* =================================================================================================
 * FILE: HeightfieldCooker.cs
 * LOCATION: tools/Jatayu.Cooker/HeightfieldCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Processes 16-bit / floating-point heightmaps into cooked 256m terrain tile records (.jtile)
 *   and coarse height pyramids (.jtex/raw) for the Jatayu continuous clipmap terrain system.
 *   - Computes elevation ranges (MinHeight, MaxHeight).
 *   - Quantizes / encodes height values into 16-byte aligned binary payloads.
 *   - Generates procedural terrain tiles covering 2x2 cells (D15) for testing and cooker tooling.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;

namespace Jatayu.Cooker;

/// <summary>
/// Offline cooker processing heightfield elevation arrays into .jtile archives.
/// </summary>
public static class HeightfieldCooker
{
    public const uint DefaultSamplesPerTile = 65; // 65x65 vertices for 256m tile (4m quad spacing)

    /// <summary>
    /// Bakes raw elevation heights into a CookedTerrainTile binary payload (.jtile).
    /// </summary>
    public static byte[] CookTerrainTile(
        int tileX,
        int tileZ,
        uint samplesX,
        uint samplesZ,
        ReadOnlySpan<float> heights,
        AssetGuid normalMapGuid = default,
        AssetGuid splatMapGuid = default)
    {
        if (samplesX == 0 || samplesZ == 0)
            throw new ArgumentOutOfRangeException(nameof(samplesX), "Terrain sample dimensions must be positive.");

        ulong sampleCount = (ulong)samplesX * samplesZ;
        if (sampleCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(samplesX), "Terrain sample dimensions are too large.");
        if (sampleCount > (ulong)int.MaxValue / sizeof(float))
            throw new ArgumentOutOfRangeException(nameof(samplesX), "Terrain height payload is too large.");
        if (heights.Length != (int)sampleCount)
            throw new ArgumentException($"Height array length ({heights.Length}) does not match sample dimensions ({samplesX}x{samplesZ}).");

        // 1. Calculate elevation range
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        for (int i = 0; i < heights.Length; i++)
        {
            float h = heights[i];
            if (h < minHeight) minHeight = h;
            if (h > maxHeight) maxHeight = h;
        }

        if (heights.IsEmpty)
        {
            minHeight = 0f;
            maxHeight = 0f;
        }

        // Derive deterministic tile GUID
        ulong packedTile = ((ulong)(uint)tileX << 32) | (uint)tileZ;
        var guid = new AssetGuid(packedTile ^ 0x54494C455F54494CUL); // FNV salt

        int headerSize = Marshal.SizeOf<CookedTerrainTileHeader>();
        uint heightDataSize = (uint)(heights.Length * sizeof(float));

        var header = new CookedTerrainTileHeader
        {
            Magic = CookedTerrainTileHeader.MagicValue,
            Version = CookedTerrainTileHeader.CurrentVersion,
            Guid = guid,
            TileX = tileX,
            TileZ = tileZ,
            HeightSamplesX = samplesX,
            HeightSamplesZ = samplesZ,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            NormalMapGuid = normalMapGuid,
            SplatMapGuid = splatMapGuid,
            HeightDataOffset = (ulong)headerSize,
            HeightDataSize = heightDataSize,
        };

        byte[] payload = new byte[headerSize + (int)heightDataSize];

        // Write header
        MemoryMarshal.Write(payload.AsSpan(0, headerSize), in header);

        // Write height data
        Span<byte> dstHeights = payload.AsSpan(headerSize, (int)heightDataSize);
        MemoryMarshal.AsBytes(heights).CopyTo(dstHeights);

        return payload;
    }

    /// <summary>
    /// Generates a procedural rolling hills terrain tile with Perlin-style sinusoidal synthesis.
    /// </summary>
    public static byte[] GenerateProceduralTile(
        int tileX,
        int tileZ,
        uint samples = DefaultSamplesPerTile,
        float maxElevation = 45.0f)
    {
        if (samples < 2 || samples > 4096)
            throw new ArgumentOutOfRangeException(nameof(samples), "A terrain tile requires at least 2 samples per axis.");
        ulong sampleCount = (ulong)samples * samples;
        if (sampleCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(samples), "Terrain sample dimensions are too large.");

        var heights = new float[(int)sampleCount];
        float tileSize = WorldConstants.TileSize;
        float step = tileSize / (samples - 1);

        double baseWorldX = (double)tileX * tileSize;
        double baseWorldZ = (double)tileZ * tileSize;

        for (uint z = 0; z < samples; z++)
        {
            double wz = baseWorldZ + (z * step);
            for (uint x = 0; x < samples; x++)
            {
                double wx = baseWorldX + (x * step);

                // Multi-frequency sinusoidal terrain synthesis
                double h1 = Math.Sin(wx * 0.005) * Math.Cos(wz * 0.005) * maxElevation;
                double h2 = Math.Sin(wx * 0.015 + 1.2) * Math.Sin(wz * 0.015 + 0.8) * (maxElevation * 0.35);
                double h3 = Math.Cos(wx * 0.04 - 0.5) * Math.Sin(wz * 0.04 + 1.7) * (maxElevation * 0.1);

                float elevation = (float)(h1 + h2 + h3);
                heights[z * samples + x] = elevation;
            }
        }

        return CookTerrainTile(tileX, tileZ, samples, samples, heights);
    }
}
