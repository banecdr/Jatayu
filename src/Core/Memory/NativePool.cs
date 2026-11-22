/* =================================================================================================
 * FILE: NativePool.cs
 * LOCATION: src/Core/Memory/NativePool.cs
 * 
 * WHAT THIS FILE DOES:
 *   Maintains pools of unmanaged native memory buffers categorized by power-of-two size classes.
 *   Recycles native memory across streaming decodes, compression buffers, and render passes.
 * ================================================================================================= */

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Core.Memory;

/// <summary>
/// Pool of reusable unmanaged native memory buffers organized by size buckets.
/// </summary>
public sealed unsafe class NativePool : IDisposable
{
    private const int MinBucketPower = 10; // 1 KB
    private const int MaxBucketPower = 26; // 64 MB
    private const int BucketCount = MaxBucketPower - MinBucketPower + 1;

    private readonly ConcurrentBag<IntPtr>[] _buckets;
    private bool _disposed;

    /// <summary>
    /// Global shared instance of the NativePool.
    /// </summary>
    public static NativePool Shared { get; } = new();

    public NativePool()
    {
        _buckets = new ConcurrentBag<IntPtr>[BucketCount];
        for (int i = 0; i < BucketCount; i++)
        {
            _buckets[i] = new ConcurrentBag<IntPtr>();
        }
    }

    /// <summary>
    /// Rents a native memory buffer of at least the requested size in bytes.
    /// </summary>
    public void* Rent(nuint minimumBytes)
    {
        ThrowIfDisposed();

        if (minimumBytes == 0)
            return null;

        int bucketIndex = GetBucketIndex(minimumBytes);
        if (bucketIndex >= 0 && bucketIndex < BucketCount)
        {
            if (_buckets[bucketIndex].TryTake(out var existing))
            {
                return (void*)existing;
            }

            nuint bucketSize = (nuint)1 << (bucketIndex + MinBucketPower);
            return NativeMemory.Alloc(bucketSize);
        }

        // Exceeds max bucket; allocate directly
        return NativeMemory.Alloc(minimumBytes);
    }

    /// <summary>
    /// Returns a previously rented native memory buffer to the pool.
    /// </summary>
    public void Return(void* ptr, nuint sizeBytes)
    {
        if (ptr == null || _disposed) return;

        int bucketIndex = GetBucketIndex(sizeBytes);
        if (bucketIndex >= 0 && bucketIndex < BucketCount)
        {
            _buckets[bucketIndex].Add((IntPtr)ptr);
        }
        else
        {
            NativeMemory.Free(ptr);
        }
    }

    private static int GetBucketIndex(nuint bytes)
    {
        if (bytes <= (1 << MinBucketPower))
            return 0;

        uint rounded = BitOperations.RoundUpToPowerOf2((uint)Math.Min((nuint)uint.MaxValue, bytes));
        int power = BitOperations.TrailingZeroCount(rounded);
        return Math.Max(0, power - MinBucketPower);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < BucketCount; i++)
        {
            while (_buckets[i].TryTake(out var ptr))
            {
                NativeMemory.Free((void*)ptr);
            }
        }
    }
}
