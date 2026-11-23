// ==================================================================================================
// SPRINT 6a: WorldGrid Unit Tests
// Validates 2D sparse hash grid, spatial lookups, active cell registration, radius queries, and origin shifts.
// ==================================================================================================

using System.Collections.Generic;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;
using Jatayu.World;
using Xunit;

namespace Jatayu.Tests;

public class WorldGridTests
{
    [Fact]
    public void GetOrCreateCell_CreatesAndReusesCells()
    {
        var grid = new WorldGrid();
        var coord = new CellCoord(2, 5);

        var cellA = grid.GetOrCreateCell(coord);
        var cellB = grid.GetOrCreateCell(coord);

        Assert.NotNull(cellA);
        Assert.Same(cellA, cellB);
        Assert.Equal(coord, cellA.Coord);
        Assert.Equal(1, grid.TotalCellCount);
    }

    [Fact]
    public void GetCellAtWorld_ResolvesCorrectWorldCoordinates()
    {
        var grid = new WorldGrid();

        var cell = grid.GetCellAtWorld(256.0, -128.0);
        Assert.Equal(new CellCoord(2, -1), cell.Coord);
    }

    [Fact]
    public void MarkActiveAndUnloaded_MaintainsActiveCellCollection()
    {
        var grid = new WorldGrid();
        var cellA = grid.GetOrCreateCell(new CellCoord(0, 0));
        var cellB = grid.GetOrCreateCell(new CellCoord(1, 0));

        Assert.Equal(0, grid.ActiveCellCount);

        grid.MarkActive(cellA);
        grid.MarkActive(cellB);
        Assert.Equal(2, grid.ActiveCellCount);
        Assert.Contains(cellA, grid.ActiveCells);
        Assert.Contains(cellB, grid.ActiveCells);

        grid.MarkUnloaded(cellA);
        Assert.Equal(1, grid.ActiveCellCount);
        Assert.DoesNotContain(cellA, grid.ActiveCells);
        Assert.Contains(cellB, grid.ActiveCells);
    }

    [Fact]
    public void GetCellCoordsInRadius_CollectsCircularDiscCorrectly()
    {
        var grid = new WorldGrid();
        var center = new WorldPosition(64.0, 0.0, 64.0); // Center of cell (0, 0)
        var coords = new List<CellCoord>();

        // Query small radius (64m) - should only return center cell (0, 0)
        grid.GetCellCoordsInRadius(center, 50.0f, coords);
        Assert.Single(coords);
        Assert.Equal(new CellCoord(0, 0), coords[0]);

        // Query larger radius (300m) - should return a cluster of cells
        grid.GetCellCoordsInRadius(center, 300.0f, coords);
        Assert.True(coords.Count >= 9); // At least 3x3 cells
        Assert.Contains(new CellCoord(0, 0), coords);
        Assert.Contains(new CellCoord(1, 0), coords);
        Assert.Contains(new CellCoord(-1, 0), coords);
        Assert.Contains(new CellCoord(0, 1), coords);
        Assert.Contains(new CellCoord(0, -1), coords);
    }

    [Fact]
    public void ShiftOrigin_TranslatesCellOriginCoordinatesDeterministically()
    {
        var grid = new WorldGrid();
        var cell = grid.GetOrCreateCell(new CellCoord(1, 1));
        var initialOrigin = cell.Origin;

        // Apply a 256m terrain tile multiple origin shift (Section 1.3)
        grid.ShiftOrigin(256.0, 0.0, -512.0);

        Assert.Equal(initialOrigin.X + 256.0, cell.Origin.X, 3);
        Assert.Equal(initialOrigin.Y, cell.Origin.Y, 3);
        Assert.Equal(initialOrigin.Z - 512.0, cell.Origin.Z, 3);
    }
}
