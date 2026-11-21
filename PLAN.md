<!-- =================================================================================================
 * FILE: PLAN.md
 * LOCATION: PLAN.md
 * 
 * WHAT THIS FILE DOES:
 *   This is the master architectural blueprint and implementation roadmap for the HelloBrutal 3D
 *   game engine built on .NET 10 and the BRUTAL framework (Ahwoo.BRUTAL.*).
 *   It details every single subsystem, directory, and source file required to evolve this project
 *   from our current working Vulkan 1.3 foundation into a feature-complete, commercial-grade 3D
 *   game engine capable of delivering high-fidelity visuals (PBR, shadows, textures, models)
 *   and 1,000+ FPS Vulkan performance.
 *
 * HOW THE ENGINE ARCHITECTURE WORKS INTERNALLY:
 *   1. Core & Platform: Windowing (GLFW), high-precision GameTime clock, and Input polling.
 *   2. Modern Vulkan 1.3 RHI: Hardware abstraction layer (VMA memory sub-allocation, multi-frame
 *      in-flight synchronization, swapchain recreation, dynamic rendering, and command recording).
 *   3. Asset Pipeline: On-the-fly ShaderC compilation, glTF 2.0 3D model parsing, and image loaders.
 *   4. 3D Graphics & Renderer: Perspective Camera3D, Depth buffering, PBR metallic-roughness
 *      lighting, Cascaded Shadow Maps, Skybox, and Draw call submission.
 *   5. Scene & Entities: Entity hierarchy, spatial Transforms, and Component-based data structures.
 *   6. Physics Engine: 3D rigid bodies, colliders, character controllers, and raycasts (Jolt).
 *   7. Audio Engine: 3D spatial sound emitters, attenuation, and streaming BGM (OpenAL / FMOD).
 *   8. Developer Tooling & UI: Embedded ImGui overlays, performance profiling, and scene inspection.
 * ================================================================================================= -->

# HelloBrutal: Complete 3D Game Engine Master Architecture Plan

```
                               ┌──────────────────────────────────────────────┐
                               │                 GAME LOGIC                   │
                               │   (Player Controller, Gameplay Systems)      │
                               └──────────────────────┬───────────────────────┘
                                                      │
                                                      ▼
 ┌───────────────────────────┐         ┌──────────────────────────────┐         ┌───────────────────────────┐
 │       PHYSICS ENGINE      │◄────────┤         SCENE GRAPH          ├────────►│        AUDIO ENGINE       │
 │   (Jolt 3D Rigid Bodies,  │         │   (Entities, Transforms,     │         │    (OpenAL / FMOD 3D      │
 │   Colliders, Raycasts)    │         │    Mesh/Light Components)    │         │     Spatial Sound)        │
 └───────────────────────────┘         └──────────────┬───────────────┘         └───────────────────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │         3D RENDERER          │
                                       │  (Forward+ PBR, Shadows,     │
                                       │   Camera3D, Depth Buffer)    │
                                       └──────────────┬───────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │      MODERN VULKAN 1.3 RHI   │
                                       │ (VulkanContext, GpuAllocator,│
                                       │  VulkanSwapchain, FrameSync) │
                                       └──────────────┬───────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │      OPERATING SYSTEM & GPU  │
                                       │  (Windows, NVIDIA/AMD/Intel) │
                                       └──────────────────────────────┘
```

---

## 1. Complete File Catalog & System Inventory

Every single file in this catalog serves a distinct, modular purpose. No monoliths, no bloated spaghetti code.

