/* =================================================================================================
 * FILE: TextureCooker.cs
 * LOCATION: tools/Jatayu.Cooker/TextureCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Bakes images into cooked .jtex textures with full mip pyramids:
 *   - Computes multiresolution mip levels using 2x2 box filtering.
 *   - Identifies resident mip tail (mips with width/height <= 64) for immediate residency.
 *   - Packs streaming mips and resident tail with individual offsets for async IO streaming.
 *   - Serializes into high-speed .jtex binary blobs.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Brutal.StbApi;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

/// <summary>
/// Bakes textures into cooked .jtex binary packages with resident mip tails.
/// </summary>
public static class TextureCooker
{
    private static bool TryCheckedPixelSize(int width, int height, out int byteCount)
    {
        byteCount = 0;
        if (width <= 0 || height <= 0)
            return false;

        long pixels = (long)width * height;
        if (pixels > int.MaxValue / 4)
            return false;

        byteCount = (int)pixels * 4;
        return true;
    }

    /// <summary>
    /// Cooks raw RGBA8 pixel data into a .jtex binary blob with mipmaps and resident tail.
    /// </summary>
    public static byte[] CookRgba8(
        AssetGuid guid,
        int width,
        int height,
        ReadOnlySpan<byte> basePixels,
        bool isSrgb = true)
    {
        if (width <= 0 || height <= 0 || basePixels.IsEmpty)
            throw new ArgumentException("Invalid texture dimensions or pixel data.");
        if (width > 16384 || height > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions exceed the supported 16384-pixel limit.");
        if (!TryCheckedPixelSize(width, height, out int expectedPixels))
            throw new ArgumentOutOfRangeException(nameof(basePixels), "Texture pixel buffer size exceeds supported limits.");
        if (basePixels.Length != expectedPixels)
            throw new ArgumentException($"Texture buffer length {basePixels.Length} does not match {width}x{height} RGBA8 ({expectedPixels} bytes).", nameof(basePixels));

        // 1. Calculate mip level count down to 1x1
        int mipLevels = 1 + (int)MathF.Floor(MathF.Log2(Math.Max(width, height)));
        var mips = new byte[mipLevels][];
        var mipWidths = new int[mipLevels];
        var mipHeights = new int[mipLevels];

        mips[0] = basePixels.ToArray();
        mipWidths[0] = width;
        mipHeights[0] = height;

        // 2. Generate Mipmap Pyramid (Box Filter Downsampling)
        for (int m = 1; m < mipLevels; m++)
        {
            int prevW = mipWidths[m - 1];
            int prevH = mipHeights[m - 1];
            int currW = Math.Max(1, prevW / 2);
            int currH = Math.Max(1, prevH / 2);

            mipWidths[m] = currW;
            mipHeights[m] = currH;

            byte[] prevPixels = mips[m - 1];
            byte[] currPixels = new byte[currW * currH * 4];

            for (int y = 0; y < currH; y++)
            {
                int srcY0 = Math.Min(y * 2, prevH - 1);
                int srcY1 = Math.Min(y * 2 + 1, prevH - 1);

                for (int x = 0; x < currW; x++)
                {
                    int srcX0 = Math.Min(x * 2, prevW - 1);
                    int srcX1 = Math.Min(x * 2 + 1, prevW - 1);

                    int p00 = (srcY0 * prevW + srcX0) * 4;
                    int p10 = (srcY0 * prevW + srcX1) * 4;
                    int p01 = (srcY1 * prevW + srcX0) * 4;
                    int p11 = (srcY1 * prevW + srcX1) * 4;

                    int dst = (y * currW + x) * 4;
                    currPixels[dst + 0] = (byte)((prevPixels[p00 + 0] + prevPixels[p10 + 0] + prevPixels[p01 + 0] + prevPixels[p11 + 0]) / 4);
                    currPixels[dst + 1] = (byte)((prevPixels[p00 + 1] + prevPixels[p10 + 1] + prevPixels[p01 + 1] + prevPixels[p11 + 1]) / 4);
                    currPixels[dst + 2] = (byte)((prevPixels[p00 + 2] + prevPixels[p10 + 2] + prevPixels[p01 + 2] + prevPixels[p11 + 2]) / 4);
                    currPixels[dst + 3] = (byte)((prevPixels[p00 + 3] + prevPixels[p10 + 3] + prevPixels[p01 + 3] + prevPixels[p11 + 3]) / 4);
                }
            }

            mips[m] = currPixels;
        }

        // 3. Identify Resident Mip Tail (mips with width <= 64 and height <= 64)
        int residentStartMip = 0;
        for (int m = 0; m < mipLevels; m++)
        {
            if (mipWidths[m] <= 64 && mipHeights[m] <= 64)
            {
                residentStartMip = m;
                break;
            }
        }
        uint residentCount = (uint)(mipLevels - residentStartMip);

        // 4. Build Cooked Texture Header & Mip Descriptors
        var header = new CookedTextureHeader
        {
            Magic = CookedTextureHeader.MagicValue,
            Version = CookedTextureHeader.CurrentVersion,
            Guid = guid,
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)mipLevels,
            ResidentMips = residentCount,
            Format = CookedTextureFormat.Rgba8Unorm,
            Flags = TextureFlags.HasMipmaps | (isSrgb ? TextureFlags.Srgb : TextureFlags.None),
        };

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        int headerSize = Marshal.SizeOf<CookedTextureHeader>();
        int mipDescriptorSize = Marshal.SizeOf<CookedTextureMip>();

        // Reserve space for Header + Mip Descriptors
        long payloadStartOffset = headerSize + (mipLevels * mipDescriptorSize);
        payloadStartOffset = (payloadStartOffset + 15L) & ~15L; // 16-byte aligned

        ms.Seek(payloadStartOffset, SeekOrigin.Begin);

        var mipRecords = new CookedTextureMip[mipLevels];
        ulong residentTailOffset = 0;
        uint residentTailSize = 0;

        for (int m = 0; m < mipLevels; m++)
        {
            long currentOffset = ms.Position;
            if (m == residentStartMip)
            {
                residentTailOffset = (ulong)currentOffset;
            }

            writer.Write(mips[m]);

            int remainder = (int)(ms.Position % 16);
            if (remainder != 0)
            {
                writer.Write(new byte[16 - remainder]);
            }

            mipRecords[m] = new CookedTextureMip
            {
                MipIndex = (uint)m,
                Width = (uint)mipWidths[m],
                Height = (uint)mipHeights[m],
                Offset = (ulong)currentOffset,
                Size = (uint)mips[m].Length,
            };

        }

        if (residentTailOffset != 0)
        {
            long residentTailEnd = ms.Position;
            residentTailSize = (uint)Math.Clamp(residentTailEnd - (long)residentTailOffset, 0, uint.MaxValue);
        }

        header.ResidentTailOffset = residentTailOffset;
        header.ResidentTailSize = residentTailSize;

        // Write Header
        ms.Seek(0, SeekOrigin.Begin);
        Span<byte> headerBytes = stackalloc byte[headerSize];
        MemoryMarshal.Write(headerBytes, in header);
        writer.Write(headerBytes);

        // Write Mip Descriptors
        Span<byte> mipBytes = stackalloc byte[mipDescriptorSize];
        for (int m = 0; m < mipLevels; m++)
        {
            MemoryMarshal.Write(mipBytes, in mipRecords[m]);
            writer.Write(mipBytes);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Cooks an image file (PNG/JPEG) into a .jtex binary blob.
    /// </summary>
    public static byte[] CookImageFile(string filePath, AssetGuid guid, bool isSrgb = true)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source image not found: {filePath}");

        var loaded = Jatayu.AssetFormats.Loaders.TextureLoader.LoadFromFile(filePath, isSrgb);
        return CookRgba8(guid, loaded.Width, loaded.Height, loaded.Data, isSrgb);
    }
}
