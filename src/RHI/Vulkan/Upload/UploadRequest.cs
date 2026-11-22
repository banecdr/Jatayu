/* =================================================================================================
 * FILE: UploadRequest.cs
 * LOCATION: src/RHI/Vulkan/Upload/UploadRequest.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines request descriptors for asynchronous GPU buffer and texture uploads.
 *   These structs encapsulate copy dimensions, offsets, subresource layers, and target layouts.
 * ================================================================================================= */

using Brutal.VulkanApi;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.RHI.Vulkan.Upload;

/// <summary>
/// Describes a pending buffer upload operation.
/// </summary>
public readonly struct BufferUploadRequest
{
    /// <summary>
    /// Gets the destination GPU buffer.
    /// </summary>
    public GpuBuffer TargetBuffer { get; }

    /// <summary>
    /// Byte offset within the destination buffer.
    /// </summary>
    public ulong DestinationOffset { get; }

    /// <summary>
    /// Offset within the staging buffer containing the source data.
    /// </summary>
    public ulong StagingOffset { get; }

    /// <summary>
    /// Number of bytes to transfer.
    /// </summary>
    public ulong SizeInBytes { get; }

    public BufferUploadRequest(GpuBuffer targetBuffer, ulong destinationOffset, ulong stagingOffset, ulong sizeInBytes)
    {
        TargetBuffer = targetBuffer;
        DestinationOffset = destinationOffset;
        StagingOffset = stagingOffset;
        SizeInBytes = sizeInBytes;
    }
}

/// <summary>
/// Describes a pending 2D image / mip upload operation.
/// </summary>
public readonly struct ImageUploadRequest
{
    /// <summary>
    /// The destination raw Vulkan image.
    /// </summary>
    public VkImage TargetImage { get; }

    /// <summary>
    /// The pixel format of the image.
    /// </summary>
    public VkFormat Format { get; }

    /// <summary>
    /// Width of this subresource / mip in pixels.
    /// </summary>
    public uint Width { get; }

    /// <summary>
    /// Height of this subresource / mip in pixels.
    /// </summary>
    public uint Height { get; }

    /// <summary>
    /// Mip level index being uploaded.
    /// </summary>
    public uint MipLevel { get; }

    /// <summary>
    /// Array layer index.
    /// </summary>
    public uint ArrayLayer { get; }

    /// <summary>
    /// Offset in the staging ring containing raw pixel bytes.
    /// </summary>
    public ulong StagingOffset { get; }

    /// <summary>
    /// Number of bytes in this subresource slice.
    /// </summary>
    public ulong DataSize { get; }

    /// <summary>
    /// Final image layout desired after upload completes (typically ShaderReadOnlyOptimal).
    /// </summary>
    public VkImageLayout FinalLayout { get; }

    public ImageUploadRequest(
        VkImage targetImage,
        VkFormat format,
        uint width,
        uint height,
        uint mipLevel,
        uint arrayLayer,
        ulong stagingOffset,
        ulong dataSize,
        VkImageLayout finalLayout)
    {
        TargetImage = targetImage;
        Format = format;
        Width = width;
        Height = height;
        MipLevel = mipLevel;
        ArrayLayer = arrayLayer;
        StagingOffset = stagingOffset;
        DataSize = dataSize;
        FinalLayout = finalLayout;
    }
}
