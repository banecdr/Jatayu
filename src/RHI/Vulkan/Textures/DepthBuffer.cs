/* =================================================================================================
 * FILE: DepthBuffer.cs
 * LOCATION: src/RHI/Vulkan/Textures/DepthBuffer.cs
 * 
 * WHAT THIS FILE DOES:
 *   Encapsulates the GPU Depth Buffer (Z-Buffer) image attachment for modern Vulkan 1.3 rendering.
 *   
 *   In 3D graphics, when multiple objects (or faces of the same 3D mesh) overlap on screen, the GPU
 *   must determine which surfaces are in front and which are hidden behind. The Depth Buffer stores
 *   the distance (Z-value) from the camera for every pixel rendered. If a new incoming pixel is 
 *   further away than what is already stored in the depth buffer, the GPU discards it (depth occlusion).
 *   Without a depth buffer, 3D objects will appear inside-out, transparent, or scrambled.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Hardware Format Selection:
 *      Scans the GPU physical device for the best supported 32-bit floating-point or 24-bit depth format
 *      (D32SFloat, D32SFloatS8UInt, or D24UNormS8UInt) with optimal tiling depth/stencil support.
 *
 *   2. VMA GPU Memory Allocation:
 *      Allocates a 2D VkImage directly on dedicated VRAM (VmaMemoryUsage.GpuOnly) using our GpuAllocator
 *      with the VkImageUsageFlags.DepthStencilAttachmentBit flag.
 *
 *   3. ImageView Creation:
 *      Constructs a VkImageView configured with VkImageAspectFlags.DepthBit so the Vulkan dynamic
 *      rendering pass can attach it directly to command buffer rendering info.
 *
 *   4. Zero-Crash Dynamic Resize:
 *      Exposes Recreate(width, height) to seamlessly recreate the depth texture whenever the desktop
 *      window is resized, matching the swapchain dimensions perfectly.
 *
 *   5. Vulkan 1.3 Dynamic Rendering Integration:
 *      Provides a GetAttachmentInfo() helper that produces a ready-to-use VkRenderingAttachmentInfo
 *      configured to clear depth to 1.0 (furthest possible distance) and perform depth testing.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Context: The parent VulkanContext providing GPU handles.
 *   - Allocator: The GpuAllocator managing GPU memory sub-allocations.
 *   - Format: The chosen Vulkan depth format (e.g. VkFormat.D32SFloat).
 *   - Width / Height: Current resolution in framebuffer pixels.
 *   - Image: The raw native VkImage handle.
 *   - ImageView: The native VkImageView handle bound to dynamic rendering.
 *   - Extent: VkExtent2D describing the dimensions.
 *
 * USAGE EXAMPLES:
 *   // 1. Initialize depth buffer matching swapchain size
 *   using var depthBuffer = new DepthBuffer(context, allocator, swapchain.Width, swapchain.Height);
 *
 *   // 2. On window resize:
 *   depthBuffer.Recreate(newWidth, newHeight);
 *
 *   // 3. Attach to Vulkan 1.3 dynamic rendering:
 *   var depthAttachment = depthBuffer.GetAttachmentInfo();
 *   var renderingInfo = new VkRenderingInfo
 *   {
 *       RenderArea = new VkRect2D(swapchain.Extent),
 *       ColorAttachmentCount = 1,
 *       ColorAttachments = &colorAttachment,
 *       DepthAttachment = &depthAttachment,
 *   };
 *   cmd.BeginRendering(renderingInfo);
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.Renderer.Camera;

namespace Jatayu.RHI.Vulkan.Textures;

/// <summary>
/// Encapsulates a Vulkan 1.3 depth attachment image and image view for 3D depth testing.
/// Backed by high-speed VRAM using the AMD Vulkan Memory Allocator.
/// </summary>
public sealed class DepthBuffer : IDisposable
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
    /// Gets the hardware-supported depth format (e.g. D32SFloat or D24UNormS8UInt).
    /// </summary>
    public VkFormat Format { get; }

    /// <summary>
    /// Gets the depth buffer width in pixels.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// Gets the depth buffer height in pixels.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// Gets the depth buffer dimensions as a VkExtent2D.
    /// </summary>
    public VkExtent2D Extent => new(Width, Height);

    /// <summary>
    /// Gets the underlying native Vulkan image handle.
    /// </summary>
    public VkImage Image => _image;

    /// <summary>
    /// Gets the native Vulkan image view handle bound to dynamic rendering.
    /// </summary>
    public VkImageView ImageView => _imageView;

    /// <summary>
    /// Initializes a new DepthBuffer with optimal GPU hardware format selection.
    /// </summary>
    /// <param name="context">Active VulkanContext.</param>
    /// <param name="allocator">GPU memory allocator.</param>
    /// <param name="width">Initial width in physical pixels.</param>
    /// <param name="height">Initial height in physical pixels.</param>
    public DepthBuffer(VulkanContext context, GpuAllocator allocator, int width, int height)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        Format = FindSupportedDepthFormat(context);

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        Create();
    }

    /// <summary>
    /// Queries the physical GPU device for the highest fidelity supported depth format.
    /// Prioritizes 32-bit floating point depth (D32SFloat) for maximum precision.
    /// </summary>
    private static VkFormat FindSupportedDepthFormat(VulkanContext context)
    {
        VkFormat[] candidates =
        [
            VkFormat.D32SFloat,
            VkFormat.D32SFloatS8UInt,
            VkFormat.D24UNormS8UInt,
        ];

        var physicalDevice = context.PhysicalDevice;

        foreach (var format in candidates)
        {
            var props = physicalDevice.GetFormatProperties(format);
            if ((props.OptimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachmentBit) != 0)
            {
                return format;
            }
        }

        // Fallback default
        return VkFormat.D32SFloat;
    }

    /// <summary>
    /// Allocates the depth image on the GPU and creates its view.
    /// </summary>
    private unsafe void Create()
    {
        var device = Context.Device;

        var imageCreateInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = Format,
            Extent = new VkExtent3D(Width, Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };

        Allocator.CreateImage(
            imageCreateInfo,
            VmaMemoryUsage.GpuOnly,
            VmaAllocationCreateFlags.None,
            out _image,
            out _allocation,
            out _
        );

        var viewCreateInfo = new VkImageViewCreateInfo
        {
            Image = _image,
            ViewType = VkImageViewType._2D,
            Format = Format,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        _imageView = device.CreateImageView(viewCreateInfo, null);
    }

    /// <summary>
    /// Recreates the depth buffer when the window or swapchain is resized.
    /// </summary>
    /// <param name="newWidth">New width in pixels.</param>
    /// <param name="newHeight">New height in pixels.</param>
    public void Recreate(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0)
            return;

        Destroy();

        Width = newWidth;
        Height = newHeight;

        Create();
    }

    /// <summary>
    /// Constructs a VkRenderingAttachmentInfo for Vulkan 1.3 dynamic rendering.
    /// </summary>
    /// <param name="loadOp">Clear or Load operation (default: Clear).</param>
    /// <param name="storeOp">Store or DontCare operation (default: DontCare for transient depth).</param>
    /// <param name="clearDepth">Clear depth value (0.0 = furthest distance under Reverse-Z).</param>
    /// <returns>A populated attachment info struct.</returns>
    public VkRenderingAttachmentInfo GetAttachmentInfo(
        VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear,
        VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.DontCare,
        float clearDepth = DepthConvention.ClearDepth)
    {
        return new VkRenderingAttachmentInfo
        {
            ImageView = _imageView,
            ImageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = loadOp,
            StoreOp = storeOp,
            ClearValue = new VkClearDepthStencilValue { Depth = clearDepth, Stencil = 0 },
        };
    }

    /// <summary>
    /// Releases the native image view and returns the GPU allocation back to VMA.
    /// </summary>
    private unsafe void Destroy()
    {
        var device = Context.Device;

        if (_imageView.VkHandle != 0)
        {
            device.DestroyImageView(_imageView, null);
            _imageView = default;
        }

        if (_image.VkHandle != 0 && !_allocation.IsNull())
        {
            Allocator.DestroyImage(_image, _allocation);
            _image = default;
            _allocation = default;
        }
    }

    /// <summary>
    /// Disposes the depth buffer and frees all associated GPU resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Destroy();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
