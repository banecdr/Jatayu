/* =================================================================================================
 * FILE: GpuTexture.cs
 * LOCATION: src/RHI/Vulkan/Textures/GpuTexture.cs
 * 
 * WHAT THIS FILE DOES:
 *   Encapsulates 2D GPU textures and texture arrays backed by high-speed VRAM via VMA.
 *   Manages image creation, format mappings, pipeline layout transitions, image view generation,
 *   and synchronous CPU-to-GPU data staging/uploads (Sprint 3a Decision D14).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Allocates a VkImage using VMA (VmaMemoryUsage.GpuOnly) with Sampled and TransferDst bits.
 *   2. Creates a 2D VkImageView for sampling inside shaders.
 *   3. Implements TransitionLayout() using Vulkan pipeline barriers with automatic access mask calculation.
 *   4. Provides CreateFromImageData() helper: stages pixel data in a host-visible staging buffer,
 *      transitions layout to TransferDstOptimal, copies buffer to image via vkCmdCopyBufferToImage,
 *      and transitions to ShaderReadOnlyOptimal.
 *   5. Implements IDisposable for safe resource destruction via allocator.DestroyImage and device.DestroyImageView.
 * ================================================================================================= */

using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.Loaders;
using Jatayu.RHI.Vulkan.Commands;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;

namespace Jatayu.RHI.Vulkan.Textures;

/// <summary>
/// Encapsulates a 2D GPU texture backed by VMA with mipmaps and image view management.
/// </summary>
public sealed class GpuTexture : IDisposable
{
    private bool _disposed;
    private VkImage _image;
    private VmaAllocation _allocation;
    private VkImageView _imageView;

    /// <summary>
    /// Gets the parent VulkanContext.
    /// </summary>
    public VulkanContext Context { get; }

    /// <summary>
    /// Gets the GPU memory allocator.
    /// </summary>
    public GpuAllocator Allocator { get; }

    /// <summary>
    /// Gets the native Vulkan image handle.
    /// </summary>
    public VkImage Image => _image;

    /// <summary>
    /// Gets the VMA allocation handle.
    /// </summary>
    public VmaAllocation Allocation => _allocation;

    /// <summary>
    /// Gets the native Vulkan image view handle for descriptor binding.
    /// </summary>
    public VkImageView ImageView => _imageView;

    /// <summary>
    /// Gets the texture width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the texture height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of mip levels.
    /// </summary>
    public int MipLevels { get; }

    /// <summary>
    /// Gets the Vulkan image format.
    /// </summary>
    public VkFormat Format { get; }

    /// <summary>
    /// Gets the current Vulkan image layout.
    /// </summary>
    public VkImageLayout CurrentLayout { get; private set; }

