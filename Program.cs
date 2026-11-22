/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: Program.cs
 * 
 * WHAT THIS APPLICATION DOES:
 *   Jatayu Engine - Phase 2 with Panini (Pannini) Projection.
 *   
 *   Renders a fully interactive, solid 3D textured/colored cube in true 3D space with:
 *     1. Depth Testing (Z-Buffer): Hardware occlusion via dedicated DepthBuffer (D32_SFLOAT).
 *     2. 3D Perspective Camera: View matrix + Vulkan-corrected Projection matrix (Y-flipped NDC).
 *     3. Panini Projection Support: Advanced cylindrical projection model that eliminates peripheral
 *        stretching at wide and ultra-wide fields of view (FOV 90°–140°) while keeping vertical lines
 *        straight and upright!
 *     4. Vulkan Push Constants: 80-byte push constant block (64-byte MVP matrix + 16-byte Panini parameters).
 *     5. Indexed 3D Mesh: 24 vertices (Position, Color, Normal, TexCoord) and 36 indices.
 *     6. Directional Diffuse Shading: Surface normals evaluated in GLSL for realistic 3D lighting.
 *     7. Multi-Frame Concurrency: 2 Frames in Flight allowing parallel CPU recording and GPU rendering.
 *     8. Dynamic Rendering & Resizing: Zero-crash dynamic swapchain and depth buffer recreation.
 *
 * INTERACTIVE CONTROLS:
 *   [P]          : Cycle Panini Projection (OFF -> Moderate d=0.5 -> Full d=1.0)
 *   [F]          : Toggle FOV (Normal 60° <-> Ultra-Wide 110° to showcase Panini edge correction!)
 *   [1] / [2]    : Fine-tune Panini distance 'd' (-0.1 / +0.1)
 *   [Space]      : Toggle Auto-Orbit rotation On / Off
 *   [Left/Right] : Orbit camera horizontally (Yaw)
 *   [Up/Down]    : Orbit camera vertically (Pitch)
 *   [W / S]      : Zoom camera in / out (Distance)
 *   [V]          : Toggle V-Sync Live (Observe FPS change in Title Bar!)
 *   [C]          : Cycle background clear color
 *   [Drag Border]: Seamlessly resize window with live swapchain + depth buffer rebuild
 *   [ESC]        : Clean shutdown
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.Pointers;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.Loaders;
using Jatayu.Core;
using Jatayu.Core.Profiling;
using Jatayu.Core.Time;
using Jatayu.DebugUI;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Graph;
using Jatayu.Renderer.Mesh;
using Jatayu.RHI.Vulkan.Commands;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.RHI.Vulkan.Profiling;
using Jatayu.RHI.Vulkan.Swapchain;
using Jatayu.RHI.Vulkan.Sync;
using Jatayu.RHI.Vulkan.Textures;
using Jatayu.RHI.Vulkan.Upload;

namespace Jatayu;

/// <summary>
/// Main application entry point for Jatayu Engine.
/// </summary>
public static class Program
{
    // -------------------------------------------------------------------------------------------------
    // NATIVE VULKAN LOADER & RUNTIME INITIALIZATION
    // -------------------------------------------------------------------------------------------------
    public static unsafe void Main(string[] args)
    {
        _ = Vulkan.GetInstanceProcAddr;
        RunEngineDemo();
    }