### 1.1 Core Platform & Runtime (`src/Core/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Window.cs` | **DONE** | GLFW desktop window wrapper. DPI awareness, screen centering, V-Sync toggle, minimization safety, and OS event dispatch. |
| `src/Core/Time/GameTime.cs` | **DONE** | High-precision stopwatch clock (`Stopwatch.GetTimestamp`). Clamped frame deltas, FPS tracking, and fixed-timestep accumulator for physics. |
| `src/Core/Input/KeyCodes.cs` | **PLANNED** | Strongly typed C# enum mapping GLFW key and mouse codes into clean, game-ready names (`Key.W`, `Key.Space`, `MouseButton.Left`). |
| `src/Core/Input/Input.cs` | **PLANNED** | Central input polling system. Tracks current/previous button states (`IsKeyDown`, `IsKeyPressed`, `IsKeyReleased`), mouse delta, and scroll. |
| `src/Core/Events/EventBus.cs` | **PLANNED** | High-speed, allocation-free publish/subscribe event dispatcher for engine events (`WindowResized`, `SceneLoaded`, `PhysicsCollision`). |
| `src/Core/Logging/Log.cs` | **PLANNED** | Formatted console & file logger wrapping `Ahwoo.BRUTAL.Logging` with colors and severity levels (`Info`, `Warn`, `Error`). |
| `src/Core/EngineConfig.cs` | **PLANNED** | Configuration record holding startup settings (Window resolution, Fullscreen, V-Sync, FramesInFlight, ShadowResolution). |
| `src/Core/Engine.cs` | **PLANNED** | Master engine coordinator. Initializes all subsystems, runs the game loop (Input → Physics FixedUpdate → Update → Render → Present). |

---

### 1.2 Modern Vulkan 1.3 RHI (`src/RHI/Vulkan/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/RHI/Vulkan/Context/QueueFamilies.cs` | **DONE** | Scans physical GPU hardware to discover Graphics, Presentation, Compute, and Transfer queue families. Prioritizes unified queues. |
| `src/RHI/Vulkan/Context/VulkanContext.cs` | **DONE** | Boots Vulkan 1.3 `VkInstance`, creates window surface, scores and picks the discrete GPU, enables Dynamic Rendering, builds logical `VkDevice`. |
| `src/RHI/Vulkan/Memory/GpuAllocator.cs` | **DONE** | High-performance VRAM manager wrapping AMD's Vulkan Memory Allocator (VMA). Sub-allocates GPU buffers and textures from large memory blocks. |
| `src/RHI/Vulkan/Memory/GpuBuffer.cs` | **DONE** | Object-oriented GPU memory buffer (`VkBuffer` + `VmaAllocation`). Handles direct host pointer mapping, SIMD uploads, and cache flushing. |
| `src/RHI/Vulkan/Swapchain/VulkanSwapchain.cs` | **DONE** | Presentation engine. Manages double/triple buffering, sRGB formats, image views, acquire/present, and zero-crash dynamic window resizing. |
| `src/RHI/Vulkan/Sync/FrameSync.cs` | **DONE** | Multi-frame concurrency ($N = 2$ or $3$). Ring buffer of semaphores and fences allowing CPU to record Frame 1 while GPU renders Frame 0. |
| `src/RHI/Vulkan/Textures/DepthBuffer.cs` | **DONE** | Dedicated depth-stencil image attachment (`D32_SFLOAT` or `D24_UNORM_S8_UINT`). Enables 3D objects to properly occlude geometry behind them. |
| `src/RHI/Vulkan/Textures/VulkanSampler.cs` | **PLANNED** | Configurable texture sampler wrapper (Bilinear, Trilinear, Anisotropic 16x filtering, Clamp-to-edge, Repeat wrapping). |
| `src/RHI/Vulkan/Textures/GpuTexture.cs` | **PLANNED** | 2D GPU image wrapper. Backed by VMA, creates `VkImageView`, manages mipmap generation, and handles pipeline layout transitions. |
| `src/RHI/Vulkan/Commands/CommandPoolManager.cs`| **PLANNED** | Manages command pool allocation per frame-in-flight and provides primary/secondary `VkCommandBuffer` instances. |
| `src/RHI/Vulkan/Pipelines/ShaderCompiler.cs` | **PLANNED** | Uses Google ShaderC to compile GLSL shaders (`.vert`, `.frag`, `.comp`) to binary SPIR-V with disk-based bytecode caching. |
| `src/RHI/Vulkan/Pipelines/VulkanPipeline.cs` | **PLANNED** | Encapsulates `VkPipeline` and `VkPipelineLayout`. Binds vertex layouts, dynamic rendering color/depth formats, and rasterizer presets. |
| `src/RHI/Vulkan/Pipelines/PipelineBuilder.cs` | **PLANNED** | Fluent builder pattern for constructing graphics and compute pipelines without writing 100 lines of nested Vulkan structs. |
| `src/RHI/Vulkan/Descriptors/DescriptorLayout.cs` | **PLANNED** | Helper for defining shader resource binding layouts (Uniform Buffers at binding 0, Texture Samplers at binding 1). |
| `src/RHI/Vulkan/Descriptors/DescriptorAllocator.cs`| **PLANNED** | High-efficiency pool allocator for `VkDescriptorSet` handles, automatically recycling sets across frames. |

