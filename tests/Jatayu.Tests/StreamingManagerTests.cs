// ==================================================================================================
// SPRINT 6a: StreamingManager Unit Tests
// Validates velocity scaling (D19), 900m prefetch cap (D23), hysteresis unloading (D4),
// and directional prioritization along velocity vector.
// ==================================================================================================

using System.Numerics;
using System.IO;
using System.Threading;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Assets;
using Jatayu.Cooker;
using Jatayu.Core.Events;
using Jatayu.Core.Mathematics;
using Jatayu.World;
using Xunit;

namespace Jatayu.Tests;

public class StreamingManagerTests
{
    public StreamingManagerTests()
    {
        EventBus.Clear<OriginShiftedEvent>();
    }

    [Fact]
    public void VelocityScaling_ComputesCorrectEffectiveRadiusWithCap()
    {
        var grid = new WorldGrid();
        var config = new WorldConfig
        {
            LoadRadiusRingA = 600f,
            LeadSeconds = 6f,
            PrefetchRadiusCap = 900f
        };
        using var streamer = new StreamingManager(grid, config);

        var playerPos = new WorldPosition(0, 0, 0);

        // At rest (speed = 0): effective radius should be 600m
        streamer.Update(0.016, in playerPos, Vector3.Zero);
        Assert.Equal(600f, streamer.EffectiveLoadRadius);
        Assert.Equal(0f, streamer.CurrentSpeedMps);

        // Speed = 25 m/s (90 km/h): 600 + 25 * 6 = 750m
        streamer.Update(0.016, in playerPos, new Vector3(25f, 0f, 0f));
        Assert.Equal(750f, streamer.EffectiveLoadRadius);
        Assert.Equal(25f, streamer.CurrentSpeedMps);

        // Extreme speed = 100 m/s (360 km/h): 600 + 100 * 6 = 1200m -> Clamped to 900m (D23)
        streamer.Update(0.016, in playerPos, new Vector3(0f, 0f, 100f));
        Assert.Equal(900f, streamer.EffectiveLoadRadius);
        Assert.Equal(100f, streamer.CurrentSpeedMps);
    }

    [Fact]
    public void HysteresisBand_PreventsUnloadingWithinUnloadRadius()
    {
        var grid = new WorldGrid();
        var config = new WorldConfig
        {
            LoadRadiusRingA = 600f,
            UnloadRadiusRingA = 750f,
            MaxConcurrentLoadingJobs = 0
        };
        using var streamer = new StreamingManager(grid, config);

        var playerPos = new WorldPosition(0, 0, 0);

        // Force-load a cell at 650m (e.g. cell (5, 0) is around 640m center)
        var coord = new CellCoord(5, 0);
        var cell = grid.GetOrCreateCell(coord);
        cell.State = CellState.Active;
        grid.MarkActive(cell);

        Assert.Equal(1, streamer.ActiveCellCount);

        // Update with player at origin: distance is ~640m (which is > 600m load radius, but < 750m unload radius)
        streamer.Update(0.016, in playerPos, Vector3.Zero);

        // Cell must STAY ACTIVE due to 150m hysteresis band (D4)
        Assert.Equal(1, streamer.ActiveCellCount);
        Assert.Equal(CellState.Active, cell.State);

        // Now move player far away to 2000m: cell must now be unloaded
        var farPlayerPos = new WorldPosition(2000, 0, 0);
        streamer.Update(0.016, in farPlayerPos, Vector3.Zero);

        Assert.Equal(0, streamer.ActiveCellCount);
        Assert.Equal(CellState.Unloaded, cell.State);
    }

    [Fact]
    public void DirectionalPrioritization_RanksForwardCellsHigherThanBackwardCells()
    {
        // When traveling east (+X), a cell at (+200, 0) should have lower Priority value
        // (i.e. processed earlier) than a cell at (-200, 0) equidistant from the player.
        var playerPos = new WorldPosition(0, 0, 0);
        var vel = new Vector3(50f, 0f, 0f); // traveling +X at 50 m/s
        var velDir = new Vector2(1f, 0f);

        var forwardCoord = new CellCoord(2, 0);
        forwardCoord.GetWorldCenter(128f, out double fx, out double fz);
        float fdx = (float)fx;
        float fdist = MathF.Abs(fdx);
        float forwardPriority = fdist - ((velDir.X * fdx) * 1.5f);

        var backwardCoord = new CellCoord(-2, 0);
        backwardCoord.GetWorldCenter(128f, out double bx, out double bz);
        float bdx = (float)bx;
        float bdist = MathF.Abs(bdx);
        float backwardPriority = bdist - ((velDir.X * bdx) * 1.5f);

        var reqForward = new PrioritizedLoadRequest(forwardCoord, forwardPriority);
        var reqBackward = new PrioritizedLoadRequest(backwardCoord, backwardPriority);

        Assert.True(reqForward.CompareTo(reqBackward) < 0, "Forward cell should have higher priority than backward cell");
    }