    private static unsafe void RunEngineDemo()
    {
    const int InitialWidth = 1280;
    const int InitialHeight = 720;

    // ---------------------------------------------------------------------------------------------
    // PHASE 1: DESKTOP WINDOW & VULKAN 1.3 HARDWARE DEVICE BOOTSTRAP
    // ---------------------------------------------------------------------------------------------
    using var window = new Window(
        title: "Jatayu Vulkan 1.3 3D Engine",
        width: InitialWidth,
        height: InitialHeight,
        resizable: true,
        vsync: true
    );

    using var context = new VulkanContext(window);
    var device = context.Device;
    var queue = context.GraphicsQueue;
    var queueFamily = context.QueueFamilies.GraphicsFamily!.Value;

    // High-performance AMD Vulkan Memory Allocator
    using var allocator = new GpuAllocator(context);

    // Presentation swapchain
    using var swapchain = new VulkanSwapchain(context, window);

    // ---------------------------------------------------------------------------------------------
    // PHASE 2: DEPTH BUFFER & 3D PERSPECTIVE CAMERA WITH PANINI PROJECTION
    // ---------------------------------------------------------------------------------------------
    // Dedicated GPU Depth Buffer for hardware depth testing (Z-buffering occlusion)
    using var depthBuffer = new DepthBuffer(context, allocator, swapchain.Width, swapchain.Height);

    // 3D Perspective Camera with Panini projection capabilities
    var camera = new Camera3D(
        position: new Vector3(0f, 2.0f, 4.5f),
        target: Vector3.Zero,
        width: swapchain.Width,
        height: swapchain.Height,
        fovDegrees: 60f,
        nearPlane: 0.1f,
        farPlane: 1000f
    )
    {
        PaniniEnabled = false,
        PaniniDistance = 0.5f,
        PaniniScale = 1.0f,
    };

    // Camera controller for smooth orbiting and keyboard input
    var cameraController = new CameraController(camera)
    {
        Distance = 4.5f,
        Pitch = 0.35f,
        Yaw = 0.0f,
        AutoOrbitEnabled = true,
        OrbitSpeed = 0.75f,
        MoveSpeed = 4.0f,
    };

    // Recreate depth buffer and camera aspect ratio on window resize
    window.Resized += (newWidth, newHeight) =>
    {
        depthBuffer.Recreate(newWidth, newHeight);
        camera.UpdateAspectRatio(newWidth, newHeight);
    };

    // ---------------------------------------------------------------------------------------------
    // PHASE 3: 3D CUBE GEOMETRY & INDEX BUFFER
    // ---------------------------------------------------------------------------------------------
    // 24 vertices (4 per face) with distinct colors and normals for each of the 6 cube faces
    Vertex3D[] cubeVertices =
    [
        // Face 1: Front (+Z) - Red tint
        new(new float3(-1f, -1f,  1f), new float3(1.0f, 0.25f, 0.25f), new float3(0f, 0f, 1f), new float2(0f, 0f)),
        new(new float3( 1f, -1f,  1f), new float3(1.0f, 0.25f, 0.25f), new float3(0f, 0f, 1f), new float2(1f, 0f)),
        new(new float3( 1f,  1f,  1f), new float3(1.0f, 0.25f, 0.25f), new float3(0f, 0f, 1f), new float2(1f, 1f)),
        new(new float3(-1f,  1f,  1f), new float3(1.0f, 0.25f, 0.25f), new float3(0f, 0f, 1f), new float2(0f, 1f)),

        // Face 2: Back (-Z) - Cyan tint
        new(new float3( 1f, -1f, -1f), new float3(0.2f, 0.9f, 0.9f), new float3(0f, 0f, -1f), new float2(0f, 0f)),
        new(new float3(-1f, -1f, -1f), new float3(0.2f, 0.9f, 0.9f), new float3(0f, 0f, -1f), new float2(1f, 0f)),
        new(new float3(-1f,  1f, -1f), new float3(0.2f, 0.9f, 0.9f), new float3(0f, 0f, -1f), new float2(1f, 1f)),
        new(new float3( 1f,  1f, -1f), new float3(0.2f, 0.9f, 0.9f), new float3(0f, 0f, -1f), new float2(0f, 1f)),

        // Face 3: Top (+Y) - Green tint
        new(new float3(-1f,  1f,  1f), new float3(0.25f, 1.0f, 0.25f), new float3(0f, 1f, 0f), new float2(0f, 0f)),
        new(new float3( 1f,  1f,  1f), new float3(0.25f, 1.0f, 0.25f), new float3(0f, 1f, 0f), new float2(1f, 0f)),
        new(new float3( 1f,  1f, -1f), new float3(0.25f, 1.0f, 0.25f), new float3(0f, 1f, 0f), new float2(1f, 1f)),
        new(new float3(-1f,  1f, -1f), new float3(0.25f, 1.0f, 0.25f), new float3(0f, 1f, 0f), new float2(0f, 1f)),

        // Face 4: Bottom (-Y) - Magenta tint
        new(new float3(-1f, -1f, -1f), new float3(0.95f, 0.25f, 0.95f), new float3(0f, -1f, 0f), new float2(0f, 0f)),
        new(new float3( 1f, -1f, -1f), new float3(0.95f, 0.25f, 0.95f), new float3(0f, -1f, 0f), new float2(1f, 0f)),
        new(new float3( 1f, -1f,  1f), new float3(0.95f, 0.25f, 0.95f), new float3(0f, -1f, 0f), new float2(1f, 1f)),
        new(new float3(-1f, -1f,  1f), new float3(0.95f, 0.25f, 0.95f), new float3(0f, -1f, 0f), new float2(0f, 1f)),

        // Face 5: Right (+X) - Blue tint
        new(new float3( 1f, -1f,  1f), new float3(0.25f, 0.5f, 1.0f), new float3(1f, 0f, 0f), new float2(0f, 0f)),
        new(new float3( 1f, -1f, -1f), new float3(0.25f, 0.5f, 1.0f), new float3(1f, 0f, 0f), new float2(1f, 0f)),
        new(new float3( 1f,  1f, -1f), new float3(0.25f, 0.5f, 1.0f), new float3(1f, 0f, 0f), new float2(1f, 1f)),
        new(new float3( 1f,  1f,  1f), new float3(0.25f, 0.5f, 1.0f), new float3(1f, 0f, 0f), new float2(0f, 1f)),

        // Face 6: Left (-X) - Amber/Yellow tint
        new(new float3(-1f, -1f, -1f), new float3(1.0f, 0.85f, 0.2f), new float3(-1f, 0f, 0f), new float2(0f, 0f)),
        new(new float3(-1f, -1f,  1f), new float3(1.0f, 0.85f, 0.2f), new float3(-1f, 0f, 0f), new float2(1f, 0f)),
        new(new float3(-1f,  1f,  1f), new float3(1.0f, 0.85f, 0.2f), new float3(-1f, 0f, 0f), new float2(1f, 1f)),
        new(new float3(-1f,  1f, -1f), new float3(1.0f, 0.85f, 0.2f), new float3(-1f, 0f, 0f), new float2(0f, 1f)),
    ];

    // 36 indices forming 12 triangles across 6 cube faces
    ushort[] cubeIndices =
    [
        // Face 1: Front
        0, 1, 2,  2, 3, 0,
        // Face 2: Back
        4, 5, 6,  6, 7, 4,
        // Face 3: Top
        8, 9, 10, 10, 11, 8,
        // Face 4: Bottom
        12, 13, 14, 14, 15, 12,
        // Face 5: Right
        16, 17, 18, 18, 19, 16,
        // Face 6: Left
        20, 21, 22, 22, 23, 20,
    ];

    // ---------------------------------------------------------------------------------------------
    // PHASE 4: ASYNC UPLOAD PIPELINE & DEFERRED DELETION (SPRINT 3B FOUNDATION)
    // ---------------------------------------------------------------------------------------------
    using var uploadManager = new UploadManager(context, allocator);
    using var deletionQueue = new DeferredDeletionQueue(context);
    var vramBudget = new VramBudget(context);

    // Asynchronously upload vertex and index buffers via UploadManager and StagingRing
    using var vertexBuffer = allocator.CreateBuffer(
        (ulong)(sizeof(Vertex3D) * cubeVertices.Length),
        VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit,
        VmaMemoryUsage.GpuOnly
    );
    using var indexBuffer = allocator.CreateBuffer(
        (ulong)(sizeof(ushort) * cubeIndices.Length),
        VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit,
        VmaMemoryUsage.GpuOnly
    );

    var vUpload = uploadManager.UploadBuffer(vertexBuffer, 0, MemoryMarshal.AsBytes<Vertex3D>(cubeVertices));
    var iUpload = uploadManager.UploadBuffer(indexBuffer, 0, MemoryMarshal.AsBytes<ushort>(cubeIndices));

    using var commandPool = new CommandPoolManager(context, queueFamily);

    // Asynchronously upload 2D procedural checkerboard texture via UploadManager (retires D14 synchronous helper)
    var checkerImage = TextureLoader.CreateCheckerboard(width: 256, height: 256, tileSize: 32);
    using var texture = uploadManager.CreateTextureFromImageData(checkerImage, out var texUpload);

    // Wait until initial scene geometry and texture transfers complete
    uploadManager.WaitForUpload(Math.Max(Math.Max(vUpload, iUpload), texUpload));

    // Create 16x anisotropic repeat sampler
    using var sampler = VulkanSampler.CreateAnisotropic16xRepeat(context);

    // Descriptor set layout with combined image sampler binding at binding = 0
    using var descriptorLayout = DescriptorLayout.Create()
        .AddBinding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit)
        .Build(context);

