// ==================================================================================================
// SPRINT 6a: World Cooker & Loader Integration Tests
// Validates end-to-end cooking of cells with CellCooker, packing into .jpak, and memory-mapped decoding via CellLoader.
// ==================================================================================================

using System;
using System.IO;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Assets;
using Jatayu.Cooker;
using Jatayu.World;
using Xunit;

namespace Jatayu.Tests;

public class WorldCookerAndLoadingTests : IDisposable
{
    private readonly string _tempDir;

    public WorldCookerAndLoadingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Jatayu_WorldCookerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CellCookerAndLoader_RoundtripsInstancesCorrectly()
    {
        var coord = new CellCoord(4, -8);
        var instances = new CookedStaticInstance[3];

        instances[0] = new CookedStaticInstance
        {
            PosX = 10f, PosY = 0f, PosZ = 20f,
            RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
            ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f,
            MeshGuid = AssetGuid.FromPath("rock"),
            MaterialGuid = AssetGuid.Empty,
            MaterialId = 1,
            BoundingRadius = 3f,
            Flags = StaticInstanceFlags.CastShadow
        };

        instances[1] = new CookedStaticInstance
        {
            PosX = 64f, PosY = 2f, PosZ = 64f,
            RotX = 0f, RotY = 0.7071f, RotZ = 0f, RotW = 0.7071f,
            ScaleX = 2f, ScaleY = 5f, ScaleZ = 2f,
            MeshGuid = AssetGuid.FromPath("building"),
            MaterialGuid = AssetGuid.Empty,
            MaterialId = 2,
            BoundingRadius = 10f,
            Flags = StaticInstanceFlags.CastShadow | StaticInstanceFlags.Occluder
        };

        instances[2] = new CookedStaticInstance
        {
            PosX = 100f, PosY = 0f, PosZ = 110f,
            RotX = 0f, RotY = 0f, RotZ = 0f, RotW = 1f,
            ScaleX = 1f, ScaleY = 1.5f, ScaleZ = 1f,
            MeshGuid = AssetGuid.FromPath("tree"),
            MaterialGuid = AssetGuid.Empty,
            MaterialId = 3,
            BoundingRadius = 4f,
            Flags = StaticInstanceFlags.CastShadow
        };

        byte[] cookedBytes = CellCooker.CookCellWithInstances(coord, instances);
        Assert.NotNull(cookedBytes);
        Assert.True(cookedBytes.Length > 0);

        bool success = CellLoader.TryDecodeCell(cookedBytes, out var decoded);
        Assert.True(success);
        Assert.NotNull(decoded);
        Assert.Equal(coord, decoded.Coord);
        Assert.Equal(3, decoded.Instances.Length);

        // Verify instance 1
        Assert.Equal(64f, decoded.Instances[1].PosX);
        Assert.Equal(AssetGuid.FromPath("building"), decoded.Instances[1].MeshGuid);
        Assert.Equal(2u, decoded.Instances[1].MaterialId);
        Assert.Equal(10f, decoded.Instances[1].BoundingRadius);
        Assert.True(decoded.Instances[1].Flags.HasFlag(StaticInstanceFlags.Occluder));
    }

    [Fact]
    public void PackWriterAndReader_RoundtripsCellsThroughJpakPackage()
    {
        string packPath = Path.Combine(_tempDir, "test_world.jpak");

        var coord = new CellCoord(-3, 7);
        var instances = new CookedStaticInstance[1]
        {
            new CookedStaticInstance
            {
                PosX = 15f, PosY = 1f, PosZ = 30f,
                RotW = 1f,
                ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f,
                MeshGuid = AssetGuid.FromPath("rock"),
                MaterialId = 0,
                BoundingRadius = 2f,
                Flags = StaticInstanceFlags.CastShadow
            }
        };

        byte[] cellPayload = CellCooker.CookCellWithInstances(coord, instances);
        var cellGuid = coord.ToAssetGuid();

        // 1. Pack with PackWriter
        using (var writer = new PackWriter())
        {
            writer.AddEntry(cellGuid, AssetType.WorldCell, cellPayload);
            writer.WriteToFile(packPath);
        }

        Assert.True(File.Exists(packPath));

        // 2. Read with PackReader & CellLoader
        using (var reader = new PackReader(packPath))
        {
            Assert.True(reader.Contains(cellGuid));

            bool loaded = CellLoader.TryLoadCellFromPack(reader, coord, out var payload);
            Assert.True(loaded);
            Assert.NotNull(payload);
            Assert.Equal(coord, payload.Coord);
            Assert.Single(payload.Instances);
            Assert.Equal(15f, payload.Instances[0].PosX);
            Assert.Equal(AssetGuid.FromPath("rock"), payload.Instances[0].MeshGuid);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
