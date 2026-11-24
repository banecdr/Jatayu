/* =================================================================================================
 * FILE: TerrainClipmapTests.cs
 * LOCATION: tests/Jatayu.Tests/TerrainClipmapTests.cs
 *
 * WHAT THIS FILE DOES:
 *   Unit tests validating the modular Terrain3D clipmap mesher, discrete grid snapping,
 *   piece layout, geomorph instance generation, and push constant formatting.
 * ================================================================================================= */

using System;
using System.Numerics;
using Jatayu.AssetFormats.World;
using Jatayu.Terrain;
using Xunit;

namespace Jatayu.Tests;

public class TerrainClipmapTests
{
    [Fact]
    public void ClipmapMeshPieces_BuildsAll10Pieces_WithCorrectTopology()
    {
        var clipmap = new TerrainClipmap(meshSize: 64, lodCount: 7, vertexSpacing: 2.0f);

        // Verify all 10 piece types exist and have non-zero index counts
        for (int i = 0; i < 10; i++)
        {
            var type = (ClipmapMeshType)i;
            var piece = clipmap.GetPiece(type);
            Assert.True(piece.IndexCount > 0, $"Piece {type} should have non-zero index count");
            Assert.True(piece.SizeX > 0, $"Piece {type} should have non-zero SizeX");
            Assert.True(piece.SizeZ > 0, $"Piece {type} should have non-zero SizeZ");
        }

        // Standard and regular tiles: 64x64 quads * 6 indices/quad = 24,576
        var tile = clipmap.GetPiece(ClipmapMeshType.Tile);
        Assert.Equal(64, tile.SizeX);
        Assert.Equal(64, tile.SizeZ);
        Assert.Equal(64u * 64u * 6u, tile.IndexCount);

        var stdTile = clipmap.GetPiece(ClipmapMeshType.StandardTile);
        Assert.Equal(64, stdTile.SizeX);
        Assert.Equal(64, stdTile.SizeZ);
        Assert.Equal(64u * 64u * 6u, stdTile.IndexCount);

        // EdgeA: 2 x (4*64 + 8) = 2 x 264 quads
        var edgeA = clipmap.GetPiece(ClipmapMeshType.EdgeA);
        Assert.Equal(2, edgeA.SizeX);
        Assert.Equal(264, edgeA.SizeZ);
        Assert.Equal(2u * 264u * 6u, edgeA.IndexCount);

        // EdgeB: (4*64 + 4) x 2 = 260 x 2 quads
        var edgeB = clipmap.GetPiece(ClipmapMeshType.EdgeB);
        Assert.Equal(260, edgeB.SizeX);
        Assert.Equal(2, edgeB.SizeZ);
        Assert.Equal(260u * 2u * 6u, edgeB.IndexCount);

        // FillA: 4 x 64 quads
        var fillA = clipmap.GetPiece(ClipmapMeshType.FillA);
        Assert.Equal(4, fillA.SizeX);
        Assert.Equal(64, fillA.SizeZ);
        Assert.Equal(4u * 64u * 6u, fillA.IndexCount);

        // FillB: 64 x 4 quads
        var fillB = clipmap.GetPiece(ClipmapMeshType.FillB);
        Assert.Equal(64, fillB.SizeX);
        Assert.Equal(4, fillB.SizeZ);
        Assert.Equal(64u * 4u * 6u, fillB.IndexCount);

        // Total index count is sum of all pieces
        Assert.Equal(70992u, clipmap.IndexCount);
    }

    [Theory]
    [InlineData(1, 24)]                // LOD0 only: 16 tiles + 4 edge strips + 4 trims = 24
    [InlineData(2, 24 + 20)]           // LOD0 (24) + LOD1 (20) = 44
    [InlineData(3, 24 + 20 * 2)]       // LOD0 (24) + LOD1..2 (40) = 64
    [InlineData(7, 24 + 20 * 6)]       // LOD0 (24) + LOD1..6 (120) = 144
    public void Update_ProducesExactInstanceCountsPerLOD(int lodCount, int expectedInstances)
    {
        var clipmap = new TerrainClipmap(meshSize: 64, lodCount: lodCount, vertexSpacing: 2.0f);

        bool updated = clipmap.Update(0.0, 0.0);
        Assert.True(updated);
        Assert.Equal(expectedInstances, clipmap.Instances.Length);
    }