    using var descriptorAllocator = new DescriptorAllocator(context);
    var descriptorSet = descriptorAllocator.Allocate(descriptorLayout);
    descriptorAllocator.WriteImage(descriptorSet, 0, sampler, texture);

    // ---------------------------------------------------------------------------------------------
    // PHASE 5: SHADER COMPILATION & GRAPHICS PIPELINE (PIPELINE BUILDER & REVERSE-Z)
    // ---------------------------------------------------------------------------------------------
    using var shaderCompiler = new ShaderCompiler("assets/shaders/cache");
    var vertShader = shaderCompiler.CompileShaderModuleFromFile(device, "assets/shaders/cube.vert", ShaderKind.VertexShader);
    var fragShader = shaderCompiler.CompileShaderModuleFromFile(device, "assets/shaders/cube.frag", ShaderKind.FragmentShader);

    using var shaderStages = new ShaderStages(
        device,
        [
            Presets.ShaderStage.Vertex(vertShader),
            Presets.ShaderStage.Fragment(fragShader),
        ],
        null
    );

    // Push constant range (64-byte MVP + 16-byte Panini parameters)
    var pushConstantRange = new VkPushConstantRange
    {
        StageFlags = VkShaderStageFlags.VertexBit,
        Offset = new ByteSize32(0),
        Size = new ByteSize32((uint)sizeof(PushConstantData)),
    };