---

### 1.3 Asset Pipeline (`src/Assets/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Assets/AssetManager.cs` | **PLANNED** | Central asset registry and cache. Prevents duplicate loading of the same texture, material, sound, or 3D model into memory. |
| `src/Assets/Loaders/TextureLoader.cs` | **PLANNED** | Reads image files (PNG, JPEG, TGA, HDR, KTX) using `Ahwoo.BRUTAL.Stb` and `BRUTAL.Ktx`, uploads to staging buffer, and creates `GpuTexture`. |
| `src/Assets/Loaders/ModelLoader.cs` | **PLANNED** | Parses 3D glTF 2.0 files (`.gltf`, `.glb`) using `Ahwoo.BRUTAL.Gltf`. Extracts meshes, submeshes, materials, vertex attributes, and hierarchies. |
| `src/Assets/Loaders/ShaderLoader.cs` | **PLANNED** | Loads shader source files from `assets/shaders/`, compiles with `ShaderCompiler`, and watches files for live runtime hot-reloading. |

---

### 1.4 3D Graphics & Rendering (`src/Renderer/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/Camera/Camera3D.cs` | **DONE** | 3D Perspective camera. Computes View and Projection matrices based on Position, Target, FOV, Aspect Ratio, and Near/Far planes. |
| `src/Renderer/Camera/CameraController.cs` | **DONE** | Smooth camera controller supporting first-person free-look (WASD + mouse look) and third-person orbit/pan controls. |
| `src/Renderer/Camera/Frustum.cs` | **PLANNED** | Extracts 6 bounding planes from the View-Projection matrix to perform high-speed CPU frustum culling on models before rendering. |
| `src/Renderer/Mesh/Vertex3D.cs` | **DONE** | Standard 3D PBR vertex struct: Position (`float3`), Normal (`float3`), Tangent (`float4`), UV (`float2`), and Color (`float4`). |
| `src/Renderer/Mesh/Mesh.cs` | **PLANNED** | GPU geometry container holding a vertex `GpuBuffer`, index `GpuBuffer`, triangle count, and bounding box for culling. |
| `src/Renderer/Mesh/Model.cs` | **PLANNED** | Multi-mesh 3D model container containing sub-meshes, local transforms, and material assignments parsed from glTF. |
| `src/Renderer/Material/Material.cs` | **PLANNED** | Physically-Based Rendering material. Binds Albedo texture, Normal map, Metallic-Roughness map, Ambient Occlusion, and base factors. |
| `src/Renderer/Lighting/DirectionalLight.cs` | **PLANNED** | Sunlight/directional light parameters (Direction, Color, Intensity, CastShadows flag). |
| `src/Renderer/Lighting/PointLight.cs` | **PLANNED** | Point light parameters (World Position, Color, Intensity, Attenuation Radius). |
| `src/Renderer/Lighting/LightEnvironment.cs` | **PLANNED** | Scene lighting container holding sun light, ambient light intensity, and active point lights submitted to shaders. |
| `src/Renderer/Shadows/ShadowMap.cs` | **PLANNED** | Depth render target and orthogonal light-projection matrix used to generate real-time directional sun shadows. |
| `src/Renderer/Skybox/Skybox.cs` | **PLANNED** | Procedural atmospheric or cubemap skybox background renderer that draws behind all 3D geometry at infinite depth. |
| `src/Renderer/ForwardRenderer3D.cs` | **PLANNED** | Master 3D renderer: runs Depth pre-pass, Shadow pass, PBR opaque geometry pass, Skybox pass, and presents via `VulkanSwapchain`. |
| `src/Renderer/Debug/DebugRenderer.cs` | **PLANNED** | Immediate-mode 3D line, ray, box, and sphere wireframe renderer for visualizing physics colliders and light bounds. |

