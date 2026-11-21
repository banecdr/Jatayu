/* =================================================================================================
 * FILE: VulkanContext.cs
 * LOCATION: src/RHI/Vulkan/Context/VulkanContext.cs
 * 
 * WHAT THIS FILE DOES:
 *   This is the master anchor for everything Vulkan in the entire engine. It initializes the 
 *   Vulkan 1.3 graphics driver, connects to your GLFW window surface, selects a suitable physical 
 *   graphics card (GPU) on your computer, creates the logical Device, enables modern 1.3 Dynamic 
 *   Rendering, and retrieves the active hardware Queues (Graphics and Present).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Loader & Instance Setup:
 *      Touching Brutal.VulkanApi.Vulkan.GetInstanceProcAddr ensures Brutal's native Vulkan loader 
 *      (libVulkanEx) is ready before GLFW queries anything. It asks GLFW for the required OS 
 *      window extensions, adds platform requirements, and creates a Vulkan 1.3 Instance.
 *
 *   2. Window Surface Creation:
 *      It takes your Window.GlfwHandle and calls Glfw.CreateWindowSurface() to create a native 
 *      VkSurfaceKHR. This surface is the bridge where Vulkan draws into your OS window.
 *
 *   3. GPU Selection (Physical Device):
 *      It scans all graphics cards installed on your PC. Using QueueFamilies.Find(), it filters 
 *      out cards that cannot draw or present to your window. If a GPU supports unified graphics 
 *      and presentation (true on 99% of desktop cards), it selects it immediately.
 *
 *   4. Logical Device & Modern Vulkan 1.3 Dynamic Rendering:
 *      Vulkan 1.3 introduces Dynamic Rendering (DynamicRendering = true). This is a monumental 
 *      feature that completely eliminates legacy VkRenderPass and VkFramebuffer boilerplate! 
 *      It builds the logical device with queue create infos for all unique queue families.
 *
 *   5. Queue Retrieval:
 *      Retrieves the hardware Queue instances for submitting graphics commands and presenting 
 *      images to the screen.
 *
 *   6. Clean Disposal:
 *      When the engine shuts down, WaitIdle() ensures the GPU is finished executing commands, 
 *      and destroys the Device, Surface, and Instance in safe reverse order.
 *
 * PROPERTIES (IN PLAIN ENGLISH):
 *   - Instance: The root Vulkan 1.3 API instance.
 *   - Surface: The VkSurfaceKHR connecting Vulkan to the GLFW window.
 *   - PhysicalDevice: The selected hardware graphics card (e.g., NVIDIA GeForce / AMD Radeon).
 *   - Device: The active logical Vulkan device used to create pipelines, buffers, and textures.
 *   - QueueFamilies: The discovered queue family indices (Graphics, Present, Compute, Transfer).
 *   - GraphicsQueue: The queue where draw commands and rendering pipelines are submitted.
 *   - PresentQueue: The queue where finished swapchain images are displayed to your screen.
 *
 * HOW TO USE IT:
 *   using var window = new Window("My Game", 1920, 1080);
 *   using var context = new VulkanContext(window);
 * ================================================================================================= */

using Brutal.GlfwApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.Core;

namespace Jatayu.RHI.Vulkan.Context;

