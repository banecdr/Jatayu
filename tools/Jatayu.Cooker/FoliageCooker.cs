/* =================================================================================================
 * FILE: FoliageCooker.cs
 * LOCATION: tools/Jatayu.Cooker/FoliageCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Bakes procedural foliage instances (grass blades, shrubs) for a 128m world cell.
 *   Uses seeded jittered grid distribution, samples height and slope, and outputs packed
 *   16-byte FoliageInstanceData records for fast streaming and GPU instancing.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using Jatayu.AssetFormats.World;

namespace Jatayu.Cooker;

/// <summary>
/// Bakes procedural foliage instances for a 128m cell based on terrain height and slope.
/// </summary>
public static class FoliageCooker
{
    /// <summary>
    /// Generates a set of packed foliage instance records for the given cell coordinates.
    /// </summary>
    public static FoliageInstanceData[] GenerateCellFoliage(
        CellCoord coord,
        Func<float, float, float> heightSampler,
        int instancesPerAxis = 32, // 32x32 = 1,024 instances per 128m cell
        float maxSlopeRadians = 0.785f) // 45 degrees max slope
    {
        ArgumentNullException.ThrowIfNull(heightSampler);

        int totalExpected = instancesPerAxis * instancesPerAxis;
        var result = new List<FoliageInstanceData>(totalExpected);

        // Deterministic pseudo-random seed based on cell coordinates
        int seed = (coord.X * 73856093) ^ (coord.Z * 19349663);
        var rng = new Random(seed);

        float step = WorldConstants.CellSize / instancesPerAxis;

        for (int z = 0; z < instancesPerAxis; z++)
        {
            for (int x = 0; x < instancesPerAxis; x++)
            {
                // Jittered grid placement within the cell
                float localX = (x + (float)rng.NextDouble()) * step;
                float localZ = (z + (float)rng.NextDouble()) * step;

                coord.GetWorldCenter(WorldConstants.CellSize, out double cellOriginX, out double cellOriginZ);
                float worldX = (float)(cellOriginX - (WorldConstants.CellSize * 0.5f) + localX);
                float worldZ = (float)(cellOriginZ - (WorldConstants.CellSize * 0.5f) + localZ);

                float h = heightSampler(worldX, worldZ);

                // Sample neighboring heights to compute slope
                float hR = heightSampler(worldX + 1.0f, worldZ);
                float hU = heightSampler(worldX, worldZ + 1.0f);
                float slopeX = MathF.Abs(hR - h);
                float slopeZ = MathF.Abs(hU - h);
                float slope = MathF.Atan(MathF.Sqrt(slopeX * slopeX + slopeZ * slopeZ));

                // Reject foliage on steep cliff faces
                if (slope > maxSlopeRadians)
                {
                    continue;
                }

                float rot = (float)(rng.NextDouble() * Math.PI * 2.0);
                float scale = 0.75f + (float)rng.NextDouble() * 0.5f; // [0.75, 1.25]
                byte tint = (byte)rng.Next(0, 256);
                byte windPhase = (byte)rng.Next(0, 256);

                var pos = new Vector3(localX, h, localZ);
                result.Add(new FoliageInstanceData(pos, rot, scale, tint, windPhase));
            }
        }

        return result.ToArray();
    }
}
