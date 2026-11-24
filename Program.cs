/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: Program.cs
 * 
 * WHAT THIS APPLICATION DOES:
 *   Jatayu Engine - Sprint 5b: Large-World 3D Forward Rendering Demo
 *   
 *   Showcases:
 *     1. Large-World Precision (CL-02): Zero vertex jitter at 50,000 meters from world origin
 *        via 64-bit double-precision WorldPosition and camera-relative transform evaluation.
 *     2. Floating-Origin Rebase (CL-03): Deterministic 256m terrain-tile-snapped origin rebasing
 *        triggered when the camera exceeds threshold distance, broadcasting OriginShiftedEvent.
 *     3. Physically-Based Rendering (PBR): Cook-Torrance GGX microfacet specular and Lambertian
 *        diffuse BRDF with metallic/roughness material workflow and hemispherical sky/ground ambient.
 *     4. Real-Time Directional Soft Shadows: 2048x2048 D32_SFLOAT shadow map with Reverse-Z comparison
 *        sampler and 16-tap Poisson disk percentage-closer filtering (PCF).
 *     5. Reverse-Z Procedural Skybox: Fullscreen atmospheric sky dome rendered at infinite depth
 *        (Z = 0.0) with zero precision degradation.
 *     6. Bindless Texturing: Vulkan 1.2 core descriptor indexing table supporting 4,096 textures
 *        with runtime slot assignment and anisotropic 16x filtering.
 *     7. Monolithic Geometry Pool: 64MB vertex / 32MB index buffer pool sharing GPU geometry.
 *     8. Immediate-Mode 3D Debug Wireframes: Diagnostics lines, coordinate axes, and bounding boxes.
 *     9. Panini (Pannini) Projection: Cylindrical perspective model eliminating edge distortion at wide FOV.
 *    10. Zero Steady-State Allocations: Verified 0 bytes GC heap allocation per frame via AllocationTracker.
 *
 * INTERACTIVE CONTROLS:
 *   [T]          : Teleport between World Origin (0m) and 50,000m Monolith Cluster
 *   [L]          : Toggle Sun Light Orbit animation (animates soft PCF directional shadows)
 *   [B]          : Toggle 3D Wireframe Debug bounds & coordinate axes (DebugRenderer)
 *   [Space]      : Toggle Auto-Orbit camera rotation On / Off
 *   [P]          : Toggle shadow PCF quality (3x3 vs 16-tap Poisson)
 *   [G]          : Cycle shadow debug view (cascades, depth, bias, off)
 *   [F]          : Toggle FOV (Standard 60° <-> Ultra-Wide 110°)
 *   [-] / [=]    : Fine-tune Panini distance 'd' (-0.1 / +0.1)
 *   [Left/Right] : Orbit camera horizontally (Yaw)
 *   [Up/Down]    : Orbit camera vertically (Pitch)
 *   [W / S]      : Zoom camera in / out (Distance)
 *   [V]          : Toggle V-Sync Live (Observe FPS change in Title Bar / HUD)
 *   [F1]         : Toggle Diagnostics HUD overlay (Telemetry, Allocations, Pass Timers, VRAM)
 *   [ESC]        : Clean shutdown
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.Loaders;
using Jatayu.Core;
using Jatayu.Core.Config;
using Jatayu.Core.Events;
using Jatayu.Core.Mathematics;
using Jatayu.Core.Profiling;
using Jatayu.Core.Time;
using Jatayu.DebugUI;
using Jatayu.Renderer;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Debug;
using Jatayu.Renderer.GpuDriven;
using Jatayu.Renderer.Graph;
using Jatayu.Renderer.Lighting;
using Jatayu.Renderer.Material;
using Jatayu.Renderer.Mesh;
using Jatayu.Renderer.Particles;
using Jatayu.Renderer.Shadows;
using Jatayu.Renderer.Transparent;
using Jatayu.Renderer.Vegetation;
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
using Jatayu.Scene;
using Jatayu.Scene.Components;
using Jatayu.AssetFormats.World;
using Jatayu.Assets;
using Jatayu.Assets.Textures;
using Jatayu.Terrain;
using Jatayu.World;

namespace Jatayu;

/// <summary>
/// Main application entry point for Jatayu Engine.
/// </summary>
public static class Program
{
    public static unsafe void Main(string[] args)
    {
        _ = Vulkan.GetInstanceProcAddr;
        RunEngineDemo(args);
    }

