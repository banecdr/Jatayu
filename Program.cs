/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: Program.cs
 * 
 * WHAT THIS FILE DOES:
 *   This is the complete, working "Hello Triangle" Vulkan 1.3 program built directly on the 
 *   BRUTAL framework (Ahwoo.BRUTAL.Vulkan, Vulkan.Abstractions, Vulkan.Vma, Glfw, ShaderC).
 *   It opens a GLFW desktop window, initializes modern Vulkan 1.3 with Dynamic Rendering, 
 *   allocates a vertex buffer using VMA, compiles GLSL shaders to SPIR-V at startup, 
 *   records command buffers, and renders a smooth color-interpolated triangle to your screen 
 *   while updating the window title with a live FPS counter.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. GLFW Setup & Vulkan Loader:
 *      Touching Vulkan.GetInstanceProcAddr ensures Brutal's native Vulkan loader (libVulkanEx) 
 *      is loaded before GLFW queries extensions. GLFW is configured with ClientApi = 0 (no OpenGL).
 *
 *   2. Vulkan 1.3 Instance & Surface:
 *      Creates a Vulkan 1.3 instance with platform-required extensions and binds to the GLFW 
 *      window handle using Glfw.CreateWindowSurface to produce a VkSurfaceKHR.
 *
 *   3. GPU Selection & Device Creation:
 *      Iterates candidate GPUs to find one supporting graphics commands AND presentation to our 
 *      window surface. Enables Vulkan 1.3 Dynamic Rendering (which completely eliminates legacy 
 *      VkRenderPass / VkFramebuffer boilerplate!).
 *
 *   4. Memory Management (VMA):
 *      Initializes an AMD Vulkan Memory Allocator (VMA) instance. Uses it to create a host-visible, 
 *      persistently mapped vertex buffer holding the 3 vertices (Red, Green, Blue).
 *
 *   5. Shaders & Graphics Pipeline:
 *      Compiles GLSL vertex and fragment shaders into SPIR-V bytecode on startup using ShaderC. 
 *      Sets up fixed-function pipeline states using Brutal's Presets (TriangleList, CullNone).
 *
 *   6. Double-Buffered Swapchain & In-Flight Frame Synchronization:
 *      Allocates swapchain images and image views. Uses fences and semaphores (ImageAvailable, 
 *      RenderFinished) so the CPU never overwrites in-flight GPU frames.
 *
 *   7. Dynamic Rendering & Draw Loop:
 *      Each frame, transitions the swapchain image to ColorAttachmentOptimal, calls 
 *      BeginRendering(), binds the pipeline and vertex buffer, issues Draw(3), calls EndRendering(), 
 *      transitions image to PresentSrcKHR, and submits to the presentation queue.
 *
 *   8. Live FPS Counter:
 *      Integrates our HelloBrutal.Core.Time.GameTime clock to measure frame delta times and 
 *      updates the window title with live FPS and frame time in milliseconds.
 * ================================================================================================= */

using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using HelloBrutal.Core;
using HelloBrutal.Core.Time;
using HelloBrutal.RHI.Vulkan.Context;

// 1. Ensure Brutal's Vulkan loader is initialized before GLFW queries extensions
_ = Vulkan.GetInstanceProcAddr;

RunVulkanTriangle();

