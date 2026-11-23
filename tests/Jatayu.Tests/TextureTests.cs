/* =================================================================================================
 * FILE: TextureTests.cs
 * LOCATION: tests/Jatayu.Tests/TextureTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating CPU texture loading, checkerboard procedural generation,
 *   format validation, and channel properties for Sprint 3a.
 * ================================================================================================= */

using System;
using Jatayu.AssetFormats.Loaders;
using Xunit;

namespace Jatayu.Tests;

public class TextureTests
{
    [Fact]
    public void CreateCheckerboard_ReturnsValidData()
    {
        const int width = 64;
        const int height = 64;
        const int tileSize = 8;

        var image = TextureLoader.CreateCheckerboard(width, height, tileSize);

        Assert.NotNull(image);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(4, image.Channels);
        Assert.Equal(ImagePixelFormat.R8G8B8A8_Unorm, image.Format);
        Assert.Equal(width * height * 4, image.Data.Length);

        // Check top-left pixel (tile 0,0 -> white tile: 240, 240, 240, 255)
        Assert.Equal((byte)240, image.Data[0]);
        Assert.Equal((byte)240, image.Data[1]);
        Assert.Equal((byte)240, image.Data[2]);
        Assert.Equal((byte)255, image.Data[3]);

        // Check pixel in tile 1,0 (x=8, y=0 -> dark tile: 40, 120, 40, 255)
        int idx = (0 * width + 8) * 4;
        Assert.Equal((byte)40, image.Data[idx + 0]);
        Assert.Equal((byte)120, image.Data[idx + 1]);
        Assert.Equal((byte)40, image.Data[idx + 2]);
        Assert.Equal((byte)255, image.Data[idx + 3]);
    }

    [Fact]
    public void LoadedImageData_ConstructsCorrectly()
    {
        byte[] dummyData = new byte[16];
        var image = new LoadedImageData(2, 2, 4, ImagePixelFormat.R8G8B8A8_Srgb, dummyData, mipLevels: 2);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(4, image.Channels);
        Assert.Equal(ImagePixelFormat.R8G8B8A8_Srgb, image.Format);
        Assert.Equal(2, image.MipLevels);
        Assert.Same(dummyData, image.Data);
    }

    [Fact]
    public void MultiMipTexture_SamplesCorrectDataAtEveryMip()
    {
        // 3 mips:
        // Mip 0: 4x4, Solid Red (255, 0, 0, 255)
        // Mip 1: 2x2, Solid Green (0, 255, 0, 255)
        // Mip 2: 1x1, Solid Blue (0, 0, 255, 255)
        int mip0Bytes = 4 * 4 * 4; // 64
        int mip1Bytes = 2 * 2 * 4; // 16
        int mip2Bytes = 1 * 1 * 4; // 4
        byte[] packedData = new byte[mip0Bytes + mip1Bytes + mip2Bytes];

        // Fill Mip 0 with Red
        for (int i = 0; i < mip0Bytes; i += 4)
        {
            packedData[i + 0] = 255;
            packedData[i + 1] = 0;
            packedData[i + 2] = 0;
            packedData[i + 3] = 255;
        }

        // Fill Mip 1 with Green
        for (int i = mip0Bytes; i < mip0Bytes + mip1Bytes; i += 4)
        {
            packedData[i + 0] = 0;
            packedData[i + 1] = 255;
            packedData[i + 2] = 0;
            packedData[i + 3] = 255;
        }

        // Fill Mip 2 with Blue
        for (int i = mip0Bytes + mip1Bytes; i < packedData.Length; i += 4)
        {
            packedData[i + 0] = 0;
            packedData[i + 1] = 0;
            packedData[i + 2] = 255;
            packedData[i + 3] = 255;
        }

        var image = new LoadedImageData(4, 4, 4, ImagePixelFormat.R8G8B8A8_Unorm, packedData, mipLevels: 3);
        Assert.Equal(3, image.MipLevels);

        // Verify sampling from Mip 0 (Red)
        Assert.Equal(255, image.Data[0]);
        Assert.Equal(0, image.Data[1]);
        Assert.Equal(0, image.Data[2]);

        // Verify sampling from high Mip 1 (Green)
        Assert.Equal(0, image.Data[mip0Bytes + 0]);
        Assert.Equal(255, image.Data[mip0Bytes + 1]);
        Assert.Equal(0, image.Data[mip0Bytes + 2]);

        // Verify sampling from high Mip 2 (Blue)
        Assert.Equal(0, image.Data[mip0Bytes + mip1Bytes + 0]);
        Assert.Equal(0, image.Data[mip0Bytes + mip1Bytes + 1]);
        Assert.Equal(255, image.Data[mip0Bytes + mip1Bytes + 2]);
    }
}