    [Fact]
    public void Update_ThresholdShortCircuitsSubSpacingMovement()
    {
        var clipmap = new TerrainClipmap(meshSize: 64, lodCount: 7, vertexSpacing: 2.0f);

        // First update at (100.0, 100.0) -> rebuilds
        Assert.True(clipmap.Update(100.0, 100.0));

        // Sub-spacing movement (< 2.0m) -> short-circuits (returns false, no rebuild)
        Assert.False(clipmap.Update(100.5, 101.2));
        Assert.False(clipmap.Update(101.9, 100.0));

        // Movement >= 2.0m -> rebuilds
        Assert.True(clipmap.Update(102.1, 100.0));

        // Reset forces next update even with zero movement
        clipmap.ResetTargetPosition();
        Assert.True(clipmap.Update(102.1, 100.0));
    }

    [Fact]
    public void MakePushConstants_ComputesCameraRelativeOriginsAndEncodesDebugFlag()
    {
        var clipmap = new TerrainClipmap(meshSize: 64, lodCount: 7, vertexSpacing: 2.0f);
        clipmap.Update(1000.0, 2000.0);

        var firstInst = clipmap.Instances[0];
        var vp = Matrix4x4.Identity;

        // Camera at (1000.0, 50.0, 2000.0)
        double camX = 1000.0;
        double camY = 50.0;
        double camZ = 2000.0;

        // 1. Without debug rings: TerrainParams.z should be +64
        var pcNormal = clipmap.MakePushConstants(firstInst, vp, camX, camY, camZ, debugRings: false);
        Assert.Equal((float)(firstInst.WorldX - camX), pcNormal.CameraRelativeOrigin.X, precision: 3);
        Assert.Equal(-50.0f, pcNormal.CameraRelativeOrigin.Y, precision: 3);
        Assert.Equal((float)(firstInst.WorldZ - camZ), pcNormal.CameraRelativeOrigin.Z, precision: 3);
        Assert.Equal(firstInst.Scale, pcNormal.CameraRelativeOrigin.W);
        Assert.Equal(64.0f, pcNormal.TerrainParams.Z);

        // 2. With debug rings: TerrainParams.z should be -64 (negative encodes debug active)
        var pcDebug = clipmap.MakePushConstants(firstInst, vp, camX, camY, camZ, debugRings: true);
        Assert.Equal(-64.0f, pcDebug.TerrainParams.Z);
        Assert.Equal(firstInst.Lod, (int)pcDebug.TerrainParams.W);
    }

    [Fact]
    public void SampleElevation_EvaluatesResidentTileBeforePyramid()
    {
        var pyramid = new TerrainHeightPyramid(resolution: 32, worldDimension: 1000.0, originX: 0.0, originZ: 0.0);
        pyramid.Initialize((wx, wz) => 10.0f);

        var clipmap = new TerrainClipmap(meshSize: 64, lodCount: 7, vertexSpacing: 2.0f, heightPyramid: pyramid);

        // Point (50, 50) without tile -> pyramid returns 10.0f
        Assert.Equal(10.0f, clipmap.SampleElevation(50.0, 50.0), precision: 2);

        // Register resident tile at (0, 0) covering [0, 256) with height = 75.0f
        var heights = new float[9];
        Array.Fill(heights, 75.0f);
        var tile = new TerrainTile(new TileCoord(0, 0), 3, 3, heights, 75.0f, 75.0f);
        clipmap.RegisterTile(tile);
        Assert.Equal(1, clipmap.ResidentTileCount);

        // Now (50, 50) samples from the registered tile
        Assert.Equal(75.0f, clipmap.SampleElevation(50.0, 50.0), precision: 2);

        // Unregister tile -> falls back to pyramid
        var coord = new TileCoord(0, 0);
        clipmap.UnregisterTile(in coord);
        Assert.Equal(0, clipmap.ResidentTileCount);
        Assert.Equal(10.0f, clipmap.SampleElevation(50.0, 50.0), precision: 2);
    }
}
