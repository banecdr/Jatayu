/* =================================================================================================
 * FILE: AssetManager.cs
 * LOCATION: src/Assets/AssetManager.cs
 * 
 * WHAT THIS FILE DOES:
 *   Asynchronous asset registry and lifetime coordinator for the Jatayu Engine.
 *   - Mounts one or more .jpak asset packages via PackReader.
 *   - Resolves 64-bit AssetGuid to cooked payloads (.jmesh, .jtex, .jmat).
 *   - Coordinates asynchronous background loading jobs via JobSystem.
 *   - Provides reference counting and cache eviction for unused asset memory.
 * ================================================================================================= */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Jatayu.AssetFormats;
using Jatayu.Core.Jobs;

namespace Jatayu.Assets;

/// <summary>
/// Runtime asset manager coordinating package mounts, asset caching, and asynchronous loading.
/// </summary>
public sealed class AssetManager : IDisposable
{
    private readonly List<PackReader> _mountedPacks = new();
    private readonly Dictionary<AssetGuid, PackReader> _guidToPack = new();
    private readonly ConcurrentDictionary<AssetGuid, byte[]> _loadedByteCache = new();
    private readonly ConcurrentDictionary<AssetGuid, int> _refCounts = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the number of currently mounted .jpak packages.
    /// </summary>
    public int MountedPackCount
    {
        get
        {
            lock (_lock) return _mountedPacks.Count;
        }
    }

    /// <summary>
    /// Mounts a .jpak package file into the asset lookup table.
    /// </summary>
    public void MountPackage(string jpakPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jpakPath);

        var reader = new PackReader(jpakPath);
        lock (_lock)
        {
            _mountedPacks.Add(reader);
            // Index GUIDs to this reader
            foreach (var guid in GetEntryGuids(reader))
            {
                _guidToPack[guid] = reader;
            }
        }
    }

    /// <summary>
    /// Checks whether an asset is indexed in any mounted package.
    /// </summary>
    public bool ContainsAsset(AssetGuid guid)
    {
        lock (_lock) return _guidToPack.ContainsKey(guid);
    }

    /// <summary>
    /// Synchronously retrieves an asset's raw bytes, caching it in memory with an incremented ref count.
    /// </summary>
    public byte[] LoadAsset(AssetGuid guid)
    {
        _refCounts.AddOrUpdate(guid, 1, (_, count) => count + 1);

        if (_loadedByteCache.TryGetValue(guid, out var cached))
            return cached;

        PackReader? reader;
        lock (_lock)
        {
            if (!_guidToPack.TryGetValue(guid, out reader))
                throw new KeyNotFoundException($"Asset {guid} is not present in any mounted .jpak package.");
        }

        byte[] data = reader.ReadAssetArray(guid);
        _loadedByteCache[guid] = data;
        return data;
    }

    /// <summary>
    /// Asynchronously schedules an asset load job on the JobSystem (Background priority).
    /// </summary>
    public JobHandle LoadAssetAsync(AssetGuid guid, JobSystem jobSystem, Action<byte[]> onLoaded)
    {
        ArgumentNullException.ThrowIfNull(jobSystem);
        ArgumentNullException.ThrowIfNull(onLoaded);

        return jobSystem.Schedule(() =>
        {
            byte[] bytes = LoadAsset(guid);
            onLoaded(bytes);
        }, JobPriority.Background);
    }

    /// <summary>
    /// Decrements the reference count of an asset, optionally evicting from cache if ref count reaches zero.
    /// </summary>
    public void ReleaseAsset(AssetGuid guid, bool evictIfZero = false)
    {
        if (_refCounts.TryGetValue(guid, out int current))
        {
            int updated = Math.Max(0, current - 1);
            if (updated == 0)
            {
                _refCounts.TryRemove(guid, out _);
                if (evictIfZero)
                {
                    _loadedByteCache.TryRemove(guid, out _);
                }
            }
            else
            {
                _refCounts[guid] = updated;
            }
        }
    }

    private static IEnumerable<AssetGuid> GetEntryGuids(PackReader reader)
    {
        var list = new List<AssetGuid>();
        // Using reflection / internal accessor to index GUIDs
        var field = typeof(PackReader).GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(reader) is Dictionary<AssetGuid, JpakEntry> dict)
        {
            list.AddRange(dict.Keys);
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var pack in _mountedPacks)
                pack.Dispose();

            _mountedPacks.Clear();
            _guidToPack.Clear();
        }

        _loadedByteCache.Clear();
        _refCounts.Clear();
    }
}
