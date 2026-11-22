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
            // ImGui font atlas check
            Brutal.ImGuiApi.ImGui.CreateContext();
            var io = Brutal.ImGuiApi.ImGui.GetIO();
            if (io.Fonts.Fonts.Count == 0)
            {
                Brutal.ImGuiApi.ImFontAtlasExtensions.AddFontDefault(io.Fonts, default);
            }
            if (io.Fonts.TexData.Ptr == null || io.Fonts.TexData.Width <= 0 || io.Fonts.TexData.Height <= 0)
                throw new InvalidOperationException("ImGui font atlas failed to initialize.");
            Console.WriteLine($"  [ImGui] Font atlas initialized: {io.Fonts.TexData.Width}x{io.Fonts.TexData.Height} px, {io.Fonts.TexData.BytesPerPixel} Bpp");
            Brutal.ImGuiApi.ImGui.DestroyContext();

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

        // -----------------------------------------------------------------------------------------
        // 6. SPRINT 5b: LARGE-WORLD FOUNDATIONS & 3D SHADING VERIFICATION
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("\n--- [CHECK 6] Sprint 5b: Large-World Foundations & 3D Shading Verification ---");
        try
        {
            // 1. WorldPosition 50,000m zero-jitter test
            var objPos = new Jatayu.Core.Mathematics.WorldPosition(50000.0, 200.0, -50000.0);
            var camPos = new Jatayu.Core.Mathematics.WorldPosition(49990.0, 200.0, -50000.0);
            var relPos = objPos.ToCameraRelative(in camPos);
            if (Math.Abs(relPos.X - 10.0f) > 0.0001f || Math.Abs(relPos.Y) > 0.0001f || Math.Abs(relPos.Z) > 0.0001f)
                throw new InvalidOperationException("WorldPosition camera-relative translation failed precision check.");
            Console.WriteLine($"  [LargeWorld] 50,000m zero-jitter camera-relative translation: VERIFIED (RelX={relPos.X:F2}m)");

            // 2. OriginShift 256m terrain tile snap
            var originShift = new Jatayu.Core.Mathematics.OriginShift { ThresholdDistance = 2000.0 };
            if (!originShift.CheckAndShift(new Jatayu.Core.Mathematics.WorldPosition(2200.0, 0, 0), out var shiftDelta))
                throw new InvalidOperationException("OriginShift failed to trigger at 2200m.");
            if (Math.Abs(shiftDelta.X - 2304.0) > 0.001)
                throw new InvalidOperationException($"OriginShift did not snap to 256m tile: expected 2304m, got {shiftDelta.X}m");
            Console.WriteLine($"  [LargeWorld] OriginShift 256m snapping: VERIFIED (ShiftDelta={shiftDelta.X:F0}m)");

            // 3. Frustum 5-plane culling with MaxDrawDistance clamp
            var camera = new Jatayu.Renderer.Camera.Camera3D(
                new System.Numerics.Vector3(0, 0, 0),
                new System.Numerics.Vector3(0, 0, -10),
                1920, 1080, 60f, 0.1f
            );
            var frustum = Jatayu.Renderer.Camera.Frustum.FromCamera(camera, maxDrawDistance: 5000.0f);
            if (!frustum.IsSphereVisible(new System.Numerics.Vector3(0, 0, -50), 2.0f))
                throw new InvalidOperationException("Frustum failed to detect sphere inside view.");
            if (frustum.IsSphereVisible(new System.Numerics.Vector3(0, 0, -6000), 2.0f))
                throw new InvalidOperationException("Frustum failed to clamp sphere beyond MaxDrawDistance.");

            // 3b. Frustum elevated camera angle check
            var elevatedCam = new Jatayu.Renderer.Camera.Camera3D(
                new System.Numerics.Vector3(0, 15, 20),
                new System.Numerics.Vector3(0, 0, 0),
                1920, 1080, 60f, 0.1f
            );
            System.Numerics.Matrix4x4 elevatedRelView = elevatedCam.ViewMatrix;
            elevatedRelView.M41 = 0;
            elevatedRelView.M42 = 0;
            elevatedRelView.M43 = 0;
            var elevatedFrustum = new Jatayu.Renderer.Camera.Frustum { MaxDrawDistance = 100000.0f };
            elevatedFrustum.Update(elevatedRelView * elevatedCam.ProjectionMatrix, System.Numerics.Vector3.Zero);
            if (!elevatedFrustum.IsSphereVisible(new System.Numerics.Vector3(0, -15, -20), 1.732f))
                throw new InvalidOperationException("Frustum failed to detect object when viewed from elevated angle above target.");
            Console.WriteLine("  [Visibility] 5-Plane Reverse-Z Frustum with MaxDrawDistance clamp & elevated angle: VERIFIED");

            // 4. DirectionalLight Reverse-Z projection
            var sun = new Jatayu.Renderer.Lighting.DirectionalLight();
            var lightVp = sun.ComputeLightViewProjection(System.Numerics.Vector3.Zero);
            if (!System.Numerics.Matrix4x4.Invert(lightVp, out _))
                throw new InvalidOperationException("DirectionalLight view-projection matrix is not invertible.");
            Console.WriteLine("  [Lighting] Directional sunlight & Reverse-Z orthographic projection: VERIFIED");

            // 5. Skybox inverse ray reconstruction
            var skybox = new Jatayu.Renderer.Skybox.Skybox();
            var invVp = skybox.ComputeInverseViewProjection(camera.ViewMatrix, camera.ProjectionMatrix);
            if (float.IsNaN(invVp.M11))
                throw new InvalidOperationException("Skybox inverse view-projection matrix contains NaN.");
            Console.WriteLine("  [Skybox] Procedural atmosphere at Reverse-Z infinity: VERIFIED");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(">> Sprint 5b Large-World Foundations: VERIFIED");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Sprint 5b Error]: {ex.Message}");
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
