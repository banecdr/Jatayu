/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: Program.cs
 * 
 * WHAT THIS APPLICATION DOES:
 *   HelloBrutal Engine - Phase 2: 3D Geometry, Depth Buffering & 3D Perspective Camera.
 *   
 *   Renders a fully interactive, solid 3D textured/colored cube in true 3D space with:
 *     1. Depth Testing (Z-Buffer): Hardware occlusion via our dedicated DepthBuffer attachment
 *        (D32_SFLOAT). Faces behind other faces are automatically occluded, preventing inside-out
 *        rendering artifacts.
 *     2. 3D Perspective Camera: Mathematically transforms 3D coordinates using a View matrix (eye
 *        looking at target) and a Vulkan-corrected Projection matrix (Y-flipped NDC, [0, 1] depth).
 *     3. Camera Controller: Supports automatic orbital rotation and interactive user keyboard controls
 *        (orbit yaw/pitch, zoom in/out, flycam motion).
 *     4. Vulkan Push Constants: Injects the 64-byte Model-View-Projection (MVP) matrix directly into
 *        the GPU command stream at maximum memory bandwidth with zero CPU allocations.
 *     5. Indexed 3D Mesh: 24 vertices (interleaved Position, Color, Normal, TexCoord) and 36 indices
 *        for the 12 triangles of the cube.
 *     6. Directional Diffuse Shading: Surface normals evaluated in GLSL for realistic 3D lighting.
 *     7. Multi-Frame Concurrency: 2 Frames in Flight allowing parallel CPU recording and GPU rendering.
 *     8. Dynamic Rendering & Resizing: Zero-crash dynamic swapchain and depth buffer recreation.
 *
 * INTERACTIVE CONTROLS:
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
using HelloBrutal.Core;
using HelloBrutal.Core.Time;
using HelloBrutal.Renderer.Camera;
using HelloBrutal.Renderer.Mesh;
using HelloBrutal.RHI.Vulkan.Context;
using HelloBrutal.RHI.Vulkan.Memory;
using HelloBrutal.RHI.Vulkan.Swapchain;
using HelloBrutal.RHI.Vulkan.Sync;
using HelloBrutal.RHI.Vulkan.Textures;

// -------------------------------------------------------------------------------------------------
// NATIVE VULKAN LOADER & RUNTIME INITIALIZATION
// -------------------------------------------------------------------------------------------------
_ = Vulkan.GetInstanceProcAddr;

RunEngineDemo();

