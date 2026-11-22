/* =================================================================================================
 * FILE: AssetManagerTests.cs
 * LOCATION: tests/Jatayu.Tests/AssetManagerTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Validates the Jatayu Runtime Asset Manager and PackReader (Sprint 4.4):
 *   - PackReader memory-mapped seek and zero-copy byte retrieval.
 *   - AssetManager mounting .jpak packages and finding assets by AssetGuid.
 *   - Asynchronous asset loading via JobSystem.
 *   - Reference counting and cache eviction on zero references.
 * ================================================================================================= */

using System;
using System.IO;
using Jatayu.AssetFormats;
using Jatayu.Assets;
using Jatayu.Cooker;
using Jatayu.Core.Jobs;
using Xunit;

namespace Jatayu.Tests;

public class AssetManagerTests
{
    [Fact]
    public void PackReader_ReadsZeroCopyPayloads()
    {
        string tempPak = Path.Combine(Path.GetTempPath(), $"pack_reader_{Guid.NewGuid():N}.jpak");
        var guid = AssetGuid.FromPath("textures/sand.png");
        byte[] originalData = new byte[256];
        for (int i = 0; i < originalData.Length; i++) originalData[i] = (byte)(i % 255);

        try
        {
            using (var writer = new PackWriter())
            {
                writer.AddEntry(guid, AssetType.Texture, originalData);
                writer.WriteToFile(tempPak);
            }

            using var reader = new PackReader(tempPak);
            Assert.Equal(1, reader.EntryCount);
            Assert.True(reader.Contains(guid));

            var span = reader.ReadAssetBytes(guid);
            Assert.Equal(originalData.Length, span.Length);
            Assert.True(span.SequenceEqual(originalData));
        }
        finally
        {
            if (File.Exists(tempPak)) File.Delete(tempPak);
        }
    }

    [Fact]
    public void AssetManager_MountsPaksAndLoadsAssets()
    {
        string tempPak = Path.Combine(Path.GetTempPath(), $"asset_mgr_{Guid.NewGuid():N}.jpak");
        var guid = AssetGuid.FromPath("models/house.gltf");
        byte[] originalData = new byte[512];
        Array.Fill(originalData, (byte)0x77);

        try
        {
            using (var writer = new PackWriter())
            {
                writer.AddEntry(guid, AssetType.Mesh, originalData);
                writer.WriteToFile(tempPak);
            }

            using var assetMgr = new AssetManager();
            assetMgr.MountPackage(tempPak);

            Assert.Equal(1, assetMgr.MountedPackCount);
            Assert.True(assetMgr.ContainsAsset(guid));

            byte[] loaded = assetMgr.LoadAsset(guid);
            Assert.Equal(originalData, loaded);

            // Verify cached on subsequent load
            byte[] cached = assetMgr.LoadAsset(guid);
            Assert.Same(loaded, cached);

            // Release
            assetMgr.ReleaseAsset(guid, evictIfZero: true);
        }
        finally
        {
            if (File.Exists(tempPak)) File.Delete(tempPak);
        }
    }

    [Fact]
    public void AssetManager_LoadAssetAsync_CompletesOnJobSystem()
    {
        string tempPak = Path.Combine(Path.GetTempPath(), $"async_mgr_{Guid.NewGuid():N}.jpak");
        var guid = AssetGuid.FromPath("textures/sky.png");
        byte[] payload = new byte[128];
        Array.Fill(payload, (byte)0x42);

        try
        {
            using (var writer = new PackWriter())
            {
                writer.AddEntry(guid, AssetType.Texture, payload);
                writer.WriteToFile(tempPak);
            }

            using var jobSystem = new JobSystem(workerCount: 2);
            using var assetMgr = new AssetManager();
            assetMgr.MountPackage(tempPak);

            byte[]? result = null;
            var handle = assetMgr.LoadAssetAsync(guid, jobSystem, bytes =>
            {
                result = bytes;
            });

            handle.Complete();
            Assert.True(handle.IsCompleted);
            Assert.NotNull(result);
            Assert.Equal(payload, result);
        }
        finally
        {
            if (File.Exists(tempPak)) File.Delete(tempPak);
        }
    }
}