---

### 1.5 Scene & Entity Architecture (`src/Scene/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Scene/Transform.cs` | **PLANNED** | Spatial transform component: Position (`float3`), Rotation (`Quaternion`), Scale (`float3`), and computed Local-to-World matrix. |
| `src/Scene/Entity.cs` | **PLANNED** | Lightweight game object representation holding an ID, name, parent/child relationships, and a collection of components. |
| `src/Scene/Scene.cs` | **PLANNED** | Container for all active entities, active camera, and light environment. Manages updates, spatial iteration, and lifetime. |
| `src/Scene/Components/MeshRenderer.cs` | **PLANNED** | Component that pairs a 3D `Mesh` with a `Material` to be picked up and drawn by `ForwardRenderer3D`. |
| `src/Scene/Components/LightComponent.cs` | **PLANNED** | Component attaching directional or point light properties to an entity's transform. |
| `src/Scene/Components/CameraComponent.cs` | **PLANNED** | Component attaching a `Camera3D` to an entity to serve as the active viewport. |
| `src/Scene/Components/RigidBodyComponent.cs` | **PLANNED** | Component linking an entity's `Transform` to a 3D physics body in the Jolt simulation. |

---

### 1.6 Physics Engine (`src/Physics/` via BRUTAL.Jolt)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Physics/PhysicsEngine.cs` | **PLANNED** | Master physics wrapper over `Ahwoo.BRUTAL.Jolt`. Manages gravity, broadphase layers, collision dispatch, and fixed-timestep ticking. |
| `src/Physics/PhysicsBody.cs` | **PLANNED** | 3D rigid body wrapper (Motion types: Static, Kinematic, Dynamic; Linear/Angular velocity, Mass, Restitution, Friction). |
| `src/Physics/Colliders/Collider.cs` | **PLANNED** | Abstract base class for all 3D collision shapes. |
| `src/Physics/Colliders/BoxCollider.cs` | **PLANNED** | Box collision shape defined by half-extents (`float3`). |
| `src/Physics/Colliders/SphereCollider.cs` | **PLANNED** | Sphere collision shape defined by radius (`float`). |
| `src/Physics/Colliders/CapsuleCollider.cs` | **PLANNED** | Capsule collision shape (radius + height) tailored for upright character controllers. |
| `src/Physics/Colliders/MeshCollider.cs` | **PLANNED** | Exact triangle mesh collider or convex hull collider generated directly from a 3D `Mesh`. |
| `src/Physics/CharacterController.cs` | **PLANNED** | Kinematic virtual character controller for smooth player movement, stair climbing, slope handling, and jumping. |
| `src/Physics/RaycastHit.cs` | **PLANNED** | Raycast query result struct containing Hit (bool), Position (`float3`), Normal (`float3`), Distance (`float`), and Hit Entity. |

---

### 1.7 Audio Engine (`src/Audio/` via BRUTAL.OpenAl / Fmod)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Audio/AudioEngine.cs` | **PLANNED** | Master audio system manager using `Ahwoo.BRUTAL.OpenAl`. Manages audio device, context, master gain, and listener positioning. |
| `src/Audio/SoundClip.cs` | **PLANNED** | In-memory decoded audio buffer (WAV / OGG). Shared across multiple audio sources without duplicating memory. |
| `src/Audio/AudioSource.cs` | **PLANNED** | 3D sound emitter: Play, Pause, Stop, Volume, Pitch, Looping, and 3D spatial attenuation (falloff radius). |
| `src/Audio/AudioListener.cs` | **PLANNED** | Represents the player's 3D ears in the world. Syncs position and forward/up orientation with the active `Camera3D`. |