    var pipelineLayout = device.CreatePipelineLayout([descriptorLayout.Handle], [pushConstantRange], null);
    var vertexInput = new VertexInput(1, 4).AddBindingWithAttributes<Vertex3D>(0, 0, VkVertexInputRate.Vertex);

    // Modern Vulkan 1.3 Graphics Pipeline built cleanly via PipelineBuilder
    using var pipeline = PipelineBuilder.Create()
        .SetShaderStages(shaderStages)
        .SetVertexInput(vertexInput)
        .SetColorAttachmentFormat(swapchain.ColorFormat)
        .SetDepthAttachmentFormat(depthBuffer.Format)
        .SetPipelineLayout(pipelineLayout)
        .Build(context);

    // ---------------------------------------------------------------------------------------------
    // PHASE 6: MULTI-FRAME IN-FLIGHT SYNCHRONIZATION & PROFILING
    // ---------------------------------------------------------------------------------------------
    using var frameSync = new FrameSync(context, maxFramesInFlight: 2);
    var commandBuffers = commandPool.AllocateMultiple(frameSync.MaxFramesInFlight);
    using var gpuTimer = new GpuTimer(context, maxQueriesPerFrame: 64, framesInFlight: frameSync.MaxFramesInFlight);
    using var imguiBackend = new ImGuiVulkanBackend(context, allocator, uploadManager, window, swapchain.ColorFormat);
    var debugOverlay = new DebugOverlay();

    using var cubeMesh = new Mesh(
        vertexBuffer,
        indexBuffer,
        (uint)cubeVertices.Length,
        (uint)cubeIndices.Length,
        ownsBuffers: false
    );

    // Modern Vulkan 1.3 Render Graph for DAG pass scheduling and automated sync2 synchronization
    using var renderGraph = new RenderGraph();

    // ---------------------------------------------------------------------------------------------
    // INTERACTIVE HOTKEY EVENT HOOKS
    // ---------------------------------------------------------------------------------------------
    float4[] clearColors =
    [
        new float4(0.08f, 0.08f, 0.12f, 1.0f), // Deep Slate Blue
        new float4(0.05f, 0.05f, 0.05f, 1.0f), // Dark Charcoal
        new float4(0.12f, 0.05f, 0.08f, 1.0f), // Deep Crimson
        new float4(0.04f, 0.10f, 0.08f, 1.0f), // Dark Forest
    ];
    var clearColorIndex = 0;

    // Panini mode state machine (0 = OFF, 1 = Moderate d=0.5, 2 = Full d=1.0)
    var paniniMode = 0;

