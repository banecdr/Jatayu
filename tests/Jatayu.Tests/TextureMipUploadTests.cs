/* =================================================================================================
 * FILE: TextureMipUploadTests.cs
 * LOCATION: tests/Jatayu.Tests/TextureMipUploadTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating multi-level mip copy region generation, per-mip buffer offsets,
 *   subresource mip indexing, dimension halving, and block-compressed sizing (BC7/BC5/BC4)
 *   for asynchronous GPU texture uploads (F-04).
 * ================================================================================================= */

using Brutal.VulkanApi;
using Jatayu.RHI.Vulkan.Upload;
using Xunit;

namespace Jatayu.Tests;

public class TextureMipUploadTests
{
    [Fact]
    public void GenerateMipCopyRegions_UncompressedRGBA8_GeneratesAllMipsWithAccurateOffsets()
    {
        const int width = 64;
        const int height = 64;
        const int mipLevels = 4;
        const ulong stagingOffset = 1024;

        // Calculate total bytes for 4 mips:
        // Mip 0 (64x64): 64 * 64 * 4 = 16384
        // Mip 1 (32x32): 32 * 32 * 4 = 4096
        // Mip 2 (16x16): 16 * 16 * 4 = 1024
        // Mip 3 (8x8):   8 * 8 * 4   = 256
        // Total = 21760 bytes
        ulong totalBytes = 21760;

        var regions = UploadManager.GenerateMipCopyRegions(
            VkFormat.R8G8B8A8UNorm,
            width,
            height,
            mipLevels,
            stagingOffset,
            totalBytes,
            out int validMipCount
        );

        Assert.Equal(4, validMipCount);
        Assert.Equal(4, regions.Length);

        // Mip 0
        Assert.Equal((int)stagingOffset, (int)regions[0].BufferOffset.Value);
        Assert.Equal(0, regions[0].ImageSubresource.MipLevel);
        Assert.Equal(64, regions[0].ImageExtent.Width);
        Assert.Equal(64, regions[0].ImageExtent.Height);
        Assert.Equal(1, regions[0].ImageExtent.Depth);

        // Mip 1
        Assert.Equal((int)(stagingOffset + 16384), (int)regions[1].BufferOffset.Value);
        Assert.Equal(1, regions[1].ImageSubresource.MipLevel);
        Assert.Equal(32, regions[1].ImageExtent.Width);
        Assert.Equal(32, regions[1].ImageExtent.Height);

        // Mip 2
        Assert.Equal((int)(stagingOffset + 16384 + 4096), (int)regions[2].BufferOffset.Value);
        Assert.Equal(2, regions[2].ImageSubresource.MipLevel);
        Assert.Equal(16, regions[2].ImageExtent.Width);
        Assert.Equal(16, regions[2].ImageExtent.Height);

        // Mip 3
        Assert.Equal((int)(stagingOffset + 16384 + 4096 + 1024), (int)regions[3].BufferOffset.Value);
        Assert.Equal(3, regions[3].ImageSubresource.MipLevel);
        Assert.Equal(8, regions[3].ImageExtent.Width);
        Assert.Equal(8, regions[3].ImageExtent.Height);
    }

    [Fact]
    public void GenerateMipCopyRegions_BlockCompressedBC7_CalculatesBlockDimensions()
    {
        const int width = 32;
        const int height = 32;
        const int mipLevels = 3;
        const ulong stagingOffset = 0;

        // BC7: 16 bytes per 4x4 block
        // Mip 0 (32x32): 8x8 blocks = 64 blocks * 16 = 1024 bytes
        // Mip 1 (16x16): 4x4 blocks = 16 blocks * 16 = 256 bytes
        // Mip 2 (8x8):   2x2 blocks = 4 blocks * 16  = 64 bytes
        // Total = 1344 bytes
        ulong totalBytes = 1344;

        var regions = UploadManager.GenerateMipCopyRegions(
            VkFormat.Bc7UNormBlock,
            width,
            height,
            mipLevels,
            stagingOffset,
            totalBytes,
            out int validMipCount
        );

        Assert.Equal(3, validMipCount);
        Assert.Equal(0, (int)regions[0].BufferOffset.Value);
        Assert.Equal(1024, (int)regions[1].BufferOffset.Value);
        Assert.Equal(1280, (int)regions[2].BufferOffset.Value);
    }

    [Fact]
    public void GenerateMipCopyRegions_PartialData_ClampsToAvailableMips()
    {
        const int width = 64;
        const int height = 64;
        const int mipLevels = 4;
        const ulong stagingOffset = 0;

        // Only provide bytes for Mip 0 (16384 bytes) and Mip 1 (4096 bytes) = 20480 bytes
        // Mip 2 (requires additional 1024 bytes) does not fit
        ulong totalBytes = 20480;

        var regions = UploadManager.GenerateMipCopyRegions(
            VkFormat.R8G8B8A8UNorm,
            width,
            height,
            mipLevels,
            stagingOffset,
            totalBytes,
            out int validMipCount
        );

        Assert.Equal(2, validMipCount);
        Assert.Equal(0, regions[0].ImageSubresource.MipLevel);
        Assert.Equal(1, regions[1].ImageSubresource.MipLevel);
    }
}
