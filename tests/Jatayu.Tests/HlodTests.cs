/* =================================================================================================
 * FILE: HlodTests.cs
 * LOCATION: tests/Jatayu.Tests/HlodTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating HLOD proxy cluster activation (Ring B: 600m - 2,000m),
 *   HlodCooker binary generation, and ImpostorCooker 8-angle billboard atlas coordinates.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Cooker;
using Jatayu.Core.Mathematics;
using Jatayu.World;
using Xunit;

namespace Jatayu.Tests;

public class HlodTests
{
    [Fact]
    public void HlodProxyCluster_IsActiveAtDistance_EnforcesRingBBoundaries()
    {
        var cluster = new HlodProxyCluster(
            guid: new AssetGuid(1),
            proxyMeshGuid: new AssetGuid(2),
            impostorAtlasGuid: new AssetGuid(3),
            center: new WorldPosition(0.0, 0.0, 0.0),
            boundingRadius: 100f,
            minDistance: 600f,
            maxDistance: 2000f
        );

        Assert.False(cluster.IsActiveAtDistance(599.0));  // In Ring A (< 600m)
        Assert.True(cluster.IsActiveAtDistance(600.0));   // Exactly at Ring B threshold
        Assert.True(cluster.IsActiveAtDistance(1200.0));  // Mid Ring B
        Assert.True(cluster.IsActiveAtDistance(2000.0));  // Far Ring B boundary
        Assert.False(cluster.IsActiveAtDistance(2001.0)); // In Ring C (> 2000m)
    }

    [Fact]
    public void HlodManager_GetActiveClusters_FiltersByCameraPosition()
    {
        var manager = new HlodManager();

        var nearCluster = new HlodProxyCluster(
            new AssetGuid(10), new AssetGuid(11), new AssetGuid(12),
            new WorldPosition(0.0, 0.0, 300.0), 50f, 600f, 2000f);

        var midCluster = new HlodProxyCluster(
            new AssetGuid(20), new AssetGuid(21), new AssetGuid(22),
            new WorldPosition(0.0, 0.0, 1000.0), 50f, 600f, 2000f);

        var farCluster = new HlodProxyCluster(
            new AssetGuid(30), new AssetGuid(31), new AssetGuid(32),
            new WorldPosition(0.0, 0.0, 5000.0), 50f, 600f, 2000f);

        manager.RegisterCluster(nearCluster);
        manager.RegisterCluster(midCluster);
        manager.RegisterCluster(farCluster);

        var camera = new WorldPosition(0.0, 0.0, 0.0);
        var active = new List<HlodProxyCluster>();
        manager.GetActiveClusters(camera, active);

        Assert.Single(active);
        Assert.Equal(midCluster.Guid, active[0].Guid);
    }

    [Fact]
    public void HlodCooker_CookHlodProxy_ProducesValidBinaryPayload()
    {
        var guid = new AssetGuid(0xAABBCCDDEEFF1122UL);
        var meshGuid = new AssetGuid(0x3344556677889900UL);
        var atlasGuid = new AssetGuid(0x1122334455667788UL);

        byte[] payload = HlodCooker.CookHlodProxy(
            guid: guid,
            proxyMeshGuid: meshGuid,
            impostorAtlasGuid: atlasGuid,
            centerX: 512.0,
            centerY: 25.0,
            centerZ: 1024.0,
            boundingRadius: 180f,
            minDistance: 600f,
            maxDistance: 2000f
        );

        Assert.NotNull(payload);
        Assert.Equal(Marshal.SizeOf<CookedHlodHeader>(), payload.Length);

        var header = MemoryMarshal.Read<CookedHlodHeader>(payload);
        Assert.True(header.IsValid);
        Assert.Equal(guid, header.Guid);
        Assert.Equal(meshGuid, header.ProxyMeshGuid);
        Assert.Equal(atlasGuid, header.ImpostorAtlasGuid);
        Assert.Equal(512.0, header.CenterX);
        Assert.Equal(25.0, header.CenterY);
        Assert.Equal(1024.0, header.CenterZ);
        Assert.Equal(180f, header.BoundingRadius);
        Assert.Equal(600f, header.MinDistance);
        Assert.Equal(2000f, header.MaxDistance);
    }

    [Fact]
    public void ImpostorCooker_AtlasUvAndAngleSlices_AreAccurate()
    {
        // 8 slices in 4x2 grid
        for (int i = 0; i < ImpostorCooker.AngleCount; i++)
        {
            ImpostorCooker.GetAngleAtlasUv(i, out float uMin, out float vMin, out float uMax, out float vMax);
            Assert.True(uMin >= 0f && uMin < 1f);
            Assert.True(vMin >= 0f && vMin < 1f);
            Assert.True(uMax > uMin && uMax <= 1f);
            Assert.True(vMax > vMin && vMax <= 1f);
            Assert.Equal(0.25f, uMax - uMin, precision: 4);
            Assert.Equal(0.50f, vMax - vMin, precision: 4);
        }

        // Test angle index mapping for cardinal directions
        // Forward (0, 1): angle 0 -> slice 0
        int forwardSlice = ImpostorCooker.ComputeBestAngleIndex(0f, 1f);
        Assert.Equal(0, forwardSlice);

        // Right (1, 0): angle 90 deg (pi/2) -> slice 2
        int rightSlice = ImpostorCooker.ComputeBestAngleIndex(1f, 0f);
        Assert.Equal(2, rightSlice);

        // Back (0, -1): angle 180 deg (pi) -> slice 4
        int backSlice = ImpostorCooker.ComputeBestAngleIndex(0f, -1f);
        Assert.Equal(4, backSlice);

        // Left (-1, 0): angle 270 deg (3pi/2) -> slice 6
        int leftSlice = ImpostorCooker.ComputeBestAngleIndex(-1f, 0f);
        Assert.Equal(6, leftSlice);
    }
}