    /// <summary>
    /// Initializes a new GpuTexture from existing image and allocation handles.
    /// </summary>
    public GpuTexture(
        VulkanContext context,
        GpuAllocator allocator,
        VkImage image,
        VmaAllocation allocation,
        VkImageView imageView,
        int width,
        int height,
        VkFormat format,
        int mipLevels = 1,
        VkImageLayout initialLayout = VkImageLayout.Undefined)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _image = image;
        _allocation = allocation;
        _imageView = imageView;
        Width = width;
        Height = height;
        Format = format;
        MipLevels = mipLevels;
        CurrentLayout = initialLayout;
    }

    /// <summary>
    /// Creates an uninitialized 2D GPU texture.
    /// </summary>
    public static unsafe GpuTexture Create2D(
        VulkanContext context,
        GpuAllocator allocator,
        int width,
        int height,
        VkFormat format,
        VkImageUsageFlags usage,
        int mipLevels = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allocator);

        int graphicsFamily = context.QueueFamilies.GraphicsFamily ?? 0;
        int? transferFamily = context.QueueFamilies.TransferFamily;
        bool hasDedicatedTransfer = transferFamily.HasValue && transferFamily.Value != graphicsFamily;

        int[] queueFamilyIndices = hasDedicatedTransfer
            ? [graphicsFamily, transferFamily!.Value]
            : [graphicsFamily];

        VkImage image;
        VmaAllocation allocation;

        fixed (int* pQueueFamilies = queueFamilyIndices)
        {
            var imageCreateInfo = new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D(width, height, 1),
                MipLevels = Math.Max(1, mipLevels),
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = hasDedicatedTransfer ? VkSharingMode.Concurrent : VkSharingMode.Exclusive,
                QueueFamilyIndexCount = hasDedicatedTransfer ? 2 : 0,
                QueueFamilyIndices = hasDedicatedTransfer ? pQueueFamilies : null,
                InitialLayout = VkImageLayout.Undefined,
            };

            allocator.CreateImage(
                imageCreateInfo,
                VmaMemoryUsage.GpuOnly,
                VmaAllocationCreateFlags.None,
                out image,
                out allocation,
                out _
            );
        }

        var viewCreateInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = format,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = Math.Max(1, mipLevels),
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var imageView = context.Device.CreateImageView(viewCreateInfo, null);

        return new GpuTexture(
            context,
            allocator,
            image,
            allocation,
            imageView,
            width,
            height,
            format,
            mipLevels,
            VkImageLayout.Undefined
        );
    }

    /// <summary>
    /// Creates and uploads a 2D texture synchronously from decoded CPU image data.
    /// TEMPORARY(Sprint 3b/4): Synchronous upload helper per Decision D14; replaced by UploadManager in Sprint 3b.
    /// </summary>
    public static unsafe GpuTexture CreateFromImageData(
        VulkanContext context,
        GpuAllocator allocator,
        CommandPoolManager commandPool,
        Queue queue,
        LoadedImageData imageData)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(commandPool);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(imageData);

        var format = MapPixelFormat(imageData.Format);
        int width = imageData.Width;
        int height = imageData.Height;

        // 1. Create GPU texture with Sampled and TransferDst usage
        var texture = Create2D(
            context,
            allocator,
            width,
            height,
            format,
            VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
            imageData.MipLevels
        );

        // 2. Stage pixel data in a host-visible staging buffer
        using var stagingBuffer = allocator.CreateBufferWithData(
            imageData.Data,
            VkBufferUsageFlags.TransferSrcBit
        );

        // 3. Record and submit synchronous transfer command
        commandPool.ExecuteImmediate(queue, cmd =>
        {
            // Transition image layout: Undefined -> TransferDstOptimal
            texture.TransitionLayout(
                cmd,
                VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.TopOfPipeBit,
                VkPipelineStageFlags.TransferBit
            );

            // Copy staging buffer to GPU texture image
            var region = new VkBufferImageCopy
            {
                BufferOffset = new ByteSize64(0),
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new VkOffset3D(0, 0, 0),
                ImageExtent = new VkExtent3D(width, height, 1),
            };

            context.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(
                cmd.Handle,
                stagingBuffer.Handle,
                texture.Image,
                VkImageLayout.TransferDstOptimal,
                1,
                &region
            );

            // Transition image layout: TransferDstOptimal -> ShaderReadOnlyOptimal
            texture.TransitionLayout(
                cmd,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.FragmentShaderBit
            );
        });

        return texture;
    }

    /// <summary>
    /// Records a pipeline barrier to transition the image layout.
    /// </summary>
    public void TransitionLayout(
        CommandBuffer cmd,
        VkImageLayout newLayout,
        VkPipelineStageFlags srcStage = VkPipelineStageFlags.AllCommandsBit,
        VkPipelineStageFlags dstStage = VkPipelineStageFlags.AllCommandsBit,
        int srcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        int dstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED)
    {
        if (CurrentLayout == newLayout && srcQueueFamilyIndex == dstQueueFamilyIndex)
            return;

        var barrier = new VkImageMemoryBarrier
        {
            OldLayout = CurrentLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = srcQueueFamilyIndex,
            DstQueueFamilyIndex = dstQueueFamilyIndex,
            Image = _image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcAccessMask = GetSrcAccessMask(CurrentLayout),
            DstAccessMask = GetDstAccessMask(newLayout),
        };

        cmd.PipelineBarrier(
            srcStage,
            dstStage,
            VkDependencyFlags.None,
            [],
            [],
            [barrier]
        );

        CurrentLayout = newLayout;
    }

    private static VkAccessFlags GetSrcAccessMask(VkImageLayout layout)
    {
        return layout switch
        {
            VkImageLayout.Undefined => VkAccessFlags.None,
            VkImageLayout.TransferDstOptimal => VkAccessFlags.TransferWriteBit,
            VkImageLayout.TransferSrcOptimal => VkAccessFlags.TransferReadBit,
            VkImageLayout.ColorAttachmentOptimal => VkAccessFlags.ColorAttachmentWriteBit,
            VkImageLayout.ShaderReadOnlyOptimal => VkAccessFlags.ShaderReadBit,
            _ => VkAccessFlags.None,
        };
    }

    private static VkAccessFlags GetDstAccessMask(VkImageLayout layout)
    {
        return layout switch
        {
            VkImageLayout.TransferDstOptimal => VkAccessFlags.TransferWriteBit,
            VkImageLayout.TransferSrcOptimal => VkAccessFlags.TransferReadBit,
            VkImageLayout.ColorAttachmentOptimal => VkAccessFlags.ColorAttachmentWriteBit,
            VkImageLayout.ShaderReadOnlyOptimal => VkAccessFlags.ShaderReadBit,
            _ => VkAccessFlags.None,
        };
    }

    public static VkFormat MapPixelFormat(ImagePixelFormat format)
    {
        return format switch
        {
            ImagePixelFormat.R8_Unorm => VkFormat.R8UNorm,
            ImagePixelFormat.R8G8B8A8_Unorm => VkFormat.R8G8B8A8UNorm,
            ImagePixelFormat.R8G8B8A8_Srgb => VkFormat.R8G8B8A8SRGB,
            ImagePixelFormat.R16_Unorm => VkFormat.R16UNorm,
            ImagePixelFormat.R32_Sfloat => VkFormat.R32SFloat,
            ImagePixelFormat.Bc7_Unorm => VkFormat.Bc7UNormBlock,
            ImagePixelFormat.Bc7_Srgb => VkFormat.Bc7SRGBBlock,
            ImagePixelFormat.Bc5_Unorm => VkFormat.Bc5UNormBlock,
            ImagePixelFormat.Bc4_Unorm => VkFormat.Bc4UNormBlock,
            _ => VkFormat.R8G8B8A8UNorm,
        };
    }

    /// <summary>
    /// Disposes the native Vulkan image view and memory allocation.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_imageView.VkHandle != 0)
            {
                Context.Device.DestroyImageView(_imageView, null);
                _imageView = default;
            }

            if (_image.VkHandle != 0)
            {
                Allocator.DestroyImage(_image, _allocation);
                _image = default;
                _allocation = default;
            }

            _disposed = true;
        }
    }
}