/// <summary>
/// Encapsulates the Vulkan 1.3 Instance, Surface, Physical GPU, Logical Device, and hardware Queues.
/// </summary>
public sealed class VulkanContext : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the root Vulkan 1.3 Instance.
    /// </summary>
    public Instance Instance { get; }

    /// <summary>
    /// Gets the native window surface handle (VkSurfaceKHR) connected to the GLFW window.
    /// </summary>
    public VkSurfaceKHR Surface { get; }

    /// <summary>
    /// Gets the selected physical graphics card (GPU).
    /// </summary>
    public PhysicalDevice PhysicalDevice { get; }

    /// <summary>
    /// Gets the active logical Vulkan Device.
    /// Used across the engine to create shaders, pipelines, buffers, and textures.
    /// </summary>
    public Device Device { get; }

    /// <summary>
    /// Gets the discovered queue family hardware indices.
    /// </summary>
    public QueueFamilies QueueFamilies { get; }

    /// <summary>
    /// Gets the graphics queue for submitting rendering command buffers.
    /// </summary>
    public Queue GraphicsQueue { get; }

    /// <summary>
    /// Gets the presentation queue for showing rendered images on the display.
    /// </summary>
    public Queue PresentQueue { get; }

    /// <summary>
    /// Initializes Vulkan 1.3, binds to the window surface, selects the best GPU, and builds the logical device.
    /// </summary>
    /// <param name="window">The desktop window to render into.</param>
    public unsafe VulkanContext(Window window)
    {
        // 1. Ensure Brutal's native Vulkan loader is ready before GLFW queries extensions
        _ = Brutal.VulkanApi.Vulkan.GetInstanceProcAddr;

        // 2. Query GLFW window extensions and platform requirements (e.g. MoltenVK on macOS)
        var instanceExtensions = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);

        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var glfwExtensionCount);
        for (var i = 0; i < glfwExtensionCount; i++)
        {
            instanceExtensions.Add(Utf8.GetString(glfwExtensions[i]));
        }

        // 3. Create Vulkan 1.3 Instance
        Instance = Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo
            {
                ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3,
            },
            Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
            EnabledExtensions = instanceExtensions,
            EnabledLayers = [],
        });

        // 4. Create Window Surface
        if (Glfw.CreateWindowSurface(Instance.VkHandle, window.GlfwHandle, out var surfaceHandle) != (int)VkResult.Success)
            throw new InvalidOperationException("Failed to create a Vulkan window surface.");

        Surface = new VkSurfaceKHR((nint)surfaceHandle);

        // 5. Select the best Physical GPU (supporting graphics and presentation)
        var (bestGpu, bestFamilies) = PickBestGpu(Instance, Surface);
        PhysicalDevice = bestGpu;
        QueueFamilies = bestFamilies;

        // 6. Gather device extensions (Swapchain extension is mandatory for window rendering)
        var deviceExtensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, PhysicalDevice);

        // 7. Prepare Queue Create Infos for all unique queue family indices
        var uniqueIndices = QueueFamilies.GetUniqueIndices();
        var queueCreateInfos = new VkDeviceQueueCreateInfo[uniqueIndices.Length];
        var priority = 1.0f;

        for (var i = 0; i < uniqueIndices.Length; i++)
        {
            queueCreateInfos[i] = new VkDeviceQueueCreateInfo
            {
                QueueFamilyIndex = uniqueIndices[i],
                QueueCount = 1,
                QueuePriorities = &priority,
            };
        }

        // 8. Enable Vulkan 1.3 Dynamic Rendering (removes old VkRenderPass / VkFramebuffer requirements!)
        var vulkan13Features = new VkPhysicalDeviceVulkan13Features
        {
            DynamicRendering = true,
        };

        // 9. Create Logical Device
        Device = PhysicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos = queueCreateInfos,
            Next = &vulkan13Features,
        });

        // 10. Retrieve Queue handles
        GraphicsQueue = Device.GetQueue(QueueFamilies.GraphicsFamily!.Value, 0);
        PresentQueue = Device.GetQueue(QueueFamilies.PresentFamily!.Value, 0);
    }

    private static (PhysicalDevice, QueueFamilies) PickBestGpu(Instance instance, VkSurfaceKHR surface)
    {
        PhysicalDevice? selectedDevice = null;
        QueueFamilies? selectedFamilies = null;

        foreach (var candidate in instance.PhysicalDevices)
        {
            var families = QueueFamilies.Find(candidate, surface);

            // Must support graphics and presentation to our window
            if (!families.IsComplete)
                continue;

            selectedDevice = candidate;
            selectedFamilies = families;

            // If unified (graphics and presentation share the same queue), pick it immediately
            if (families.IsUnified)
                break;
        }

        if (selectedDevice is null || selectedFamilies is null)
            throw new InvalidOperationException("No suitable Vulkan GPU found supporting graphics and presentation to this window.");

        return (selectedDevice, selectedFamilies);
    }

    /// <summary>
    /// Blocks the CPU until the GPU has finished executing all submitted work.
    /// Call this before resizing, rebuilding swapchains, or shutting down.
    /// </summary>
    public void WaitIdle()
    {
        Device.WaitIdle();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            WaitIdle();

            Device.Dispose();
            Instance.DestroySurfaceKHR(Surface, null);
            Instance.Dispose();

            _disposed = true;
        }
    }
}
