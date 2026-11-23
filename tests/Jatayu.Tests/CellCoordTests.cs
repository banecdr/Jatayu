// ==================================================================================================
// SPRINT 6a: CellCoord Unit Tests
// Validates 2D integer coordinates, 64-bit packing, FNV-1a GUID derivation, distances, and neighbors.
// ==================================================================================================

using System;
using System.Collections.Generic;
using Jatayu.AssetFormats.World;
using Xunit;

namespace Jatayu.Tests;

public class CellCoordTests
{
    [Fact]
    public void ConstructorAndProperties_SetCorrectly()
    {
        var coord = new CellCoord(15, -42);
        Assert.Equal(15, coord.X);
        Assert.Equal(-42, coord.Z);
    }

    [Fact]
    public void Packing_RoundtripsCorrectlyAcrossFullIntegerRanges()
    {
        int[] testCoords = { 0, 1, -1, 100, -100, 32767, -32768, int.MaxValue, int.MinValue };

        foreach (int x in testCoords)
        {
            foreach (int z in testCoords)
            {
                var original = new CellCoord(x, z);
                ulong packed = original.ToPackedUlong();
                var unpacked = CellCoord.FromPackedUlong(packed);

                Assert.Equal(original, unpacked);
                Assert.Equal(original.X, unpacked.X);
                Assert.Equal(original.Z, unpacked.Z);
            }
        }
    }

    [Fact]
    public void ToAssetGuid_GeneratesNonEmptyAndDeterministicGuid()
    {
        var coordA = new CellCoord(5, 12);
        var coordB = new CellCoord(5, 12);
        var coordC = new CellCoord(5, 13);

        var guidA = coordA.ToAssetGuid();
        var guidB = coordB.ToAssetGuid();
        var guidC = coordC.ToAssetGuid();

        Assert.True(guidA.IsValid);
        Assert.Equal(guidA, guidB);
        Assert.NotEqual(guidA, guidC);
    }

    [Fact]
    public void FromWorld_ComputesCorrectGridCoordinates()
    {
        float cellSize = 128.0f;

        // Origin cell (0..128, 0..128)
        var c0 = CellCoord.FromWorld(0.0, 0.0, cellSize);
        Assert.Equal(new CellCoord(0, 0), c0);

        var c1 = CellCoord.FromWorld(64.0, 127.9, cellSize);
        Assert.Equal(new CellCoord(0, 0), c1);

        var c2 = CellCoord.FromWorld(128.0, 256.0, cellSize);
        Assert.Equal(new CellCoord(1, 2), c2);

        // Negative coordinates must floor properly (-1..-128 is -1)
        var cNeg = CellCoord.FromWorld(-1.0, -128.0, cellSize);
        Assert.Equal(new CellCoord(-1, -1), cNeg);

        var cNeg2 = CellCoord.FromWorld(-128.1, -256.0, cellSize);
        Assert.Equal(new CellCoord(-2, -2), cNeg2);
    }

    [Fact]
    public void WorldOriginAndCenter_ComputeCorrectMeters()
    {
        float cellSize = 128.0f;
        var coord = new CellCoord(3, -2);

        coord.GetWorldOrigin(cellSize, out double ox, out double oz);
        Assert.Equal(384.0, ox, 3);
        Assert.Equal(-256.0, oz, 3);

        coord.GetWorldCenter(cellSize, out double cx, out double cz);
        Assert.Equal(448.0, cx, 3);
        Assert.Equal(-192.0, cz, 3);
    }

    [Fact]
    public void DistanceMetrics_CalculateAccurately()
    {
        var a = new CellCoord(0, 0);
        var b = new CellCoord(3, 4);

        Assert.Equal(4, a.ChebyshevDistance(in b));
        Assert.Equal(7, a.ManhattanDistance(in b));
        Assert.Equal(25, a.DistanceSquared(in b));
        Assert.Equal(5.0f, a.Distance(in b), 4);
    }

    [Fact]
    public void GetNeighbors_ReturnsExactlyEightUniqueAdjacentNeighbors()
    {
        var center = new CellCoord(10, 20);
        var set = new HashSet<CellCoord>();

        foreach (var neighbor in center.GetNeighbors())
        {
            Assert.NotEqual(center, neighbor);
            Assert.True(Math.Abs(neighbor.X - center.X) <= 1);
            Assert.True(Math.Abs(neighbor.Z - center.Z) <= 1);
            set.Add(neighbor);
        }

        Assert.Equal(8, set.Count);
    }

    [Fact]
    public void Operators_AddAndSubtractCorrectly()
    {
        var a = new CellCoord(10, -5);
        var b = new CellCoord(3, 8);

        Assert.Equal(new CellCoord(13, 3), a + b);
        Assert.Equal(new CellCoord(7, -13), a - b);
    }
}
