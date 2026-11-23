/* =================================================================================================
 * FILE: TerrainHeightPyramidTests.cs
 * LOCATION: tests/Jatayu.Tests/TerrainHeightPyramidTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating far-field TerrainHeightPyramid elevation sampling and world boundary handling.
 * ================================================================================================= */

using System;
using Jatayu.Terrain;
using Xunit;

namespace Jatayu.Tests;

public class TerrainHeightPyramidTests
{
    [Fact]
    public void HeightPyramid_InitializesAndSamplesContinuousElevation()
    {
        // 64x64 grid spanning 1000m x 1000m from (0, 0)
        var pyramid = new TerrainHeightPyramid(
            resolution: 64,
            worldDimension: 1000.0,
            originX: 0.0,
            originZ: 0.0
        );

        // Simple linear elevation plane: H = X * 0.1 + Z * 0.2
        pyramid.Initialize((wx, wz) => (float)(wx * 0.1 + wz * 0.2));

        Assert.Equal(64, pyramid.Resolution);
        Assert.Equal(1000.0, pyramid.WorldDimension);

        // Sample at corners
        Assert.Equal(0f, pyramid.SampleHeightWorld(0.0, 0.0), precision: 2);
        Assert.Equal(100f, pyramid.SampleHeightWorld(1000.0, 0.0), precision: 2);
        Assert.Equal(200f, pyramid.SampleHeightWorld(0.0, 1000.0), precision: 2);
        Assert.Equal(300f, pyramid.SampleHeightWorld(1000.0, 1000.0), precision: 2);

        // Sample at center (500, 500) -> 50 + 100 = 150
        Assert.Equal(150f, pyramid.SampleHeightWorld(500.0, 500.0), precision: 2);
    }

    [Fact]
    public void HeightPyramid_OutOfBoundsCoordinates_ReturnsZeroWithoutThrowing()
    {
        var pyramid = new TerrainHeightPyramid(
            resolution: 32,
            worldDimension: 1000.0,
            originX: 0.0,
            originZ: 0.0
        );

        pyramid.Initialize((wx, wz) => 50.0f);

        Assert.Equal(0f, pyramid.SampleHeightWorld(-50.0, 500.0));
        Assert.Equal(0f, pyramid.SampleHeightWorld(1500.0, 500.0));
        Assert.Equal(0f, pyramid.SampleHeightWorld(500.0, -100.0));
        Assert.Equal(0f, pyramid.SampleHeightWorld(500.0, 1100.0));
    }
}
