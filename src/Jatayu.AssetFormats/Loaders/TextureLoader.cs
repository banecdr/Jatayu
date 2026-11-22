/* =================================================================================================
 * FILE: TextureLoader.cs
 * LOCATION: src/Jatayu.AssetFormats/Loaders/TextureLoader.cs
 * 
 * WHAT THIS FILE DOES:
 *   Loads raw CPU image data from common image files (.png, .jpg, .bmp, .tga) using Stb.
 *   Provides clean, decoded byte arrays and dimensions decoupled from Vulkan and RHI runtime.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Decodes raw file bytes using Brutal.StbApi.Stb.Load or Stb.LoadFromMemory.
 *   2. Enforces standard 4-channel RGBA (or user-specified channel count).
 *   3. Copies the unmanaged pixel data into a managed byte[] array and disposes the native buffer.
 *   4. Returns a LoadedImageData record struct containing dimensions, channel count, and pixel bytes.
 * ================================================================================================= */

using System;
using System.IO;
using Brutal.StbApi;

namespace Jatayu.AssetFormats.Loaders;

/// <summary>
/// Pixel format classifications independent of graphics API.
/// </summary>
public enum ImagePixelFormat
{
    R8_Unorm,
    R8G8B8A8_Unorm,
    R8G8B8A8_Srgb,
    R16_Unorm,
    R32_Sfloat,
    Bc7_Unorm,
    Bc7_Srgb,
    Bc5_Unorm,
    Bc4_Unorm,
}

/// <summary>
/// Decoded CPU texture image data ready for GPU staging and upload.
/// </summary>
public sealed class LoadedImageData
{
    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public ImagePixelFormat Format { get; }
    public byte[] Data { get; }
    public int MipLevels { get; }

    public LoadedImageData(int width, int height, int channels, ImagePixelFormat format, byte[] data, int mipLevels = 1)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Format = format;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        MipLevels = Math.Max(1, mipLevels);
    }
}

/// <summary>
/// CPU image loader for reading texture files from disk or memory streams.
/// </summary>
public static class TextureLoader
{
    /// <summary>
    /// Loads an image file from disk and decodes it to 4-channel RGBA8 Unorm.
    /// </summary>
    public static unsafe LoadedImageData LoadFromFile(string filePath, bool isSrgb = false)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Texture file not found: {filePath}", filePath);

        var fileBytes = File.ReadAllBytes(filePath);
        return LoadFromMemory(fileBytes, isSrgb);
    }

    /// <summary>
    /// Decodes an encoded image buffer (PNG/JPG/BMP) to 4-channel RGBA8.
    /// </summary>
    public static unsafe LoadedImageData LoadFromMemory(ReadOnlySpan<byte> buffer, bool isSrgb = false)
    {
        using var stbImage = Stb.LoadFromMemory(buffer, 4);
        if (stbImage.IsNull() || stbImage.Data == null)
            throw new InvalidOperationException("Failed to decode image buffer using STB.");

        int width = stbImage.Width;
        int height = stbImage.Height;
        int byteCount = width * height * 4;

        byte[] pixelData = new byte[byteCount];
        fixed (byte* pDst = pixelData)
        {
            Buffer.MemoryCopy(stbImage.Data, pDst, byteCount, byteCount);
        }

        var format = isSrgb ? ImagePixelFormat.R8G8B8A8_Srgb : ImagePixelFormat.R8G8B8A8_Unorm;
        return new LoadedImageData(width, height, 4, format, pixelData);
    }

    /// <summary>
    /// Generates a procedural 2D checkerboard texture (useful for placeholder textures and debugging).
    /// </summary>
    public static LoadedImageData CreateCheckerboard(int width = 256, int height = 256, int tileSize = 32)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool isWhite = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                byte color = isWhite ? (byte)240 : (byte)40;
                int idx = (y * width + x) * 4;
                pixels[idx + 0] = color;
                pixels[idx + 1] = isWhite ? color : (byte)120; // Slight blue-tint for contrast
                pixels[idx + 2] = color;
                pixels[idx + 3] = 255;
            }
        }

        return new LoadedImageData(width, height, 4, ImagePixelFormat.R8G8B8A8_Unorm, pixels);
    }
}
