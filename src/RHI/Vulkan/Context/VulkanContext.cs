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
        deviceExtensions.Add("VK_EXT_memory_budget");

        // 7. Verify full D10 hardware floor compliance
        VerifyD10HardwareFloor(Instance, PhysicalDevice, deviceExtensions);

        // 8. Prepare Queue Create Infos for all unique queue family indices
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

        // 9. Enable complete D10 Vulkan 1.3 core hardware features
        var vulkan11Features = new VkPhysicalDeviceVulkan11Features
        {
            ShaderDrawParameters = true,
        };

        var vulkan12Features = new VkPhysicalDeviceVulkan12Features
        {
            Next = &vulkan11Features,
            TimelineSemaphore = true,
            BufferDeviceAddress = true,
            DrawIndirectCount = true,
            SamplerFilterMinmax = true,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingUpdateUnusedWhilePending = true,
            DescriptorBindingVariableDescriptorCount = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            ShaderStorageBufferArrayNonUniformIndexing = true,
        };

        var vulkan13Features = new VkPhysicalDeviceVulkan13Features
        {
            Next = &vulkan12Features,
            DynamicRendering = true,
            Synchronization2 = true,
            Maintenance4 = true,
        };

        var core10Features = new VkPhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            MultiDrawIndirect = true,
            TextureCompressionBC = true,
            ShaderInt64 = true,
            FragmentStoresAndAtomics = true,
        };

        // 10. Create Logical Device with fully chained features
        Device = PhysicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos = queueCreateInfos,
            Features = &core10Features,
            Next = &vulkan13Features,
        });

        // 11. Retrieve Queue handles
        GraphicsQueue = Device.GetQueue(QueueFamilies.GraphicsFamily!.Value, 0);
        PresentQueue = Device.GetQueue(QueueFamilies.PresentFamily!.Value, 0);
    }

    /// <summary>
    /// Validates that the selected physical device satisfies the mandatory D10 hardware floor.
    /// Throws an InvalidOperationException explicitly enumerating any missing features.
    /// </summary>
    public static unsafe void VerifyD10HardwareFloor(Instance instance, PhysicalDevice physicalDevice, HashSet<string>? deviceExtensions = null)
    {
        var missing = new List<string>();

        var vk11 = new VkPhysicalDeviceVulkan11Features();
        var vk12 = new VkPhysicalDeviceVulkan12Features();
        var vk13 = new VkPhysicalDeviceVulkan13Features();

        vk13.Next = &vk12;
        vk12.Next = &vk11;

        var features2 = new VkPhysicalDeviceFeatures2
        {
            Next = &vk13
        };

        instance.PInvoke.vkGetPhysicalDeviceFeatures2.Invoke(physicalDevice.Handle, &features2);

        // Core 1.3 features
        if (!vk13.DynamicRendering) missing.Add("Vulkan 1.3: DynamicRendering");
        if (!vk13.Synchronization2) missing.Add("Vulkan 1.3: Synchronization2");
        if (!vk13.Maintenance4) missing.Add("Vulkan 1.3: Maintenance4");

        // Core 1.2 features
        if (!vk12.TimelineSemaphore) missing.Add("Vulkan 1.2: TimelineSemaphore");
        if (!vk12.BufferDeviceAddress) missing.Add("Vulkan 1.2: BufferDeviceAddress");
        if (!vk12.DrawIndirectCount) missing.Add("Vulkan 1.2: DrawIndirectCount");
        if (!vk12.SamplerFilterMinmax) missing.Add("Vulkan 1.2: SamplerFilterMinmax");
        if (!vk12.RuntimeDescriptorArray) missing.Add("Vulkan 1.2: RuntimeDescriptorArray");
        if (!vk12.DescriptorBindingPartiallyBound) missing.Add("Vulkan 1.2: DescriptorBindingPartiallyBound");
        if (!vk12.DescriptorBindingSampledImageUpdateAfterBind) missing.Add("Vulkan 1.2: DescriptorBindingSampledImageUpdateAfterBind");
        if (!vk12.DescriptorBindingUpdateUnusedWhilePending) missing.Add("Vulkan 1.2: DescriptorBindingUpdateUnusedWhilePending");
        if (!vk12.DescriptorBindingVariableDescriptorCount) missing.Add("Vulkan 1.2: DescriptorBindingVariableDescriptorCount");
        if (!vk12.ShaderSampledImageArrayNonUniformIndexing) missing.Add("Vulkan 1.2: ShaderSampledImageArrayNonUniformIndexing");
        if (!vk12.ShaderStorageBufferArrayNonUniformIndexing) missing.Add("Vulkan 1.2: ShaderStorageBufferArrayNonUniformIndexing");

        // Core 1.1 features
        if (!vk11.ShaderDrawParameters) missing.Add("Vulkan 1.1: ShaderDrawParameters");

        // Core 1.0 features
        if (!features2.Features.SamplerAnisotropy) missing.Add("Vulkan 1.0: SamplerAnisotropy");
        if (!features2.Features.MultiDrawIndirect) missing.Add("Vulkan 1.0: MultiDrawIndirect");
        if (!features2.Features.TextureCompressionBC) missing.Add("Vulkan 1.0: TextureCompressionBC");
        if (!features2.Features.ShaderInt64) missing.Add("Vulkan 1.0: ShaderInt64");
        if (!features2.Features.FragmentStoresAndAtomics) missing.Add("Vulkan 1.0: FragmentStoresAndAtomics");

        if (missing.Count > 0)
        {
            var deviceName = VulkanDeviceInfo.GetDeviceName(physicalDevice);
            throw new InvalidOperationException(
                $"Selected Vulkan GPU '{deviceName}' does not meet the Jatayu D10 hardware floor!\n" +
                $"Missing required features ({missing.Count}):\n  - " +
                string.Join("\n  - ", missing)
            );
        }
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
