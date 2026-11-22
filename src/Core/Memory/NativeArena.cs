/* =================================================================================================
 * FILE: NativeArena.cs
 * LOCATION: src/Core/Memory/NativeArena.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-speed linear frame allocator operating directly on unmanaged native memory.
 *   Provides sub-nanosecond pointer bumps with zero Garbage Collector (GC) pressure.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Bump Allocation:
 *      Maintains a raw unmanaged byte buffer. Allocation simply advances an offset pointer.
 *   2. Per-Frame Reset:
 *      At the start/end of each frame, Reset() sets the offset back to 0. Memory is never freed
 *      or reallocated per frame, guaranteeing 0 bytes GC allocations in hot rendering paths.
 * ================================================================================================= */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Jatayu.Core.Memory;

/// <summary>
/// Linear unmanaged frame allocator with 16-byte SIMD alignment and zero GC allocations.
/// </summary>
public sealed unsafe class NativeArena : IDisposable
{
    private byte* _memory;
    private nuint _capacity;
    private nuint _offset;
    private bool _disposed;

    /// <summary>
    /// Gets the total capacity of the arena in bytes.
    /// </summary>
    public nuint Capacity => _capacity;

    /// <summary>
    /// Gets the number of bytes currently allocated in this frame.
    /// </summary>
    public nuint AllocatedBytes => _offset;

    /// <summary>
    /// Initializes a new linear native memory arena.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity in bytes (default: 4 MB).</param>
    public NativeArena(nuint initialCapacity = 4 * 1024 * 1024)
    {
        if (initialCapacity == 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _capacity = initialCapacity;
        _memory = (byte*)NativeMemory.Alloc(_capacity);
        _offset = 0;
    }

    /// <summary>
    /// Allocates an aligned unmanaged memory block from the arena.
    /// </summary>
    /// <param name="byteCount">Number of bytes to allocate.</param>
    /// <param name="alignment">Byte alignment (must be a power of two, default: 16).</param>
    /// <returns>A pointer to the allocated unmanaged memory block.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void* Allocate(nuint byteCount, nuint alignment = 16)
    {
        ThrowIfDisposed();

        // Align up current offset
        nuint alignedOffset = (_offset + (alignment - 1)) & ~(alignment - 1);
        nuint newOffset = alignedOffset + byteCount;

        if (newOffset > _capacity)
        {
            Grow(newOffset);
            alignedOffset = (_offset + (alignment - 1)) & ~(alignment - 1);
            newOffset = alignedOffset + byteCount;
        }

        _offset = newOffset;
        return _memory + alignedOffset;
    }

    /// <summary>
    /// Allocates an array of unmanaged structures from the arena.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AllocateSpan<T>(int count) where T : unmanaged
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0)
            return Span<T>.Empty;

        nuint size = (nuint)(sizeof(T) * count);
        nuint align = (nuint)Math.Min(16, sizeof(T));
        void* ptr = Allocate(size, align);
        return new Span<T>(ptr, count);
    }

    /// <summary>
    /// Resets the arena allocation offset back to 0 without releasing underlying OS memory.
    /// Call once per frame at the frame boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _offset = 0;
    }

    private void Grow(nuint requiredCapacity)
    {
        nuint newCapacity = Math.Max(_capacity * 2, requiredCapacity);
        byte* newMemory = (byte*)NativeMemory.Realloc(_memory, newCapacity);
        if (newMemory == null)
            throw new OutOfMemoryException($"Failed to expand NativeArena to {newCapacity} bytes.");

        _memory = newMemory;
        _capacity = newCapacity;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_memory != null)
        {
            NativeMemory.Free(_memory);
            _memory = null;
        }
    }
}