    window.KeyAction += (key, action) =>
    {
        if (action == GlfwKeyAction.Press)
        {
            if (key == GlfwKey.V)
            {
                window.VSync = !window.VSync;
                Console.WriteLine($"[Input] V-Sync toggled -> {(window.VSync ? "ENABLED (Locked to monitor refresh)" : "DISABLED (Uncapped FPS)")}");
            }
            else if (key == GlfwKey.C)
            {
                clearColorIndex = (clearColorIndex + 1) % clearColors.Length;
                Console.WriteLine($"[Input] Clear Color switched to Palette #{clearColorIndex + 1}");
            }
            else if (key == GlfwKey.Space)
            {
                cameraController.AutoOrbitEnabled = !cameraController.AutoOrbitEnabled;
                Console.WriteLine($"[Input] Auto-Orbit toggled -> {(cameraController.AutoOrbitEnabled ? "ENABLED" : "PAUSED (Manual control active)")}");
            }
            else if (key == GlfwKey.P)
            {
                // Cycle Panini projection modes: OFF -> Moderate (d=0.5) -> Full (d=1.0) -> OFF
                paniniMode = (paniniMode + 1) % 3;
                switch (paniniMode)
                {
                    case 0:
                        camera.PaniniEnabled = false;
                        Console.WriteLine("[Panini] Projection: OFF (Standard Rectilinear Perspective)");
                        break;
                    case 1:
                        camera.PaniniEnabled = true;
                        camera.PaniniDistance = 0.5f;
                        Console.WriteLine("[Panini] Projection: ON (Moderate d = 0.50 - ideal for 90°-100° FOV)");
                        break;
                    case 2:
                        camera.PaniniEnabled = true;
                        camera.PaniniDistance = 1.0f;
                        Console.WriteLine("[Panini] Projection: ON (Full Cylindrical d = 1.00 - ideal for 110°-140° FOV)");
                        break;
                }
            }
            else if (key == GlfwKey.F)
            {
                // Toggle between normal 60° FOV and ultra-wide 110° FOV to witness Panini edge correction!
                if (camera.FovDegrees < 80f)
                {
                    camera.FovDegrees = 110f;
                    Console.WriteLine("[Camera] FOV switched to ULTRA-WIDE (110° FOV) - Notice how Panini [P] eliminates edge stretching!");
                }
                else
                {
                    camera.FovDegrees = 60f;
                    Console.WriteLine("[Camera] FOV switched to STANDARD (60° FOV)");
                }
            }
            else if (key == GlfwKey.Minus)
            {
                camera.PaniniDistance = MathF.Max(0.0f, camera.PaniniDistance - 0.1f);
                camera.PaniniEnabled = camera.PaniniDistance > 0.001f;
                Console.WriteLine($"[Panini] Distance 'd' decreased -> {camera.PaniniDistance:F2} (Effective: {(camera.PaniniEnabled ? "ON" : "OFF")})");
            }
            else if (key == GlfwKey.Equal)
            {
                camera.PaniniDistance = MathF.Min(2.0f, camera.PaniniDistance + 0.1f);
                camera.PaniniEnabled = true;
                Console.WriteLine($"[Panini] Distance 'd' increased -> {camera.PaniniDistance:F2} (Effective: ON)");
            }
            else if (key == GlfwKey.F1)
            {
                debugOverlay.IsVisible = !debugOverlay.IsVisible;
                Console.WriteLine($"[Diagnostics] Debug Overlay HUD: {(debugOverlay.IsVisible ? "VISIBLE" : "HIDDEN")}");
            }
            else if (key == GlfwKey.Escape)
            {
                window.Close();
            }
        }
    };