static unsafe void RunVulkanTriangle()
{
    const int WindowWidth = 1200;
    const int WindowHeight = 900;

    // Use our custom HelloBrutal.Core.Window (Vulkan-ready, centers on monitor, tracks High-DPI pixels)
    using var window = new Window("BRUTAL Vulkan Triangle", WindowWidth, WindowHeight, resizable: false, vsync: false);

    window.GlfwHandle.OnKey += (handle, key, _, _, _) =>
    {
        if (key == GlfwKey.Escape)
            handle.ShouldClose = true;
    };

    // 2. Initialize VulkanContext (Instance, Surface, Physical GPU selection, Dynamic Rendering, Device & Queues)
    using var context = new VulkanContext(window);
    var instance = context.Instance;
    var surface = context.Surface;
    var physicalDevice = context.PhysicalDevice;
    var device = context.Device;
    var queue = context.GraphicsQueue;
    var queueFamily = context.QueueFamilies.GraphicsFamily!.Value;

    // 3. Initialize VMA (Vulkan Memory Allocator)
    var allocator = device.CreateVmaAllocator(
        new VmaAllocatorCreateInfo
        {
            Instance = instance,
            PhysicalDevice = physicalDevice,
            Device = device,
            VulkanApiVersion = (uint)VulkanHelpers.Api.VERSION_1_3,
        }
    );

    // 8. Allocate Vertex Buffer with 3 Colored Vertices (RGB Triangle)
    ReadOnlySpan<Vertex> vertices =
    [
        new(new float2(0.0f, -0.6f), new float3(1.0f, 0.2f, 0.2f)), // Top (Red)
        new(new float2(0.6f, 0.6f), new float3(0.2f, 1.0f, 0.2f)),  // Bottom Right (Green)
        new(new float2(-0.6f, 0.6f), new float3(0.2f, 0.4f, 1.0f)), // Bottom Left (Blue)
    ];
    var verticesSize = ByteSize.Of(vertices);

    allocator.CreateBuffer(
        new VkBufferCreateInfo { Size = verticesSize, Usage = VkBufferUsageFlags.VertexBufferBit },
        new VmaAllocationCreateInfo
        {
            Usage = VmaMemoryUsage.Auto,
            Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
        },
        out var vertexBuffer,
        out var vertexAllocation,
        out var vertexAllocationInfo
    );

    vertices.CopyTo(new Span<Vertex>((void*)vertexAllocationInfo.PMappedData, vertices.Length));
    allocator.FlushAllocation(vertexAllocation, ByteSize.Zero, verticesSize);

    // 9. Create Swapchain
    var capabilities = physicalDevice.GetSurfaceCapabilitiesKHR(surface);
    var extent = capabilities.CurrentExtent;
    if (extent.Width == -1 && extent.Height == -1)
    {
        extent = new VkExtent2D(
            Math.Clamp(window.FramebufferWidth, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Math.Clamp(window.FramebufferHeight, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        );
    }

    var imageCount = Math.Max(capabilities.MinImageCount, 2);
    if (capabilities.MaxImageCount > 0)
        imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

    VkCompositeAlphaFlagsKHR[] alphaModes =
    [
        VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
        VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
        VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
        VkCompositeAlphaFlagsKHR.InheritBitKHR,
    ];
    var compositeAlpha = alphaModes.First(mode => (capabilities.SupportedCompositeAlpha & mode) != 0);

    Span<VkSurfaceFormatKHR> surfaceFormats = stackalloc VkSurfaceFormatKHR[physicalDevice.GetSurfaceFormatsKHR_Count(surface)];
    physicalDevice.GetSurfaceFormatsKHR_Write(surface, surfaceFormats);
    var format = surfaceFormats[0].Format;

    var swapchain = device.CreateSwapchainKHR(
        new VkSwapchainCreateInfoKHR
        {
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = format,
            ImageColorSpace = surfaceFormats[0].ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = compositeAlpha,
            PresentMode = window.VSync ? VkPresentModeKHR.FifoKHR : VkPresentModeKHR.ImmediateKHR,
            Clipped = true,
        },
        null
    );

    var images = new VkImage[device.GetSwapchainImagesKHR_Count(swapchain)];
    device.GetSwapchainImagesKHR_Write(swapchain, images);

    var colorRange = new VkImageSubresourceRange
    {
        AspectMask = VkImageAspectFlags.ColorBit,
        LevelCount = 1,
        LayerCount = 1,
    };

    var imageViews = new VkImageView[images.Length];
    for (var i = 0; i < images.Length; i++)
    {
        imageViews[i] = device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = images[i],
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = colorRange,
            },
            null
        );
    }

    // 10. Compile Shaders using ShaderC
    using var compiler = ShaderC.CreateCompiler();
    using var options = ShaderC.CreateCompilerOptions();

    VkShaderModule CompileShader(ReadOnlySpan<byte> glsl, ShaderKind kind)
    {
        using var spirv = compiler.Compile(glsl, kind, "shader"u8, "main"u8, options);
        return ShaderModuleExtensions.FromSpirv(
            device,
            new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()),
            null
        );
    }

    using var shaderStages = new ShaderStages(
        device,
        [
            Presets.ShaderStage.Vertex(
                CompileShader(
                    """
                    #version 450
                    layout(location = 0) in vec2 position;
                    layout(location = 1) in vec3 inColor;
                    layout(location = 0) out vec3 color;
                    void main() {
                        gl_Position = vec4(position, 0.0, 1.0);
                        color = inColor;
                    }
                    """u8,
                    ShaderKind.VertexShader
                )
            ),
            Presets.ShaderStage.Fragment(
                CompileShader(
                    """
                    #version 450
                    layout(location = 0) in vec3 color;
                    layout(location = 0) out vec4 outColor;
                    void main() {
                        outColor = vec4(color, 1.0);
                    }
                    """u8,
                    ShaderKind.FragmentShader
                )
            ),
        ],
        null
    );

    // 11. Create Graphics Pipeline with Dynamic Rendering
    var vertexInput = new VertexInput(1, 2).AddBindingWithAttributes<Vertex>(0, 0, VkVertexInputRate.Vertex);
    var viewport = new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1 };
    var scissor = new VkRect2D(extent);
    var viewportState = new VkPipelineViewportStateCreateInfo
    {
        ViewportCount = 1,
        Viewports = &viewport,
        ScissorCount = 1,
        Scissors = &scissor,
    };
    var rendering = new VkPipelineRenderingCreateInfo { ColorAttachmentCount = 1, ColorAttachmentFormats = &format };

    var pipelineLayout = device.CreatePipelineLayout([], [], null);
    var pipeline = device.CreateGraphicsPipeline(
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
            ColorBlendState = Presets.BlendState.BlendNone,
            Layout = pipelineLayout,
        },
        null
    );

    // 12. Create Command Pool & Buffers
    var commandPool = device.CreateCommandPool(
        new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = queueFamily,
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
        },
        null
    );

    var commandBuffer = device.AllocateCommandBuffer(
        new VkCommandBufferAllocateInfo { CommandPool = commandPool, Level = VkCommandBufferLevel.Primary }
    );

    var imageAvailable = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
    var renderFinished = images.Select(_ => device.CreateSemaphore(new VkSemaphoreCreateInfo(), null)).ToArray();
    var frameFence = device.CreateFence(VkFenceCreateFlags.SignaledBit, null);

    void TransitionImage(
        VkImage image,
        VkImageLayout from,
        VkImageLayout to,
        VkPipelineStageFlags srcStage,
        VkPipelineStageFlags dstStage,
        VkAccessFlags srcAccess,
        VkAccessFlags dstAccess
    )
    {
        commandBuffer.PipelineBarrier(
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
                    Image = image,
                    SubresourceRange = colorRange,
                },
            ]
        );
    }

    // 13. Initialize GameTime Clock for Frame Timing and FPS
    var time = new GameTime();

    Console.WriteLine("========================================");
    Console.WriteLine(" BRUTAL Vulkan 1.3 Triangle Running!");
    Console.WriteLine(" Press ESC or close window to exit.");
    Console.WriteLine("========================================");

    // 14. Master Render Loop
    while (!window.ShouldClose)
    {
        window.PollEvents();
        time.Update();

        // Update Window Title with Live FPS & Frame Time
        window.Title = $"BRUTAL Vulkan Triangle | FPS: {time.Fps:F0} ({time.AverageFrameTimeMs:F2} ms)";

        // Wait for previous frame fence
        device.WaitForFence(frameFence, -1);
        device.ResetFences([frameFence]);

        // Acquire next swapchain image
        device.AcquireNextImageKHR(swapchain, -1, imageAvailable, VkFence.NullHandle, out var imageIndex);
        var image = images[imageIndex];

        // Record commands
        commandBuffer.Reset();
        commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        TransitionImage(
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
            ImageView = imageViews[imageIndex],
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            ClearValue = new VkClearColorValue { Float32 = new float4(0.08f, 0.08f, 0.12f, 1.0f) }, // Dark navy clear
        };

        commandBuffer.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
            }
        );

        commandBuffer.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        commandBuffer.BindVertexBuffers(0, [vertexBuffer], [new ByteSize64(0)]);
        commandBuffer.Draw(3, 1, 0, 0);
        commandBuffer.EndRendering();

        TransitionImage(
            image,
            VkImageLayout.ColorAttachmentOptimal,
            VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkAccessFlags.ColorAttachmentWriteBit,
            VkAccessFlags.None
        );

        commandBuffer.End();

        // Submit to Queue
        var signalSemaphore = renderFinished[imageIndex];
        queue.Submit(
            [imageAvailable],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [commandBuffer],
            [signalSemaphore],
            frameFence
        );

        // Present to Window
        queue.PresentKHR(
            new VkPresentInfoKHR
            {
                WaitSemaphoreCount = 1,
                WaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                Swapchains = &swapchain,
                ImageIndices = &imageIndex,
            }
        );
    }

    // 15. Cleanup Vulkan Resources
    device.WaitIdle();

    allocator.DestroyBuffer(vertexBuffer, vertexAllocation);
    device.DestroyVmaAllocator(allocator);
    device.DestroyFence(frameFence, null);
    device.DestroySemaphore(imageAvailable, null);
    foreach (var semaphore in renderFinished)
        device.DestroySemaphore(semaphore, null);
    device.DestroyCommandPool(commandPool, null);
    device.DestroyPipeline(pipeline, null);
    device.DestroyPipelineLayout(pipelineLayout, null);
    foreach (var imageView in imageViews)
        device.DestroyImageView(imageView, null);
    device.DestroySwapchainKHR(swapchain, null);
}

/// <summary>
/// Vertex structure holding 2D position and RGB color.
/// </summary>
struct Vertex
{
    public float2 Position;
    public float3 Color;

    public Vertex(float2 position, float3 color)
    {
        Position = position;
        Color = color;
    }
}