static unsafe void RunEngineDemo()
{
    const int InitialWidth = 1280;
    const int InitialHeight = 720;

    // ---------------------------------------------------------------------------------------------
    // PHASE 1: DESKTOP WINDOW & VULKAN 1.3 HARDWARE DEVICE BOOTSTRAP
    // ---------------------------------------------------------------------------------------------
    using var window = new Window(
        title: "BRUTAL Engine | Phase 2: 3D Geometry & Depth Buffer",
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
    // PHASE 2: DEPTH BUFFER & 3D PERSPECTIVE CAMERA INITIALIZATION
    // ---------------------------------------------------------------------------------------------
    // Create dedicated GPU Depth Buffer for hardware depth testing (Z-buffering occlusion)
    using var depthBuffer = new DepthBuffer(context, allocator, swapchain.Width, swapchain.Height);

    // 3D Perspective Camera looking at the origin
    var camera = new Camera3D(
        position: new Vector3(0f, 2.0f, 4.5f),
        target: Vector3.Zero,
        width: swapchain.Width,
        height: swapchain.Height,
        fovDegrees: 60f,
        nearPlane: 0.1f,
        farPlane: 1000f
    );

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

    using var vertexBuffer = allocator.CreateBufferWithData<Vertex3D>(
        cubeVertices,
        VkBufferUsageFlags.VertexBufferBit
    );

    using var indexBuffer = allocator.CreateBufferWithData<ushort>(
        cubeIndices,
        VkBufferUsageFlags.IndexBufferBit
    );

    // ---------------------------------------------------------------------------------------------
    // PHASE 4: 3D SHADER COMPILATION & VULKAN GRAPHICS PIPELINE
    // ---------------------------------------------------------------------------------------------
    using var compiler = ShaderC.CreateCompiler();
    using var options = ShaderC.CreateCompilerOptions();

    VkShaderModule CompileShaderFromFile(string filePath, ShaderKind kind)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var spirv = compiler.Compile(bytes, kind, "shader"u8, "main"u8, options);
        return ShaderModuleExtensions.FromSpirv(
            device,
            new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()),
            null
        );
    }

    using var shaderStages = new ShaderStages(
        device,
        [
            Presets.ShaderStage.Vertex(CompileShaderFromFile("assets/shaders/cube.vert", ShaderKind.VertexShader)),
            Presets.ShaderStage.Fragment(CompileShaderFromFile("assets/shaders/cube.frag", ShaderKind.FragmentShader)),
        ],
        null
    );

    // Configure Vulkan Push Constant range for the 64-byte MVP matrix
    var pushConstantRange = new VkPushConstantRange
    {
        StageFlags = VkShaderStageFlags.VertexBit,
        Offset = new ByteSize32(0),
        Size = new ByteSize32((uint)sizeof(Matrix4x4)), // 64 bytes
    };

    var pipelineLayout = device.CreatePipelineLayout([], [pushConstantRange], null);

    // Configure Vulkan 1.3 Graphics Pipeline with Dynamic Rendering and Hardware Depth Stencil
    var format = swapchain.ColorFormat;
    var vertexInput = new VertexInput(1, 4).AddBindingWithAttributes<Vertex3D>(0, 0, VkVertexInputRate.Vertex);
    var viewport = new VkViewport { Width = swapchain.Width, Height = swapchain.Height, MaxDepth = 1 };
    var scissor = new VkRect2D(swapchain.Extent);
    var viewportState = new VkPipelineViewportStateCreateInfo
    {
        ViewportCount = 1,
        Viewports = &viewport,
        ScissorCount = 1,
        Scissors = &scissor,
    };

    // Specify both Color and Depth attachment formats directly on the pipeline!
    var rendering = new VkPipelineRenderingCreateInfo
    {
        ColorAttachmentCount = 1,
        ColorAttachmentFormats = &format,
        DepthAttachmentFormat = depthBuffer.Format,
    };

    VkDynamicState[] dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];
    VkPipeline pipeline;
    fixed (VkDynamicState* pDynamicStates = dynamicStates)
    {
        var dynamicState = new VkPipelineDynamicStateCreateInfo
        {
            DynamicStateCount = 2,
            DynamicStates = pDynamicStates,
        };

        pipeline = device.CreateGraphicsPipeline(
            VkPipelineCache.NullHandle,
            new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = shaderStages.Count,
                Stages = shaderStages,
                VertexInputState = vertexInput,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                ViewportState = &viewportState,
                RasterizationState = Presets.Rasterization.Fill.CullNone,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = Presets.DepthStencil.DepthTestWrite, // Active Depth Testing & Writing!
                ColorBlendState = Presets.BlendState.BlendNone,
                DynamicState = &dynamicState,
                Layout = pipelineLayout,
            },
            null
        );
    }

    // ---------------------------------------------------------------------------------------------
    // PHASE 5: COMMAND POOL & MULTI-FRAME IN-FLIGHT SYNCHRONIZATION
    // ---------------------------------------------------------------------------------------------
    var commandPool = device.CreateCommandPool(
        new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = queueFamily,
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
        },
        null
    );

    using var frameSync = new FrameSync(context, maxFramesInFlight: 2);

    var commandBuffers = new CommandBuffer[frameSync.MaxFramesInFlight];
    for (var i = 0; i < frameSync.MaxFramesInFlight; i++)
    {
        commandBuffers[i] = device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary }
        );
    }

    var colorRange = new VkImageSubresourceRange
    {
        AspectMask = VkImageAspectFlags.ColorBit,
        LevelCount = 1,
        LayerCount = 1,
    };

    void TransitionColorImage(
        CommandBuffer cmd,
        VkImage img,
        VkImageLayout from,
        VkImageLayout to,
        VkPipelineStageFlags srcStage,
        VkPipelineStageFlags dstStage,
        VkAccessFlags srcAccess,
        VkAccessFlags dstAccess
    )
    {
        cmd.PipelineBarrier(
            srcStage,
            dstStage,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = srcAccess,
                    DstAccessMask = dstAccess,
                    OldLayout = from,
                    NewLayout = to,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = img,
                    SubresourceRange = colorRange,
                },
            ]
        );
    }

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
    Console.WriteLine("        HELLO BRUTAL VULKAN 1.3 3D ENGINE - PHASE 2 (3D CUBE & DEPTH)           ");
    Console.WriteLine("================================================================================");
    Console.WriteLine($" [Window]        : {window.Width}x{window.Height} (Framebuffer: {window.FramebufferWidth}x{window.FramebufferHeight} px)");
    Console.WriteLine($" [Vulkan API]    : 1.3 (Dynamic Rendering, Push Constants, Hardware Depth Stencil)");
    Console.WriteLine($" [Depth Buffer]  : {depthBuffer.Width}x{depthBuffer.Height} | Format: {depthBuffer.Format} (32-bit Float Precision)");
    Console.WriteLine($" [3D Mesh]       : 3D Colored Cube ({cubeVertices.Length} Vertices, {cubeIndices.Length} Indices, 12 Triangles)");
    Console.WriteLine($" [Push Constants]: 64-byte MVP Matrix directly in Command Stream");
    Console.WriteLine($" [Camera]        : 3D Perspective (FOV: {camera.FovDegrees}°, Vulkan Y-Inverted NDC, [0,1] Depth)");
    Console.WriteLine($" [Swapchain]     : {swapchain.Width}x{swapchain.Height} | Buffers: {swapchain.ImageCount} | Format: {swapchain.ColorFormat}");
    Console.WriteLine($" [Sync Engine]   : {frameSync.MaxFramesInFlight} Frames in Flight (Parallel CPU/GPU Execution)");
    Console.WriteLine("--------------------------------------------------------------------------------");
    Console.WriteLine(" INTERACTIVE HOTKEYS:");
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
        // 1. Process OS window events
        window.PollEvents();

        // Skip rendering while minimized
        if (window.IsMinimized)
            continue;

        // 2. Tick high-precision game clock
        time.Update();
        var dt = time.DeltaTime;

        // Interactive manual camera controls when held down
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

        // Update live telemetry in the window title
        window.Title = $"BRUTAL 3D Vulkan 1.3 | FPS: {time.Fps:F0} ({time.AverageFrameTimeMs:F2} ms) | {swapchain.Width}x{swapchain.Height} | Depth: {depthBuffer.Format} | VSync: {(window.VSync ? "ON" : "OFF")} | Orbit: {(cameraController.AutoOrbitEnabled ? "AUTO" : "MANUAL")} | Frame #{time.FrameCount}";

        // Handle dynamic swapchain / window resize
        if (swapchain.IsOutOfDate)
        {
            swapchain.Recreate();
            depthBuffer.Recreate(swapchain.Width, swapchain.Height);
            camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
            continue;
        }

        // 3. Wait for current frame fence
        frameSync.WaitForFrameFence();

        // 4. Acquire next swapchain image
        var acquireResult = swapchain.AcquireNextImage(frameSync.CurrentImageAvailable, out var imageIndex);
        if (acquireResult is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || swapchain.IsOutOfDate)
        {
            swapchain.Recreate();
            depthBuffer.Recreate(swapchain.Width, swapchain.Height);
            camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
            continue;
        }

        frameSync.ResetFrameFence();

        var cmd = commandBuffers[frameSync.CurrentFrame];
        var image = swapchain.Images[imageIndex];
        var imageView = swapchain.ImageViews[imageIndex];

        // 5. Record GPU Rendering Commands
        cmd.Reset();
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        // Transition swapchain image: Undefined -> ColorAttachmentOptimal
        TransitionColorImage(
            cmd,
            image,
            VkImageLayout.Undefined,
            VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkAccessFlags.None,
            VkAccessFlags.ColorAttachmentWriteBit
        );

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            ImageView = imageView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = clearColors[clearColorIndex] },
        };

        // Depth Attachment: Clear depth buffer to 1.0 (furthest distance) every frame
        var depthAttachment = depthBuffer.GetAttachmentInfo(
            loadOp: VkAttachmentLoadOp.Clear,
            storeOp: VkAttachmentStoreOp.DontCare,
            clearDepth: 1.0f
        );

        // Begin Modern Vulkan 1.3 Dynamic Rendering with Depth Buffer!
        cmd.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(swapchain.Extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthAttachment = &depthAttachment,
            }
        );

        // Bind 3D graphics pipeline, set dynamic viewport & scissor
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        cmd.SetViewport(0, [new VkViewport { Width = swapchain.Width, Height = swapchain.Height, MaxDepth = 1 }]);
        cmd.SetScissor(0, [new VkRect2D(swapchain.Extent)]);

        // Calculate Model-View-Projection Matrix:
        // Rotate 3D cube around multiple axes to showcase true 3D spatial geometry and depth occlusion
        var rotTime = (float)time.TotalTime;
        var model = Matrix4x4.CreateRotationY(rotTime * 0.9f) *
                    Matrix4x4.CreateRotationX(rotTime * 0.45f);
        var mvp = model * camera.ViewProjection;

        // Push MVP Matrix to GPU via high-speed Push Constants (64 bytes)
        cmd.PushConstants(
            pipelineLayout,
            VkShaderStageFlags.VertexBit,
            0,
            new ReadOnlySpan<byte>(&mvp, sizeof(Matrix4x4))
        );

        // Bind vertex and index buffers and draw the 36 indices (12 triangles)
        cmd.BindVertexBuffers(0, [vertexBuffer.Handle], [new ByteSize64(0)]);
        cmd.BindIndexBuffer(indexBuffer.Handle, new ByteSize64(0), VkIndexType.UInt16);
        cmd.DrawIndexed(36, 1, 0, 0, 0);

        cmd.EndRendering();

        // Transition swapchain image: ColorAttachmentOptimal -> PresentSrcKHR
        TransitionColorImage(
            cmd,
            image,
            VkImageLayout.ColorAttachmentOptimal,
            VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkAccessFlags.ColorAttachmentWriteBit,
            VkAccessFlags.None
        );

        cmd.End();

        // 6. Submit recorded commands to GPU Graphics Queue
        var waitSemaphore = frameSync.CurrentImageAvailable;
        var signalSemaphore = frameSync.CurrentRenderFinished;
        queue.Submit(
            [waitSemaphore],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [cmd],
            [signalSemaphore],
            frameSync.CurrentFence
        );

        // 7. Present rendered image to the desktop window surface
        var presentResult = swapchain.Present(queue, signalSemaphore, imageIndex);
        if (presentResult is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || swapchain.IsOutOfDate)
        {
            swapchain.Recreate();
            depthBuffer.Recreate(swapchain.Width, swapchain.Height);
            camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
        }

        // 8. Advance ring buffer to next frame slot (0 -> 1 -> 0 -> 1)
        frameSync.AdvanceFrame();
    }

    // ---------------------------------------------------------------------------------------------
    // PHASE 7: GRACEFUL SHUTDOWN & CLEANUP
    // ---------------------------------------------------------------------------------------------
    Console.WriteLine("[Shutdown] Waiting for GPU idle...");
    device.WaitIdle();

    device.DestroyCommandPool(commandPool, null);
    device.DestroyPipeline(pipeline, null);
    device.DestroyPipelineLayout(pipelineLayout, null);

    // C# 'using' automatically disposes indexBuffer, vertexBuffer, depthBuffer,
    // frameSync, swapchain, allocator, context, and window.
    Console.WriteLine("[Shutdown] All 3D resources released cleanly. Goodbye!");
}