    // ---------------------------------------------------------------------------------------------
    // PRINT DETAILED ENGINE DIAGNOSTICS & STATUS
    // ---------------------------------------------------------------------------------------------
    Console.WriteLine("================================================================================");
    Console.WriteLine("     JATAYU VULKAN 1.3 3D ENGINE - PHASE 2 + PANINI PROJECTION            ");
    Console.WriteLine("================================================================================");
    Console.WriteLine($" [Window]        : {window.Width}x{window.Height} (Framebuffer: {window.FramebufferWidth}x{window.FramebufferHeight} px)");
    Console.WriteLine($" [Vulkan API]    : 1.3 (Dynamic Rendering, Push Constants, Hardware Depth Stencil)");
    Console.WriteLine($" [Depth Buffer]  : {depthBuffer.Width}x{depthBuffer.Height} | Format: {depthBuffer.Format} (32-bit Float Reverse-Z)");
    Console.WriteLine($" [3D Mesh]       : 3D Colored Cube ({cubeVertices.Length} Vertices, {cubeIndices.Length} Indices, 12 Triangles)");
    Console.WriteLine($" [Push Constants]: 80-byte PushConstantData (64-byte MVP + 16-byte Panini parameters)");
    Console.WriteLine($" [Camera]        : 3D Perspective (FOV: {camera.FovDegrees}°, Vulkan Y-Inverted NDC, Reverse-Z [1,0])");
    Console.WriteLine($" [Panini Proj]   : Active: {(camera.PaniniEnabled ? "YES" : "NO")} | Distance 'd': {camera.PaniniDistance:F2} | Scale: {camera.PaniniScale:F2}");
    Console.WriteLine($" [Swapchain]     : {swapchain.Width}x{swapchain.Height} | Buffers: {swapchain.ImageCount} | Format: {swapchain.ColorFormat}");
    Console.WriteLine($" [Sync Engine]   : {frameSync.MaxFramesInFlight} Frames in Flight (Parallel CPU/GPU Execution)");
    Console.WriteLine($" [Diagnostics]   : Dear ImGui HUD Active (Toggle with [F1])");
    Console.WriteLine("--------------------------------------------------------------------------------");
    Console.WriteLine(" INTERACTIVE HOTKEYS:");
    Console.WriteLine("   [F1]          : Toggle Diagnostics Telemetry HUD (FPS, Allocations, Pass Times, VRAM)");
    Console.WriteLine("   [P]           : Cycle Panini Projection (OFF -> Moderate d=0.5 -> Full d=1.0)");
    Console.WriteLine("   [F]           : Toggle FOV (Standard 60° <-> Ultra-Wide 110°)");
    Console.WriteLine("   [-] / [=]     : Fine-tune Panini distance 'd' (-0.1 / +0.1)");
    Console.WriteLine("   [Space]       : Toggle Auto-Orbit rotation On / Off");
    Console.WriteLine("   [Left/Right]  : Manual horizontal orbit (Yaw)");
    Console.WriteLine("   [Up/Down]     : Manual vertical orbit (Pitch)");
    Console.WriteLine("   [W / S]       : Zoom camera in / out");
    Console.WriteLine("   [V]           : Toggle V-Sync Live (Observe FPS change in Title Bar!)");
    Console.WriteLine("   [C]           : Cycle background clear colors");
    Console.WriteLine("   [Resize]      : Drag window border to test dynamic swapchain + depth buffer rebuild");
    Console.WriteLine("   [ESC]         : Graceful Shutdown");
    Console.WriteLine("================================================================================");

    // ---------------------------------------------------------------------------------------------
    // PHASE 6: THE MASTER 3D RENDER LOOP
    // ---------------------------------------------------------------------------------------------
    var time = new GameTime();