---

### 1.8 Full Interactive 3D Editor Suite (`src/Editor/` via BRUTAL.ImGui)
The engine includes a complete, standalone **Interactive 3D Editor** (resembling modern engines like Unity, Godot, and Unreal) built on Dear ImGui with Vulkan 1.3 Dynamic Rendering.

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ [PLAY] [PAUSE] [STOP]   [Translate: W] [Rotate: E] [Scale: R]   [World/Local]  [Snap]  │  ◄── EditorToolbar
├─────────────────────┬───────────────────────────────────────────┬──────────────────────┤
│ SCENE HIERARCHY     │           INTERACTIVE 3D VIEWPORT         │      INSPECTOR       │
│                     │                                           │                      │
│ ├─ Main Camera      │           ┌───────┐                       │  Entity: "Player"    │
│ ├─ Directional Sun  │           │  3D   │  ◄── 3D Transform     │  ──────────────────  │
│ ├─ Player (Selected)│           │ Gizmo │      Gizmo (Translate)│  ▼ Transform         │
│ │  └─ Sword         │           └───────┘                       │    Position: [0,1,0] │
│ └─ Ground Mesh      │                                           │    Rotation: [0,0,0] │
│                     │  [Render-to-Texture Offscreen Viewport]   │    Scale:    [1,1,1] │
│                     │  [Drag-and-Drop Models from Browser]      │  ▼ MeshRenderer      │
│                     │  [Click to Select Objects in 3D View]     │  ▼ RigidBody (Jolt)  │
├─────────────────────┴───────────────────────────────────────────┴──────────────────────┤
│  ASSET BROWSER: assets/models/knight.glb, assets/textures/wood.png  │  CONSOLE: Logs   │
└─────────────────────────────────────────────────────────────────────┴──────────────────┘
```

| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Editor/EditorApp.cs` | **PLANNED** | Master editor application coordinator. Manages editor state (Edit vs Play mode), scene snapshots, and panel layout. |
| `src/Editor/EditorDockspace.cs` | **PLANNED** | Full-screen ImGui Dockspace container allowing panels (Viewport, Inspector, Hierarchy, Assets) to be docked, tabbed, split, or floated. |
| `src/Editor/EditorViewport.cs` | **PLANNED** | Interactive 3D scene viewport. Renders the 3D engine off-screen to an HDR/sRGB `VkImage` and displays it inside an ImGui window. Handles viewport resizing and aspect ratio matching. |
| `src/Editor/EditorCamera.cs` | **PLANNED** | Dedicated editor camera (independent of game cameras): WASD flycam (holding RMB), Shift to sprint, Alt+LMB orbit, Alt+RMB zoom, and 'F' to focus on selected entity. |
| `src/Editor/Gizmos/TransformGizmo.cs` | **PLANNED** | 3D interactive manipulation handles (Translation arrows, Rotation rings, Scale cubes) drawn directly in the 3D viewport with grid snapping support. |
| `src/Editor/Picking/ObjectPicker.cs` | **PLANNED** | 3D viewport mouse picking. Converts 2D cursor clicks into 3D world rays (or reads GPU entity ID buffer) to instantly select objects clicked in the 3D view. |
| `src/Editor/Panels/EditorToolbar.cs` | **PLANNED** | Top toolbar: Play/Pause/Stop simulation buttons, Gizmo mode switcher (Translate `W`, Rotate `E`, Scale `R`), Coordinate space toggle (World vs Local), and Snap settings. |
| `src/Editor/Panels/SceneHierarchyPanel.cs`| **PLANNED** | Visual scene graph tree view. Click to select entities, drag-and-drop to re-parent, rename, duplicate, delete, or right-click to spawn 3D Primitives (Cubes, Spheres, Lights, Cameras). |
| `src/Editor/Panels/InspectorPanel.cs` | **PLANNED** | Live component property inspector. Edit Transforms with numeric drag sliders, pick Materials/Textures, adjust Light colors/intensities, tune Jolt Physics colliders, and click "Add Component". |
| `src/Editor/Panels/AssetBrowserPanel.cs` | **PLANNED** | Visual file manager for `assets/` (models, textures, sounds, scenes). Supports thumbnail icons and **drag-and-drop**: drag a `.glb` model directly into the 3D viewport to spawn it! |
| `src/Editor/Panels/ConsolePanel.cs` | **PLANNED** | Real-time debug log console inside the editor. Colored message streams (Info, Warn, Error), stack trace view, search filter, and "Clear on Play". |
| `src/Editor/Panels/PerformancePanel.cs` | **PLANNED** | Real-time diagnostics panel: FPS graph, frame time breakdown (CPU vs GPU time), VRAM memory allocation graphs, draw call counters, and active Jolt physics bodies. |
| `src/Editor/Serialization/SceneSerializer.cs` | **PLANNED** | Scene persistence: Serializes the entire entity hierarchy and component data to human-readable `.scene` (JSON) files. Enables Save Scene (`Ctrl+S`), Load Scene (`Ctrl+O`), and New Scene. |
| `src/Editor/ImGuiVulkanBackend.cs` | **PLANNED** | Custom Vulkan 1.3 Dynamic Rendering backend for Dear ImGui. Binds ImGui fonts and pipeline directly to the presentation pass with zero legacy render passes. |

