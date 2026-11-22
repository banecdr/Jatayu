/* =================================================================================================
 * FILE: VulkanSwapchain.cs
 * LOCATION: src/RHI/Vulkan/Swapchain/VulkanSwapchain.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages the Vulkan Swapchain (VkSwapchainKHR) and its associated display images and views.
 *   The swapchain is the fundamental bridge between our rendering engine and your physical monitor.
 *   It controls double/triple buffering, presentation modes (V-Sync on vs. off), surface formats,
 *   and dynamically recreates itself when you resize or restore the desktop window.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Surface Inspection:
 *      Queries physical device surface capabilities (min/max image count, dimensions, transforms)
 *      and selects the optimal surface format (preferring standard 32-bit sRGB: B8G8R8A8_SRGB).
 *
 *   2. Presentation Modes (V-Sync):
 *      - V-Sync ON:  Uses VkPresentModeKHR.FifoKHR (tear-free, locked to display refresh rate).
 *      - V-Sync OFF: Uses VkPresentModeKHR.ImmediateKHR or MailboxKHR (uncapped framerates).
 *
 *   3. Image Views Creation:
 *      Vulkan GPUs do not draw to raw VkImage handles directly. We create a VkImageView for each
 *      swapchain image, specifying how pixel data is routed into the color attachment.
 *
 *   4. Safe Resizing & Recreation (Zero Crashes):
 *      When you resize the window or change display modes, the driver invalidates the current swapchain
 *      (returning VK_ERROR_OUT_OF_DATE_KHR). The Recreate() method waits for the GPU to idle,
 *      safely destroys old image views, and builds a fresh swapchain matching the new pixel dimensions.
 *      If the window is minimized (0x0 pixels), recreation pauses cleanly until restored.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Context: The parent VulkanContext providing GPU device handles.
 *   - Window: The desktop window associated with this swapchain.
 *   - Handle: The underlying native VkSwapchainKHR handle.
 *   - Images: The list of raw VkImage display buffers managed by the swapchain.
 *   - ImageViews: The list of VkImageView handles used as color targets during rendering.
 *   - ColorFormat: The pixel color format (e.g. B8G8R8A8_SRGB).
 *   - Extent: The physical pixel width and height of the swapchain buffers.
 *   - AspectRatio: Width divided by Height (used to compute camera perspective projection).
 *   - AcquireNextImage: Asks the display hardware for the next available buffer to draw on.
 *   - Present: Submits the finished rendered buffer to your physical monitor.
 *   - Recreate: Rebuilds the swapchain to match new window dimensions.
 *
 * USAGE EXAMPLE:
 *   // 1. Create swapchain
 *   using var swapchain = new VulkanSwapchain(context, window);
 *
 *   // 2. In render loop: Acquire next image
 *   var result = swapchain.AcquireNextImage(imageAvailableSemaphore, out uint imageIndex);
 *   if (result == VkResult.ErrorOutOfDateKHR || swapchain.IsOutOfDate)
 *   {
 *       swapchain.Recreate();
 *       continue;
 *   }
 *
 *   // 3. Draw to swapchain.ImageViews[imageIndex]...
 *
 *   // 4. Present to screen
 *   swapchain.Present(graphicsQueue, renderFinishedSemaphore, imageIndex);
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.Core;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Swapchain;

/// <summary>
/// Manages the Vulkan presentation swapchain, display image views, and dynamic window resizing.
/// </summary>
public sealed class VulkanSwapchain : IDisposable
{
    private bool _disposed;
    private VkSwapchainKHR _swapchain;
    private VkImage[] _images = [];
    private VkImageView[] _imageViews = [];

    /// <summary>
    /// Gets the parent VulkanContext providing GPU device handles.
    /// </summary>
    public VulkanContext Context { get; }

    /// <summary>
    /// Gets the desktop window presenting this swapchain.
    /// </summary>
    public Window Window { get; }

    /// <summary>
    /// Gets the native Vulkan swapchain handle.
    /// </summary>
    public VkSwapchainKHR Handle => _swapchain;

    /// <summary>
    /// Gets the array of swapchain display images.
    /// </summary>
    public IReadOnlyList<VkImage> Images => _images;

    /// <summary>
    /// Gets the array of swapchain color image views.
    /// </summary>
    public IReadOnlyList<VkImageView> ImageViews => _imageViews;

    /// <summary>
    /// Gets the total number of display images in the swapchain (typically 2 or 3).
    /// </summary>
    public int ImageCount => _images.Length;

    /// <summary>
    /// Gets the surface color format (e.g. B8G8R8A8_SRGB).
    /// </summary>
    public VkFormat ColorFormat { get; private set; }

    /// <summary>
    /// Gets the surface color space (e.g. SrgbNonlinearKHR).
    /// </summary>
    public VkColorSpaceKHR ColorSpace { get; private set; }

    /// <summary>
    /// Gets the current pixel resolution (width and height) of the swapchain buffers.
    /// </summary>
    public VkExtent2D Extent { get; private set; }

    /// <summary>
    /// Gets the width of the swapchain in physical framebuffer pixels.
    /// </summary>
    public int Width => Extent.Width;

    /// <summary>
    /// Gets the height of the swapchain in physical framebuffer pixels.
    /// </summary>
    public int Height => Extent.Height;

    /// <summary>
    /// Gets the aspect ratio (width / height) used to construct 3D camera projection matrices.
    /// </summary>
    public float AspectRatio => Height > 0 ? (float)Width / Height : 1.0f;

    /// <summary>
    /// Gets or sets a value indicating whether the swapchain needs recreation (e.g. after a window resize).
    /// </summary>
    public bool IsOutOfDate { get; set; }

    /// <summary>
    /// Initializes a new instance of the VulkanSwapchain.
    /// </summary>
    /// <param name="context">The active VulkanContext.</param>
    /// <param name="window">The target desktop window.</param>
    public VulkanSwapchain(VulkanContext context, Window window)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Window = window ?? throw new ArgumentNullException(nameof(window));

        // Listen for window resize and VSync toggles to trigger recreation
        Window.Resized += OnWindowResized;
        Window.VSyncChanged += OnVSyncChanged;

        Create();
    }

    private void OnWindowResized(int width, int height)
    {
        IsOutOfDate = true;
    }

    private void OnVSyncChanged(bool enabled)
    {
        IsOutOfDate = true;
    }

    /// <summary>
    /// Creates or rebuilds the swapchain and all its image views.
    /// </summary>
    private unsafe void Create()
    {
        var physicalDevice = Context.PhysicalDevice;
        var surface = Context.Surface;
        var device = Context.Device;

        // 1. Query surface capabilities
        var capabilities = physicalDevice.GetSurfaceCapabilitiesKHR(surface);

        // Compute extent matching window framebuffer pixels
        var extent = capabilities.CurrentExtent;
        if (extent.Width == -1 && extent.Height == -1)
        {
            extent = new VkExtent2D(
                Math.Clamp(Window.FramebufferWidth, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(Window.FramebufferHeight, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
            );
        }
        Extent = extent;

        // 2. Select image count (request double buffering, clamped by hardware limits)
        var imageCount = Math.Max(capabilities.MinImageCount, 2);
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        // 3. Choose composite alpha mode
        VkCompositeAlphaFlagsKHR[] alphaModes =
        [
            VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.InheritBitKHR,
        ];
        var compositeAlpha = alphaModes.First(mode => (capabilities.SupportedCompositeAlpha & mode) != 0);

        // 4. Select surface format (simple sRGB: B8G8R8A8UNorm or R8G8B8A8UNorm with SRGBNonlinearKHR)
        Span<VkSurfaceFormatKHR> surfaceFormats = stackalloc VkSurfaceFormatKHR[physicalDevice.GetSurfaceFormatsKHR_Count(surface)];
        physicalDevice.GetSurfaceFormatsKHR_Write(surface, surfaceFormats);

        var chosenFormat = surfaceFormats[0];
        foreach (var format in surfaceFormats)
        {
            if ((format.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm) &&
                format.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
            {
                chosenFormat = format;
                break;
            }
        }

        ColorFormat = chosenFormat.Format;
        ColorSpace = chosenFormat.ColorSpace;

        // 5. Select present mode based on VSync setting
        var presentMode = Window.VSync ? VkPresentModeKHR.FifoKHR : VkPresentModeKHR.ImmediateKHR;

        // 6. Create Swapchain handle
        var createInfo = new VkSwapchainCreateInfoKHR
        {
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = ColorFormat,
            ImageColorSpace = ColorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = compositeAlpha,
            PresentMode = presentMode,
            Clipped = true,
        };

        _swapchain = device.CreateSwapchainKHR(createInfo, null);

        // 7. Retrieve swapchain images
        _images = new VkImage[device.GetSwapchainImagesKHR_Count(_swapchain)];
        device.GetSwapchainImagesKHR_Write(_swapchain, _images);

        // 8. Create image views for all images
        var colorRange = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };

        _imageViews = new VkImageView[_images.Length];
        for (var i = 0; i < _images.Length; i++)
        {
            _imageViews[i] = device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = _images[i],
                    ViewType = VkImageViewType._2D,
                    Format = ColorFormat,
                    SubresourceRange = colorRange,
                },
                null
            );
        }

        IsOutOfDate = false;
    }

    /// <summary>
    /// Recreates the swapchain when the window is resized or the display mode changes.
    /// Safely waits for all pending GPU work to finish and cleans up old image views.
    /// </summary>
    public void Recreate()
    {
        // If window is minimized (zero pixel area), pause recreation
        if (Window.FramebufferWidth == 0 || Window.FramebufferHeight == 0)
            return;

        // Block CPU until GPU completes all executing commands
        Context.WaitIdle();

        // Destroy previous image views and swapchain
        Cleanup();

        // Build new swapchain with updated dimensions
        Create();
    }

    /// <summary>
    /// Acquires the next available swapchain image index for rendering.
    /// </summary>
    /// <param name="semaphore">Semaphore signaled when the image is ready for writing.</param>
    /// <param name="imageIndex">Outputs the index of the acquired swapchain image.</param>
    /// <param name="timeoutNanoseconds">Timeout in nanoseconds (defaults to -1 for infinite wait).</param>
    /// <returns>The VkResult of the operation.</returns>
    public VkResult AcquireNextImage(VkSemaphore semaphore, out int imageIndex, nint timeoutNanoseconds = -1)
    {
        try
        {
            var result = (VkResult)Context.Device.AcquireNextImageKHR(_swapchain, timeoutNanoseconds, semaphore, VkFence.NullHandle, out imageIndex);
            if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            {
                IsOutOfDate = true;
            }
            return result;
        }
        catch (VkResultError ex)
        {
            IsOutOfDate = true;
            imageIndex = -1;
            return ex.Result;
        }
    }

    /// <summary>
    /// Submits the rendered image to the presentation engine to display on screen.
    /// </summary>
    /// <param name="queue">The presentation queue.</param>
    /// <param name="waitSemaphore">Semaphore to wait on before presenting.</param>
    /// <param name="imageIndex">The image index to present.</param>
    /// <returns>The VkResult of the presentation.</returns>
    public unsafe VkResult Present(Queue queue, VkSemaphore waitSemaphore, int imageIndex)
    {
        var swapchainHandle = _swapchain;
        var presentInfo = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &waitSemaphore,
            SwapchainCount = 1,
            Swapchains = &swapchainHandle,
            ImageIndices = &imageIndex,
        };

        try
        {
            var result = (VkResult)queue.PresentKHR(presentInfo);
            if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            {
                IsOutOfDate = true;
            }
            return result;
        }
        catch (VkResultError ex)
        {
            IsOutOfDate = true;
            return ex.Result;
        }
    }

    private void Cleanup()
    {
        var device = Context.Device;

        foreach (var imageView in _imageViews)
        {
            if (imageView.VkHandle != 0)
                device.DestroyImageView(imageView, null);
        }
        _imageViews = [];

        if (_swapchain.VkHandle != 0)
        {
            device.DestroySwapchainKHR(_swapchain, null);
            _swapchain = default;
        }
    }

    /// <summary>
    /// Disposes the swapchain and all associated image views.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Window.Resized -= OnWindowResized;
            Window.VSyncChanged -= OnVSyncChanged;
            Context.WaitIdle();
            Cleanup();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
