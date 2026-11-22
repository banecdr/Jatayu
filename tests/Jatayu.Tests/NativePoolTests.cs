/* =================================================================================================
 * FILE: NativePoolTests.cs
 * LOCATION: tests/Jatayu.Tests/NativePoolTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating the unmanaged NativePool power-of-two size bucketing:
 *   - Allocation and returning of unmanaged blocks
 *   - Reuse of returned blocks across calls
 *   - Global shared instance operation
 * ================================================================================================= */

using System;
using Jatayu.Core.Memory;
using Xunit;

namespace Jatayu.Tests;

public class NativePoolTests
{
    [Fact]
    public unsafe void RentAndReturn_RecyclesAllocatedBlock()
    {
        using var pool = new NativePool();

        void* ptr1 = pool.Rent(2048);
        Assert.True(ptr1 != null);

        // Write a test pattern to verify memory access
        var span1 = new Span<byte>(ptr1, 2048);
        span1.Fill(0xAB);
        Assert.Equal((byte)0xAB, span1[0]);
        Assert.Equal((byte)0xAB, span1[2047]);

        // Return block to the pool
        pool.Return(ptr1, 2048);

        // Rent same size again - should recycle the returned pointer
        void* ptr2 = pool.Rent(2048);
        Assert.True(ptr2 != null);
        Assert.Equal((IntPtr)ptr1, (IntPtr)ptr2);

        pool.Return(ptr2, 2048);
    }

    [Fact]
    public unsafe void Rent_RoundsUpToPowerOfTwoBucket()
    {
        using var pool = new NativePool();

        // 1500 bytes belongs to 2048 byte bucket (power of 2)
        void* ptr = pool.Rent(1500);
        Assert.True(ptr != null);

        // Renting 2000 bytes can reuse a returned 1500 byte block if they share the same bucket
        pool.Return(ptr, 1500);
        void* ptrReused = pool.Rent(2000);
        Assert.Equal((IntPtr)ptr, (IntPtr)ptrReused);

        pool.Return(ptrReused, 2000);
    }

    [Fact]
    public unsafe void RentZero_ReturnsNull()
    {
        using var pool = new NativePool();
        void* ptr = pool.Rent(0);
        Assert.True(ptr == null);
    }
}