---

### 1.9 Asset Files (`assets/`)
| File Path | Status | Purpose |
| :--- | :---: | :--- |
| `assets/shaders/triangle.vert` | **DONE** | 2D test vertex shader. |
| `assets/shaders/triangle.frag` | **DONE** | 2D test fragment shader. |
| `assets/shaders/cube.vert` | **DONE** | 3D cube vertex shader with 64-byte MVP push constant. |
| `assets/shaders/cube.frag` | **DONE** | 3D cube fragment shader with directional diffuse normal shading. |
| `assets/shaders/pbr.vert` | **PLANNED** | 3D PBR vertex shader: transforms vertices with MVP matrices, outputs world normals, tangents, and shadow coordinates. |
| `assets/shaders/pbr.frag` | **PLANNED** | 3D PBR pixel shader: Cook-Torrance microfacet specular BRDF, metallic-roughness workflow, normal mapping, and directional shadow calculation. |
| `assets/shaders/shadow.vert` | **PLANNED** | Lightweight depth-only vertex shader for baking directional shadow maps from the light's point of view. |
| `assets/shaders/shadow.frag` | **PLANNED** | Empty/depth-only fragment shader for the shadow pass. |
| `assets/shaders/skybox.vert` | **PLANNED** | Fullscreen or cube vertex shader rendering the sky at depth = 1.0. |
| `assets/shaders/skybox.frag` | **PLANNED** | Procedural atmospheric gradient or cubemap skybox shader. |
| `assets/shaders/debug_line.vert`| **PLANNED** | Vertex shader for drawing 3D physics debug lines and wireframes. |
| `assets/shaders/debug_line.frag`| **PLANNED** | Unlit flat-color fragment shader for debug lines. |
| `assets/models/` | **PLANNED** | Directory holding 3D models (`.glb` / `.gltf`). |
| `assets/textures/` | **PLANNED** | Directory holding PNG, TGA, and KTX texture maps. |
| `assets/audio/` | **PLANNED** | Directory holding sound effects and music tracks (`.wav`, `.ogg`). |

---