    while (!window.ShouldClose)
    {
        AllocationTracker.BeginFrame();
        Profiler.BeginFrame();

        // 1. Process OS window events
        using (Profiler.Scope("PollEvents"))
        {
            window.PollEvents();
        }

        // Skip rendering while minimized
        if (window.IsMinimized)
        {
            Profiler.EndFrame();
            AllocationTracker.EndFrame();
            continue;
        }

        // 2. Tick high-precision game clock
        using (Profiler.Scope("GameTime"))
        {
            time.Update();
        }
        var dt = time.DeltaTime;

        // Interactive manual camera controls when held down
        using (Profiler.Scope("CameraUpdate"))
        {
            if (window.IsKeyDown(GlfwKey.Left))
            {
                cameraController.AutoOrbitEnabled = false;
                cameraController.Yaw -= 2.0f * dt;
            }
            if (window.IsKeyDown(GlfwKey.Right))
            {
                cameraController.AutoOrbitEnabled = false;
                cameraController.Yaw += 2.0f * dt;
            }
            if (window.IsKeyDown(GlfwKey.Up))
            {
                cameraController.Pitch += 1.5f * dt;
            }
            if (window.IsKeyDown(GlfwKey.Down))
            {
                cameraController.Pitch -= 1.5f * dt;
            }
            if (window.IsKeyDown(GlfwKey.W))
            {
                cameraController.Zoom(-3.0f * dt);
            }
            if (window.IsKeyDown(GlfwKey.S))
            {
                cameraController.Zoom(3.0f * dt);
            }

            // Update camera position
            cameraController.Update(dt);
        }

        // Prepare ImGui HUD frame
        if (debugOverlay.IsVisible)
        {
            using (Profiler.Scope("ImGuiNewFrame"))
            {
                imguiBackend.NewFrame();
                debugOverlay.Draw(time, camera, window.VSync, vramBudget, Profiler.LatestSnapshot, gpuTimer.LatestSnapshot);
            }
        }

        // Handle dynamic swapchain / window resize
        if (swapchain.IsOutOfDate)
        {
            swapchain.Recreate();
            depthBuffer.Recreate(swapchain.Width, swapchain.Height);
            camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
            Profiler.EndFrame();
            AllocationTracker.EndFrame();
            continue;
        }

        // 3. Wait for current frame fence
        using (Profiler.Scope("WaitForFence"))
        {
            frameSync.WaitForFrameFence();
        }

        // 4. Acquire next swapchain image
        var acquireResult = swapchain.AcquireNextImage(frameSync.CurrentImageAvailable, out var imageIndex);
        if (acquireResult is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || swapchain.IsOutOfDate)
        {
            swapchain.Recreate();
            depthBuffer.Recreate(swapchain.Width, swapchain.Height);
            camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
            Profiler.EndFrame();
            AllocationTracker.EndFrame();
            continue;
        }

        frameSync.ResetFrameFence();

        var cmd = commandBuffers[frameSync.CurrentFrame];
        var image = swapchain.Images[imageIndex];
        var imageView = swapchain.ImageViews[imageIndex];

        // 5. Record GPU Rendering Commands
        using (Profiler.Scope("RecordCommands"))
        {
            cmd.Reset();
            cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

            gpuTimer.BeginFrame(cmd, frameSync.CurrentFrame);

            renderGraph.Reset();

            // Import swapchain presentation target and depth buffer into render graph
            var swapchainTarget = renderGraph.ImportTexture(
                "SwapchainColor",
                image,
                imageView,
                swapchain.ColorFormat,
                (uint)swapchain.Width,
                (uint)swapchain.Height,
                VkImageLayout.Undefined,
                VkPipelineStageFlags2.ColorAttachmentOutputBit,
                VkAccessFlags2.None
            );

            var depthTarget = renderGraph.ImportTexture(
                "SceneDepth",
                depthBuffer.Image,
                depthBuffer.ImageView,
                depthBuffer.Format,
                (uint)swapchain.Width,
                (uint)swapchain.Height,
                VkImageLayout.Undefined,
                VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
                VkAccessFlags2.None
            );

            // Calculate Model-View-Projection Matrix:
            var rotTime = (float)time.TotalTime;
            var model = Matrix4x4.CreateRotationY(rotTime * 0f) *
                        Matrix4x4.CreateRotationX(rotTime * 0f);
            var mvp = model * camera.ViewProjection;

            // 1. Main 3D Scene Rendering Pass (Dynamic Rendering with Reverse-Z Depth)
            renderGraph.AddPass(
                "MainScenePass",
                setup =>
                {
                    setup.SetColorAttachment(
                        swapchainTarget,
                        loadOp: VkAttachmentLoadOp.Clear,
                        storeOp: VkAttachmentStoreOp.Store,
                        clearValue: new VkClearColorValue { Float32 = clearColors[clearColorIndex] }
                    );
                    setup.SetDepthAttachment(
                        depthTarget,
                        loadOp: VkAttachmentLoadOp.Clear,
                        storeOp: VkAttachmentStoreOp.DontCare,
                        clearDepth: DepthConvention.ClearDepth
                    );
                },
                ctx =>
                {
                    var pCmd = ctx.CommandBuffer;
                    pipeline.Bind(pCmd);
                    pCmd.SetViewport(0, [new VkViewport { Width = swapchain.Width, Height = swapchain.Height, MaxDepth = 1 }]);
                    pCmd.SetScissor(0, [new VkRect2D(swapchain.Extent)]);

                    // Populate 80-byte PushConstantData containing MVP matrix and live Panini parameters
                    var pushData = new PushConstantData
                    {
                        Mvp = mvp,
                        PaniniDistance = camera.EffectivePaniniDistance,
                        PaniniScale = camera.PaniniScale,
                        AspectRatio = camera.AspectRatio,
                        Padding = 0f,
                    };

                    pCmd.PushConstants(
                        pipeline.Layout,
                        VkShaderStageFlags.VertexBit,
                        0,
                        new ReadOnlySpan<byte>(&pushData, sizeof(PushConstantData))
                    );

                    pCmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, pipeline.Layout, 0, [descriptorSet], []);
                    cubeMesh.Bind(pCmd);
                    cubeMesh.Draw(pCmd);

                    // Render Diagnostics Overlay (Dear ImGui)
                    if (debugOverlay.IsVisible)
                    {
                        imguiBackend.Render(pCmd);
                    }
                }
            );

            // 2. Presentation Transition Pass (ensures swapchain image is transitioned to PresentSrcKHR)
            renderGraph.AddPass(
                "PresentTransition",
                setup =>
                {
                    setup.ReadTexture(
                        swapchainTarget,
                        VkPipelineStageFlags2.ColorAttachmentOutputBit,
                        VkAccessFlags2.None,
                        VkImageLayout.PresentSrcKHR
                    );
                    setup.HasSideEffects(true);
                },
                ctx => { }
            );

            // Compile & Execute Render Graph DAG (sync2 barriers + Dynamic Rendering + GPU timer queries)
            renderGraph.Compile();
            renderGraph.Execute(cmd, gpuTimer);

            cmd.End();
        }

