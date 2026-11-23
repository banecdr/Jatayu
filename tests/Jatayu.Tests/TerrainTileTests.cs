/* =================================================================================================
 * FILE: TerrainTileTests.cs
 * LOCATION: tests/Jatayu.Tests/TerrainTileTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating Sprint 6b Terrain Tile coordinate math (D15), bilinear elevation
 *   interpolation, origin shifting, and binary .jtile encoding/decoding.
 * ================================================================================================= */

using System;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Cooker;
using Jatayu.Core.Mathematics;
using Jatayu.Terrain;
using Xunit;

namespace Jatayu.Tests;

public class TerrainTileTests
{
    [Fact]
    public void TileCoord_FromCell_Maps2x2CellsToTile()
    {
        // 2x2 cells map to 1 tile (TileCoord = CellCoord / 2)
        var c00 = new CellCoord(0, 0);
        var c10 = new CellCoord(1, 0);
        var c01 = new CellCoord(0, 1);
        var c11 = new CellCoord(1, 1);

        var t00 = TileCoord.FromCell(in c00);
        var t10 = TileCoord.FromCell(in c10);
        var t01 = TileCoord.FromCell(in c01);
        var t11 = TileCoord.FromCell(in c11);

        Assert.Equal(new TileCoord(0, 0), t00);
        Assert.Equal(new TileCoord(0, 0), t10);
        Assert.Equal(new TileCoord(0, 0), t01);
        Assert.Equal(new TileCoord(0, 0), t11);

        var c23 = new CellCoord(2, 3);
        var t23 = TileCoord.FromCell(in c23);
        Assert.Equal(new TileCoord(1, 1), t23);

        var cNeg = new CellCoord(-1, -2);
        var tNeg = TileCoord.FromCell(in cNeg);
        Assert.Equal(new TileCoord(-1, -1), tNeg);
    }

    [Fact]
    public void TileCoord_FromWorld_MatchesTileSize()
    {
        var t1 = TileCoord.FromWorld(0.0, 0.0);
        Assert.Equal(new TileCoord(0, 0), t1);

        var t2 = TileCoord.FromWorld(255.9, 511.9);
        Assert.Equal(new TileCoord(0, 1), t2);

        var t3 = TileCoord.FromWorld(256.0, 512.0);
        Assert.Equal(new TileCoord(1, 2), t3);

        var tNeg = TileCoord.FromWorld(-10.0, -256.1);
        Assert.Equal(new TileCoord(-1, -2), tNeg);
    }

    [Fact]
    public void TileCoord_GetWorldOrigin_ComputesCorrectWorldCoordinates()
    {
        var tile = new TileCoord(3, -2);
        tile.GetWorldOrigin(WorldConstants.TileSize, out double ox, out double oz);

        Assert.Equal(768.0, ox, precision: 4);
        Assert.Equal(-512.0, oz, precision: 4);
    }

    [Fact]
    public void TerrainTile_SampleHeightLocal_PerformsAccurateBilinearInterpolation()
    {
        // 3x3 sample grid across 256m
        // (0,0)=10, (1,0)=20, (2,0)=30
        // (0,1)=20, (1,1)=30, (2,1)=40
        // (0,2)=30, (1,2)=40, (2,2)=50
        uint samples = 3;
        float[] heights =
        [
            10f, 20f, 30f,
            20f, 30f, 40f,
            30f, 40f, 50f
        ];

        var tile = new TerrainTile(new TileCoord(0, 0), samples, samples, heights, 10f, 50f);

        // Test corners
        Assert.Equal(10f, tile.SampleHeightLocal(0f, 0f), precision: 3);
        Assert.Equal(30f, tile.SampleHeightLocal(256f, 0f), precision: 3);
        Assert.Equal(30f, tile.SampleHeightLocal(0f, 256f), precision: 3);
        Assert.Equal(50f, tile.SampleHeightLocal(256f, 256f), precision: 3);

        // Test exact center (128m, 128m)
        Assert.Equal(30f, tile.SampleHeightLocal(128f, 128f), precision: 3);

        // Test half-way point between (0,0) and (128, 0) -> (64, 0) => 15m
        Assert.Equal(15f, tile.SampleHeightLocal(64f, 0f), precision: 3);
    }

    [Fact]
    public void TerrainTile_ShiftOrigin_ShiftsDoublePrecisionOriginCorrectly()
    {
        var tile = new TerrainTile(new TileCoord(1, 1), 3, 3, new float[9], 0f, 0f);
        Assert.Equal(256.0, tile.Origin.X, precision: 4);
        Assert.Equal(256.0, tile.Origin.Z, precision: 4);

        tile.ShiftOrigin(-256.0, 0.0, -256.0);
        Assert.Equal(0.0, tile.Origin.X, precision: 4);
        Assert.Equal(0.0, tile.Origin.Z, precision: 4);
    }

    [Fact]
    public void HeightfieldCooker_CookAndDecodeRoundtrip_PreservesData()
    {
        uint samples = 5;
        float[] heights = new float[samples * samples];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = i * 2.5f;
        }

        var normalGuid = new AssetGuid(0x1234567890ABCDEFUL);
        var splatGuid = new AssetGuid(0xFEDCBA0987654321UL);

        byte[] cookedBytes = HeightfieldCooker.CookTerrainTile(
            tileX: 4,
            tileZ: -3,
            samplesX: samples,
            samplesZ: samples,
            heights: heights,
            normalMapGuid: normalGuid,
            splatMapGuid: splatGuid
        );

        Assert.NotNull(cookedBytes);
        Assert.True(cookedBytes.Length > 0);

        bool success = TerrainTile.TryDecodeTile(cookedBytes, out var decodedTile);
        Assert.True(success);
        Assert.NotNull(decodedTile);

        Assert.Equal(4, decodedTile.Coord.X);
        Assert.Equal(-3, decodedTile.Coord.Z);
        Assert.Equal(samples, decodedTile.SamplesX);
        Assert.Equal(samples, decodedTile.SamplesZ);
        Assert.Equal(0f, decodedTile.MinHeight, precision: 3);
        Assert.Equal(heights[^1], decodedTile.MaxHeight, precision: 3);
        Assert.Equal(normalGuid, decodedTile.NormalMapGuid);
        Assert.Equal(splatGuid, decodedTile.SplatMapGuid);
        Assert.Equal(heights.Length, decodedTile.Heights.Length);

        for (int i = 0; i < heights.Length; i++)
        {
            Assert.Equal(heights[i], decodedTile.Heights[i], precision: 4);
        }
    }
}