## 2. Step-by-Step Implementation Sprints

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 1: Modern Vulkan 1.3 RHI Foundation [COMPLETED]                           │
│  - Window, GameTime, VulkanContext, GpuAllocator, VulkanSwapchain, FrameSync     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 2: 3D Geometry, Depth Buffer & Camera [COMPLETED]                         │
│  - DepthBuffer (Hardware Z-buffering occlusion in D32_SFLOAT)                    │
│  - Camera3D (Vulkan Y-inverted NDC, [0,1] Depth, View/Proj matrices)             │
│  - CameraController (Smooth auto-orbit, manual yaw/pitch, zoom, flycam)          │
│  - Vertex3D & IndexBuffer (Interleaved Position, Color, Normal, TexCoord)        │
│  - Vulkan Push Constants (64-byte MVP matrix in command stream)                  │
│  - Rotating 3D Shaded Cube with directional lighting and dynamic resizing       │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 3: Textures, Shaders & Pipeline Abstraction [NEXT]                        │
│  - GpuTexture & TextureLoader (PNG/KTX loading via Stb)                          │
│  - ShaderCompiler (ShaderC GLSL->SPIR-V disk compilation)                        │
│  - VulkanPipeline & PipelineBuilder (Dynamic Rendering with Depth)               │
│  - Textured 3D Cube with Bilinear/Anisotropic filtering                          │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 4: 3D Model Loading & Physically-Based Rendering (PBR)                    │
│  - ModelLoader via BRUTAL.Gltf (parsing Blender models)                          │
│  - PBR Shaders (pbr.vert, pbr.frag: metallic, roughness, normal maps)            │
│  - Directional Sun Light & Shadow Mapping (shadow.vert)                          │
│  - ForwardRenderer3D (Multi-pass: Shadows -> Depth -> PBR -> Skybox)             │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 5: Scene Graph, Input & Engine Runtime Loop                               │
│  - Input System (Keyboard & Mouse polling)                                       │
│  - Scene, Entity, and Component Architecture (MeshRenderer, Transform)           │
│  - Engine & EngineConfig (Consolidates Program.cs into clean Engine.Run())       │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 6: Jolt 3D Physics & 3D Spatial Audio                                     │
│  - PhysicsEngine via BRUTAL.Jolt (Rigid bodies, colliders, raycasting)           │
│  - CharacterController (Smooth WASD player movement & jumping)                   │
│  - AudioEngine via BRUTAL.OpenAl (3D spatial audio emitters & BGM)               │
│  - Playable 3D game demo (Walk around a 3D world with physics & sound)           │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 7: Full Interactive 3D Editor Suite                                       │
│  - ImGuiVulkanBackend (Vulkan 1.3 Dynamic Rendering overlay)                     │
│  - EditorDockspace & EditorViewport (Render-to-Texture 3D scene view)            │
│  - EditorCamera (Flycam, Orbit, Pan, and 'F' Focus)                              │
│  - TransformGizmo (3D Translate, Rotate, Scale handles with snapping)            │
│  - ObjectPicker (Click 3D objects in viewport to select)                         │
│  - SceneHierarchyPanel & InspectorPanel (Live component editing & creation)      │
│  - AssetBrowserPanel (Thumbnail browser & drag-and-drop model spawning)          │
│  - EditorToolbar (Play, Pause, Stop simulation state machine)                    │
│  - SceneSerializer (Save Scene Ctrl+S, Load Scene Ctrl+O to .scene files)        │
│  - ConsolePanel & PerformancePanel (Live logs, frame graphs, VRAM metrics)       │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Progress Tracking Dashboard

- **Total Planned Code Files:** 58
- **Total Completed Files:** 14
- **Current Completion:** **24.1% (Phase 2 Complete)**

| Subsystem | Completed / Planned | Status |
| :--- | :---: | :--- |
| **Core Platform** | 2 / 7 | In Progress |
| **Vulkan 1.3 RHI** | 7 / 15 | **Depth Buffer Added** |
| **Asset Pipeline** | 0 / 4 | Ready for Sprint 3 |
| **3D Renderer** | 3 / 14 | **Camera3D & Vertex3D Added** |
| **Scene & Entities** | 0 / 7 | Ready for Sprint 5 |
| **Physics (Jolt)** | 0 / 9 | Ready for Sprint 6 |
| **Audio (OpenAL)** | 0 / 4 | Ready for Sprint 6 |
| **Interactive Editor Suite** | 0 / 14 | **Full Suite in Sprint 7** |
| **Asset Shaders** | 4 / 8 | In Progress (cube.vert, cube.frag) |
