/* =================================================================================================
 * FILE: QueueFamilies.cs
 * LOCATION: src/RHI/Vulkan/Context/QueueFamilies.cs
 * 
 * WHAT THIS FILE DOES:
 *   In Vulkan, your graphics card doesn't just execute commands wherever it wants. Instead, the 
 *   GPU hardware exposes "Queue Families" (pipelines/conveyor belts). Different queue families 
 *   specialize in different tasks: drawing triangles (Graphics), sending pictures to your monitor 
 *   (Presentation), running GPU compute algorithms like physics (Compute), or quickly copying 
 *   memory (Transfer).
 * 
 *   This file queries your physical GPU, searches through all its available queue families, and 
 *   finds the exact hardware indices for Graphics, Presentation, Compute, and Transfer.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Hardware Inspection:
 *      It calls physicalDevice.GetQueueFamilyProperties to inspect every queue family the GPU 
 *      supports, checking the flag bits (VkQueueFlags.GraphicsBit, ComputeBit, TransferBit).
 *
 *   2. Presentation Support Check:
 *      Not all queue families can display images to a window! It calls GetSurfaceSupportKHR() to 
 *      confirm that the queue family can talk to our GLFW window surface (VkSurfaceKHR).
 *
 *   3. Dedicated Queue Discovery:
 *      - While a Graphics queue can technically do compute and transfers, modern GPUs often have 
 *        dedicated compute and transfer hardware that runs in parallel without stalling rendering.
 *      - This class tries to find dedicated queues where possible, and gracefully falls back to 
 *        the main graphics queue if no separate queue exists.
 *
 *   4. Unique Queue Family Consolidation:
 *      On most modern desktop GPUs (NVIDIA/AMD), the Graphics queue and the Presentation queue 
 *      share the same family index (e.g., Family 0). On some laptops or mobile GPUs, they might 
 *      be different. GetUniqueIndices() returns only the distinct indices so the logical device 
 *      doesn't crash by trying to create duplicate queues for the same index.
 *
 * PROPERTIES & METHODS (IN PLAIN ENGLISH):
 *   - GraphicsFamily: The index of the queue family that can draw geometry and execute shaders.
 *   - PresentFamily: The index of the queue family that can flip swapchain images to your screen.
 *   - ComputeFamily: The index of the queue family for GPU compute (particles, culling, physics).
 *   - TransferFamily: The index of the queue family for fast DMA memory copying from CPU to GPU.
 *   - IsComplete: Returns true if at least Graphics and Presentation were found (minimum needed to render).
 *   - IsUnified: Returns true if Graphics and Presentation share the same queue (enables optimal exclusive mode).
 *   - Find(device, surface): Scans the GPU and returns a populated QueueFamilies instance.
 *   - GetUniqueIndices(): Returns the list of unique indices needed to build the logical device.
 * ================================================================================================= */

using Brutal.VulkanApi;

namespace HelloBrutal.RHI.Vulkan.Context;

/// <summary>
/// Discovers and tracks GPU queue family indices for Graphics, Presentation, Compute, and Transfer.
/// </summary>
public sealed class QueueFamilies
{
    /// <summary>
    /// Index of the queue family supporting graphics rendering commands.
    /// </summary>
    public int? GraphicsFamily { get; private set; }

    /// <summary>
    /// Index of the queue family capable of presenting images to the window surface.
    /// </summary>
    public int? PresentFamily { get; private set; }

    /// <summary>
    /// Index of the queue family supporting compute shaders.
    /// </summary>
    public int? ComputeFamily { get; private set; }

    /// <summary>
    /// Index of the queue family supporting memory transfer operations.
    /// </summary>
    public int? TransferFamily { get; private set; }

    /// <summary>
    /// Returns true if the minimum required queues for rendering to a window were found (Graphics + Present).
    /// </summary>
    public bool IsComplete => GraphicsFamily.HasValue && PresentFamily.HasValue;

    /// <summary>
    /// Returns true if Graphics and Present share the same queue family index.
    /// This is true on 99% of desktop GPUs and allows the swapchain to use optimal exclusive sharing mode.
    /// </summary>
    public bool IsUnified => GraphicsFamily.HasValue && GraphicsFamily == PresentFamily;

    /// <summary>
    /// Scans a physical device (GPU) and finds all supported queue families for the given window surface.
    /// </summary>
    /// <param name="physicalDevice">The physical GPU to inspect.</param>
    /// <param name="surface">The window surface to test presentation support against.</param>
    /// <returns>A populated <see cref="QueueFamilies"/> object.</returns>
    public static QueueFamilies Find(PhysicalDevice physicalDevice, VkSurfaceKHR surface)
    {
        var result = new QueueFamilies();

        var count = physicalDevice.GetQueueFamilyProperties_Count();
        var families = new VkQueueFamilyProperties[count];
        physicalDevice.GetQueueFamilyProperties_Write(families);

        // 1. First pass: Find graphics and presentation
        for (var i = 0; i < families.Length; i++)
        {
            var flags = families[i].QueueFlags;

            // Check graphics support
            if ((flags & VkQueueFlags.GraphicsBit) != 0 && !result.GraphicsFamily.HasValue)
            {
                result.GraphicsFamily = i;
            }

            // Check presentation support to our window surface
            if (physicalDevice.GetSurfaceSupportKHR(i, surface) && !result.PresentFamily.HasValue)
            {
                result.PresentFamily = i;
            }

            // If we found a unified queue that does both, prioritize it!
            if ((flags & VkQueueFlags.GraphicsBit) != 0 && physicalDevice.GetSurfaceSupportKHR(i, surface))
            {
                result.GraphicsFamily = i;
                result.PresentFamily = i;
                break;
            }
        }

        // 2. Second pass: Find compute and transfer (preferring dedicated queues if available)
        for (var i = 0; i < families.Length; i++)
        {
            var flags = families[i].QueueFlags;

            // Dedicated compute queue (has compute but not graphics)
            if ((flags & VkQueueFlags.ComputeBit) != 0 && (flags & VkQueueFlags.GraphicsBit) == 0)
            {
                result.ComputeFamily = i;
            }

            // Dedicated transfer queue (has transfer but not graphics or compute)
            if ((flags & VkQueueFlags.TransferBit) != 0 && 
                (flags & VkQueueFlags.GraphicsBit) == 0 && 
                (flags & VkQueueFlags.ComputeBit) == 0)
            {
                result.TransferFamily = i;
            }
        }

        // 3. Fallbacks: If dedicated queues weren't found, standard Vulkan spec guarantees
        // that any Graphics queue family also supports Compute and Transfer.
        if (!result.ComputeFamily.HasValue && result.GraphicsFamily.HasValue)
        {
            result.ComputeFamily = result.GraphicsFamily;
        }

        if (!result.TransferFamily.HasValue && result.GraphicsFamily.HasValue)
        {
            result.TransferFamily = result.GraphicsFamily;
        }

        return result;
    }

    /// <summary>
    /// Returns the distinct set of queue family indices required to create the logical device.
    /// Consolidates duplicate indices to avoid Vulkan creation errors.
    /// </summary>
    public int[] GetUniqueIndices()
    {
        var set = new HashSet<int>();

        if (GraphicsFamily.HasValue) set.Add(GraphicsFamily.Value);
        if (PresentFamily.HasValue) set.Add(PresentFamily.Value);
        if (ComputeFamily.HasValue) set.Add(ComputeFamily.Value);
        if (TransferFamily.HasValue) set.Add(TransferFamily.Value);

        return [.. set];
    }
}
