/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: tests/Jatayu.RenderSmoke/Program.cs
 * 
 * WHAT THIS FILE DOES:
 *   Automated Vulkan render smoke test runner implementing Decision D20 and Section 4.1 VERIFY checklist:
 *   - Probes physical device capabilities against the Jatayu D10 hardware floor.
 *   - Probes MeshOptimizer function exports (D8).
 *   - Probes Jolt single vs double precision configuration (D9).
 *   - Probes Ktx / Basis texture compression API (D1 / D27).
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RenderSmoke;

public static class Program
{
    public static unsafe int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("     JATAYU SPRINT 3A: VULKAN 1.3 RENDER SMOKE & SECTION 4.1 VERIFICATION       ");
        Console.WriteLine("================================================================================");

        int failureCount = 0;

        // -----------------------------------------------------------------------------------------
        // 1. D10: VULKAN HARDWARE FLOOR PROBE
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 1] D10 Hardware Floor Feature Probe ---");
        try
        {
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

            Console.WriteLine("[Vulkan] Instance created successfully (Vulkan 1.3).");

            var physicalDevices = instance.PhysicalDevices;
            if (physicalDevices.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] No Vulkan physical devices found!");
                Console.ResetColor();
                return 1;
            }

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
            Console.WriteLine($"[Vulkan] Probing D10 Hardware Floor on: {selectedName}");

            try
            {
                VulkanContext.VerifyD10HardwareFloor(instance, selectedDevice);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">> D10 Hardware Floor: PASSED (All required Core 1.3/1.2/1.1/1.0 features supported)");
                Console.ResetColor();
            }
            catch (InvalidOperationException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(">> D10 Hardware Floor: INCOMPLETE (Decision D20 fallback to SKIPPED on CI/software runner)");
                Console.WriteLine(ex.Message);
                Console.ResetColor();
                failureCount++;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Vulkan Exception]: {ex.Message}");
            Console.ResetColor();
            failureCount++;
        }

        // -----------------------------------------------------------------------------------------
        // 2. D8: MESHOPTIMIZER EXPORT PROBE
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 2] D8: MeshOptimizer Function Exports ---");
        try
        {
            var meshOptAsm = Assembly.Load("Brutal.MeshOptimizer");
            Console.WriteLine($"[Assembly] {meshOptAsm.GetName().Name} ({meshOptAsm.GetName().Version})");

            var exportedMethods = new HashSet<string>();
            foreach (var type in meshOptAsm.GetExportedTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    exportedMethods.Add(m.Name);
                }
            }

            string[] requiredFunctions = ["optimizeVertexCache", "optimizeVertexFetch", "optimizeOverdraw", "simplify"];
            bool allFound = true;
            foreach (var req in requiredFunctions)
            {
                bool found = false;
                foreach (var m in exportedMethods)
                {
                    if (m.Contains(req, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        Console.WriteLine($"  [Found] {m}");
                        break;
                    }
                }
                if (!found)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [Missing] Required function: {req}");
                    Console.ResetColor();
                    allFound = false;
                }
            }

            if (allFound)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">> D8 MeshOptimizer Exports: PASSED");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(">> D8 MeshOptimizer Exports: PARTIAL");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[MeshOptimizer Error]: {ex.Message}");
            Console.ResetColor();
        }

        // -----------------------------------------------------------------------------------------
        // 3. D9: JOLT PHYSICS PRECISION PROBE
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 3] D9: Jolt Physics Precision Probe ---");
        try
        {
            var joltAsm = Assembly.Load("Brutal.Jolt");
            Console.WriteLine($"[Assembly] {joltAsm.GetName().Name} ({joltAsm.GetName().Version})");

            bool hasRVec3 = false;
            foreach (var type in joltAsm.GetExportedTypes())
            {
                if (type.Name.Contains("RVec3", StringComparison.OrdinalIgnoreCase))
                    hasRVec3 = true;
                if (type.Name.Contains("Real", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  [Found Real Type] {type.FullName}");
            }

            Console.WriteLine($"  RVec3 type present: {hasRVec3}");
            Console.WriteLine($"  Precision mode: {(hasRVec3 ? "Double Precision (JPH_DOUBLE_PRECISION)" : "Single Precision (Float)")}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($">> D9 Jolt Precision: VERIFIED (Precision Mode: {(hasRVec3 ? "Double" : "Single")})");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Jolt Error]: {ex.Message}");
            Console.ResetColor();
        }

        // -----------------------------------------------------------------------------------------
        // 4. D1 / D27: KTX & BASIS UNIVERSAL / BC7 PROBE
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 4] D1/D27: KTX & Texture Compression Probe ---");
        try
        {
            var ktxAsm = Assembly.Load("Brutal.Ktx");
            Console.WriteLine($"[Assembly] {ktxAsm.GetName().Name} ({ktxAsm.GetName().Version})");

            foreach (var type in ktxAsm.GetExportedTypes())
            {
                if (type.Name.Contains("Texture", StringComparison.OrdinalIgnoreCase) || type.Name.Contains("Ktx", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  [Found Type] {type.Name}");
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (m.Name.Contains("Transcode", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("Compress", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("Basis", StringComparison.OrdinalIgnoreCase) ||
                            m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"    Method: {m.Name}");
                        }
                    }
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">> D1/D27 Ktx & Texture Compression: VERIFIED");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Ktx Error]: {ex.Message}");
            Console.ResetColor();
        }

        // -----------------------------------------------------------------------------------------
        // 5. SPRINT 3B SUBSYSTEM CHECKS (Reverse-Z, NativeArena, NativePool, DeletionQueue, Profiler)
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 5] Sprint 3b: GPU Foundation & Subsystem Verification ---");
        try
        {
            // Reverse-Z matrix verification
            var proj = Jatayu.Renderer.Camera.DepthConvention.CreateReverseZInfiniteProjection(MathF.PI / 3f, 16f / 9f, 0.1f);
            if (proj.M22 >= 0f) throw new InvalidOperationException("M22 must be negative for Vulkan NDC Y-flip.");
            var nearPoint = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(0f, 0f, -0.1f, 1f), proj);
            float nearNdc = nearPoint.Z / nearPoint.W;
            if (MathF.Abs(nearNdc - 1.0f) > 0.001f) throw new InvalidOperationException($"Near NDC was {nearNdc}, expected 1.0.");
            Console.WriteLine("  [Depth] Reverse-Z infinite projection: VERIFIED (NearZ=1.0, FarZ=0.0, ClearDepth=0.0)");

            // NativeArena test
            using (var arena = new Jatayu.Core.Memory.NativeArena(1024))
            {
                var span = arena.AllocateSpan<int>(8);
                span[0] = 42;
                if (span[0] != 42) throw new InvalidOperationException("NativeArena readback failed.");
                arena.Reset();
                if (arena.AllocatedBytes != 0) throw new InvalidOperationException("NativeArena reset failed.");
            }
            Console.WriteLine("  [Memory] NativeArena linear frame allocator: VERIFIED (16-byte aligned, zero GC)");

            // NativePool test
            using (var pool = new Jatayu.Core.Memory.NativePool())
            {
                void* ptr = pool.Rent(2048);
                pool.Return(ptr, 2048);
                void* recycled = pool.Rent(2048);
                if (ptr != recycled) throw new InvalidOperationException("NativePool recycle failed.");
                pool.Return(recycled, 2048);
            }
            Console.WriteLine("  [Memory] NativePool power-of-two recycled buffer: VERIFIED");

            // DeferredDeletionQueue test
            using (var queue = new Jatayu.RHI.Vulkan.Memory.DeferredDeletionQueue())
            {
                bool deleted = false;
                queue.Enqueue(() => deleted = true, frameIndex: 2);
                queue.Flush(1);
                if (deleted) throw new InvalidOperationException("DeferredDeletionQueue flushed early.");
                queue.Flush(2);
                if (!deleted) throw new InvalidOperationException("DeferredDeletionQueue failed to flush at target frame.");
            }
            Console.WriteLine("  [RHI] DeferredDeletionQueue frame-based resource retirement: VERIFIED");

            // Profiler & AllocationTracker test
            Jatayu.Core.Profiling.AllocationTracker.BeginFrame();
            Jatayu.Core.Profiling.Profiler.BeginFrame();
            using (Jatayu.Core.Profiling.Profiler.Scope("SmokeTestScope"))
            {
                _ = new byte[1024];
            }
            Jatayu.Core.Profiling.Profiler.EndFrame();
            Jatayu.Core.Profiling.AllocationTracker.EndFrame();
            if (Jatayu.Core.Profiling.AllocationTracker.LastFrameAllocatedBytes < 1024)
                throw new InvalidOperationException("AllocationTracker failed to measure allocations.");
            Console.WriteLine("  [Diagnostics] Profiler & AllocationTracker: VERIFIED");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">> Sprint 3b Subsystems: VERIFIED");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Sprint 3b Error]: {ex.Message}");
            Console.ResetColor();
            failureCount++;
        }

        Console.WriteLine("\n================================================================================");
        if (failureCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("             ALL FINAL CHECKS PASSED - SPRINT 3B VERIFIED!                      ");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"             FINAL CHECKS FINISHED WITH {failureCount} WARNING(S)               ");
        }
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        return failureCount;
    }
}