    [Fact]
    public void OriginShiftEvent_DoesNotTranslateAbsoluteCellOrigins()
    {
        var grid = new WorldGrid();
        using var streamer = new StreamingManager(grid);

        var cell = grid.GetOrCreateCell(new CellCoord(2, 3));
        var initialOrigin = cell.Origin;

        EventBus.Publish(new OriginShiftedEvent(256.0, 0.0, 512.0));

        Assert.Equal(initialOrigin.X, cell.Origin.X, 3);
        Assert.Equal(initialOrigin.Z, cell.Origin.Z, 3);
    }

    [Fact]
    public void ReturningToSpawnAfterFarTrip_ReloadsCellAtItsWorldOrigin()
    {
        var config = new WorldConfig
        {
            LoadRadiusRingA = 100f,
            UnloadRadiusRingA = 150f,
            MaxConcurrentLoadingJobs = 1,
            FinalizationBudgetMs = 10f
        };
        var grid = new WorldGrid(config);
        using var streamer = new StreamingManager(grid, config);
        var spawnCoord = new CellCoord(0, 0);
        var spawnCell = grid.GetOrCreateCell(spawnCoord);

        spawnCell.State = CellState.Active;
        grid.MarkActive(spawnCell);

        var farPosition = new WorldPosition(10000, 0, 10000);
        EventBus.Publish(new OriginShiftedEvent(9984, 0, 9984));
        streamer.Update(0.016, in farPosition, Vector3.Zero);
        Assert.Equal(CellState.Unloaded, spawnCell.State);

        var spawnPosition = new WorldPosition(0, 0, 0);
        EventBus.Publish(new OriginShiftedEvent(-9984, 0, -9984));
        for (int i = 0; i < 1000 && spawnCell.State != CellState.Active; i++)
        {
            streamer.Update(0.016, in spawnPosition, Vector3.Zero);
            Thread.Sleep(1);
        }

        Assert.Equal(CellState.Active, spawnCell.State);
        Assert.Equal(0.0, spawnCell.Origin.X);
        Assert.Equal(0.0, spawnCell.Origin.Z);
    }

    [Fact]
    public void MissingPackCells_AreCachedWithoutLoadErrors()
    {
        string packPath = Path.Combine(Path.GetTempPath(), $"Jatayu_MissingCells_{System.Guid.NewGuid():N}.jpak");
        var existingCoord = new CellCoord(0, 0);
        byte[] payload = CellCooker.CookCellWithInstances(existingCoord, Array.Empty<CookedStaticInstance>());

        using (var writer = new PackWriter())
        {
            writer.AddEntry(existingCoord.ToAssetGuid(), AssetType.WorldCell, payload);
            writer.WriteToFile(packPath);
        }

        try
        {
            using var reader = new PackReader(packPath);
            var config = new WorldConfig
            {
                LoadRadiusRingA = 150f,
                UnloadRadiusRingA = 200f,
                MaxConcurrentLoadingJobs = 4,
                FinalizationBudgetMs = 10f
            };
            var grid = new WorldGrid(config);
            using var streamer = new StreamingManager(grid, config, reader);
            var position = new WorldPosition(64, 0, 64);

            for (int i = 0; i < 100; i++)
            {
                streamer.Update(0.016, in position, Vector3.Zero);
                Thread.Sleep(1);
            }

            int missingCount = streamer.KnownMissingCellCount;
            Assert.True(missingCount > 0);
            Assert.Null(streamer.LastLoadError);
            Assert.Equal(0, streamer.InFlightCount);

            for (int i = 0; i < 10; i++)
                streamer.Update(0.016, in position, Vector3.Zero);

            Assert.Equal(missingCount, streamer.KnownMissingCellCount);
            Assert.Null(streamer.LastLoadError);
            Assert.Equal(0, streamer.InFlightCount);
        }
        finally
        {
            File.Delete(packPath);
        }
    }
}
