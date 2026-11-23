/* =================================================================================================
 * FILE: Hlod.cs
 * LOCATION: src/World/Hlod.cs
 * 
 * WHAT THIS FILE DOES:
 *   Hierarchical LOD (HLOD) and impostor coordinator for mid-range rendering (Ring B: 600m - 2,000m).
 *   - Tracks registered HLOD proxy meshes and 8-angle billboard impostor atlases.
 *   - Evaluates camera distance thresholds to switch seamlessly between Ring A high-detail meshes
 *     and Ring B proxy geometry.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;

namespace Jatayu.World;

/// <summary>
/// Runtime record representing an active HLOD proxy cluster in the world.
/// </summary>
public sealed class HlodProxyCluster
{
    public AssetGuid Guid { get; }
    public AssetGuid ProxyMeshGuid { get; }
    public AssetGuid ImpostorAtlasGuid { get; }
    public WorldPosition Center { get; }
    public float BoundingRadius { get; }
    public float MinDistance { get; }
    public float MaxDistance { get; }

    public HlodProxyCluster(
        AssetGuid guid,
        AssetGuid proxyMeshGuid,
        AssetGuid impostorAtlasGuid,
        in WorldPosition center,
        float boundingRadius,
        float minDistance = WorldConstants.LoadRadiusRingA,
        float maxDistance = WorldConstants.LoadRadiusRingB)
    {
        Guid = guid;
        ProxyMeshGuid = proxyMeshGuid;
        ImpostorAtlasGuid = impostorAtlasGuid;
        Center = center;
        BoundingRadius = boundingRadius;
        MinDistance = minDistance;
        MaxDistance = maxDistance;
    }

    /// <summary>
    /// Checks whether this HLOD proxy should be rendered given the camera distance.
    /// </summary>
    public bool IsActiveAtDistance(double distance) => distance >= MinDistance && distance <= MaxDistance;
}

/// <summary>
/// Coordinates HLOD proxy mesh selection and distance transitions for Ring B.
/// </summary>
public sealed class HlodManager
{
    private readonly List<HlodProxyCluster> _clusters = new();

    public IReadOnlyList<HlodProxyCluster> Clusters => _clusters;
    public int ClusterCount => _clusters.Count;

    public void RegisterCluster(HlodProxyCluster cluster)
    {
        _clusters.Add(cluster);
    }

    public void UnregisterCluster(HlodProxyCluster cluster)
    {
        _clusters.Remove(cluster);
    }

    /// <summary>
    /// Collects active HLOD proxy clusters visible in Ring B.
    /// </summary>
    public void GetActiveClusters(in WorldPosition cameraPos, List<HlodProxyCluster> destination)
    {
        destination.Clear();
        for (int i = 0; i < _clusters.Count; i++)
        {
            var cluster = _clusters[i];
            double dist = cluster.Center.DistanceTo(cameraPos);
            if (cluster.IsActiveAtDistance(dist))
            {
                destination.Add(cluster);
            }
        }
    }

    public void Clear()
    {
        _clusters.Clear();
    }
}
