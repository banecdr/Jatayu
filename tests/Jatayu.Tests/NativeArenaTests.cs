/* =================================================================================================
 * FILE: NativeArenaTests.cs
 * LOCATION: tests/Jatayu.Tests/NativeArenaTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating the unmanaged NativeArena linear allocator:
 *   - Correct pointer bump allocation
 *   - 16-byte SIMD alignment
 *   - Dynamic growth when capacity is exceeded
 *   - Per-frame Reset() resetting allocated bytes to zero
 *   - AllocateSpan<T> unmanaged memory access
 * ================================================================================================= */

using System;
using Jatayu.Core.Memory;
using Xunit;

namespace Jatayu.Tests;

public class NativeArenaTests
{
    [Fact]
    public unsafe void Allocate_ReturnsAlignedPointer()
    {
        using var arena = new NativeArena(initialCapacity: 1024);

        void* ptr1 = arena.Allocate(13, alignment: 16);
        void* ptr2 = arena.Allocate(7, alignment: 16);
        void* ptr3 = arena.Allocate(32, alignment: 16);

        Assert.True((nuint)ptr1 % 16 == 0, "ptr1 must be 16-byte aligned");
        Assert.True((nuint)ptr2 % 16 == 0, "ptr2 must be 16-byte aligned");
        Assert.True((nuint)ptr3 % 16 == 0, "ptr3 must be 16-byte aligned");
        Assert.True((nuint)ptr2 >= (nuint)ptr1 + 16, "ptr2 must be offset past ptr1");
        Assert.True(arena.AllocatedBytes > 0);
    }

    [Fact]
    public unsafe void AllocateSpan_AllowsDirectMemoryReadWrite()
    {
        using var arena = new NativeArena(initialCapacity: 1024);

        var span = arena.AllocateSpan<int>(4);
        Assert.Equal(4, span.Length);

        span[0] = 10;
        span[1] = 20;
        span[2] = 30;
        span[3] = 40;

        Assert.Equal(10, span[0]);
        Assert.Equal(20, span[1]);
        Assert.Equal(30, span[2]);
        Assert.Equal(40, span[3]);
    }

    [Fact]
    public unsafe void Reset_ResetsAllocatedBytesToZero()
    {
        using var arena = new NativeArena(initialCapacity: 1024);

        _ = arena.Allocate(256);
        Assert.True(arena.AllocatedBytes >= 256);

        arena.Reset();
        Assert.Equal((nuint)0, arena.AllocatedBytes);

        // Can allocate again after reset
        void* ptr = arena.Allocate(128);
        Assert.True(ptr != null);
        Assert.True(arena.AllocatedBytes >= 128);
    }

    [Fact]
    public unsafe void Allocate_GrowsWhenCapacityExceeded()
    {
        using var arena = new NativeArena(initialCapacity: 64);
        var initialCapacity = arena.Capacity;

        // Allocate more than initial capacity
        void* ptr = arena.Allocate(128);
        Assert.True(ptr != null);
        Assert.True(arena.Capacity > initialCapacity, "Arena must grow to accommodate larger request");
        Assert.True(arena.AllocatedBytes >= 128);
    }
}