        // 6. Submit recorded commands to GPU Graphics Queue
        var signalSemaphore = frameSync.CurrentRenderFinished;
        using (Profiler.Scope("QueueSubmit"))
        {
            var waitSemaphore = frameSync.CurrentImageAvailable;
            queue.Submit(
                [waitSemaphore],
                [VkPipelineStageFlags.ColorAttachmentOutputBit],
                [cmd],
                [signalSemaphore],
                frameSync.CurrentFence
            );
        }

        // 7. Present rendered image to the desktop window surface
        using (Profiler.Scope("QueuePresent"))
        {
            var presentResult = swapchain.Present(queue, signalSemaphore, imageIndex);
            if (presentResult is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || swapchain.IsOutOfDate)
            {
                swapchain.Recreate();
                depthBuffer.Recreate(swapchain.Width, swapchain.Height);
                camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
            }
        }

        // 8. Retire deferred resource deletions & advance frame ring
        deletionQueue.Flush((ulong)frameSync.CurrentFrame);
        frameSync.AdvanceFrame();

        Profiler.EndFrame();
        AllocationTracker.EndFrame();
        AllocationTracker.AssertZeroAllocations();
    }

    // ---------------------------------------------------------------------------------------------
    // PHASE 7: GRACEFUL SHUTDOWN & CLEANUP
    // ---------------------------------------------------------------------------------------------
    Console.WriteLine("[Shutdown] Waiting for GPU idle...");
    device.WaitIdle();

    // C# 'using' automatically disposes pipeline, descriptorAllocator, descriptorLayout,
    // sampler, texture, commandPool, shaderStages, indexBuffer, vertexBuffer,
    // depthBuffer, frameSync, swapchain, allocator, context, and window.
    Console.WriteLine("[Shutdown] All 3D resources released cleanly. Goodbye!");
    }
}

/// <summary>
/// 80-byte Push Constant data structure matching the layout in cube.vert.
/// Injects Model-View-Projection matrix and real-time Panini parameters with zero allocation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct PushConstantData
{
    /// <summary>
    /// Combined 64-byte Model-View-Projection transformation matrix.
    /// </summary>
    public Matrix4x4 Mvp;

    /// <summary>
    /// Panini projection distance parameter 'd' (0.0 = linear perspective, >0 = Panini compression).
    /// </summary>
    public float PaniniDistance;

    /// <summary>
    /// Panini scale/crop factor (default: 1.0).
    /// </summary>
    public float PaniniScale;

    /// <summary>
    /// Viewport aspect ratio (Width / Height) needed to keep vertical proportions conformal.
    /// </summary>
    public float AspectRatio;

    /// <summary>
    /// 4-byte padding to maintain 16-byte alignment.
    /// </summary>
    public float Padding;
}
