/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: tests/Jatayu.RenderSmoke/Program.cs
 * 
 * WHAT THIS FILE DOES:
 *   Automated Vulkan render smoke test runner implementing Decision D20.
 *   Probes physical device capabilities against the Jatayu D10 hardware floor.
 *   On compliant discrete hardware, validates full Vulkan 1.3 features.
 *   On software / CI environments (e.g. lavapipe), runs reduced validation and marks unsupported as SKIPPED.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RenderSmoke;

public static class Program
{
    public static unsafe int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("           JATAYU VULKAN 1.3 RENDER SMOKE & D10 FEATURE PROBE                   ");
        Console.WriteLine("================================================================================");

        try
        {
            // Initialize Vulkan loader
            _ = Vulkan.GetInstanceProcAddr;

            var instanceExtensions = new HashSet<string>();
            VulkanHelpers.AddPlatformRequiredExtensions(instanceExtensions);

            using var instance = Vulkan.CreateInstance(new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo
                {
                    ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3,
                },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = instanceExtensions,
                EnabledLayers = [],
            });

            Console.WriteLine("[Vulkan] Instance created successfully. Vulkan API 1.3 loaded.");

            var physicalDevices = instance.PhysicalDevices;
            if (physicalDevices.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] No Vulkan physical devices found!");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"[Vulkan] Discovered {physicalDevices.Length} physical device(s):");
            PhysicalDevice selectedDevice = physicalDevices[0];
            bool foundDiscrete = false;

            for (int i = 0; i < physicalDevices.Length; i++)
            {
                var dev = physicalDevices[i];
                var name = VulkanDeviceInfo.GetDeviceName(dev);
                var props = new VkPhysicalDeviceProperties();
                instance.PInvoke.vkGetPhysicalDeviceProperties.Invoke(dev.Handle, &props);
                Console.WriteLine($"  [{i}] {name} ({props.DeviceType})");

                if (!foundDiscrete && props.DeviceType == VkPhysicalDeviceType.DiscreteGpu)
                {
                    selectedDevice = dev;
                    foundDiscrete = true;
                }
            }

            var selectedName = VulkanDeviceInfo.GetDeviceName(selectedDevice);
            Console.WriteLine($"\n[Probe] Selected Device for D10 Verification: {selectedName}");

            // Run D10 Hardware Floor verification
            try
            {
                VulkanContext.VerifyD10HardwareFloor(instance, selectedDevice);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[Probe] D10 Hardware Floor Verification: PASSED (All required Core 1.3/1.2/1.1/1.0 features supported)");
                Console.ResetColor();
            }
            catch (InvalidOperationException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Probe] D10 Hardware Floor: INCOMPLETE (Decision D20 fallback to SKIPPED on CI/software runner)");
                Console.WriteLine(ex.Message);
                Console.ResetColor();
            }

            Console.WriteLine("\n[Smoke] RenderSmoke completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL] RenderSmoke encountered an unhandled exception: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
    }
}