    private static unsafe void RunEngineDemo(string[]? args = null)
    {
        int maxFrames = int.MaxValue;
        bool initialStress = false;
        bool initialNoVsync = false;
        bool benchmarkMode = false;
        bool initialAutopilot = false;

        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out int f))
                {
                    maxFrames = f;
                    i++;
                }
                else if (args[i] == "--stress")
                {
                    initialStress = true;
                }
                else if (args[i] == "--no-vsync")
                {
                    initialNoVsync = true;
                }
                else if (args[i] == "--benchmark")
                {
                    benchmarkMode = true;
                }
                else if (args[i] == "--autotour")
                {
                    initialAutopilot = true;
                }
            }
        }

        const int InitialWidth = 1280;
        const int InitialHeight = 720;

        // ---------------------------------------------------------------------------------------------
        // PHASE 1: DESKTOP WINDOW & VULKAN 1.3 HARDWARE DEVICE BOOTSTRAP
        // ---------------------------------------------------------------------------------------------
        using var window = new Window(
            title: "Jatayu Engine - Sprint 7: GPU-Driven Indirect, Wind Foliage & Particles",
            width: InitialWidth,
            height: InitialHeight,
            resizable: true,
            vsync: !initialNoVsync
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
        // PHASE 2: DEPTH BUFFER & 3D CAMERA WITH PANINI PROJECTION
        // ---------------------------------------------------------------------------------------------
        using var depthBuffer = new DepthBuffer(context, allocator, swapchain.Width, swapchain.Height);

        var camera = new Camera3D(
            position: new Vector3(0f, 3.5f, 9.0f),
            target: new Vector3(0f, 1.2f, 0f),
            width: swapchain.Width,
            height: swapchain.Height,
            fovDegrees: 60f,
            nearPlane: 0.1f,
            farPlane: 1000f
        );

        var cameraController = new CameraController(camera)
        {
            Distance = 9.0f,
            Pitch = 0.28f,
            Yaw = 0.0f,
            AutoOrbitEnabled = true,
            OrbitSpeed = 0.35f,
            MoveSpeed = 10.0f,
        };

        window.Resized += (newWidth, newHeight) =>
        {
            depthBuffer.Recreate(newWidth, newHeight);
            camera.UpdateAspectRatio(newWidth, newHeight);
        };

        // ---------------------------------------------------------------------------------------------
        // PHASE 3: ASYNC UPLOAD MANAGER, BINDLESS TABLE & TEXTURES
        // ---------------------------------------------------------------------------------------------
        using var uploadManager = new UploadManager(context, allocator);
        using var deletionQueue = new DeferredDeletionQueue(context);
        var vramBudget = new VramBudget(context);

        // Global Bindless Texture Table (Vulkan 1.2 Descriptor Indexing)
        using var bindlessTable = new BindlessTable(context);

        // Procedural 2D checkerboard texture registered into bindless slot
        var checkerImage = TextureLoader.CreateCheckerboard(width: 256, height: 256, tileSize: 32);
        using var texture = uploadManager.CreateTextureFromImageData(checkerImage, out _);
        using var sampler = VulkanSampler.CreateAnisotropic16xRepeat(context);
        uint checkerSlot = bindlessTable.RegisterTexture(texture.ImageView, sampler.Handle);

        // ---------------------------------------------------------------------------------------------
        // PHASE 4: MATERIAL TABLE (GPU STORAGE BUFFER - 2048 SLOTS)
        // ---------------------------------------------------------------------------------------------
        using var materialTable = new MaterialTable(context, allocator);

        // Slot 1: Hero Gold (Metallic = 1.0, Roughness = 0.15)
        uint goldMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "Gold",
            BaseColor = new Vector4(1.0f, 0.76f, 0.33f, 1.0f),
            Metallic = 1.0f,
            Roughness = 0.15f
        });

        // Slot 2: Orbiting Copper (Metallic = 0.95, Roughness = 0.25)
        uint copperMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "Copper",
            BaseColor = new Vector4(0.95f, 0.64f, 0.54f, 1.0f),
            Metallic = 0.95f,
            Roughness = 0.25f
        });

        // Slot 3: Smooth Chrome (Metallic = 1.0, Roughness = 0.05)
        uint chromeMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "Chrome",
            BaseColor = new Vector4(0.95f, 0.95f, 0.95f, 1.0f),
            Metallic = 1.0f,
            Roughness = 0.05f
        });

        // Slot 4: Rough Terracotta (Metallic = 0.0, Roughness = 0.85)
        uint terracottaMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "Terracotta",
            BaseColor = new Vector4(0.85f, 0.35f, 0.20f, 1.0f),
            Metallic = 0.0f,
            Roughness = 0.85f
        });

        // Slot 5: Emissive Neon Blue Crystal
        uint emissiveMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "NeonBlue",
            BaseColor = new Vector4(0.1f, 0.1f, 0.2f, 1.0f),
            Metallic = 0.2f,
            Roughness = 0.2f,
            EmissiveColor = new Vector3(0.2f, 0.8f, 1.0f),
            EmissiveIntensity = 4.0f
        });

        // Slot 6: Bindless Checkerboard Textured
        uint checkerMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "Checkerboard",
            BaseColor = Vector4.One,
            Metallic = 0.1f,
            Roughness = 0.4f,
            AlbedoTextureSlot = checkerSlot
        });

        // Slot 7: Ground Slate
        uint groundMatId = materialTable.RegisterMaterial(new Material
        {
            Name = "GroundSlate",
            BaseColor = new Vector4(0.52f, 0.55f, 0.58f, 1.0f),
            Metallic = 0.05f,
            Roughness = 0.85f
        });

        materialTable.Flush(uploadManager);

        // ---------------------------------------------------------------------------------------------
        // PHASE 5: MONOLITHIC GEOMETRY POOL & MESH ALLOCATIONS
        // ---------------------------------------------------------------------------------------------
        using var geometryPool = new GeometryPool(context, allocator);

        // 1. Central Exhibition Pavilion Plinth (32m x 32m paved slate platform at Y = 0.01)
        const float PlinthExtent = 16.0f;
        const float PlinthY = 0.01f;
        Vertex3D[] groundVertices =
        [
            new(new float3(-PlinthExtent, PlinthY,  PlinthExtent), new float3(1f, 1f, 1f), new float3(0f, 1f, 0f), new float2(0f, 0f)),
            new(new float3( PlinthExtent, PlinthY,  PlinthExtent), new float3(1f, 1f, 1f), new float3(0f, 1f, 0f), new float2(8f, 0f)),
            new(new float3( PlinthExtent, PlinthY, -PlinthExtent), new float3(1f, 1f, 1f), new float3(0f, 1f, 0f), new float2(8f, 8f)),
            new(new float3(-PlinthExtent, PlinthY, -PlinthExtent), new float3(1f, 1f, 1f), new float3(0f, 1f, 0f), new float2(0f, 8f)),
        ];
        ushort[] groundIndices = [0, 1, 2, 2, 3, 0];

        // 2. Unit Cube 3D geometry (24 vertices, 36 indices)
        Vertex3D[] cubeVertices =
        [
            // Front (+Z)
            new(new float3(-1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, 1f), new float2(0f, 0f)),
            new(new float3( 1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, 1f), new float2(1f, 0f)),
            new(new float3( 1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, 1f), new float2(1f, 1f)),
            new(new float3(-1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, 1f), new float2(0f, 1f)),

            // Back (-Z)
            new(new float3( 1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, -1f), new float2(0f, 0f)),
            new(new float3(-1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, -1f), new float2(1f, 0f)),
            new(new float3(-1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, -1f), new float2(1f, 1f)),
            new(new float3( 1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 0f, -1f), new float2(0f, 1f)),

            // Top (+Y)
            new(new float3(-1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 1f, 0f), new float2(0f, 0f)),
            new(new float3( 1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 1f, 0f), new float2(1f, 0f)),
            new(new float3( 1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 1f, 0f), new float2(1f, 1f)),
            new(new float3(-1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, 1f, 0f), new float2(0f, 1f)),

            // Bottom (-Y)
            new(new float3(-1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, -1f, 0f), new float2(0f, 0f)),
            new(new float3( 1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, -1f, 0f), new float2(1f, 0f)),
            new(new float3( 1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, -1f, 0f), new float2(1f, 1f)),
            new(new float3(-1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(0f, -1f, 0f), new float2(0f, 1f)),

            // Right (+X)
            new(new float3( 1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(1f, 0f, 0f), new float2(0f, 0f)),
            new(new float3( 1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(1f, 0f, 0f), new float2(1f, 0f)),
            new(new float3( 1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(1f, 0f, 0f), new float2(1f, 1f)),
            new(new float3( 1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(1f, 0f, 0f), new float2(0f, 1f)),

            // Left (-X)
            new(new float3(-1f, -1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(-1f, 0f, 0f), new float2(0f, 0f)),
            new(new float3(-1f, -1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(-1f, 0f, 0f), new float2(1f, 0f)),
            new(new float3(-1f,  1f,  1f), new float3(1.0f, 1.0f, 1.0f), new float3(-1f, 0f, 0f), new float2(1f, 1f)),
            new(new float3(-1f,  1f, -1f), new float3(1.0f, 1.0f, 1.0f), new float3(-1f, 0f, 0f), new float2(0f, 1f)),
        ];

        ushort[] cubeIndices =
        [
            0, 1, 2,  2, 3, 0,
            4, 5, 6,  6, 7, 4,
            8, 9, 10, 10, 11, 8,
            12, 13, 14, 14, 15, 12,
            16, 17, 18, 18, 19, 16,
            20, 21, 22, 22, 23, 20,
        ];

        var groundSlice = geometryPool.AllocateMesh(uploadManager, groundVertices, groundIndices);
        var cubeSlice = geometryPool.AllocateMesh(uploadManager, cubeVertices, cubeIndices);

        using var groundMesh = new Mesh(
            geometryPool.VertexBuffer,
            geometryPool.IndexBuffer,
            groundSlice.VertexCount,
            groundSlice.IndexCount,
            groundSlice.VertexOffsetBytes,
            groundSlice.IndexOffsetBytes,
            VkIndexType.UInt16,
            boundingMin: new Vector3(-PlinthExtent, -0.05f, -PlinthExtent),
            boundingMax: new Vector3(PlinthExtent, 0.05f, PlinthExtent),
            boundingRadius: PlinthExtent * 1.4142f,
            ownsBuffers: false
        );

        using var cubeMesh = new Mesh(
            geometryPool.VertexBuffer,
            geometryPool.IndexBuffer,
            cubeSlice.VertexCount,
            cubeSlice.IndexCount,
            cubeSlice.VertexOffsetBytes,
            cubeSlice.IndexOffsetBytes,
            VkIndexType.UInt16,
            boundingMin: new Vector3(-1f, -1f, -1f),
            boundingMax: new Vector3(1f, 1f, 1f),
            boundingRadius: 1.732f,
            ownsBuffers: false
        );

        if (uploadManager.LastSubmittedValue > 0)
        {
            uploadManager.WaitForUpload(uploadManager.LastSubmittedValue);
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 6: LIGHTING, SHADOWS, DEBUG RENDERER & ORIGIN SHIFT
        // ---------------------------------------------------------------------------------------------
        var settings = Settings.Load();
        if (!ShadowQualityPreset.TryParse(settings.ShadowQuality, out var shadowQuality))
        {
            Console.WriteLine($"[Settings] Unknown shadow quality '{settings.ShadowQuality}'. Falling back to High.");
            shadowQuality = ShadowQualityPreset.High;
        }

        using var cascadedShadowMap = new CascadedShadowMap(context, allocator, shadowQuality.Resolution);

        var lightEnv = new LightEnvironment
        {
            Sun =
            {
                Direction = Vector3.Normalize(new Vector3(-0.6f, -1.0f, -0.4f)),
                Color = new Vector3(1.0f, 0.98f, 0.92f),
                Intensity = 100000.0f,
                CastsShadows = true
            },
            AmbientSkyColor = new Vector3(0.2f, 0.35f, 0.6f),
            AmbientGroundColor = new Vector3(0.1f, 0.08f, 0.05f),
            AmbientIntensity = 0.40f,
            ShadowBias = 0.0005f,
            HighQualityShadows = shadowQuality.HighQualityFiltering,
            ShadowFilterRadius = shadowQuality.FilterRadius
        };

        using var debugRenderer = new DebugRenderer(context, allocator);
        var originShift = new OriginShift { ThresholdDistance = 2000.0 };

        EventBus.Subscribe<OriginShiftedEvent>((in OriginShiftedEvent e) =>
        {
            Console.WriteLine($"[EventBus] OriginShiftedEvent -> Delta: ({e.DeltaX:F1}, {e.DeltaY:F1}, {e.DeltaZ:F1})m | Rebase successful");
        });

        // ---------------------------------------------------------------------------------------------
        // PHASE 7: 3D FORWARD RENDERER PIPELINE & ECS SCENE
        // ---------------------------------------------------------------------------------------------
#if DEBUG
        using var shaderCompiler = new ShaderCompiler("assets/shaders/cache");
#else
        using var shaderCompiler = new ShaderCompiler();
#endif
        using var forwardRenderer = new ForwardRenderer3D(
            context,
            allocator,
            bindlessTable,
            shaderCompiler,
            swapchain.ColorFormat
        );

        using var scene = new Jatayu.Scene.Scene();

        // 1. Central Exhibition Pavilion Plinth (32m x 32m paved slate platform at Y = 0.01)
        scene.CreateEntity(
            new Transform(0.0, 0.0, 0.0),
            new MeshRenderer(groundMesh, materialId: groundMatId, castShadows: false)
        );

        // 2. Hero Gold Cube floating at plinth center
        var heroCube = scene.CreateEntity(
            new Transform(0.0, 1.2, 0.0),
            new MeshRenderer(cubeMesh, materialId: goldMatId, castShadows: true)
        );

        // 3. Orbiting Copper Cube
        var copperCube = scene.CreateEntity(
            new Transform(-4.0, 1.0, 0.0, Quaternion.Identity, new Vector3(0.8f)),
            new MeshRenderer(cubeMesh, materialId: copperMatId, castShadows: true)
        );

        // 4. Smooth Chrome Cube
        var chromeCube = scene.CreateEntity(
            new Transform(4.0, 1.0, 0.0, Quaternion.Identity, new Vector3(0.8f)),
            new MeshRenderer(cubeMesh, materialId: chromeMatId, castShadows: true)
        );

        // 5. Rough Terracotta Cube
        var terracottaCube = scene.CreateEntity(
            new Transform(0.0, 1.0, -4.0, Quaternion.Identity, new Vector3(0.8f)),
            new MeshRenderer(cubeMesh, materialId: terracottaMatId, castShadows: true)
        );

        // 6. Emissive Neon Blue Crystal floating above hero cube
        var emissiveCube = scene.CreateEntity(
            new Transform(0.0, 3.2, 0.0, Quaternion.Identity, new Vector3(0.5f, 1.5f, 0.5f)),
            new MeshRenderer(cubeMesh, materialId: emissiveMatId, castShadows: true)
        );

        // 7. Textured Checkerboard Cube (via BindlessTable slot)
        var checkerCube = scene.CreateEntity(
            new Transform(0.0, 1.0, 4.0, Quaternion.Identity, new Vector3(0.8f)),
            new MeshRenderer(cubeMesh, materialId: checkerMatId, castShadows: true)
        );

        // 8. Large-World Monolith Obelisk at (50,000.0, 5.0, -50,000.0)
        var monolith = scene.CreateEntity(
            new Transform(50000.0, 5.0, -50000.0, Quaternion.Identity, new Vector3(2.0f, 10.0f, 2.0f)),
            new MeshRenderer(cubeMesh, materialId: goldMatId, castShadows: true)
        );

        // 9. Large-World Satellite 1 at (49,990.0, 2.0, -49,990.0)
        var monolithSat1 = scene.CreateEntity(
            new Transform(49990.0, 2.0, -49990.0, Quaternion.Identity, new Vector3(1.5f)),
            new MeshRenderer(cubeMesh, materialId: chromeMatId, castShadows: true)
        );

        // 10. Large-World Satellite 2 at (50,010.0, 2.0, -50,010.0)
        var monolithSat2 = scene.CreateEntity(
            new Transform(50010.0, 2.0, -50010.0, Quaternion.Identity, new Vector3(1.5f)),
            new MeshRenderer(cubeMesh, materialId: copperMatId, castShadows: true)
        );

        // ---------------------------------------------------------------------------------------------
        // PHASE 8: MULTI-FRAME IN-FLIGHT SYNCHRONIZATION & PROFILING
        // ---------------------------------------------------------------------------------------------
        using var commandPool = new CommandPoolManager(context, queueFamily);
        using var frameSync = new FrameSync(context, maxFramesInFlight: 2, swapchainImageCount: swapchain.ImageCount);
        var commandBuffers = commandPool.AllocateMultiple(frameSync.MaxFramesInFlight);
        using var gpuTimer = new GpuTimer(context, maxQueriesPerFrame: 64, framesInFlight: frameSync.MaxFramesInFlight);
        using var imguiBackend = new ImGuiVulkanBackend(context, allocator, uploadManager, window, swapchain.ColorFormat);
        var debugOverlay = new DebugOverlay();
        var streamingOverlay = new StreamingDebugOverlay();

        string worldPakPath = Path.Combine(AppContext.BaseDirectory, "assets", "packages", "world.jpak");
        if (!File.Exists(worldPakPath))
            worldPakPath = Path.Combine(AppContext.BaseDirectory, "assets", "generated", "world.jpak");
        if (!File.Exists(worldPakPath) && File.Exists("assets/packages/world.jpak"))
            worldPakPath = "assets/packages/world.jpak";
        if (!File.Exists(worldPakPath) && File.Exists("assets/generated/world.jpak"))
            worldPakPath = "assets/generated/world.jpak";

        PackReader? worldPack = File.Exists(worldPakPath) ? new PackReader(worldPakPath) : null;
        var worldConfig = new WorldConfig();
        var worldGrid = new WorldGrid(worldConfig);
        using var streamingManager = new StreamingManager(worldGrid, worldConfig, worldPack, vramBudget: vramBudget);
        Vector3 prevCamPos = camera.Position;

        // Sprint 6b: Terrain Clipmap (D22) & Texture Mip Streamer (D5, D21)
        using var terrainClipmap = new TerrainClipmap(context, allocator);
        for (int tz = -2; tz <= 1; tz++)
        {
            for (int tx = -2; tx <= 1; tx++)
            {
                terrainClipmap.RegisterTile(TerrainTile.CreateProcedural(tx, tz));
            }
        }
        terrainClipmap.HeightPyramid.Initialize((wx, wz) =>
        {
            float fx = (float)wx;
            float fz = (float)wz;
            float maxElev = 45.0f;
            float h1 = MathF.Sin(fx * 0.005f) * MathF.Cos(fz * 0.005f) * maxElev;
            float h2 = MathF.Sin(fx * 0.015f + 1.2f) * MathF.Sin(fz * 0.015f + 0.8f) * (maxElev * 0.35f);
            float h3 = MathF.Cos(fx * 0.04f - 0.5f) * MathF.Sin(fz * 0.04f + 1.7f) * (maxElev * 0.1f);

            // Flat clearing around origin (radius 200m) for testing arena and rotating cubes
            float dist = MathF.Sqrt(fx * fx + fz * fz);
            float t = Math.Clamp((dist - 200.0f) / 60.0f, 0.0f, 1.0f);
            float flatFactor = t * t * (3.0f - 2.0f * t);
            return (h1 + h2 + h3) * flatFactor;
        });

        using var textureStreamer = new TextureStreamer(context, allocator, bindlessTable, deletionQueue, vramBudget);

        // ---------------------------------------------------------------------------------------------
        // SPRINT 7: GPU-DRIVEN RENDERING, PROCEDURAL FOLIAGE & GPU PARTICLES
        // ---------------------------------------------------------------------------------------------
        const int ReferenceStaticInstanceCount = 200000;
        const int MaxGpuInstanceCount = 262144;
        using var instanceBuffer = new InstanceBuffer(context, allocator, MaxGpuInstanceCount);
        using var indirectDrawBuffer = new IndirectDrawBuffer(context, allocator, 5, MaxGpuInstanceCount);
        using var hizBuilder = new HiZBuilder(context, allocator, shaderCompiler, (uint)swapchain.Width, (uint)swapchain.Height);
        using var gpuCuller = new GpuCuller(context, allocator, shaderCompiler);

        // Procedural wind foliage grass field (Sprint 7b - 50,000 blades surrounding the plinth)
        using var foliageSystem = new FoliageSystem(context, allocator, shaderCompiler, swapchain.ColorFormat);
        var foliageList = new List<FoliageInstanceData>(50000);
        var foliageRng = new Random(1337);
        for (int i = 0; i < 50000; i++)
        {
            float fx = (float)(foliageRng.NextDouble() * 160.0 - 80.0);
            float fz = (float)(foliageRng.NextDouble() * 160.0 - 80.0);
            if (MathF.Abs(fx) < PlinthExtent + 1.0f && MathF.Abs(fz) < PlinthExtent + 1.0f) continue; // Keep central exhibition plinth clean
            float rot = (float)(foliageRng.NextDouble() * Math.PI * 2.0);
            float scale = 0.8f + (float)foliageRng.NextDouble() * 0.5f;
            byte tint = (byte)foliageRng.Next(150, 255);
            byte windPhase = (byte)foliageRng.Next(0, 256);
            float fy = MathF.Max(terrainClipmap.SampleElevation(fx, fz), 0.0f);
            foliageList.Add(new FoliageInstanceData(new Vector3(fx, fy, fz), rot, scale, tint, windPhase));
        }
        foliageSystem.SetInstances(CollectionsMarshal.AsSpan(foliageList));

        // GPU compute particle emitter fountain (Sprint 7b - erupts above the emissive crystal)
        using var particleSystem = new ParticleSystem(context, allocator, shaderCompiler, swapchain.ColorFormat, maxParticles: 16384)
        {
            EmitterPosition = new Vector3(0.0f, 4.0f, 0.0f)
        };
        using var transparentPass = new TransparentPass(context, allocator, shaderCompiler, swapchain.ColorFormat, swapchain.Width, swapchain.Height);

        // Sprint 7b: Image-Based Lighting BRDF LUT pre-integration & 3D Clustered Light Culling
        using var iblProbe = new IblProbe(context, allocator);
        commandPool.ExecuteImmediate(queue, cmd =>
        {
            iblProbe.GenerateBrdfLut(cmd.Handle, shaderCompiler);
            iblProbe.GenerateEnvironmentMaps(cmd.Handle, shaderCompiler);
        });

        using var clusteredLightCulling = new ClusteredLightCulling(context, allocator, shaderCompiler);
        var pointLights = new PointLight[224];
        var spotLights = new SpotLight[32];

        var dynInstances = new GpuInstanceData[10];
        var streamingGpuInstances = new GpuInstanceData[ReferenceStaticInstanceCount];
        bool gpuDrivenEnabled = true;
        bool foliageEnabled = true;
        bool particlesEnabled = true;
        bool referenceScaleEnabled = initialStress;

        using var renderGraph = new RenderGraph();

        var waitSemaphores = new VkSemaphore[1];
        var waitStages = new VkPipelineStageFlags[] { VkPipelineStageFlags.ColorAttachmentOutputBit };
        var submitCmds = new CommandBuffer[1];
        var signalSemaphores = new VkSemaphore[1];

        // ---------------------------------------------------------------------------------------------
        // INTERACTIVE HOTKEY EVENT HOOKS
        // ---------------------------------------------------------------------------------------------
        bool sunOrbitEnabled = true;
        bool debugWireframeEnabled = true;
        bool atLargeWorldCluster = false;
        bool flyCamEnabled = false;
        bool autopilotTourEnabled = initialAutopilot;
        double tourTime = 0.0;

        window.KeyAction += (key, action) =>
        {
            if (action == GlfwKeyAction.Press)
            {
                if (key == GlfwKey.V)
                {
                    window.VSync = !window.VSync;
                    Console.WriteLine($"[Input] V-Sync -> {(window.VSync ? "ENABLED (Locked to monitor refresh)" : "DISABLED (Uncapped FPS)")}");
                }
                else if (key == GlfwKey.Space)
                {
                    cameraController.AutoOrbitEnabled = !cameraController.AutoOrbitEnabled;
                    Console.WriteLine($"[Input] Auto-Orbit -> {(cameraController.AutoOrbitEnabled ? "ENABLED" : "PAUSED (Manual control active)")}");
                }
                else if (key == GlfwKey.P)
                {
                    lightEnv.HighQualityShadows = !lightEnv.HighQualityShadows;
                    Console.WriteLine($"[Shadows] {(lightEnv.HighQualityShadows ? "16-Tap Poisson PCF" : "3x3 PCF")} at {cascadedShadowMap.Resolution}x{cascadedShadowMap.Resolution}");
                }
                else if (key == GlfwKey.G)
                {
                    lightEnv.ShadowDebugMode = lightEnv.ShadowDebugMode switch
                    {
                        ShadowDebugMode.Off => ShadowDebugMode.Cascades,
                        ShadowDebugMode.Cascades => ShadowDebugMode.Depth,
                        ShadowDebugMode.Depth => ShadowDebugMode.Bias,
                        _ => ShadowDebugMode.Off
                    };
                    Console.WriteLine($"[Shadows] Debug view -> {lightEnv.ShadowDebugMode}");
                }
                else if (key == GlfwKey.F)
                {
                    if (camera.FovDegrees < 80f)
                    {
                        camera.FovDegrees = 110f;
                        Console.WriteLine("[Camera] FOV switched to ULTRA-WIDE (110° FOV)");
                    }
                    else
                    {
                        camera.FovDegrees = 60f;
                        Console.WriteLine("[Camera] FOV switched to STANDARD (60° FOV)");
                    }
                }
                else if (key == GlfwKey.F1)
                {
                    debugOverlay.IsVisible = !debugOverlay.IsVisible;
                    Console.WriteLine($"[Diagnostics] Debug Overlay HUD: {(debugOverlay.IsVisible ? "VISIBLE" : "HIDDEN")}");
                }
                else if (key == GlfwKey.F2)
                {
                    streamingOverlay.IsVisible = !streamingOverlay.IsVisible;
                    Console.WriteLine($"[Diagnostics] World Streaming Radar: {(streamingOverlay.IsVisible ? "VISIBLE" : "HIDDEN")}");
                }
                else if (key == GlfwKey.B)
                {
                    debugWireframeEnabled = !debugWireframeEnabled;
                    Console.WriteLine($"[Diagnostics] 3D Wireframe Debug: {(debugWireframeEnabled ? "ENABLED" : "DISABLED")}");
                }
                else if (key == GlfwKey.L)
                {
                    sunOrbitEnabled = !sunOrbitEnabled;
                    Console.WriteLine($"[Lighting] Sun Light Orbit: {(sunOrbitEnabled ? "ANIMATING" : "PAUSED")}");
                }
                else if (key == GlfwKey.T)
                {
                    atLargeWorldCluster = !atLargeWorldCluster;
                    if (atLargeWorldCluster)
                    {
                        camera.Target = new Vector3(50000.0f, 5.0f, -50000.0f);
                        cameraController.Distance = 25.0f;
                        cameraController.Pitch = 0.25f;
                        cameraController.Yaw = 0.5f;
                        Console.WriteLine("[Teleport] Teleported to 50,000m Large-World Monolith Cluster (X=50000, Y=5, Z=-50000)");
                    }
                    else
                    {
                        camera.Target = new Vector3(0.0f, 1.2f, 0.0f);
                        cameraController.Distance = 9.0f;
                        cameraController.Pitch = 0.28f;
                        cameraController.Yaw = 0.0f;
                        Console.WriteLine("[Teleport] Teleported back to Origin (X=0, Y=1.2, Z=0)");
                    }
                }
                else if (key == GlfwKey.C)
                {
                    flyCamEnabled = !flyCamEnabled;
                    if (flyCamEnabled)
                    {
                        autopilotTourEnabled = false;
                        cameraController.AutoOrbitEnabled = false;
                        Console.WriteLine("[Camera] Mode: 6-DOF FREE-FLYCAM (WASD/QE to fly, Shift for 100 m/s boost, Arrows to aim)");
                    }
                    else
                    {
                        Console.WriteLine("[Camera] Mode: ORBIT CAM (Target-centered)");
                    }
                }
                else if (key == GlfwKey.F3)
                {
                    autopilotTourEnabled = !autopilotTourEnabled;
                    if (autopilotTourEnabled)
                    {
                        flyCamEnabled = false;
                        cameraController.AutoOrbitEnabled = false;
                        tourTime = 0.0;
                        Console.WriteLine("[Autopilot] 100 m/s Supersonic Drone Tour ENGAGED (360 km/h streaming stress sweep)");
                    }
                    else
                    {
                        Console.WriteLine("[Autopilot] Supersonic Drone Tour DISENGAGED");
                    }
                }
                else if (key == GlfwKey.F4)
                {
                    forwardRenderer.DebugTerrainRingsEnabled = !forwardRenderer.DebugTerrainRingsEnabled;
                    Console.WriteLine($"[Terrain] Clipmap LOD Ring Colors: {(forwardRenderer.DebugTerrainRingsEnabled ? "ENABLED (LOD Bands: Green/Cyan/Yellow/Orange/Red/Purple)" : "DISABLED (PBR Slope Splatting)")}");
                }
                else if (key == GlfwKey.G)
                {
                    gpuDrivenEnabled = !gpuDrivenEnabled;
                    Console.WriteLine($"[Renderer] GPU-Driven Indirect Multi-Draw -> {(gpuDrivenEnabled ? "ENABLED (vkCmdDrawIndexedIndirectCount + Hi-Z Culling)" : "DISABLED (CPU Forward Submission)")}");
                }
                else if (key == GlfwKey.K)
                {
                    foliageEnabled = !foliageEnabled;
                    Console.WriteLine($"[Foliage] Procedural Wind Grass -> {(foliageEnabled ? $"ENABLED ({foliageSystem.ActiveInstanceCount} grass blades)" : "DISABLED")}");
                }
                else if (key == GlfwKey.J)
                {
                    particlesEnabled = !particlesEnabled;
                    Console.WriteLine($"[Particles] GPU Particle Fountain -> {(particlesEnabled ? $"ENABLED ({particleSystem.ActiveParticleCount} active, {particleSystem.MaxParticles} capacity)" : "DISABLED")}");
                }
                else if (key == GlfwKey.H)
                {
                    referenceScaleEnabled = !referenceScaleEnabled;
                    Console.WriteLine($"[Reference Scale] 200k GPU static instances -> {(referenceScaleEnabled ? "ENABLED (high GPU/CPU upload load)" : "DISABLED")}");
                }
                else if (key == GlfwKey.Escape)
                {
                    window.Close();
                }
            }
        };

        // ---------------------------------------------------------------------------------------------
        // PRINT DETAILED ENGINE STATUS
        // ---------------------------------------------------------------------------------------------
        Console.WriteLine("================================================================================");
        Console.WriteLine("     JATAYU VULKAN 1.3 3D ENGINE - SPRINT 7: GPU-DRIVEN & SCALABLE LIGHTING     ");
        Console.WriteLine("================================================================================");
        Console.WriteLine($" [Window]        : {window.Width}x{window.Height} (Framebuffer: {window.FramebufferWidth}x{window.FramebufferHeight} px)");
        Console.WriteLine($" [Renderer]      : ForwardRenderer3D (GPU-Driven Indirect + Shadows + Skybox + Terrain + Foliage + Particles)");
        Console.WriteLine($" [GPU-Driven]    : Indirect Multi-Draw (Toggle [G]) | vkCmdDrawIndexedIndirectCount + Hi-Z Culling");
        Console.WriteLine($" [Reference]     : 200k GPU static instance stress (Toggle [H], requires [G])");
        Console.WriteLine($" [Foliage]       : Procedural Wind Grass (Toggle [K]) | {foliageSystem.ActiveInstanceCount} instanced blades");
        Console.WriteLine($" [Particles]     : GPU Compute Simulation (Toggle [J]) | {particleSystem.ActiveParticleCount} active / {particleSystem.MaxParticles} capacity, {ParticleSystem.DefaultEmissionRate:0} particles/s");
        Console.WriteLine($" [Depth Buffer]  : {depthBuffer.Width}x{depthBuffer.Height} | Format: {depthBuffer.Format} (32-bit Float Reverse-Z)");
        Console.WriteLine($" [Shadow Map]    : 4-Cascade CSM ({cascadedShadowMap.Resolution}x{cascadedShadowMap.Resolution}x4 D32_SFLOAT, Texel Snapping, Reverse-Z)");
        Console.WriteLine($" [Clipmap]       : 7 nested LOD rings (8.4 km span), 10 modular piece meshes, 144 instances, watertight geomorphing");
        Console.WriteLine($" [Arena]         : 200m flat origin clearing for central pavilion plinth & rotating testing boxes");
        Console.WriteLine($" [Bindless]      : BindlessTable active ({bindlessTable.MaxTextures} slots) | Checkerboard Slot: #{checkerSlot}");
        Console.WriteLine($" [MaterialTable] : {materialTable.Count} PBR materials registered in GPU Storage Buffer");
        Console.WriteLine($" [GeometryPool]  : Monolithic shared pool active (Exhibition Pavilion Plinth + Unit Cube meshes)");
        Console.WriteLine($" [Large World]   : 64-bit precision WorldPosition active (Monolith cluster at 50,000m)");
        Console.WriteLine($" [Origin Shift]  : Snapped to 256m terrain tile multiple (Threshold: {originShift.ThresholdDistance:F0}m)");
        Console.WriteLine($" [Diagnostics]   : Dear ImGui HUD Active (Toggle [F1]) | World Radar (Toggle [F2])");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" INTERACTIVE HOTKEYS:");
        Console.WriteLine("   [G]           : Toggle GPU-Driven Multi-Draw vs CPU Draw (Live rendering comparison!)");
        Console.WriteLine("   [K]           : Toggle Procedural Foliage Grass Field (50,000 swaying wind blades)");
        Console.WriteLine("   [J]           : Toggle GPU Particle Fountain (16K compute physics + WBOIT transparency)");
        Console.WriteLine("   [C]           : Toggle 6-DOF Free-Flycam (WASD + Q/E to fly, Shift for 100 m/s boost, Arrows to aim)");
        Console.WriteLine("   [F3]          : Toggle 100 m/s Supersonic Autopilot Drone Tour (360 km/h open-world stress sweep)");
        Console.WriteLine("   [F4]          : Toggle Terrain Clipmap LOD Ring Colors (Green/Cyan/Yellow/Orange/Red/Purple)");
        Console.WriteLine("   [F2]          : Toggle World Streaming Radar & Clipmap Telemetry HUD");
        Console.WriteLine("   [F1]          : Toggle Diagnostics Telemetry HUD");
        Console.WriteLine("   [T]           : Teleport between Origin (0m) and 50,000m Monolith Cluster");
        Console.WriteLine("   [L]           : Toggle Sun Light Orbit animation (animates soft shadows)");
        Console.WriteLine("   [B]           : Toggle 3D Wireframe Debug bounds and coordinate axes");
        Console.WriteLine("   [Space]       : Toggle Auto-Orbit rotation On / Off");
        Console.WriteLine("   [P]           : Toggle shadow filtering (3x3 PCF vs 16-tap Poisson)");
        Console.WriteLine("   [G]           : Cycle shadow debug view (cascades, depth, bias, off)");
        Console.WriteLine("   [F]           : Toggle FOV (Standard 60° <-> Ultra-Wide 110°)");
        Console.WriteLine("   [Left/Right]  : Manual horizontal orbit (Yaw)");
        Console.WriteLine("   [Up/Down]     : Manual vertical orbit (Pitch)");
        Console.WriteLine("   [W / S]       : Zoom camera in / out");
        Console.WriteLine("   [V]           : Toggle V-Sync Live (Observe FPS change in Title Bar / HUD)");
        Console.WriteLine("   [ESC]         : Graceful Shutdown");
        Console.WriteLine("================================================================================");

        // ---------------------------------------------------------------------------------------------
        // PHASE 9: THE MASTER 3D RENDER LOOP
        // ---------------------------------------------------------------------------------------------
        var time = new GameTime();

        RgTextureHandle currentSwapchainTarget = default;
        RgTextureHandle currentDepthTarget = default;

        Func<Jatayu.AssetFormats.AssetGuid, Mesh?> cubeMeshResolver = _ => cubeMesh;

        Action<RenderPassBuilder> foliagePassSetup = setup =>
        {
            setup.SetColorAttachment(currentSwapchainTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
            setup.SetDepthAttachment(currentDepthTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
        };
        Action<RenderPassContext> foliagePassExecute = ctx =>
        {
            Matrix4x4 cameraRelView = camera.ViewMatrix;
            cameraRelView.M41 = 0;
            cameraRelView.M42 = 0;
            cameraRelView.M43 = 0;
            Matrix4x4 folVp = cameraRelView * camera.ProjectionMatrix;
            foliageSystem.Render(ctx.CommandBuffer, folVp, -camera.Position, (float)time.TotalTime);
        };

        Action<RenderPassContext> particlePassExecute = ctx =>
        {
            particleSystem.Render(ctx.CommandBuffer, camera.ViewMatrix, camera.ProjectionMatrix, swapchain.Width, swapchain.Height);
        };

        Action<RenderPassBuilder> imguiPassSetup = setup =>
        {
            setup.SetColorAttachment(currentSwapchainTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
        };
        Action<RenderPassContext> imguiPassExecute = ctx =>
        {
            imguiBackend.Render(ctx.CommandBuffer, frameSync.CurrentFrame);
        };

        Action<RenderPassBuilder> presentPassSetup = setup =>
        {
            setup.ReadTexture(
                currentSwapchainTarget,
                VkPipelineStageFlags2.ColorAttachmentOutputBit,
                VkAccessFlags2.None,
                VkImageLayout.PresentSrcKHR
            );
            setup.HasSideEffects(true);
        };
        Action<RenderPassContext> presentPassExecute = static _ => { };

        while (!window.ShouldClose)
        {
            AllocationTracker.BeginFrame();
            Profiler.BeginFrame();

            // 1. Process OS window events
            using (Profiler.Scope("PollEvents"))
            {
                window.PollEvents();
            }

            if (window.IsMinimized)
            {
                Profiler.EndFrame();
                AllocationTracker.EndFrame();
                continue;
            }

            // 2. Synchronize with GPU presentation engine before ticking clock
            using (Profiler.Scope("WaitForFence"))
            {
                frameSync.WaitForFrameFence();
            }

            int imageIndex;
            var acquireResult = swapchain.AcquireNextImage(frameSync.CurrentImageAvailable, out imageIndex);
            if (acquireResult is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR || swapchain.IsOutOfDate)
            {
                swapchain.Recreate();
                depthBuffer.Recreate(swapchain.Width, swapchain.Height);
                transparentPass.Resize(swapchain.Width, swapchain.Height);
                camera.UpdateAspectRatio(swapchain.Width, swapchain.Height);
                Profiler.EndFrame();
                AllocationTracker.EndFrame();
                continue;
            }

            frameSync.ResetFrameFence();

            // 3. Tick high-precision game clock (smooth, uniform interval aligned with display)
            using (Profiler.Scope("GameTime"))
            {
                time.Update();
            }
            var dt = time.DeltaTime;

            // Interactive camera controls (Autopilot Drone Tour, 6-DOF Flycam, or Target-Centered Orbit)
            using (Profiler.Scope("CameraUpdate"))
            {
                if (autopilotTourEnabled)
                {
                    tourTime += dt;
                    // Drone travels along wide circuit across cell and tile boundaries
                    // Speed is 100 m/s = 360 km/h
                    double s = tourTime * 100.0;
                    double droneX = Math.Sin(s * 0.001) * 1200.0;
                    double droneZ = Math.Cos(s * 0.0007) * 1200.0;

                    float groundElev = terrainClipmap.SampleElevation(droneX, droneZ);
                    float targetY = groundElev + 45.0f;
                    float droneY = camera.Position.Y + (targetY - camera.Position.Y) * Math.Min(1.0f, (float)dt * 6.0f);
                    camera.Position = new Vector3((float)droneX, droneY, (float)droneZ);

                    double aheadS = s + 50.0;
                    double nextX = Math.Sin(aheadS * 0.001) * 1200.0;
                    double nextZ = Math.Cos(aheadS * 0.0007) * 1200.0;
                    float nextElev = terrainClipmap.SampleElevation(nextX, nextZ);
                    Vector3 lookDir = Vector3.Normalize(new Vector3((float)(nextX - droneX), (nextElev + 45.0f) - droneY, (float)(nextZ - droneZ)));
                    camera.Target = camera.Position + lookDir * 100.0f;
                }
                else if (flyCamEnabled)
                {
                    float speed = (window.IsKeyDown(GlfwKey.LeftShift) || window.IsKeyDown(GlfwKey.RightShift)) ? 100.0f : 25.0f;
                    cameraController.MoveSpeed = speed;

                    if (window.IsKeyDown(GlfwKey.W)) cameraController.MoveForward((float)dt);
                    if (window.IsKeyDown(GlfwKey.S)) cameraController.MoveBackward((float)dt);
                    if (window.IsKeyDown(GlfwKey.A)) cameraController.MoveLeft((float)dt);
                    if (window.IsKeyDown(GlfwKey.D)) cameraController.MoveRight((float)dt);
                    if (window.IsKeyDown(GlfwKey.Space) || window.IsKeyDown(GlfwKey.E)) cameraController.MoveUp((float)dt);
                    if (window.IsKeyDown(GlfwKey.LeftControl) || window.IsKeyDown(GlfwKey.Q)) cameraController.MoveDown((float)dt);

                    var fwd = camera.Forward;
                    var right = camera.Right;
                    if (window.IsKeyDown(GlfwKey.Left))
                    {
                        fwd = Vector3.TransformNormal(fwd, Matrix4x4.CreateRotationY(2.0f * (float)dt));
                    }
                    if (window.IsKeyDown(GlfwKey.Right))
                    {
                        fwd = Vector3.TransformNormal(fwd, Matrix4x4.CreateRotationY(-2.0f * (float)dt));
                    }
                    if (window.IsKeyDown(GlfwKey.Up))
                    {
                        fwd = Vector3.TransformNormal(fwd, Matrix4x4.CreateFromAxisAngle(right, 1.5f * (float)dt));
                    }
                    if (window.IsKeyDown(GlfwKey.Down))
                    {
                        fwd = Vector3.TransformNormal(fwd, Matrix4x4.CreateFromAxisAngle(right, -1.5f * (float)dt));
                    }
                    camera.Target = camera.Position + Vector3.Normalize(fwd) * 10.0f;
                }
                else
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
                        cameraController.Zoom(-4.0f * dt);
                    }
                    if (window.IsKeyDown(GlfwKey.S))
                    {
                        cameraController.Zoom(4.0f * dt);
                    }

                    cameraController.Update(dt);
                }
            }

            // 3. Animate Sun Light Direction
            if (sunOrbitEnabled)
            {
                var sunAngle = (float)time.TotalTime * 0.25f;
                lightEnv.Sun.Direction = Vector3.Normalize(new Vector3(MathF.Cos(sunAngle), -0.75f, MathF.Sin(sunAngle)));
            }

            // 4. Animate Scene Entities
            ref var heroT = ref heroCube.Get<Transform>();
            heroT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)time.TotalTime * 0.8f) *
                             Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)time.TotalTime * 0.3f);

            ref var copperT = ref copperCube.Get<Transform>();
            copperT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -(float)time.TotalTime * 1.2f);

            ref var chromeT = ref chromeCube.Get<Transform>();
            chromeT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)time.TotalTime * 1.0f);

            ref var terracottaT = ref terracottaCube.Get<Transform>();
            terracottaT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)time.TotalTime * 0.9f);

            ref var emissiveT = ref emissiveCube.Get<Transform>();
            emissiveT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)time.TotalTime * 1.5f);

            ref var checkerT = ref checkerCube.Get<Transform>();
            checkerT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 1f, 0f)), (float)time.TotalTime * 0.7f);

            // Animate 50,000m Monolith Cluster
            ref var monoT = ref monolith.Get<Transform>();
            monoT.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)time.TotalTime * 0.2f);

            ref var sat1T = ref monolithSat1.Get<Transform>();
            sat1T.WorldX = 50000.0 + Math.Cos(time.TotalTime * 0.5) * 16.0;
            sat1T.WorldZ = -50000.0 + Math.Sin(time.TotalTime * 0.5) * 16.0;
            sat1T.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)time.TotalTime * 1.0f);

            ref var sat2T = ref monolithSat2.Get<Transform>();
            sat2T.WorldX = 50000.0 - Math.Cos(time.TotalTime * 0.5) * 16.0;
            sat2T.WorldZ = -50000.0 - Math.Sin(time.TotalTime * 0.5) * 16.0;
            sat2T.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)time.TotalTime * 1.0f);

            // 5. Evaluate Floating-Origin Rebase at Frame Boundary
            var camWorldPos = new WorldPosition(camera.Position.X, camera.Position.Y, camera.Position.Z);
            originShift.CheckAndShift(camWorldPos, out _);

            // 5b. Open-World Region Streaming (Sprint 6a) & Texture Mip Streaming (Sprint 6b)
            Vector3 camVelocity = (camera.Position - prevCamPos) / Math.Max(0.0001f, (float)dt);
            prevCamPos = camera.Position;
            using (Profiler.Scope("WorldStreaming"))
            {
                streamingManager.Update(dt, in camWorldPos, in camVelocity);
                textureStreamer.Update((ulong)frameSync.CurrentFrame);
            }

            // 5c. Update Dynamic Clustered Lights (Sprint 7b)
            using (Profiler.Scope("LightUpdate"))
            {
                float totalSec = (float)time.TotalTime;
            // Glowing Neon Blue Crystal Point Light (at 0, 3.2, 0)
            Vector3 bluePosRel = new Vector3(0.0f, 3.2f, 0.0f) - camera.Position;
            pointLights[0] = new PointLight(bluePosRel, radius: 12.0f, color: new Vector3(0.2f, 0.8f, 1.0f), intensity: 60000.0f);

            // Orbiting warm amber campfire Point Light around the exhibition cubes
            Vector3 firePosWorld = new Vector3(MathF.Cos(totalSec * 1.5f) * 5.0f, 1.5f, MathF.Sin(totalSec * 1.5f) * 5.0f);
            Vector3 firePosRel = firePosWorld - camera.Position;
            pointLights[1] = new PointLight(firePosRel, radius: 10.0f, color: new Vector3(1.0f, 0.6f, 0.2f), intensity: 45000.0f);
            for (int i = 2; i < pointLights.Length; i++)
            {
                float angle = totalSec * 0.12f + i * 2.39996323f;
                float radius = 30.0f + (i % 9) * 7.0f;
                float height = 1.0f + (i % 13) * 0.75f;
                var position = new Vector3(MathF.Cos(angle) * radius, height, MathF.Sin(angle) * radius);
                var color = new Vector3(
                    0.35f + 0.65f * MathF.Abs(MathF.Sin(i * 0.31f)),
                    0.35f + 0.65f * MathF.Abs(MathF.Sin(i * 0.47f + 1.0f)),
                    0.35f + 0.65f * MathF.Abs(MathF.Sin(i * 0.59f + 2.0f)));
                pointLights[i] = new PointLight(position, radius: 16.0f, color, intensity: 1800.0f);
            }

            // Spotlight pointing down from above the exhibition center
            Vector3 spotPosRel = new Vector3(0.0f, 8.0f, 0.0f) - camera.Position;
            spotLights[0] = new SpotLight(spotPosRel, range: 18.0f, direction: new Vector3(0.0f, -1.0f, 0.0f), innerAngleRad: 0.35f, outerAngleRad: 0.65f, color: new Vector3(1.0f, 0.95f, 0.8f));
            for (int i = 1; i < spotLights.Length; i++)
            {
                float angle = -totalSec * 0.08f + i * 2.39996323f;
                float radius = 20.0f + (i % 7) * 6.0f;
                var position = new Vector3(MathF.Cos(angle) * radius, 12.0f, MathF.Sin(angle) * radius);
                var color = new Vector3(
                    0.4f + 0.6f * MathF.Abs(MathF.Sin(i * 0.23f)),
                    0.4f + 0.6f * MathF.Abs(MathF.Sin(i * 0.41f + 1.0f)),
                    0.4f + 0.6f * MathF.Abs(MathF.Sin(i * 0.61f + 2.0f)));
                spotLights[i] = new SpotLight(position, range: 22.0f, direction: new Vector3(0.0f, -1.0f, 0.0f), innerAngleRad: 0.35f, outerAngleRad: 0.65f, color);
            }

                clusteredLightCulling.UpdateLights(pointLights, spotLights);
            }

            // 5d. Update GPU-Driven Instance Buffer (Sprint 7a)
            using (Profiler.Scope("InstanceBufferUpdate"))
            {
                if (gpuDrivenEnabled)
                {
                float camX = camera.Position.X;
                float camY = camera.Position.Y;
                float camZ = camera.Position.Z;

                // 0: Exhibition Pavilion Plinth
                Vector3 groundRel = new Vector3(-camX, -camY, -camZ);
                Matrix4x4 groundModel = Matrix4x4.CreateTranslation(groundRel);
                dynInstances[0] = new GpuInstanceData
                {
                    CurrentTransform = groundModel,
                    PreviousTransform = groundModel,
                    BoundingCenter = groundRel,
                    BoundingRadius = PlinthExtent * 1.4142f,
                    MeshId = groundSlice.MeshId,
                    MaterialId = groundMatId,
                    Flags = 0,
                    LodBias = 0f,
                };

                // 1: Hero Gold Cube
                Vector3 heroRel = heroT.Position - camera.Position;
                Matrix4x4 heroModel = Matrix4x4.CreateFromQuaternion(heroT.Rotation) * Matrix4x4.CreateTranslation(heroRel);
                dynInstances[1] = new GpuInstanceData
                {
                    CurrentTransform = heroModel,
                    PreviousTransform = heroModel,
                    BoundingCenter = heroRel,
                    BoundingRadius = 1.732f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = goldMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 2: Copper Cube
                Vector3 copperRel = copperT.Position - camera.Position;
                Matrix4x4 copperModel = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateFromQuaternion(copperT.Rotation) * Matrix4x4.CreateTranslation(copperRel);
                dynInstances[2] = new GpuInstanceData
                {
                    CurrentTransform = copperModel,
                    PreviousTransform = copperModel,
                    BoundingCenter = copperRel,
                    BoundingRadius = 1.732f * 0.8f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = copperMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 3: Chrome Cube
                Vector3 chromeRel = chromeT.Position - camera.Position;
                Matrix4x4 chromeModel = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateFromQuaternion(chromeT.Rotation) * Matrix4x4.CreateTranslation(chromeRel);
                dynInstances[3] = new GpuInstanceData
                {
                    CurrentTransform = chromeModel,
                    PreviousTransform = chromeModel,
                    BoundingCenter = chromeRel,
                    BoundingRadius = 1.732f * 0.8f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = chromeMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 4: Terracotta Cube
                Vector3 terracottaRel = terracottaT.Position - camera.Position;
                Matrix4x4 terracottaModel = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateFromQuaternion(terracottaT.Rotation) * Matrix4x4.CreateTranslation(terracottaRel);
                dynInstances[4] = new GpuInstanceData
                {
                    CurrentTransform = terracottaModel,
                    PreviousTransform = terracottaModel,
                    BoundingCenter = terracottaRel,
                    BoundingRadius = 1.732f * 0.8f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = terracottaMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 5: Emissive Crystal
                Vector3 emissiveRel = emissiveT.Position - camera.Position;
                Matrix4x4 emissiveModel = Matrix4x4.CreateScale(0.5f, 1.5f, 0.5f) * Matrix4x4.CreateFromQuaternion(emissiveT.Rotation) * Matrix4x4.CreateTranslation(emissiveRel);
                dynInstances[5] = new GpuInstanceData
                {
                    CurrentTransform = emissiveModel,
                    PreviousTransform = emissiveModel,
                    BoundingCenter = emissiveRel,
                    BoundingRadius = 1.732f * 1.5f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = emissiveMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 6: Checker Cube
                Vector3 checkerRel = checkerT.Position - camera.Position;
                Matrix4x4 checkerModel = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateFromQuaternion(checkerT.Rotation) * Matrix4x4.CreateTranslation(checkerRel);
                dynInstances[6] = new GpuInstanceData
                {
                    CurrentTransform = checkerModel,
                    PreviousTransform = checkerModel,
                    BoundingCenter = checkerRel,
                    BoundingRadius = 1.732f * 0.8f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = checkerMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 7: Monolith
                Vector3 monoRel = new Vector3((float)(monoT.WorldX - camX), (float)(monoT.WorldY - camY), (float)(monoT.WorldZ - camZ));
                Matrix4x4 monoModel = Matrix4x4.CreateScale(2f, 10f, 2f) * Matrix4x4.CreateFromQuaternion(monoT.Rotation) * Matrix4x4.CreateTranslation(monoRel);
                dynInstances[7] = new GpuInstanceData
                {
                    CurrentTransform = monoModel,
                    PreviousTransform = monoModel,
                    BoundingCenter = monoRel,
                    BoundingRadius = 1.732f * 10f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = goldMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 8: Satellite 1
                Vector3 sat1Rel = new Vector3((float)(sat1T.WorldX - camX), (float)(sat1T.WorldY - camY), (float)(sat1T.WorldZ - camZ));
                Matrix4x4 sat1Model = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromQuaternion(sat1T.Rotation) * Matrix4x4.CreateTranslation(sat1Rel);
                dynInstances[8] = new GpuInstanceData
                {
                    CurrentTransform = sat1Model,
                    PreviousTransform = sat1Model,
                    BoundingCenter = sat1Rel,
                    BoundingRadius = 1.732f * 1.5f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = chromeMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // 9: Satellite 2
                Vector3 sat2Rel = new Vector3((float)(sat2T.WorldX - camX), (float)(sat2T.WorldY - camY), (float)(sat2T.WorldZ - camZ));
                Matrix4x4 sat2Model = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromQuaternion(sat2T.Rotation) * Matrix4x4.CreateTranslation(sat2Rel);
                dynInstances[9] = new GpuInstanceData
                {
                    CurrentTransform = sat2Model,
                    PreviousTransform = sat2Model,
                    BoundingCenter = sat2Rel,
                    BoundingRadius = 1.732f * 1.5f,
                    MeshId = cubeSlice.MeshId,
                    MaterialId = copperMatId,
                    Flags = 1,
                    LodBias = 0f,
                };

                // Populate active streaming cell static instances into the GPU InstanceBuffer
                int staticInstIdx = 0;
                var activeCells = worldGrid.ActiveCells;
                for (int c = 0; c < activeCells.Count; c++)
                {
                    var cell = activeCells[c];
                    var instances = cell.StaticInstances;
                    for (int i = 0; i < instances.Length; i++)
                    {
                        if (staticInstIdx >= streamingGpuInstances.Length)
                            break;

                        ref readonly var inst = ref instances[i];
                        double objWorldX = cell.Origin.X + inst.PosX;
                        double objWorldY = cell.Origin.Y + inst.PosY;
                        double objWorldZ = cell.Origin.Z + inst.PosZ;

                        Vector3 relPos = new(
                            (float)(objWorldX - camX),
                            (float)(objWorldY - camY),
                            (float)(objWorldZ - camZ)
                        );

                        var rot = new Quaternion(inst.RotX, inst.RotY, inst.RotZ, inst.RotW);
                        var scale = new Vector3(inst.ScaleX, inst.ScaleY, inst.ScaleZ);
                        Matrix4x4 model = Matrix4x4.CreateScale(scale) *
                                          Matrix4x4.CreateFromQuaternion(rot) *
                                          Matrix4x4.CreateTranslation(relPos);

                        float maxScale = MathF.Max(inst.ScaleX, MathF.Max(inst.ScaleY, inst.ScaleZ));

                        streamingGpuInstances[staticInstIdx++] = new GpuInstanceData
                        {
                            CurrentTransform = model,
                            PreviousTransform = model,
                            BoundingCenter = relPos,
                            BoundingRadius = inst.BoundingRadius * maxScale,
                            MeshId = cubeSlice.MeshId,
                            MaterialId = inst.MaterialId,
                            Flags = (uint)inst.Flags,
                            LodBias = 0f,
                        };
                    }
                }

                if (referenceScaleEnabled && staticInstIdx < ReferenceStaticInstanceCount)
                {
                    Vector3 forward = camera.Forward;
                    Vector3 right = camera.Right;
                    Vector3 up = camera.UpVector;
                    float tanHalfFov = MathF.Tan(camera.FovDegrees * (MathF.PI / 360.0f));
                    float aspect = camera.AspectRatio;
                    int syntheticCount = ReferenceStaticInstanceCount - staticInstIdx;
                    for (int i = 0; staticInstIdx < ReferenceStaticInstanceCount; i++, staticInstIdx++)
                    {
                        float depth = 12.0f + (i % 512) * 5.5f;
                        float radial = 0.72f * MathF.Sqrt((float)(i / 512 + 1) / ((syntheticCount + 511) / 512));
                        float angle = i * 2.39996323f;
                        float horizontal = MathF.Cos(angle) * radial * depth * tanHalfFov * aspect;
                        float vertical = MathF.Sin(angle) * radial * depth * tanHalfFov;
                        Vector3 relPos = forward * depth + right * horizontal + up * vertical;
                        Matrix4x4 model = Matrix4x4.CreateScale(0.35f) * Matrix4x4.CreateTranslation(relPos);
                        streamingGpuInstances[staticInstIdx] = new GpuInstanceData
                        {
                            CurrentTransform = model,
                            PreviousTransform = model,
                            BoundingCenter = relPos,
                            BoundingRadius = 0.6f,
                            MeshId = cubeSlice.MeshId,
                            MaterialId = copperMatId,
                            Flags = 1u,
                            LodBias = 0f,
                        };
                    }
                }

                    instanceBuffer.SetStaticInstances(streamingGpuInstances.AsSpan(0, staticInstIdx));
                    instanceBuffer.SetDynamicInstances(dynInstances);
                }
            }

            // 6. Immediate-Mode 3D Debug Wireframes
            if (debugWireframeEnabled)
            {
                // Coordinate axes at camera target (Red = +X, Green = +Y, Blue = +Z)
                debugRenderer.DrawLine(camera.Target, camera.Target + new Vector3(3f, 0f, 0f), new Vector4(1f, 0.2f, 0.2f, 1f));
                debugRenderer.DrawLine(camera.Target, camera.Target + new Vector3(0f, 3f, 0f), new Vector4(0.2f, 1f, 0.2f, 1f));
                debugRenderer.DrawLine(camera.Target, camera.Target + new Vector3(0f, 0f, 3f), new Vector4(0.2f, 0.4f, 1f, 1f));

                if (!atLargeWorldCluster)
                {
                    // AABB wireframe bounding boxes for local objects
                    debugRenderer.DrawBox(new Vector3(-PlinthExtent, 0f, -PlinthExtent), new Vector3(PlinthExtent, 0.02f, PlinthExtent), new Vector4(0.5f, 0.5f, 0.6f, 0.4f));
                    debugRenderer.DrawBox(new Vector3(-1f, 0.2f, -1f), new Vector3(1f, 2.2f, 1f), new Vector4(1f, 1f, 0f, 0.8f));
                    debugRenderer.DrawBox(new Vector3(-4.8f, 0.2f, -0.8f), new Vector3(-3.2f, 1.8f, 0.8f), new Vector4(0.9f, 0.6f, 0.4f, 0.8f));
                    debugRenderer.DrawBox(new Vector3(3.2f, 0.2f, -0.8f), new Vector3(4.8f, 1.8f, 0.8f), new Vector4(0.8f, 0.8f, 0.9f, 0.8f));
                    debugRenderer.DrawBox(new Vector3(-0.8f, 0.2f, -4.8f), new Vector3(0.8f, 1.8f, -3.2f), new Vector4(0.85f, 0.35f, 0.2f, 0.8f));
                    debugRenderer.DrawBox(new Vector3(-0.8f, 0.2f, 3.2f), new Vector3(0.8f, 1.8f, 4.8f), new Vector4(0.2f, 0.8f, 0.2f, 0.8f));
                    debugRenderer.DrawBox(new Vector3(-0.5f, 1.7f, -0.5f), new Vector3(0.5f, 4.7f, 0.5f), new Vector4(0.2f, 0.8f, 1.0f, 0.8f));
                }
                else
                {
                    // AABB wireframe bounding box for 50,000m Monolith
                    debugRenderer.DrawBox(new Vector3(49998f, -5f, -50002f), new Vector3(50002f, 15f, -49998f), new Vector4(1f, 0.8f, 0.2f, 0.8f));
                }
            }

            // 7. Prepare ImGui HUD frame
            if (debugOverlay.IsVisible || streamingOverlay.IsVisible)
            {
                using (Profiler.Scope("ImGuiNewFrame"))
                {
                    imguiBackend.NewFrame();
                    if (debugOverlay.IsVisible)
                    {
                        debugOverlay.Draw(
                            time,
                            camera,
                            window.VSync,
                            vramBudget,
                            Profiler.LatestSnapshot,
                            gpuTimer.LatestSnapshot,
                            originShift: originShift,
                            lightEnv: lightEnv,
                            visibleEntities: forwardRenderer.VisibleItemCount,
                            totalEntities: 10,
                            shadowCasters: forwardRenderer.ShadowCasterCount,
                            terrainDrawCount: forwardRenderer.TerrainDrawCount,
                            terrainTriangleCount: forwardRenderer.TerrainTriangleCount,
                            atLargeWorld: atLargeWorldCluster,
                            debugWireframe: debugWireframeEnabled,
                            sunOrbiting: sunOrbitEnabled
                        );
                    }

                    if (streamingOverlay.IsVisible)
                    {
                        streamingOverlay.Draw(
                            streamingManager,
                            in camWorldPos,
                            in camVelocity,
                            terrainClipmap: terrainClipmap,
                            ringDebugColors: forwardRenderer.DebugTerrainRingsEnabled
                        );
                    }
                }
            }

            var cmd = commandBuffers[frameSync.CurrentFrame];
            var image = swapchain.Images[imageIndex];
            var imageView = swapchain.ImageViews[imageIndex];

            // 10. Record GPU Rendering Commands
            using (Profiler.Scope("RecordCommands"))
            {
                cmd.Reset();
                cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

                // Run GPU particle compute physics simulation (Sprint 7b)
                if (particlesEnabled)
                {
                    particleSystem.Simulate(cmd, (float)dt);
                }

                // Run 3D Clustered Light Culling compute shader (Sprint 7b)
                clusteredLightCulling.Cull(cmd.Handle, camera.ViewMatrix, camera.ProjectionMatrix, swapchain.Width, swapchain.Height, camera.NearPlane, 500.0f);

                gpuTimer.BeginFrame(cmd, frameSync.CurrentFrame);
                renderGraph.Reset();

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

                // Update 4-Cascade CSM splits and light-space matrices with texel snapping
                cascadedShadowMap.Update(
                    lightEnv.Sun.Direction,
                    camera.ViewMatrix,
                    camera.FovDegrees * (MathF.PI / 180.0f),
                    camera.AspectRatio,
                    camera.NearPlane,
                    maxShadowDistance: 500.0f
                );

                // ForwardRenderer3D schedules Shadows -> Skybox -> PBR Opaque (GPU-Driven Indirect or CPU Forward) -> Debug Wireframes passes
                forwardRenderer.Render(
                    renderGraph,
                    scene,
                    camera,
                    lightEnv,
                    cascadedShadowMap,
                    bindlessTable,
                    materialTable,
                    geometryPool,
                    instanceRing: null,
                    debugRenderer,
                    swapchainTarget,
                    depthTarget,
                    originShift,
                    worldGrid,
                    cubeMeshResolver,
                    terrainClipmap: terrainClipmap,
                    instanceBuffer: gpuDrivenEnabled ? instanceBuffer : null,
                    indirectDrawBuffer: gpuDrivenEnabled ? indirectDrawBuffer : null,
                    gpuCuller: gpuDrivenEnabled ? gpuCuller : null,
                    hizBuilder: gpuDrivenEnabled ? hizBuilder : null,
                    depthBuffer: gpuDrivenEnabled ? depthBuffer : null,
                    clusteredLights: clusteredLightCulling,
                    iblProbe: iblProbe
                );
                currentSwapchainTarget = swapchainTarget;
                currentDepthTarget = depthTarget;

                // Foliage Pass (Sprint 7b - Procedural Wind Grass Field)
                if (foliageEnabled && foliageSystem.ActiveInstanceCount > 0)
                {
                    renderGraph.AddPass("FoliagePass", foliagePassSetup, foliagePassExecute);
                }

                // GPU Particle Pass (Sprint 7b - Soft Radial Billboard Particles)
                if (particlesEnabled)
                {
                    transparentPass.AddPasses(renderGraph, currentSwapchainTarget, currentDepthTarget, particlePassExecute);
                }

                // ImGui Diagnostics HUD Pass
                if (debugOverlay.IsVisible || streamingOverlay.IsVisible)
                {
                    renderGraph.AddPass("ImGuiPass", imguiPassSetup, imguiPassExecute);
                }

                // Swapchain Presentation Transition Pass
                renderGraph.AddPass("PresentTransition", presentPassSetup, presentPassExecute);

                renderGraph.Compile();
                renderGraph.Execute(cmd, gpuTimer);

                cmd.End();
            }

            // 11. Submit recorded commands to GPU Graphics Queue
            uploadManager.FlushBatch();

            var signalSemaphore = frameSync.GetRenderFinishedSemaphore(imageIndex);
            using (Profiler.Scope("QueueSubmit"))
            {
                var waitSemaphore = frameSync.CurrentImageAvailable;
                var uploadSem = uploadManager.Timeline.Handle;
                ulong requiredUploadVal = uploadManager.LastSubmittedValue;

                if (requiredUploadVal > 0)
                {
                    // Graphics queue waits on both swapchain image available AND upload timeline completion
                    Span<VkSemaphore> pWaitSems = [waitSemaphore, uploadSem];
                    Span<VkPipelineStageFlags> pWaitStages = [
                        VkPipelineStageFlags.ColorAttachmentOutputBit,
                        VkPipelineStageFlags.AllCommandsBit
                    ];
                    nint waitVal0 = 0;
                    nint waitVal1 = (nint)requiredUploadVal;
                    Span<nint> pWaitVals = [waitVal0, waitVal1];

                    var timelineSubmitInfo = new VkTimelineSemaphoreSubmitInfo
                    {
                        WaitSemaphoreValueCount = 2,
                        WaitSemaphoreValues = (nint*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref pWaitVals[0])
                    };

                    var cmdHandle = cmd.Handle;
                    var sigSemHandle = signalSemaphore;

                    var submitInfo = new VkSubmitInfo
                    {
                        Next = &timelineSubmitInfo,
                        WaitSemaphoreCount = 2,
                        WaitSemaphores = (VkSemaphore*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref pWaitSems[0]),
                        WaitDstStageMask = (VkPipelineStageFlags*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref pWaitStages[0]),
                        CommandBufferCount = 1,
                        CommandBuffers = &cmdHandle,
                        SignalSemaphoreCount = 1,
                        SignalSemaphores = &sigSemHandle
                    };

                    var submitRes = context.Device.PInvoke.vkQueueSubmit.Invoke(queue.Handle, 1, &submitInfo, frameSync.CurrentFence);
                    if (submitRes != VkResult.Success)
                        throw new InvalidOperationException($"vkQueueSubmit failed: {submitRes}");
                }
                else
                {
                    waitSemaphores[0] = waitSemaphore;
                    submitCmds[0] = cmd;
                    signalSemaphores[0] = signalSemaphore;

                    queue.Submit(
                        waitSemaphores,
                        waitStages,
                        submitCmds,
                        signalSemaphores,
                        frameSync.CurrentFence
                    );
                }
            }

            // 12. Present rendered image to desktop window surface
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

            // 13. Retire deferred resource deletions & advance frame ring
            deletionQueue.Flush((ulong)frameSync.CurrentFrame);
            frameSync.AdvanceFrame();

            Profiler.EndFrame();
            AllocationTracker.EndFrame();
            AllocationTracker.AssertZeroAllocations();

            if (benchmarkMode && time.FrameCount % 60 == 0)
            {
                float gpuTotal = 0f;
                var gpuSnap = gpuTimer.LatestSnapshot;
                for (int gi = 0; gi < gpuSnap.Count; gi++) gpuTotal += gpuSnap[gi].DurationMs;
                float currentFps = time.Fps > 0f ? time.Fps : (time.TotalTime > 0.001 ? (float)(time.FrameCount / time.TotalTime) : 0f);
                Console.WriteLine($"[Benchmark] Frame {time.FrameCount}: {currentFps:F1} FPS ({(currentFps > 0f ? 1000.0 / currentFps : 0.0):F2} ms) | GPU: {gpuTotal:F2} ms | Static: {instanceBuffer.TotalCount} | Grass: {foliageSystem.ActiveInstanceCount} | Particles: {particleSystem.ActiveParticleCount}");
            }

            if (time.FrameCount >= (ulong)maxFrames)
            {
                break;
            }
        }

        if (benchmarkMode)
        {
            float effectiveFps = time.Fps > 0f ? time.Fps : (time.TotalTime > 0.001 ? (float)(time.FrameCount / time.TotalTime) : 0f);
            float effectiveMs = effectiveFps > 0f ? 1000.0f / effectiveFps : 0f;

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("                    JATAYU SPRINT 7 RUNTIME BENCHMARK REPORT                    ");
            Console.WriteLine("================================================================================");
            Console.WriteLine($" [Device]        : {context.DeviceName}");
            Console.WriteLine($" [Resolution]    : {swapchain.Width}x{swapchain.Height} (VSync: {(window.VSync ? "ON" : "OFF")})");
            Console.WriteLine($" [Frame Count]   : {time.FrameCount}");
            Console.WriteLine($" [Frame Rate]    : {effectiveFps:F1} FPS ({effectiveMs:F2} ms/frame)");
            Console.WriteLine($" [Static Meshes] : {instanceBuffer.TotalCount} instances (GPU-Driven: {(gpuDrivenEnabled ? "ON" : "OFF")})");
            Console.WriteLine($" [Foliage]       : {foliageSystem.ActiveInstanceCount} grass instances");
            Console.WriteLine($" [Particles]     : {particleSystem.ActiveParticleCount} active / {particleSystem.MaxParticles} max");
            Console.WriteLine($" [Lights]        : 224 point lights + 32 spotlights (3,072 clusters)");
            Console.WriteLine($" [Shadows]       : 4-Cascade CSM (2048x2048x4 D32_SFLOAT, texel snapping)");
            Console.WriteLine($" [IBL]           : Diffuse Irradiance + GGX Prefilter + 2D BRDF LUT");
            Console.WriteLine($" [WBOIT]         : R16G16B16A16_SFLOAT Accum + R8_UNORM Revealage");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine(" CPU Zone Breakdown (Profiler):");
            float cpuSum = 0f;
            foreach (var cpu in Profiler.LatestSnapshot)
            {
                cpuSum += cpu.DurationMs;
                Console.WriteLine($"   - {cpu.Name,-25} : {cpu.DurationMs,6:F2} ms");
            }
            Console.WriteLine($"   * {"TOTAL CPU MEASURED",-25} : {cpuSum,6:F2} ms");
            Console.WriteLine(" GPU Pass Breakdown (GpuTimer):");
            float gpuSum = 0f;
            foreach (var pass in gpuTimer.LatestSnapshot)
            {
                gpuSum += pass.DurationMs;
                Console.WriteLine($"   - {pass.Name,-25} : {pass.DurationMs,6:F2} ms");
            }
            Console.WriteLine($"   * {"TOTAL GPU TIME",-25} : {gpuSum,6:F2} ms");
            Console.WriteLine("================================================================================");
        }

        // ---------------------------------------------------------------------------------------------
        // PHASE 10: GRACEFUL SHUTDOWN & CLEANUP
        // ---------------------------------------------------------------------------------------------
        Console.WriteLine("[Shutdown] Waiting for GPU idle...");
        device.WaitIdle();
        worldPack?.Dispose();

        Console.WriteLine("[Shutdown] All 3D resources released cleanly. Goodbye!");
    }
}
