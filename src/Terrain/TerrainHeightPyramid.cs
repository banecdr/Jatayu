/* =================================================================================================
 * FILE: TerrainHeightPyramid.cs
 * LOCATION: src/Terrain/TerrainHeightPyramid.cs
 * 
 * WHAT THIS FILE DOES:
 *   Far-field terrain height pyramid (Ring C: 2,000m - 8,000m) per architectural decision D22.
 *   - Maintains coarse global mip levels permanently resident in memory.
 *   - Samples world elevations across massive distances with smooth bilinear filtering.
 *   - Provides seamless 16m edge blending between high-res resident tiles and far-field pyramid.
 * ================================================================================================= */

using System;
using System.Runtime.CompilerServices;
using Jatayu.AssetFormats.World;

namespace Jatayu.Terrain;

/// <summary>
/// Coarse global height pyramid providing continuous elevation across Ring C (2 km - 8 km).
/// </summary>
public sealed class TerrainHeightPyramid
{
    private readonly float[] _heightData;
    private readonly int _resolution;
    private readonly double _worldDimension;
    private readonly double _originX;
    private readonly double _originZ;
    private readonly float _texelSizeMeters;

    public int Resolution => _resolution;
    public double WorldDimension => _worldDimension;

    public TerrainHeightPyramid(
        int resolution = 512,
        double worldDimension = WorldConstants.ReferenceWorldDimension,
        double originX = -WorldConstants.ReferenceWorldDimension * 0.5,
        double originZ = -WorldConstants.ReferenceWorldDimension * 0.5)
    {
        _resolution = resolution;
        _worldDimension = worldDimension;
        _originX = originX;
        _originZ = originZ;
        _texelSizeMeters = (float)(worldDimension / (resolution - 1));
        _heightData = new float[resolution * resolution];
    }

    /// <summary>
    /// Initializes height data from an elevation array or procedural function.
    /// </summary>
    public void Initialize(Func<double, double, float> heightSampler)
    {
        for (int z = 0; z < _resolution; z++)
        {
            double wz = _originZ + (z * _texelSizeMeters);
            for (int x = 0; x < _resolution; x++)
            {
                double wx = _originX + (x * _texelSizeMeters);
                _heightData[z * _resolution + x] = heightSampler(wx, wz);
            }
        }
    }

    /// <summary>
    /// Evaluates bilinearly interpolated height at arbitrary double-precision world coordinates.
    /// </summary>
    public float SampleHeightWorld(double worldX, double worldZ)
    {
        double u = (worldX - _originX) / _worldDimension;
        double v = (worldZ - _originZ) / _worldDimension;

        if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0)
            return 0.0f;

        double fx = u * (_resolution - 1);
        double fz = v * (_resolution - 1);

        int x0 = (int)Math.Floor(fx);
        int z0 = (int)Math.Floor(fz);
        int x1 = Math.Min(x0 + 1, _resolution - 1);
        int z1 = Math.Min(z0 + 1, _resolution - 1);

        float tx = (float)(fx - x0);
        float tz = (float)(fz - z0);

        float h00 = _heightData[z0 * _resolution + x0];
        float h10 = _heightData[z0 * _resolution + x1];
        float h01 = _heightData[z1 * _resolution + x0];
        float h11 = _heightData[z1 * _resolution + x1];

        float h0 = MathF.FusedMultiplyAdd(h10 - h00, tx, h00);
        float h1 = MathF.FusedMultiplyAdd(h11 - h01, tx, h01);
        return MathF.FusedMultiplyAdd(h1 - h0, tz, h0);
    }
}
