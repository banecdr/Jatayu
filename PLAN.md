<!-- =================================================================================================
 * FILE: PLAN.md
 * LOCATION: PLAN.md
 * 
 * WHAT THIS FILE DOES:
 *   This is the master architectural blueprint and implementation roadmap for the Jatayu 3D
 *   game engine built on .NET 10, C# 13, and the BRUTAL framework (Ahwoo.BRUTAL.*) on Vulkan 1.3.
 *   It details every single subsystem, directory, and source file required to evolve this project
 *   from our current working Vulkan 1.3 foundation into an open-world capable 3D game engine:
 *   large streamed worlds (16 km x 16 km+), GPU-driven rendering, bindless resources, reverse-Z,
 *   sub-millisecond CPU render submission, and a 144 FPS budget (6.94 ms) on a defined reference scene.
 *
 * HOW THE ENGINE ARCHITECTURE WORKS INTERNALLY:
 *   1. Core & Platform: Windowing (GLFW), high-precision GameTime clock, double-precision WorldPosition,
 *      origin rebasing, input actions, native memory pools, and multithreading Job System.
 *   2. Modern Vulkan 1.3 RHI: Hardware abstraction layer (VMA memory sub-allocation, multi-frame
 *      in-flight synchronization, swapchain recreation, dynamic rendering, timeline semaphores,
 *      async staging ring, deferred deletion queue, and bindless descriptor tables).
 *   3. Shared Formats & Cooker: Standalone Jatayu.AssetFormats library, tools cooker producing packed,
 *      memory-mapped archives (.jpak) with BC7/BC5 textures, resident mip tails, LOD
 *      geometry optimized via meshoptimizer, and binary cell records.
 *   4. World Grid & Region Streaming: 2D cell grid (128m cells), velocity-scaled priority streaming,
 *      hysteresis radii, asynchronous loading jobs, main-thread GPU budget allocation, and HLOD proxies.
 *   5. Data-Oriented ECS & Scene: Archetype-based ECS for dynamic entities, cell-owned static instance
 *      arrays, dynamic instance ring, camera-relative transformations, and spatial queries.
 *   6. GPU-Driven Renderer & Render Graph: Hi-Z depth pyramid occlusion culling (min reduction),
 *      compute frustum (5-plane + max draw distance) / LOD culling, indirect multidraw
 *      (vkCmdDrawIndexedIndirectCount), cascaded shadow maps, clustered lighting, transient resource
 *      aliasing, and synchronization2 barriers.
 *   7. Environment Systems: Clipmap terrain with height pyramid and splat materials, GPU foliage
 *      instancing, atmospheric sky, time-of-day simulation, planar Gerstner water, transparent passes,
 *      and GPU particles.
 *   8. Physics Engine: Jolt 3D rigid bodies, cell-streamed mesh/heightfield colliders, character controller,
 *      and deterministic origin-shift rebasing.
 *   9. Audio Engine: OpenAL 3D spatial sound emitters, cell-streamed audio sources, and origin-shift syncing.
 *  10. Diagnostics & Tooling: Embedded DebugUI (ImGui Vulkan dynamic rendering overlay in Sprint 3b),
 *      allocation tracking (zero-allocation steady state), GPU timestamp queries, streaming debug map,
 *      automated smoke tests, and an optional standalone editor suite (Sprint 11).
 * ================================================================================================= -->

# Jatayu: Open-World 3D Game Engine Master Architecture Plan

```
                               ┌──────────────────────────────────────────────┐
                               │                 GAMEPLAY & AI                │
                               │   (Player, CameraController, NavMesh Agents) │
                               └──────────────────────┬───────────────────────┘
                                                      │
                                                      ▼
 ┌───────────────────────────┐         ┌──────────────────────────────┐         ┌───────────────────────────┐
 │       PHYSICS ENGINE      │◄────────┤      DATA-ORIENTED ECS       ├────────►│        AUDIO ENGINE       │
 │   (Jolt 3D Rigid Bodies,  │         │  (Archetype Dynamic Entities,│         │   (OpenAL 3D Positional   │
 │   Cell Colliders, Raycasts│         │   Local Transforms, Systems) │         │    Spatial Sound Emitters)│
 └─────────────┬─────────────┘         └──────────────┬───────────────┘         └─────────────┬─────────────┘
               │                                      │                                       │
               │                                      ▼                                       │
               │                       ┌──────────────────────────────┐                       │
               └──────────────────────►│    WORLD GRID & STREAMING    │◄──────────────────────┘
                                       │ (2D Cells 128m, Priority Q,  │
                                       │  Static Instances, HLOD, LOD)│
                                       └──────────────┬───────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐         ┌───────────────────────────┐
                                       │   RENDER GRAPH & PIPELINE    │◄────────┤         DEBUG UI          │
                                       │ (Pass DAG, Sync2 Barriers,   │         │ (ImGui Dynamic Rendering, │
                                       │  Depth Pre-Pass, Hi-Z, Post) │         │  Live Telemetry, Overlay) │
                                       └──────────────┬───────────────┘         └───────────────────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │     GPU-DRIVEN RENDERER      │
                                       │ (Compute Culling, Indirect,  │
                                       │  Bindless, Clustered Lights) │
                                       └──────────────┬───────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │    MODERN VULKAN 1.3 RHI     │
                                       │(VulkanContext, GpuAllocator, │
                                       │ UploadManager, StagingRing,  │
                                       │ DeferredDelete, Swapchain)   │
                                       └──────────────┬───────────────┘
                                                      │
                                                      ▼
                                       ┌──────────────────────────────┐
                                       │    OPERATING SYSTEM & GPU    │
                                       │ (Windows / Linux, Vulkan 1.3)│
                                       └──────────────────────────────┘
```

---

## 1. Engine Conventions & Architecture Disciplines

### 1.1 Reverse-Z Depth & Infinite Far Plane
* **Depth buffer format:** `D32_SFLOAT`.
* **Clear value:** `0.0f` (near plane maps to `1.0f`, far plane maps to `0.0f`).
* **Depth comparison:** `VK_COMPARE_OP_GREATER_OR_EQUAL`.
* **Projection matrix:** Infinite far-plane perspective projection with inverted Y for Vulkan NDC:
  $$P_{00} = \frac{1}{\text{aspect} \cdot \tan(\text{fov} / 2)}, \quad P_{11} = -\frac{1}{\tan(\text{fov} / 2)}, \quad P_{22} = 0.0, \quad P_{23} = z_{\text{near}}, \quad P_{32} = -1.0$$
* Eliminates z-fighting across massive view distances (out to 50 km+).
* **Hi-Z Occlusion Convention:** Under reverse-Z, the farthest depth in any screen region is the **minimum** depth value. Hi-Z depth pyramids therefore use a **MIN** reduction filter, and sampling uses a min-reduction sampler (`VK_EXT_sampler_filter_minmax`, core in Vulkan 1.2).

### 1.2 Large-World Precision & Camera-Relative Rendering
* **Storage:** Absolute world positions are stored in double precision (`WorldPosition` with `double X, Y, Z`).
* **Camera-Relative CPU Transform:** Before uploading transforms to the GPU, object world positions are subtracted by the camera's double-precision position ($P_{\text{rel}} = P_{\text{obj}} - P_{\text{cam}}$) and cast to standard 32-bit `float3`.
* **Shaders:** Shaders never receive absolute world coordinates; view matrices have $(0, 0, 0)$ translation.
* **Culling Volume:** Frustum culling tests 5 planes (Left, Right, Bottom, Top, Near); far culling is governed by a separate configurable `MaxDrawDistance` (default: 8,000 m).
* **Panini Projection Scope:** Panini cylindrical projection is strictly a **demo-only option** for the rotating cube sample (controlled via an off-by-default push-constant flag). It is excluded from the production GPU-driven rendering pipeline to avoid invalidating frustum culling, Hi-Z occlusion, and shadow projection.

### 1.3 Floating-Origin Rebasing Procedure
When the camera travels beyond 2,000 m from the current physics/simulation origin, `OriginShift` coordinates a deterministic world rebase:
1. **Trigger Window:** Executes at a **frame boundary**, strictly after the physics step and before any queries, raycasts, or rendering commands for the next frame are recorded.
2. **Offset Snapping:** The shift offset is computed in double precision and snapped to a multiple of **256 m** (the terrain tile dimension covering $2\times 2$ cells), ensuring terrain tile data and cell-local coordinates remain invariant.
3. **Subsystem Batch Updates:**
   - **Static Instance Survival (World Streaming):** Static instances store transforms local to their streaming cell (`float3` position relative to cell origin). Each loaded cell maintains a single double-precision world origin (`WorldPosition`). When an origin shift occurs, only the per-cell origin offsets are translated; individual static instance transform buffers in VRAM remain completely untouched, avoiding re-uploading millions of instance matrices on every 256 m snap.
   - **Jolt physics:** Active, sleeping, kinematic, character bodies, and Ring A static cell collider bodies are translated by the negative offset using batched updates (consistent with D16). Because `Ahwoo.BRUTAL.Jolt` is built in Single Precision (verified in D9 probe), simulation coordinates stay bounded within $[-2000\text{ m}, +2000\text{ m}]$, eliminating single-precision floating point degradation. If the physics origin-shift spike exceeds 3.0 ms during Sprint 8 testing, fallback to rebuilding Ring A static colliders from cell cooked data in the new origin; raycast caches are updated.
   - **Spatial audio:** AudioListener position and all active AudioSource emitter coordinates are translated.
   - **ECS & Scene:** Root dynamic entity transforms and active Camera `WorldPosition` are updated.
4. **Event Dispatch:** An `OriginShifted` event is broadcast through `EventBus` so any custom gameplay listeners can synchronize.
5. **Verification Rule:** Physics stacks must remain at rest across shifts with zero velocity spikes or millimeter-level drift.

### 1.4 Performance Discipline & Zero-Allocation Rule
* **Zero GC Heap Allocation in Hot Paths:** Per-frame render loops, culling, job submission, and physics ticking must produce **0 bytes of managed heap allocations** in steady state (`GC.GetAllocatedBytesForCurrentThread() == 0`).
* **Memory Primitives:** Structs, `Span<T>`, `ReadOnlySpan<T>`, `ArrayPool<T>`, `NativeMemory`, pooled buffers (`NativePool`), frame arenas (`NativeArena`), and pre-allocated arrays.
* **Disallowed in Hot Paths:** LINQ, closures, boxing, and per-frame string interpolations.
* **Target Frame Budget (6.94 ms for 144 FPS on Reference Scene defined in Section 5):**

#### CPU Frame Budget (Target: < 5.50 ms)
| Subsystem | CPU Time Budget | Details |
| :--- | :--- | :--- |
| **Input & OS Events** | 0.20 ms | Window dispatch, input polling & action dispatch |
| **Job Scheduling / Dispatch** | 0.30 ms | Worker thread coordination & task stealing |
| **Gameplay & ECS Systems** | 1.00 ms | Archetype queries, transform propagation |
| **Physics Simulation (Jolt)** | 1.00 ms | Fixed timestep step, character controller ticking |
| **Streaming & Texture Mips** | 1.20 ms | Cell registration (1.0 ms) + texture streaming requests (0.2 ms) |
| **Frustum & Coarse Cull** | 0.40 ms | CPU cell-level 5-plane frustum rejection |
| **CPU Render Submission** | 1.00 ms | Command recording, barrier emission, terrain/foliage updates |
| **Debug Overlay (DebugUI)** | 0.10 ms | Diagnostics HUD, metrics graphs |
| **Total CPU Budget** | **5.20 ms** | **Comfortably under 5.50 ms (192 FPS)** |

#### GPU Frame Budget (Target: 5.90 ms, leaving ~15% headroom for 144 FPS)
| GPU Pass | GPU Time Budget | Details |
| :--- | :--- | :--- |
| **Depth Pre-Pass & Hi-Z** | 0.50 ms | Reverse-Z pre-pass + min-reduction depth mip pyramid |
| **GPU Compute Culling** | 0.30 ms | 5-plane frustum + Hi-Z occlusion + indirect commands |
| **Shadow Cascades (CSM)** | 0.80 ms | 4 cascades with stable texel snapping & alpha-test |
| **Clustered Light Culling** | 0.30 ms | Compute light assignment to 3D cluster grid |
| **Opaque PBR + Terrain** | 2.00 ms | GPU-driven indirect multidraw & splat materials |
| **Foliage** | 0.50 ms | GPU-instanced wind vegetation with LOD cross-fade |
| **Water & Transparent** | 0.40 ms | Displacement waves, Fresnel, sorted transparency, particles |
| **Sky & Atmosphere** | 0.30 ms | Analytical scattering, aerial perspective fog |
| **Post-Processing Chain** | 0.70 ms | HDR tonemap (ACES), Bloom, TAA, SSAO |
| **Debug Overlay (DebugUI)** | 0.10 ms | Real-time GPU metrics and streaming visualizer |
| **Total GPU Budget** | **5.90 ms** | **170 FPS (15% headroom under 6.94 ms target)** |

*Note: 1,000+ FPS claims apply only to trivial scenes (e.g. rotating cube). All open-world engine targets are measured against the Reference Scene defined in Section 5.*

### 1.5 Memory & GPU Resource Lifecycle
* **Cooked Runtime Only:** At runtime, data is loaded exclusively from cooked `.jpak` memory-mapped archives. World cells are packed records inside `.jpak` (indexed by 64-bit GUID), never individual loose files.
* **Texture Mip Streaming:** Small mip tails ($\le 64\times 64$) are permanently resident. High-resolution mips stream dynamically on demand via `TextureStreamer` and `UploadManager`, evicting under `VramBudget` pressure.
* **Deferred Deletion Queue:** GPU resources (buffers, images, views, descriptor sets) marked for destruction are held in `DeferredDeletionQueue` for $N = \text{FramesInFlight}$ frames until GPU execution fences retire.
* **Async Transfer Queue:** Dedicated Vulkan transfer queue with a 64–256 MB persistently mapped `StagingRing` buffer and `TimelineSemaphore` completion tracking.

---

## 2. Complete File Catalog & System Inventory

### 2.1 Core Platform, Memory & Configuration (`src/Core/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Window.cs` | **DONE** | GLFW desktop window wrapper. DPI awareness, screen centering, V-Sync toggle, minimization safety, and OS event dispatch. |
| `src/Core/Time/GameTime.cs` | **DONE** | High-precision stopwatch clock (`Stopwatch.GetTimestamp`). Clamped frame deltas, FPS tracking, and fixed-timestep accumulator for physics. |
| `src/Core/Math/WorldPosition.cs` | **DONE** | Double-precision 3D vector (`double X, Y, Z`), conversion to camera-relative `float3`, distance methods, SIMD alignment. (Sprint 5b). |
| `src/Core/Math/OriginShift.cs` | **DONE** | Floating-origin coordinator. Snaps shift offset to 256m terrain tile multiples and broadcasts rebase events at frame boundaries. (Sprint 5b). |
| `src/Core/Memory/NativeArena.cs` | **DONE** | High-speed linear frame allocator allocating native memory with zero GC impact; reset once per frame. (Sprint 3b). |
| `src/Core/Memory/NativePool.cs` | **DONE** | Pooled unmanaged buffer blocks recycling memory across streaming decodes and render passes. (Sprint 3b). |
| `src/Core/Input/KeyCodes.cs` | **DONE** | Strongly typed C# enum mapping GLFW key and mouse codes into clean, game-ready names (`Key.W`, `Key.Space`, `MouseButton.Left`). (Sprint 5a). |
| `src/Core/Input/Input.cs` | **DONE** | Central input polling system. Tracks current/previous button states, mouse delta, and scroll wheel. (Sprint 5a). |
| `src/Core/Input/InputActions.cs` | **DONE** | Action/axis mapping abstraction with rebinding support ("MoveForward", "Jump", "LookX"). (Sprint 5a). |
| `src/Core/Input/Gamepad.cs` | **DONE** | Gamepad/controller state reader (sticks, triggers, buttons) with deadzone normalization. (Sprint 5a). |
| `src/Core/Events/EventBus.cs` | **DONE** | High-speed, allocation-free publish/subscribe event dispatcher for engine events (`WindowResized`, `OriginShifted`, `CellLoaded`). (Sprint 5a). |
| `src/Core/Logging/Log.cs` | **DONE** | Formatted console & file logger wrapping `Ahwoo.BRUTAL.Logging` with colors and severity levels (`Info`, `Warn`, `Error`). (Sprint 5a). |
| `src/Core/EngineConfig.cs` | **DONE** | Engine startup configuration record (Resolution, VSync, VRAM budget, CellSize, StreamingRadii, StagingRingMB, Cascades). (Sprint 5a). |
| `src/Core/Config/Settings.cs` | **DONE** | User-facing graphics settings (shadow quality, draw distance, texture budget) stored in JSON. (Sprint 5a). |
| `src/Core/Engine.cs` | **DONE** | Master engine coordinator. Initializes all subsystems, coordinates job threads, and runs the tick loop. (Sprint 5a). |
| `src/Core/Save/SaveSystem.cs` | **PLANNED** | Serializes dynamic world state (player state, modified cell entities) separately from static cooked assets. (Sprint 10). |

### 2.2 Multithreading & Job System (`src/Core/Jobs/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Jobs/JobSystem.cs` | **DONE** | Work-stealing thread pool sized to physical cores minus 1-2. Supports priorities (`High`, `Normal`, `Background`) and dependencies. (Sprint 4). |
| `src/Core/Jobs/Job.cs` | **DONE** | Pooled unmanaged/struct job definition representing an executable delegate or function pointer. Zero allocation per task. (Sprint 4). |
| `src/Core/Jobs/JobHandle.cs` | **DONE** | Lightweight completion and dependency token for jobs with `.Wait()`, `.IsCompleted`, and `.Combine()`. (Sprint 4). |
| `src/Core/Jobs/ParallelFor.cs` | **DONE** | Batched parallel-for range partitioner for parallel execution of culling, animation, and data processing. (Sprint 4). |
| `src/Core/Jobs/MainThreadDispatcher.cs` | **DONE** | Lock-free queue for job results requiring main-thread execution (GPU resource finalization, scene mutation) with ms time budgeting. (Sprint 4). |

### 2.3 Diagnostics, Profiling & GPU Metrics (`src/Core/Profiling/` & `src/RHI/Vulkan/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Profiling/Profiler.cs` | **DONE** | Lightweight CPU zone profiler with microsecond timing, frame markers, and zero-allocation scopes. (Sprint 3b). |
| `src/Core/Profiling/AllocationTracker.cs` | **DONE** | Per-frame heap allocation monitor (`GC.GetAllocatedBytesForCurrentThread()`) verifying zero GC allocations in hot paths. (Sprint 3b). |
| `src/RHI/Vulkan/Profiling/GpuTimer.cs` | **DONE** | Hardware timestamp query pool wrapper. Measures per-pass GPU execution times with N-frame latency readback. (Sprint 3b). |
| `src/RHI/Vulkan/Debug/DebugNames.cs` | **DONE** | Wraps `VK_EXT_debug_utils` to assign names and colors to Vulkan buffers, images, command labels, and queue regions. (Sprint 3b). |

### 2.4 Modern Vulkan 1.3 RHI (`src/RHI/Vulkan/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/RHI/Vulkan/Context/QueueFamilies.cs` | **DONE** | Hardware queue scanner. Identifies Graphics, Presentation, Compute, and dedicated Transfer queue families. |
| `src/RHI/Vulkan/Context/VulkanContext.cs` | **DONE** | Boots Vulkan 1.3 `VkInstance`, creates window surface, scores discrete GPU, enables Dynamic Rendering, builds logical `VkDevice`. |
| `src/RHI/Vulkan/Memory/GpuAllocator.cs` | **DONE** | High-performance VRAM manager wrapping AMD's Vulkan Memory Allocator (VMA). Sub-allocates GPU buffers and textures. |
| `src/RHI/Vulkan/Memory/GpuBuffer.cs` | **DONE** | Object-oriented GPU memory buffer (`VkBuffer` + `VmaAllocation`). Handles direct host pointer mapping and SIMD uploads. |
| `src/RHI/Vulkan/Swapchain/VulkanSwapchain.cs` | **DONE** | Presentation engine. Manages double/triple buffering, sRGB formats, image views, acquire/present, and dynamic resizing. |
| `src/RHI/Vulkan/Sync/FrameSync.cs` | **DONE** | Multi-frame concurrency ($N = 2$ or $3$). Ring buffer of semaphores and fences allowing CPU/GPU overlap. |
| `src/RHI/Vulkan/Textures/DepthBuffer.cs` | **DONE** | Hardware depth attachment image (`D32_SFLOAT`). Reverse-Z conversion complete (`DepthConvention.ClearDepth = 0.0f`). |
| `src/RHI/Vulkan/Textures/VulkanSampler.cs` | **DONE** | Sampler wrapper (Linear, Trilinear, Anisotropic 16x, Clamp, Repeat, and Min-Reduction mode for Hi-Z via `VK_EXT_sampler_filter_minmax`). (Sprint 3a). |
| `src/RHI/Vulkan/Textures/GpuTexture.cs` | **DONE** | 2D/Array GPU texture backed by VMA with mipmaps, format layouts, and image view management. (Sprint 3a). |
| `src/RHI/Vulkan/Commands/CommandPoolManager.cs`| **DONE** | Command pool manager allocating primary and secondary command buffers per frame and per thread. (Sprint 3a). |
| `src/RHI/Vulkan/Pipelines/ShaderCompiler.cs` | **DONE** | Google ShaderC wrapper compiling GLSL to SPIR-V bytecode with disk-based bytecode caching. (Sprint 3a). |
| `src/RHI/Vulkan/Pipelines/VulkanPipeline.cs` | **DONE** | Encapsulates `VkPipeline` and `VkPipelineLayout` with dynamic rendering depth/color attachment states. (Sprint 3a). |
| `src/RHI/Vulkan/Pipelines/PipelineBuilder.cs` | **DONE** | Fluent builder for graphics, compute, and mesh-shader pipelines without boilerplate structs. (Sprint 3a). |
| `src/RHI/Vulkan/Pipelines/PipelineCache.cs` | **PLANNED** | Manages persistent `VkPipelineCache` loading and saving to disk to eliminate runtime shader compilation hitches. (Sprint 10). |
| `src/RHI/Vulkan/Descriptors/DescriptorLayout.cs` | **DONE** | Descriptor set layout wrapper for non-bindless passes (ImGui, compute dispatch tables). (Sprint 3a). |
| `src/RHI/Vulkan/Descriptors/DescriptorAllocator.cs`| **DONE** | Pool allocator for `VkDescriptorSet` recycling sets across frames. (Sprint 3a). |
| `src/RHI/Vulkan/Descriptors/BindlessTable.cs` | **DONE** | Global bindless descriptor array (`VK_EXT_descriptor_indexing`) binding thousands of textures to uniform shader arrays. (Sprint 5b). |
| `src/RHI/Vulkan/Upload/UploadManager.cs` | **DONE** | Asynchronous GPU transfer coordinator using dedicated transfer queue, staging ring, and ownership transfers. (Sprint 3b). |
| `src/RHI/Vulkan/Upload/StagingRing.cs` | **DONE** | Persistently mapped 64-256 MB circular staging buffer with head/tail tracking for zero-copy CPU-to-GPU uploads. (Sprint 3b). |
| `src/RHI/Vulkan/Upload/UploadRequest.cs` | **DONE** | Struct describing buffer/image copy request, mip ranges, and target subresources. (Sprint 3b). |
| `src/RHI/Vulkan/Sync/TimelineSemaphore.cs` | **DONE** | Vulkan 1.3 timeline semaphore wrapper for fine-grained async transfer queue synchronization. (Sprint 3b). |
| `src/RHI/Vulkan/Memory/DeferredDeletionQueue.cs` | **DONE** | Retires GPU objects safely after $N$ in-flight frames have completed on the GPU timeline. (Sprint 3b). |
| `src/RHI/Vulkan/Memory/VramBudget.cs` | **DONE** | Queries `VK_EXT_memory_budget` to track physical VRAM usage, budget limits, and streaming pressure. (Sprint 3b). |

### 2.5 Shared Asset Formats & Runtime Assets (`src/Jatayu.AssetFormats/` & `src/Assets/`)
*Note: `src/Jatayu.AssetFormats/` is a lightweight shared class library without GPU/Vulkan dependencies, shared between runtime engine, cooker, and world generator.*
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Jatayu.AssetFormats/AssetGuid.cs` | **DONE** | 64-bit fast hash/GUID identifying assets uniquely across packages and runtime scenes. (Sprint 4). |
| `src/Jatayu.AssetFormats/CookedFormats.cs` | **DONE** | Shared binary structs: CookedMeshHeader, CookedTextureHeader, CookedMaterialRecord. (Sprint 4). |
| `src/Jatayu.AssetFormats/World/CellCoord.cs` | **DONE** | Integer coordinate pair `(int X, int Z)` with fast hash, distance metrics, neighbor iterators, and GUID generation. (Sprint 6a). |
| `src/Jatayu.AssetFormats/World/CellFormat.cs` | **DONE** | Canonical binary structure specification for packed cell records inside `.jpak`. (Sprint 6a). |
| `src/Jatayu.AssetFormats/World/WorldConstants.cs`| **DONE** | Shared constants (CellSize: 128m, TileSize: 256m, MaxDrawDistance: 8000m) and GUID derivation from coordinates. (Sprint 6a). |
| `src/Jatayu.AssetFormats/Loaders/TextureLoader.cs` | **DONE** | Image loader using `BRUTAL.Stb` and `BRUTAL.Ktx` for cooker tools and dev hot-reloading. (Sprint 3a). |
| `src/Jatayu.AssetFormats/Loaders/ModelLoader.cs` | **DONE** | glTF 2.0 importer using `BRUTAL.Gltf` parsing meshes, hierarchies, and materials for cooking. (Sprint 4). |
| `src/Assets/PackReader.cs` | **DONE** | High-speed memory-mapped reader for `.jpak` binary asset packages with table-of-contents seek. (Sprint 4). |
| `src/Assets/AssetManager.cs` | **DONE** | Async asset registry: resolves GUIDs to loaded assets, reference-counting, and evicting unused memory. (Sprint 4). |
| `src/Assets/Textures/TextureStreamer.cs` | **DONE** | Mip streaming coordinator: determines required mip levels based on distance/texel density and issues upload/eviction requests. (Sprint 6b). |
| `src/Assets/Textures/MipResidency.cs` | **DONE** | Tracks loaded mip ranges per texture and manages step-of-2 reallocations and bindless swaps. (Sprint 6b). |
| `src/Assets/Loaders/ShaderLoader.cs` | **DONE** | Runtime shader file watcher for live editing and hot-reloading during development. (Sprint 3a). |

### 2.6 Offline Asset Cooker & World Generator (`tools/Jatayu.Cooker/` & `tools/Jatayu.WorldGen/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `tools/Jatayu.Cooker/Program.cs` | **DONE** | CLI asset cooker entry point (commands: cook-all, cook-models, cook-textures, cook-world). (Sprint 4). |
| `tools/Jatayu.Cooker/MeshCooker.cs` | **DONE** | Optimizes vertices with `Ahwoo.BRUTAL.MeshOptimizer` (cache, overdraw, fetch), builds LOD chain, quantizes. (Sprint 4). |
| `tools/Jatayu.Cooker/TextureCooker.cs` | **DONE** | Compresses textures to BC7/BC5/BC4 with full mipmaps; splits resident mip tail ($\le 64\times 64$) from streamable mips. (Sprint 4). |
| `tools/Jatayu.Cooker/MaterialCooker.cs` | **DONE** | Packages PBR material parameters and texture GUIDs into binary descriptor blobs. (Sprint 4). |
| `tools/Jatayu.Cooker/PackWriter.cs` | **DONE** | Packs cooked binary assets and cells into contiguous, memory-mapped `.jpak` archives with TOC headers. (Sprint 4). |
| `tools/Jatayu.Cooker/CellCooker.cs` | **DONE** | Bakes world cells into binary chunk records inside `.jpak`: static instance arrays, physics colliders, audio points. (Sprint 6a). |
| `tools/Jatayu.Cooker/HeightfieldCooker.cs` | **DONE** | Processes 16-bit heightmaps into tiled terrain meshes, clipmap pyramids, and Jolt heightfield colliders. (Sprint 6b). |
| `tools/Jatayu.Cooker/HlodCooker.cs` | **DONE** | Generates merged proxy meshes for clusters of world cells for Ring B mid-range rendering. (Sprint 6b). |
| `tools/Jatayu.Cooker/ImpostorCooker.cs` | **DONE** | Generates 8-angle billboard impostor texture atlases for far-distance vegetation and props. (Sprint 6b). |
| `tools/Jatayu.Cooker/FoliageCooker.cs` | **DONE** | Evaluates procedural jittered distribution and terrain height/slope to bake 16-byte packed foliage instance buffers. (Sprint 7b). |
| `tools/Jatayu.Cooker/NavMeshCooker.cs` | **PLANNED** | Bakes per-cell Recast navigation mesh tiles from static geometry colliders. (Sprint 10). |
| `tools/Jatayu.Cooker/ShaderCooker.cs` | **PLANNED** | Precompiles all runtime GLSL shaders into SPIR-V bytecode blobs and initializes pipeline cache files. (Sprint 10). |
| `tools/Jatayu.WorldGen/Program.cs` | **DONE** | Procedural world generator: bakes Reference Scene terrain heightmaps, scatter tables, foliage maps, and procedural placeholder glTF models (rocks, buildings, trees, grass cards) into assets/generated/. (Sprint 6a). |

### 2.7 World Grid & Region Streaming (`src/World/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/World/WorldConfig.cs` | **DONE** | World configuration (CellSize: 128m, LoadRadius: 600m, UnloadRadius: 750m, MaxDrawDistance: 8000m, LeadSeconds: 6s, 900m prefetch cap). (Sprint 6a). |
| `src/World/WorldCell.cs` | **DONE** | Cell state machine (`Unloaded` → `LoadingIO` → `LoadingDecode` → `ReadyToFinalize` → `Active` → `Unloading`). Owns static instance data. (Sprint 6a). |
| `src/World/WorldGrid.cs` | **DONE** | Sparse 2D hash grid storing active and dormant cells. Fast spatial lookup by `WorldPosition`. (Sprint 6a). |
| `src/World/StreamingManager.cs` | **DONE** | Central streaming controller: evaluates player position/velocity, prioritizes loading queue, enforces time budget and VRAM limits. (Sprint 6a). |
| `src/World/CellLoader.cs` | **DONE** | Job system worker reading cooked cell records from `.jpak`, parsing instances, and preparing GPU upload payloads. (Sprint 6a). |
| `src/World/Hlod.cs` | **DONE** | Hierarchical LOD manager: swaps distant cell clusters with simplified proxy meshes and impostors. (Sprint 6b). |
| `src/World/StreamingDebugOverlay.cs` | **DONE** | Visual debug grid displaying cell states (Green=Active, Yellow=Loading, Red=Unloading), memory, and queue depths. (Sprint 6a). |

#### Distance Rings Configuration Table (`WorldConfig`)
| Ring | Distance Range | Content & Subsystems Active | Foliage / Impostors | Physics Colliders | Hysteresis Unload Band |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Ring A (Near)** | $0\,\text{m} \le d \le 600\,\text{m}$ | Full-detail static meshes, dynamic ECS entities, audio emitters | Dense 3D foliage (to ~300m) | **Yes** (Jolt bodies active) | $150\,\text{m}$ ($750\,\text{m}$ unload) |
| **Ring B (Mid)** | $600\,\text{m} < d \le 2,000\,\text{m}$ | HLOD proxy meshes, simplified materials, audio emitters | Tree Impostor Atlases | **No** (Colliders unstreamed) | $10\%$ ($2,200\,\text{m}$ unload) |
| **Ring C (Far)** | $2,000\,\text{m} < d \le 8,000\,\text{m}$ | Terrain height pyramid clipmap, sky/atmosphere, water | None | **No** (Visual only) | $10\%$ ($8,800\,\text{m}$ unload) |

### 2.8 Terrain System (`src/Terrain/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Terrain/TerrainTile.cs` | **DONE** | Represents a 256m terrain chunk (covering 2x2 cells, keyed by `TileCoord = CellCoord / 2`): height data, normal maps, splat weights, Jolt shape handle. (Sprint 6b). |
| `src/Terrain/TerrainClipmap.cs` | **DONE** | Single canonical GPU clipmap LOD renderer for all rings with continuous geomorphing and 16m tile-edge blending. (Sprint 6b). |
| `src/Terrain/TerrainMaterial.cs` | **DONE** | 4-8 layer splat-map material shader parameters with triplanar blending for steep cliff faces. (Sprint 6b). |
| `src/Terrain/TerrainHeightPyramid.cs` | **DONE** | Streamed height pyramid backing far-field clipmap (Ring C): coarse mip levels permanently resident, fine levels streamed per tile. (Sprint 6b). |

### 2.9 Render Graph & Frame Scheduling (`src/Renderer/Graph/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/Graph/RenderGraph.cs` | **DONE** | Frame render graph DAG. Passes declare read/write dependencies; compiles barriers and layout transitions via synchronization2. (Sprint 4; transient memory aliasing planned for Sprint 7a). |
| `src/Renderer/Graph/RenderPass.cs` | **DONE** | Execution node in the render graph with setup and execute callbacks receiving command buffers. (Sprint 4). |
| `src/Renderer/Graph/RenderGraphResource.cs` | **DONE** | Virtual handle for transient/imported textures and buffers tracked by the graph for synchronization. (Sprint 4). |
| `src/Renderer/Graph/BarrierBuilder.cs` | **DONE** | Automatic `VkDependencyInfo` and `VkImageMemoryBarrier2` generator using Vulkan 1.3 `synchronization2`. (Sprint 4). |

### 2.10 GPU-Driven Rendering (`src/Renderer/GpuDriven/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/GpuDriven/GeometryPool.cs` | **DONE** | Monolithic shared vertex/index GPU memory pool enabling draw calls across all static meshes without rebinding buffers. (Sprint 5b basic, 7a full). |
| `src/Renderer/GpuDriven/DynamicInstanceRing.cs` | **DONE** | Ring-buffered per-frame instance buffer region allowing dynamic ECS entities to feed the GPU-driven renderer with current and previous transforms. (Sprint 5b). |
| `src/Renderer/GpuDriven/InstanceBuffer.cs` | **DONE** | GPU storage buffer holding per-instance data: camera-relative float transforms (current & previous), mesh ID, material ID, bounding sphere. (Sprint 7a). |
| `src/Renderer/GpuDriven/IndirectDrawBuffer.cs` | **DONE** | Indirect command buffer populated by compute shader for `vkCmdDrawIndexedIndirectCount` draws. (Sprint 7a). |
| `src/Renderer/GpuDriven/GpuCuller.cs` | **DONE** | Compute shader dispatcher: executes 5-plane frustum culling, max draw distance clamp, LOD selection, and Hi-Z min-reduction occlusion across 5 views. (Sprint 7a). |
| `src/Renderer/GpuDriven/HiZBuilder.cs` | **DONE** | Generates hierarchical depth mip pyramid (Hi-Z) from the depth pre-pass using MIN reduction filters. (Sprint 7a). |

### 2.11 3D Graphics, Lighting & Environment (`src/Renderer/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/Camera/Camera3D.cs` | **DONE** | 3D Perspective camera. Computes View, Projection, and camera-relative transformation matrices. |
| `src/Renderer/Camera/CameraController.cs` | **DONE** | Smooth camera controller supporting first-person free-look, orbit, pitch/yaw, and keyboard navigation. |
| `src/Renderer/Camera/Frustum.cs` | **DONE** | Extracts 5 bounding planes (Left, Right, Bottom, Top, Near) plus configurable max draw distance for CPU-side coarse culling. (Sprint 5b). |
| `src/Renderer/Camera/DepthConvention.cs` | **DONE** | Canonical depth settings and matrices (Reverse-Z flag, infinite projection builder, clear depth constants). (Sprint 3b). |
| `src/Renderer/Mesh/Vertex3D.cs` | **DONE** | Standard 3D PBR vertex struct: Position (`float3`), Normal (`float3`), Tangent (`float4`), UV (`float2`), and Color (`float4`). |
| `src/Renderer/Mesh/Mesh.cs` | **DONE** | GPU geometry container holding vertex/index buffer slices, triangle count, and bounding box. (Sprint 4). |
| `src/Renderer/Mesh/Model.cs` | **DONE** | Multi-mesh 3D model container containing sub-meshes, materials, and local node hierarchies. (Sprint 4). |
| `src/Renderer/Material/Material.cs` | **DONE** | PBR material binding albedo, normal, metallic-roughness, AO, emissive textures and scalar factors. (Sprint 5b). |
| `src/Renderer/Material/MaterialTable.cs` | **DONE** | GPU storage buffer containing all active material parameters indexed by material ID for bindless draws. (Sprint 5b). |
| `src/Renderer/Lighting/DirectionalLight.cs` | **DONE** | Sunlight parameters: Direction, Color, Intensity, Cascaded shadow configuration. (Sprint 5b). |
| `src/Renderer/Lighting/PointLight.cs` | **DONE** | Point light parameters: Camera-relative position, Color, Intensity, Attenuation radius (32 bytes). (Sprint 7b). |
| `src/Renderer/Lighting/SpotLight.cs` | **DONE** | Spot light parameters: Position, Direction, Inner/Outer cone angles, Color, Range (48 bytes). (Sprint 7b). |
| `src/Renderer/Lighting/LightEnvironment.cs` | **DONE** | Scene lighting container holding sun light, ambient sky light, and active clustered light arrays. (Sprint 5b). |
| `src/Renderer/Lighting/ClusteredLightCulling.cs`| **DONE** | 3D view-frustum cluster grid allocator and compute pass assigning point/spot lights to 3D clusters (16x8x24). (Sprint 7b). |
| `src/Renderer/Lighting/IblProbe.cs` | **DONE** | Image-based lighting probe: diffuse irradiance cubemap, specular prefiltered reflection map, BRDF LUT. (Sprint 7b). |
| `src/Renderer/Shadows/ShadowMap.cs` | **DONE** | Depth render target and light view-projection for single directional/spot shadow passes. (Sprint 5b). |
| `src/Renderer/Shadows/CascadedShadowMap.cs` | **DONE** | 4-cascade shadow map 2D array texture renderer with texel snapping to prevent edge shimmering during camera translation. (Sprint 7b). |
| `src/Renderer/Shadows/ShadowCascade.cs` | **DONE** | Individual cascade bounds, projection matrix, split distance, and light-space texel snapping. (Sprint 7b). |
| `src/Renderer/Vegetation/FoliageSystem.cs` | **DONE** | GPU instancing manager for grass, shrubs, and trees with wind vertex animation and distance LOD dithering. (Sprint 7b). |
| `src/Renderer/Vegetation/FoliageInstanceData.cs`| **DONE** | Packed instance representation (16 bytes: position offset, scale, rotation, color tint & wind phase). (Sprint 7b). |
| `src/Renderer/Transparent/TransparentPass.cs` | **DONE** | Weighted, blended order-independent transparency (WBOIT) pass for glass, effects, and particles. (Sprint 7b). |
| `src/Renderer/Particles/ParticleSystem.cs` | **DONE** | GPU particle simulation (compute update, billboard draw) with soft particle depth fading (64 bytes std430). (Sprint 7b). |
| `src/Renderer/Sky/Atmosphere.cs` | **PLANNED** | Analytical Rayleigh/Mie atmospheric scattering shader simulation and aerial perspective fog. (Sprint 9). |
| `src/Renderer/Sky/TimeOfDay.cs` | **PLANNED** | 24-hour sun/moon orbit simulation driving directional light vector, sky color, and ambient tones. (Sprint 9). |
| `src/Renderer/Skybox/Skybox.cs` | **DONE** | Procedural atmospheric or cubemap background drawn at reverse-Z infinity ($Z = 0.0$). (Sprint 5b). |
| `src/Renderer/Water/WaterRenderer.cs` | **PLANNED** | Planar Gerstner water surface with depth absorption, wave displacement, normal blending, and screen refraction. (Sprint 9). |
| `src/Renderer/ForwardRenderer3D.cs` | **DONE** | Basic forward rendering path in Sprint 5b, converted to GPU-driven indirect renderer in Sprint 7a. (Sprint 5b). |
| `src/Renderer/Debug/DebugRenderer.cs` | **DONE** | 3D wireframe line, box, sphere, and frustum renderer for visualizing physics colliders and streaming bounds. (Sprint 5b). |

### 2.12 Post-Processing Pipeline (`src/Renderer/PostProcess/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/PostProcess/PostProcessChain.cs` | **PLANNED** | Orchestrates post-process passes via render graph attachments in HDR `R16G16B16A16_SFLOAT`. (Sprint 9). |
| `src/Renderer/PostProcess/Tonemap.cs` | **PLANNED** | ACES / AgX filmic tonemapping, auto-exposure adaptation via luminance histogram, and color grading. (Sprint 9). |
| `src/Renderer/PostProcess/Bloom.cs` | **PLANNED** | Downsample/upsample progressive blur pyramid with threshold knee and energy conservation. (Sprint 9). |
| `src/Renderer/PostProcess/Taa.cs` | **PLANNED** | Temporal Anti-Aliasing with jittered projection matrices, motion vector reprojection, and variance clipping. (Sprint 9). |
| `src/Renderer/PostProcess/MotionVectors.cs` | **PLANNED** | Generates screen-space velocity buffer correcting for camera translation and floating-origin shifts. (Sprint 9). |
| `src/Renderer/PostProcess/Ssao.cs` | **PLANNED** | Screen-Space Ambient Occlusion (or GTAO) with depth-aware bilateral blur. (Sprint 9). |

### 2.13 Scene & Entity Architecture / ECS (`src/Scene/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Scene/Transform.cs` | **DONE** | Local float transform (Position, Rotation, Scale) + `WorldPosition` reference for root entities. (Sprint 5a). |
| `src/Scene/Entity.cs` | **DONE** | Lightweight numeric entity ID handle indexing into archetype component storage. (Sprint 5a). |
| `src/Scene/Scene.cs` | **DONE** | Container managing active ECS world, dynamic entities, spatial hierarchy, and render submission. (Sprint 5a). |
| `src/Scene/Components/MeshRenderer.cs` | **DONE** | Struct component referencing mesh ID and material ID for dynamic entities. (Sprint 5a). |
| `src/Scene/Components/LightComponent.cs` | **DONE** | Struct component attaching directional, point, or spot light properties to an entity. (Sprint 5a). |
| `src/Scene/Components/CameraComponent.cs` | **DONE** | Struct component marking the active viewport camera entity. (Sprint 5a). |
| `src/Scene/Components/RigidBodyComponent.cs` | **DONE** | Struct component binding an entity to a Jolt simulation body handle. (Sprint 5a). |

### 2.14 Physics Engine (`src/Physics/` via BRUTAL.Jolt)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Physics/PhysicsEngine.cs` | **PLANNED** | Master Jolt physics wrapper. Manages broadphase layers, gravity, origin rebasing, and fixed-timestep ticking. (Sprint 8). |
| `src/Physics/PhysicsBody.cs` | **PLANNED** | 3D rigid body wrapper (Static, Kinematic, Dynamic; Velocity, Restitution, Friction, Mass). (Sprint 8). |
| `src/Physics/Colliders/Collider.cs` | **PLANNED** | Base class for collision shapes. (Sprint 8). |
| `src/Physics/Colliders/BoxCollider.cs` | **PLANNED** | Box collision shape defined by half-extents. (Sprint 8). |
| `src/Physics/Colliders/SphereCollider.cs` | **PLANNED** | Sphere collision shape defined by radius. (Sprint 8). |
| `src/Physics/Colliders/CapsuleCollider.cs` | **PLANNED** | Capsule collision shape for characters. (Sprint 8). |
| `src/Physics/Colliders/MeshCollider.cs` | **PLANNED** | Static mesh / convex hull collider streamed per world cell. (Sprint 8). |
| `src/Physics/CharacterController.cs` | **PLANNED** | Kinematic virtual character controller for player movement, stairs, slope limits, and jumping. (Sprint 8). |
| `src/Physics/RaycastHit.cs` | **PLANNED** | Raycast query result struct containing Hit, Position, Normal, Distance, and Body ID. (Sprint 8). |

### 2.15 Audio Engine (`src/Audio/` via BRUTAL.OpenAl)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Audio/AudioEngine.cs` | **PLANNED** | OpenAL context, device management, master gain, and 3D spatial coordinate updates. (Sprint 8). |
| `src/Audio/SoundClip.cs` | **PLANNED** | Shared in-memory decoded audio buffer (WAV/OGG) reference-counted across emitters. (Sprint 8). |
| `src/Audio/AudioSource.cs` | **PLANNED** | 3D positional audio emitter with attenuation curves, pitch, volume, and looping. (Sprint 8). |
| `src/Audio/AudioListener.cs` | **PLANNED** | Player audio receptor syncing world position and forward/up vectors with active camera. (Sprint 8). |

### 2.16 AI, Navigation & Animation (`src/AI/`, `src/Animation/`, `src/UI/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/AI/NavMesh.cs` | **PLANNED** | World navigation mesh manager supporting tiled navmesh queries across streamed cells. (Sprint 10). |
| `src/AI/NavMeshAgent.cs` | **PLANNED** | AI pathfinding agent following waypoints with avoidance and velocity control. (Sprint 10). |
| `src/AI/NavMeshTile.cs` | **PLANNED** | Cell-sized baked navigation tile loaded/unloaded alongside world cells. (Sprint 10). |
| `src/Animation/Skeleton.cs` | **PLANNED** | Bone hierarchy, bind poses, and inverse bind matrices parsed from glTF. (Sprint 10). |
| `src/Animation/AnimationClip.cs` | **PLANNED** | Skeletal animation keyframes (translation, rotation, scale) with interpolation. (Sprint 10). |
| `src/Animation/SkinningSystem.cs` | **PLANNED** | Computes final bone matrix palettes and dispatches GPU compute/vertex skinning. (Sprint 10). |
| `src/UI/Canvas.cs` | **PLANNED** | In-game HUD/UI container with anchor layout system independent of editor ImGui. (Sprint 10). |
| `src/UI/FontRenderer.cs` | **PLANNED** | Signed Distance Field (SDF) text rendering pipeline using `Ahwoo.BRUTAL.FreeType`. (Sprint 10). |

### 2.17 Diagnostics & Debug UI (`src/DebugUI/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/DebugUI/ImGuiVulkanBackend.cs` | **DONE** | Custom Vulkan 1.3 Dynamic Rendering backend for Dear ImGui. Zero legacy render passes. (Sprint 3b). |
| `src/DebugUI/DebugOverlay.cs` | **DONE** | Diagnostics HUD: frame time, CPU/GPU pass times, heap allocations, VRAM bar. (Sprint 3b; streaming map in 6a). |

### 2.18 Optional Standalone Editor Suite (`src/Editor/` — Sprint 11)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Editor/EditorApp.cs` | **PLANNED** | Master standalone editor application coordinator. (Sprint 11 - Optional). |
| `src/Editor/EditorDockspace.cs` | **PLANNED** | Full-screen ImGui dockspace host for panels. (Sprint 11 - Optional). |
| `src/Editor/EditorViewport.cs` | **PLANNED** | Render-to-texture 3D scene viewport displaying engine render target inside ImGui. (Sprint 11 - Optional). |
| `src/Editor/EditorCamera.cs` | **PLANNED** | Dedicated editor camera (WASD flycam, Shift sprint, Alt+LMB orbit, 'F' focus). (Sprint 11 - Optional). |
| `src/Editor/Gizmos/TransformGizmo.cs` | **PLANNED** | 3D interactive manipulation handles (Translate, Rotate, Scale) with grid snapping. (Sprint 11 - Optional). |
| `src/Editor/Picking/ObjectPicker.cs` | **PLANNED** | Viewport ray-casting and GPU entity ID picking for interactive 3D object selection. (Sprint 11 - Optional). |
| `src/Editor/Panels/EditorToolbar.cs` | **PLANNED** | Simulation controls (Play, Pause, Stop, Gizmo modes, Coordinate space). (Sprint 11 - Optional). |
| `src/Editor/Panels/SceneHierarchyPanel.cs`| **PLANNED** | Entity tree view: drag-drop re-parenting, renaming, primitive creation. (Sprint 11 - Optional). |
| `src/Editor/Panels/InspectorPanel.cs` | **PLANNED** | Live component property inspector with drag sliders, material pickers, collider tuners. (Sprint 11 - Optional). |
| `src/Editor/Panels/AssetBrowserPanel.cs` | **PLANNED** | Visual asset browser with thumbnail previews and drag-to-viewport spawning. (Sprint 11 - Optional). |
| `src/Editor/Panels/ConsolePanel.cs` | **PLANNED** | Live colored logging stream with filtering and stack trace display. (Sprint 11 - Optional). |
| `src/Editor/Panels/PerformancePanel.cs` | **PLANNED** | Detailed performance charts: CPU/GPU pass breakdown, VRAM graphs, draw calls, active bodies. (Sprint 11 - Optional). |
| `src/Editor/Serialization/SceneSerializer.cs` | **PLANNED** | JSON scene serializer/deserializer (`.scene` files) with Ctrl+S / Ctrl+O support. (Sprint 11 - Optional). |

### 2.19 Automated Testing, Validation & CI Infrastructure
| File Path | Status | Purpose & Role |
| :--- | :---: | :--- |
| `tests/Jatayu.Tests/Jatayu.Tests.csproj` | **DONE** | xUnit unit test suite covering math, origin shift, job system, staging ring, deferred deletion queue, event bus, and asset loaders (cell state machine tests planned with src/World in Sprint 6a). (Sprint 3a/3b/hotfixes). |
| `tests/Jatayu.RenderSmoke/Program.cs` | **DONE** | Automated Vulkan smoke test: runs D20 feature probe, runs full test suite on D10 hardware or reduced validation-only suite on software drivers. (Sprint 3a). |
| `.github/workflows/ci.yml` | **DONE** | Continuous integration pipeline: build, test, and run validation smoke tests. (Sprint 3a). |
| `Directory.Build.props` | **DONE** | Solution-wide compiler settings (`TreatWarningsAsErrors=true`, nullable enabled, analyzers). (Sprint 3a). |
| `docs/RENDER_CONVENTIONS.md` | **DONE** | Canonical reference documenting depth, handedness, matrix layout, units, and shader bindings. (Sprint 3a). |

### 2.20 Asset Shaders (`assets/shaders/`)
| File Path | Status | Purpose |
| :--- | :---: | :--- |
| `assets/shaders/triangle.vert` | **DONE** | 2D test vertex shader. |
| `assets/shaders/triangle.frag` | **DONE** | 2D test fragment shader. |
| `assets/shaders/cube.vert` | **DONE** | 3D cube vertex shader (push constants: MVP + demo-only Panini option). |
| `assets/shaders/cube.frag` | **DONE** | 3D cube fragment shader with directional diffuse normal shading. |
| `assets/shaders/pbr.vert` | **DONE** | Camera-relative vertex shader evaluating world normals, tangents, shadow coordinates, and velocity output for TAA. (Sprint 5b). |
| `assets/shaders/pbr.frag` | **DONE** | Cook-Torrance microfacet specular BRDF, metallic-roughness, normal mapping, cascaded shadows. (Sprint 5b). |
| `assets/shaders/shadow.vert` | **DONE** | Depth-only vertex shader for directional cascaded shadow map passes. (Sprint 5b). |
| `assets/shaders/shadow.frag` | **DONE** | Depth-only fragment shader supporting alpha-testing for foliage shadow casting. (Sprint 5b, alpha test 7b). |
| `assets/shaders/skybox.vert` | **DONE** | Fullscreen triangle vertex shader rendering background at reverse-Z depth = 0.0. (Sprint 5b). |
| `assets/shaders/skybox.frag` | **DONE** | Procedural gradient sky or prefiltered cubemap shader. (Sprint 5b). |
| `assets/shaders/debug_line.vert`| **DONE** | 3D debug wireframe vertex shader. (Sprint 5b). |
| `assets/shaders/debug_line.frag`| **DONE** | Unlit flat-color fragment shader for physics debug lines and bounds. (Sprint 5b). |
| `assets/shaders/cull.comp` | **DONE** | Compute shader for 5-plane frustum culling, max draw distance clamp, LOD selection, Hi-Z min-reduction occlusion, and indirect command generation across 5 views. (Sprint 7a). |
| `assets/shaders/hiz_build.comp` | **DONE** | Compute shader generating hierarchical depth mip pyramid using MIN reduction for reverse-Z. (Sprint 7a). |
| `assets/shaders/light_cull.comp`| **DONE** | Compute shader dividing frustum into 3D clusters (16x8x24) and assigning point/spot light indices. (Sprint 7b). |
| `assets/shaders/particle.vert` | **DONE** | GPU particle billboard vertex shader with camera-facing quad expansion. (Sprint 7b). |
| `assets/shaders/particle.frag` | **DONE** | Soft-particle fragment shader with radial alpha falloff. (Sprint 7b). |
| `assets/shaders/particle.comp` | **DONE** | Compute shader updating GPU particle positions, velocities, lifetimes (64 bytes std430). (Sprint 7b). |
| `assets/shaders/lod_dither.glsl`| **DONE** | GLSL include file providing 4x4 Bayer matrix stipple transparency for seamless LOD cross-fading. (Sprint 7b). |
| `assets/shaders/transparent_composite.frag` | **DONE** | Fullscreen composite shader blending WBOIT transparent accumulation over opaque HDR color. (Sprint 7b). |
| `assets/shaders/ibl_irradiance.comp` | **DONE** | Compute shader generating diffuse irradiance cubemap from environment map. (Sprint 7b). |
| `assets/shaders/ibl_prefilter.comp` | **DONE** | Compute shader generating specular prefiltered environment cubemap mips via GGX importance sampling. (Sprint 7b). |
| `assets/shaders/brdf_lut.comp` | **DONE** | Compute shader pre-integrating split-sum 2D BRDF LUT for PBR specular reflections. (Sprint 7b). |
| `assets/shaders/terrain.vert` | **DONE** | Terrain clipmap vertex shader with continuous geomorphing and normal generation from heightmaps. (Sprint 6b). |
| `assets/shaders/terrain.frag` | **DONE** | Multi-layer splat material shader with triplanar projection for cliff slopes. (Sprint 6b). |
| `assets/shaders/foliage.vert` | **DONE** | GPU-instanced vegetation vertex shader with procedural wind vertex oscillation and distance fade. (Sprint 7b). |
| `assets/shaders/foliage.frag` | **DONE** | Foliage fragment shader with Bayer dither fade and subsurface scattering backlighting approximation. (Sprint 7b). |
| `assets/shaders/atmosphere.frag`| **PLANNED** | Analytical Rayleigh/Mie scattering atmosphere and sun disc shader. (Sprint 9). |
| `assets/shaders/water.vert` | **PLANNED** | Water surface displacement shader using Gerstner waves. (Sprint 9). |
| `assets/shaders/water.frag` | **PLANNED** | Water lighting with depth color gradient, normal blending, Fresnel reflection and refraction. (Sprint 9). |
| `assets/shaders/exposure_histogram.comp` | **PLANNED** | Compute shader evaluating 64-bin luminance histogram for automatic eye adaptation. (Sprint 9). |
| `assets/shaders/post_tonemap.frag`| **PLANNED** | ACES / AgX filmic tonemapping, vignette, and dithering shader. (Sprint 9). |
| `assets/shaders/post_bloom.frag`| **PLANNED** | Dual-filtering progressive blur upsample/downsample shader. (Sprint 9). |
| `assets/shaders/post_taa.frag` | **PLANNED** | Temporal anti-aliasing shader with history reprojection and neighborhood clamping. (Sprint 9). |
| `assets/shaders/post_ssao.frag` | **PLANNED** | Screen-space ambient occlusion shader with depth-aware spatial filtering. (Sprint 9). |
| `assets/shaders/skinning.comp` | **PLANNED** | Compute shader evaluating dual-quaternion or matrix skeletal skinning into vertex buffers. (Sprint 10). |
| `assets/shaders/ui.vert` | **PLANNED** | 2D canvas UI ortho vertex shader. (Sprint 10). |
| `assets/shaders/ui.frag` | **PLANNED** | 2D canvas UI fragment shader for textured rectangles and borders. (Sprint 10). |
| `assets/shaders/sdf_text.frag` | **PLANNED** | Signed distance field antialiased text shader. (Sprint 10). |

---

## 3. Progress Tracking Dashboard

- **Total Planned Files:** 215
- **Total Completed Files:** 156
- **Current Completion:** **72.6% (Sprint 7 COMPLETE & ACCEPTED: 755.3 FPS ref / 0.25 ms GPU, 2.87 ms 200k stress on RTX 5060 Ti)**

| Subsystem | Completed / Planned | Status |
| :--- | :---: | :--- |
| **Core Platform, Memory & Config** | 15 / 16 | **WorldPosition, OriginShift, Engine, Settings, Input, EventBus Complete** |
| **Multithreading & Jobs** | 5 / 5 | **COMPLETE (JobSystem, Job, JobHandle, ParallelFor, MainThreadDispatcher)** |
| **Diagnostics & Profiling** | 4 / 4 | **COMPLETE (Profiler, AllocTracker, GpuTimer, DebugNames)** |
| **Modern Vulkan 1.3 RHI** | 22 / 23 | **BindlessTable, Async Uploads, Timeline, DeletionQueue Added** |
| **Shared Formats & Runtime Assets** | 12 / 12 | **COMPLETE (TextureStreamer, MipResidency, Formats Added)** |
| **Cooker & World Generator** | 11 / 13 | **FoliageCooker, HeightfieldCooker, HlodCooker, ImpostorCooker, CellCooker Complete** |
| **World Grid & Streaming** | 7 / 7 | **COMPLETE (WorldGrid, StreamingManager, CellLoader, HLOD, DebugOverlay)** |
| **Terrain System** | 4 / 4 | **COMPLETE (TerrainTile, TerrainClipmap, TerrainMaterial, HeightPyramid)** |
| **Render Graph** | 4 / 4 | **COMPLETE (RenderGraph, RenderPass, RenderGraphResource, BarrierBuilder)** |
| **GPU-Driven Rendering** | 6 / 6 | **COMPLETE (GeometryPool, DynamicInstanceRing, InstanceBuffer, IndirectDrawBuffer, GpuCuller, HiZBuilder)** |
| **3D Graphics, Lighting & Environment** | 25 / 28 | **COMPLETE (CSM, ClusteredLightCulling, IBL, Foliage, Particles, WBOIT, ForwardRenderer3D, Skybox)** |
| **Post-Processing Pipeline** | 0 / 6 | Planned (Sprint 9) |
| **Scene & ECS Architecture** | 7 / 7 | **COMPLETE (Scene, Entity, Transform, MeshRenderer, Camera, Light, RigidBody)** |
| **Physics (BRUTAL.Jolt)** | 0 / 9 | Planned (Sprint 8) |
| **Audio (BRUTAL.OpenAL)** | 0 / 4 | Planned (Sprint 8) |
| **AI, Navigation & Animation** | 0 / 8 | Planned (Sprint 10) |
| **Diagnostics & Debug UI** | 2 / 2 | **COMPLETE (ImGuiVulkanBackend, DebugOverlay)** |
| **Optional Standalone Editor Suite** | 0 / 13 | Planned (Sprint 11) |
| **Testing, Validation & CI** | 5 / 5 | **COMPLETE** |
| **Asset Shaders** | 29 / 39 | **COMPLETE (light_cull, ibl_*, brdf_lut, foliage.*, particle.*, transparent_composite, cull.comp, hiz_build.comp)** |

---

## 4. Architectural Decisions Log & Resolutions

* **D1 (Texture Compression in Cooker):**
  * *Specification:* For direct raw BC7/BC5/BC4 block output into `.jpak` archives, `tools/Jatayu.Cooker/` will evaluate Basis Universal UASTC transcoding vs native `bc7enc` (see D27).
  * *Status:* Pending: verify in Sprint 3a.
* **D2 (ECS Implementation):**
  * *Choice:* **Arch** (v1.2.8, Apache-2.0). Benchmark spike in Sprint 5a verified 100,000 entities query throughput at **0.25 ms/frame** with **0 bytes GC heap allocation** via struct `IForEach` queries (`world.InlineQuery`). Fallback Friflo.Engine.ECS was evaluated but rejected due to persistent 64-byte delegate heap allocation per query invocation.
  * *Status:* **Resolved & Adopted in Sprint 5a**.
* **D3 (Cell Sizing):**
  * *Choice:* **128 m** world cells; terrain tiles cover $2 \times 2$ cells (256 m). Stays configurable in `WorldConfig`.
* **D4 (Distance Streaming Rings):**
  * *Choice:*
    * Ring A ($0 \le d \le 600\,\text{m}$): Full detail meshes, colliders, audio, dense 3D foliage (to ~300m).
    * Ring B ($600\,\text{m} < d \le 2,000\,\text{m}$): HLOD proxy meshes, tree impostors, audio, no physics colliders.
    * Ring C ($2,000\,\text{m} < d \le 8,000\,\text{m}$): Terrain height pyramid clipmap and sky/atmosphere only.
    * Load radius = 600 m; unload radius = 750 m (150 m hysteresis band).
* **D5 (Texture Residency Strategy):**
  * *Choice:* Mip streaming via `TextureStreamer`. Small mip tails ($\le 64\times 64$) are permanently resident; streamable higher mips are loaded/evicted dynamically under `VramBudget` constraints.
* **D6 (Max Draw Distance):**
  * *Choice:* **8,000 m (8 km)** default, configurable in `WorldConfig`. Governs the far boundary of the 5-plane frustum cull and terrain clipmap outer ring.
* **D7 (Automated Testing Framework):**
  * *Choice:* **xUnit** in `tests/Jatayu.Tests/` targeting .NET 10 for all non-GPU engine logic, combined with `tests/Jatayu.RenderSmoke/` for headless/offscreen Vulkan validation smoke tests.
* **D8 (Meshoptimizer Binding):**
  * *Specification:* `Ahwoo.BRUTAL.MeshOptimizer` (v2026.10.2) is referenced in `Jatayu.csproj`.
  * *Status:* Pending: verify in Sprint 3a (MeshOptimizer function exports: cache, fetch, overdraw, simplify, remap).
* **D9 (Physics Precision):**
  * *Specification:* `Ahwoo.BRUTAL.Jolt` (v2026.10.4) is referenced in `Jatayu.csproj`.
  * *Status:* Verified in Sprint 3a: `Ahwoo.BRUTAL.Jolt` is built in Single Precision (`float3`). Deterministic origin rebasing procedure (Section 1.3) remains mandatory for large-world coordinates.
* **D10 (Final Hardware Floor):**
  * *Specification:* Vulkan 1.3 discrete GPU, at least 4 GB VRAM, with all of the following:
    - Core 1.3: `dynamicRendering`, `synchronization2`, `maintenance4`
    - Core 1.2: `timelineSemaphore`, `bufferDeviceAddress`, `drawIndirectCount`, `samplerFilterMinmax`, `descriptorIndexing` with `runtimeDescriptorArray`, `descriptorBindingPartiallyBound`, `descriptorBindingSampledImageUpdateAfterBind`, `descriptorBindingUpdateUnusedWhilePending`, `descriptorBindingVariableDescriptorCount`, `shaderSampledImageArrayNonUniformIndexing`, `shaderStorageBufferArrayNonUniformIndexing`
    - Core 1.1: `shaderDrawParameters`
    - Core 1.0: `samplerAnisotropy`, `multiDrawIndirect`, `textureCompressionBC`, `shaderInt64`, `fragmentStoresAndAtomics`
    - Extensions: `VK_EXT_memory_budget`; optional: `VK_EXT_debug_utils` (debug builds)
    `VulkanContext` will explicitly request all of these in Sprint 3a and fail with a clear message naming each missing feature.
  * *Status:* Pending: verify in Sprint 3a (GPU device feature support check).
* **D11 (Platform Target):**
  * *Choice:* Windows x64 primary, Linux x64 secondary (GLFW, Vulkan, OpenAL are cross-platform).
* **D12 (Reference GPU Tier):**
  * *Choice:* Discrete GPU of NVIDIA GeForce RTX 3060 (12 GB) or AMD Radeon RX 6600 XT (8 GB) class at $1920\times 1080$. Every measurement report will explicitly declare the physical GPU, driver version, and CPU model used.
* **D13 (Debug UI Location):**
  * *Choice:* `ImGuiVulkanBackend.cs` and `DebugOverlay.cs` are placed in `src/DebugUI/`, not `src/Editor/`. The engine depends on `DebugUI`, while the optional editor depends on the engine.
* **D14 (Sprint 3a Temporary Paths):**
  * *Choice:* Since `UploadManager` arrives in Sprint 3b and cooked archives arrive in Sprint 4, Sprint 3a is permitted a temporary synchronous upload helper (one-shot command buffer with fence wait) and runtime PNG loading via `TextureLoader`. Both are marked `// TEMPORARY(Sprint 3b/4)` and replaced in their respective sprints.
* **D15 (Terrain Ownership):**
  * *Choice:* Terrain tiles are 256 m ($2\times 2$ cells), keyed by `TileCoord = CellCoord / 2`. Render data loads when any of its 4 cells is in Ring A or B; heightfield collider is created as 1 collider per tile only while at least 1 cell is in Ring A; Ring C comes from `TerrainHeightPyramid`, not tiles.
* **D16 (Origin Shift & Static Colliders):**
  * *Choice:* Origin shift moves dynamic/kinematic/character bodies plus Ring A static bodies using Jolt's batched calls. If the physics spike exceeds 3.0 ms during Sprint 8 testing, switch to rebuilding Ring A static colliders from cell cooked data in the new origin.
* **D17 (Dynamic Instances):**
  * *Choice:* Dynamic ECS entities write into `DynamicInstanceRing` each frame (camera-relative floats). Static world instances live in cell-owned arrays uploaded once per cell load. Both feed the same culling and indirect-draw path.
* **D18 (Ring B Foliage & Impostors):**
  * *Choice:* Foliage is Ring A only (to ~300 m); Ring B shows tree impostors and HLOD proxies. Hysteresis: Ring A unload is 150 m beyond load radius; Rings B and C use 10% of radius.
* **D19 (Velocity-Scaled Streaming Radius):**
  * *Choice:* At 100 m/s, 600 m gives only 6 s lead time. `WorldConfig` adds `LeadSeconds` (default: 6 s): effective load radius = base radius + speed $\times$ LeadSeconds, capped, prioritized along player velocity.
* **D20 (CI and Hardware Floor Strategy):**
  * *Choice:* The render smoke test first runs a feature probe. On a device that meets the D10 floor it runs the full suite. On a device that does not (for example a software Vulkan driver such as lavapipe on a CI runner), it runs a reduced validation-only suite (instance, device, swapchain-less offscreen clear and triangle) and reports the skipped checks explicitly as SKIPPED, not as passed or failed. Full-suite results are required before any sprint is marked done, produced on a developer machine that meets the floor.
* **D21 (MipResidency Design):**
  * *Choice:* Replaces the "min-LOD clamp in descriptor table" concept. Vulkan fixes an image's mip count at creation and sparse residency is not in the hardware floor. Therefore: each streamed texture is a `GpuTexture` holding a contiguous range of top-mips-missing levels (always including the resident mip tail of $64\times 64$ and below). To change residency the streamer allocates a new image with the new mip range, GPU-copies the already resident mips, uploads the new ones through `UploadManager`, atomically swaps the bindless slot, and retires the old image via `DeferredDeletionQueue`. Residency changes in steps of 2 mip levels to limit churn. Target mip selection uses a CPU heuristic (distance and texel density from instance bounds of materials using the texture), not GPU feedback. GPU feedback is an optional later improvement.
* **D22 (One Terrain Renderer):**
  * *Choice:* `TerrainClipmap` is the only terrain renderer for all rings. `TerrainTile` supplies streamed data (height, normals, splat weights, collider) and `TerrainHeightPyramid` supplies the coarse far field. The clipmap samples tile data where a tile is resident and the pyramid elsewhere, with a blend over the last 16 m of a tile edge. Tiles never draw geometry themselves.
* **D23 (Streaming Radius Cap):**
  * *Choice:* The velocity-scaled radius from D19 is capped at 900 m and applies to prefetch only (load data early, around 155 cells maximum). Activation of colliders, audio, and dynamic-entity interaction still happens only inside the 600 m Ring A radius. `StreamingManager` must consult `VramBudget` and refuse or defer prefetch requests under memory pressure.
* **D24 (GPU Culling Views and LOD Selection):**
  * *Choice:* `cull.comp` selects the mesh LOD per instance from a LOD table stored in the mesh record (screen-space error threshold, with the cross-fade from `lod_dither.glsl`). Culling runs for five views per frame: the main camera and the 4 shadow cascades. The main-view cost counts in the 0.30 ms culling budget, and cascade culling counts inside the 0.80 ms shadow budget.
* **D25 (Previous-Frame Transforms for Velocity):**
  * *Choice:* `DynamicInstanceRing` and `InstanceBuffer` store the current and previous camera-relative transform for dynamic instances (static instances use previous equals current). The previous-frame view-projection is corrected using the double-precision camera delta, including origin shifts, so TAA velocity stays continuous across shifts.
* **D26 (ECS Fallback License):**
  * *Choice:* Verified in nuspec inspection: `Arch` is **Apache-2.0** and `Friflo.Engine.ECS` is **MIT**. Arch selected based on zero heap allocations and 0.25 ms query time per 100k entities.
  * *Status:* **Resolved in Sprint 5a**.
* **D27 (BC7 Encoder Evaluation):**
  * *Specification:* Before committing to native `bc7enc`, run a quick comparison on three sample textures (albedo, normal, roughness): Basis UASTC encoded and transcoded to BC7 through the Ktx binding versus `bc7enc`. If the UASTC route has no visible quality loss, prefer it (no native dependency). Otherwise use `bc7enc`. Record the result and the chosen route. BC5 and BC4 follow the same choice.
  * *Status:* Pending: verify in Sprint 3a.
* **D28 (Meshlet Scope Removal):**
  * *Choice:* Mesh shaders and meshlets are fully removed from the engine plan and catalog to maintain disciplined scope. (Optional post-Sprint-10 item).
* **D29 (Sprint 5b Scope):**
  * *Choice:* `ForwardRenderer3D` basic path, `Skybox`, `DebugRenderer`, `ShadowMap`, `LightEnvironment` and their shaders (`skybox.*`, `shadow.*`, `debug_line.*`) are in Sprint 5b.
* **D30 (Runtime Asset Dependency Direction):**
  * *Choice:* `tools/Jatayu.Cooker/` and `tools/Jatayu.WorldGen/` reference only `src/Jatayu.AssetFormats/` and, where needed, `Ahwoo.BRUTAL.*` import libraries. They never reference `src/` runtime projects or Vulkan.

### 4.1 VERIFY Checklist
* [x] **Installed Ahwoo.BRUTAL.* package names and versions:** Verified by reading `Jatayu.csproj` (all 50 packages referenced):

| Category | Package Name | Version | Category | Package Name | Version |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Core** | `Ahwoo.BRUTAL.Core` | 2026.10.4 | **Assets** | `Ahwoo.BRUTAL.Texture` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Collections` | 2026.10.2 | | `Ahwoo.BRUTAL.Texture.Abstractions` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Concurrency` | 2026.10.2 | | `Ahwoo.BRUTAL.Stb` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Interop` | 2026.10.2 | | `Ahwoo.BRUTAL.Gli` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Localization` | 2026.10.3 | | `Ahwoo.BRUTAL.Ktx` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Logging` | 2026.10.3 | | `Ahwoo.BRUTAL.Gltf` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Maths` | 2026.10.3 | | `Ahwoo.BRUTAL.MeshOptimizer` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Memory` | 2026.10.2 | | `Ahwoo.BRUTAL.FreeType` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Numerics` | 2026.10.2 | | `Ahwoo.BRUTAL.ZLib` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Strings` | 2026.10.2 | **UI** | `Ahwoo.BRUTAL.ImGui` | 2026.10.3 |
| **Window/Input** | `Ahwoo.BRUTAL.Glfw` | 2026.10.5 | | `Ahwoo.BRUTAL.ImGui.Abstractions` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Sdl` | 2026.10.3 | | `Ahwoo.BRUTAL.ImGui.Backends` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Sdl.Image` | 2026.10.3 | | `Ahwoo.BRUTAL.ImGui.Extensions` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Sdl.Mixer` | 2026.10.3 | | `Ahwoo.BRUTAL.ImGui.FreeType` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Sdl.Ttf` | 2026.10.3 | | `Ahwoo.BRUTAL.ImPlot` | 2026.10.3 |
| | `Ahwoo.BRUTAL.NativeFileDialog`| 2026.10.3 | | `Ahwoo.BRUTAL.NotCurses` | 2026.10.1 |
| **Graphics** | `Ahwoo.BRUTAL.Vulkan` | 2026.10.3 | **Physics** | `Ahwoo.BRUTAL.Jolt` | 2026.10.4 |
| | `Ahwoo.BRUTAL.Vulkan.Abstractions`| 2026.10.3 | | `Ahwoo.BRUTAL.Box2D` | 2026.10.2 |
| | `Ahwoo.BRUTAL.Vulkan.Vma` | 2026.10.3 | **Audio** | `Ahwoo.BRUTAL.OpenAl` | 2026.10.3 |
| | `Ahwoo.BRUTAL.OpenGl` | 2026.10.2 | | `Ahwoo.BRUTAL.Fmod` | 2026.10.4 |
| | `Ahwoo.BRUTAL.ShaderC` | 2026.10.3 | **Network** | `Ahwoo.BRUTAL.RakNet` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Slang` | 2026.10.3 | **Monitor** | `Ahwoo.BRUTAL.Monitor.Common` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Render.Common` | 2026.10.2 | | `Ahwoo.BRUTAL.Monitor.Host` | 2026.10.3 |
| | `Ahwoo.BRUTAL.Render.Mesh` | 2026.10.2 | | | |
| | `Ahwoo.BRUTAL.Render.Debug` | 2026.10.2 | | | |
| | `Ahwoo.BRUTAL.RenderDoc` | 2026.10.3 | | | |
| | `Ahwoo.BRUTAL.RenderDoc.ImGui` | 2026.10.3 | | | |

* [x] **D1/D27:** Ktx binding Basis Universal support verified: `Ahwoo.BRUTAL.Ktx` exports `KtxTexture2.CompressBasis`, `KtxTexture2.TranscodeBasis`, `KtxBasisParams`, `KtxPackUastcFlagBits`. Basis UASTC to BC7/BC5/BC4 transcoding confirmed supported directly through KTX without external native dependency.
* [x] **D2/D26:** Arch license (Apache-2.0) and Friflo license (MIT) verified; both compatible with .NET 10. Benchmark spike scheduled for Sprint 5a.
* [x] **D8:** MeshOptimizer function exports verified: `OptimizeVertexCache`, `OptimizeVertexFetch`, `OptimizeOverdraw`, and `Simplify` confirmed exported by `Ahwoo.BRUTAL.MeshOptimizer`.
* [x] **D9:** Jolt precision verified: `Ahwoo.BRUTAL.Jolt` uses Single Precision (`float3`). Deterministic origin rebasing procedure (Section 1.3) remains mandatory for large-world coordinates.
* [x] **D10:** Development GPU (NVIDIA GeForce RTX 5060 Ti) verified: **PASSED** all mandatory D10 hardware floor requirements (DynamicRendering, Synchronization2, Maintenance4, TimelineSemaphore, BufferDeviceAddress, DrawIndirectCount, SamplerFilterMinmax, DescriptorIndexing, TextureCompressionBC, MultiDrawIndirect, ShaderInt64, FragmentStoresAndAtomics).

---

## 5. Reference Scene for All Performance Measurements

To maintain rigorous, objective performance validation across sprints, all frame-budget claims are evaluated against a standardized **Reference Scene**:
* **World Dimensions:** 16 km $\times$ 16 km streamed world.
* **Geometry:** Procedural clipmap terrain, 200,000 visible static instances (rocks, buildings, props), 50,000 foliage instances in view.
* **Lighting:** 1 directional sunlight with 4 cascaded shadow map splits, 256 active clustered point/spot lights.
* **Camera Dynamics:** Camera traversing the world at 100 m/s across multiple cell boundaries.
* **Resolution & Presentation:** 1920 $\times$ 1080 resolution, V-Sync OFF.
* **Hardware Reporting:** Every measurement report must state the GPU model, driver version, CPU, and measured scene. (Until the Reference Scene is generated in Sprint 6a, intermediate sprint reports will explicitly state the interim test scene used).

---

## 6. Revised Open-World Sprint Roadmap

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
│  - Camera3D (Vulkan Y-inverted NDC, View/Proj matrices, demo-only Panini)        │
│  - CameraController (Smooth auto-orbit, manual yaw/pitch, zoom, flycam)          │
│  - Vertex3D & IndexBuffer (Interleaved Position, Color, Normal, TexCoord)        │
│  - Vulkan Push Constants (80-byte MVP matrix + Panini parameters)                │
│  - Rotating 3D Shaded Cube with directional lighting and dynamic resizing       │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 3a: Textures, Pipelines, Tests and CI [COMPLETED]                         │
│  - Create src/Jatayu.AssetFormats/; add TextureLoader as importer there          │
│  - GpuTexture, VulkanSampler, ShaderCompiler, ShaderLoader, Shaders              │
│  - VulkanPipeline & PipelineBuilder, CommandPoolManager, Descriptors             │
│  - D14 temporary synchronous upload helper                                       │
│  - Directory.Build.props, tests/Jatayu.Tests, tests/Jatayu.RenderSmoke (D20 probe)│
│  - docs/RENDER_CONVENTIONS.md; VulkanContext requests full D10 feature set       │
│  - Result: Textured cube through new pipeline abstraction, validation clean, CI  │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 3b: GPU Foundation and Diagnostics [COMPLETED]                            │
│  - UploadManager, StagingRing (64-256 MB), UploadRequest, TimelineSemaphore      │
│  - DeferredDeletionQueue (safe resource retirement), VramBudget                  │
│  - Reverse-Z conversion (DepthConvention, Camera3D, DepthBuffer, shaders)       │
│  - NativeArena, NativePool, Profiler, GpuTimer, DebugNames, AllocationTracker    │
│  - src/DebugUI/ImGuiVulkanBackend.cs & DebugOverlay.cs (frame time, GPU, alloc)  │
│  - Result: Textured cube with async uploads, reverse-Z, live diagnostics HUD     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 4: Jobs, Cooking & Render Graph [DONE]                                    │
│  - JobSystem, Job, JobHandle, ParallelFor, MainThreadDispatcher                  │
│  - Cooker (Program, MeshCooker, TextureCooker with D27 encoder & mip tail, etc.)  │
│  - AssetGuid, CookedFormats, ModelLoader, PackReader, AssetManager               │
│  - RenderGraph (Pass DAG, sync2 barriers, depth pre-pass, opaque pass, GPU times)│
│  - Remove D14 temporary PNG/upload exception                                     │
│  - Result: glTF model cooked offline, loaded from .jpak, rendered via graph      │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 5a: Engine Runtime and ECS [DONE]                                         │
│  - ECS spike (100k entity query benchmark, zero allocations) & decision D2/D26   │
│  - Scene, Entity, Transform, MeshRenderer, CameraComponent, RigidBodyComponent   │
│  - Engine & EngineConfig, Settings, EventBus, Log, Input, KeyCodes, Gamepad      │
│  - Result: Game loop through Engine.Run() with ECS entities, zero allocation     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 5b: Large-World Rendering Foundations [DONE]                              │
│  - WorldPosition (double), Camera-Relative Rendering, OriginShift (256m snap)    │
│  - Frustum culling (5 planes + max draw distance clamp)                          │
│  - BindlessTable, basic GeometryPool, DynamicInstanceRing, Material, MaterialTable│
│  - PBR shaders, DirectionalLight, ShadowMap with shadow.*, LightEnvironment      │
│  - Skybox with skybox.*, DebugRenderer with debug_line.*, basic ForwardRenderer3D│
│  - Result: PBR scene rendered at 50,000m from origin with zero vertex jitter     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 6a: World Grid and Streaming [DONE]                                       │
│  - WorldConfig (D18 rings, D19 lead time, D23 900m cap), WorldCell, WorldGrid    │
│  - StreamingManager, CellLoader, CellCooker, StreamingDebugOverlay (radar map)   │
│  - CellCoord, CellFormat, WorldConstants from Jatayu.AssetFormats                │
│  - tools/Jatayu.WorldGen/ procedural generator with placeholder meshes           │
│  - Temporary non-GPU instanced draw path for cell instances (replaced in 7a)     │
│  - Result: Traverse 16 km x 16 km world at 100 m/s without hitch > 2ms           │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 6b: Terrain, HLOD and Texture Streaming [COMPLETED]                      │
│  - TerrainTile (256m), TerrainClipmap (unified D22), TerrainMaterial splat       │
│  - TerrainHeightPyramid (far-field clipmap), HeightfieldCooker (D15 rules)       │
│  - Hlod, HlodCooker, ImpostorCooker                                              │
│  - TextureStreamer, MipResidency (resident mip tail, streamed mips, D21 swaps)   │
│  - Result: 3 GB cooked textures run in 2 GB budget without popping > 1 mip level │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 7a: GPU-Driven Rendering [COMPLETED]                                      │
│  - InstanceBuffer, IndirectDrawBuffer (vkCmdDrawIndexedIndirectCount)           │
│  - GpuCuller (5 views: main + 4 cascades, LOD selection D24), HiZBuilder         │
│  - Velocity output in opaque pass with previous-frame transforms (D25)           │
│  - Full GeometryPool; ForwardRenderer3D converted to GPU-driven indirect         │
│  - Result: 200,000 visible instances in < 4ms GPU, < 1ms CPU render submission   │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 7b: Scalable Lighting, Foliage & Transparency [COMPLETED]                 │
│  - CascadedShadowMap (4 cascades, texel snapping, alpha-test shadow), SpotLight  │
│  - ClusteredLightCulling (3D cluster grid compute pass), IblProbe (IBL shaders)  │
│  - FoliageSystem, FoliageInstanceData, FoliageCooker, lod_dither.glsl            │
│  - TransparentPass (order-independent transparency) & ParticleSystem (GPU comp) │
│  - Result: Reference Scene at budget with shadows, 256 lights, foliage, particles│
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 8: Physics, Character & Spatial Audio [NEXT]                               │
│  - Jolt PhysicsEngine integration with streaming per-cell colliders              │
│  - Terrain heightfield physics shapes generated per cell                         │
│  - Kinematic CharacterController (walk, sprint, jump, stairs, slopes)            │
│  - Raycasts and collision event dispatch to ECS systems                          │
│  - OpenAL AudioEngine: SoundClip, AudioSource, AudioListener, cell emitters      │
│  - Origin shift integration (D16 measured: batched body updates, listener sync)  │
│  - Zero-Alloc Hardening: Pool streaming request queues and queries so that       │
│    AllocationTracker.ZeroAllocationPercent maintains 100% steady state at 100m/s │
│  - Result: Walk/jump across world with 3 origin shifts, stable rigid body stack  │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 9: Atmosphere, Water & Post-Processing Pipeline                           │
│  - Atmosphere (Rayleigh/Mie), TimeOfDay sun/moon orbit, aerial fog               │
│  - WaterRenderer (planar Gerstner waves, Fresnel, absorption color gradient)     │
│  - PostProcessChain (HDR R16G16B16A16_SFLOAT target)                             │
│  - Tonemap (ACES/AgX, exposure histogram), Bloom, TAA, MotionVectors, SSAO/GTAO  │
│  - Result: Reference Scene inside 5.90 ms GPU budget with full post effects      │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 10: Gameplay Support & Release Readiness (Engine Complete!)               │
│  - NavMesh, NavMeshTile, NavMeshAgent, NavMeshCooker                             │
│  - Skeleton, AnimationClip, SkinningSystem (skinning.comp)                       │
│  - Canvas UI, FontRenderer (SDF ui.vert/frag, sdf_text.frag)                     │
│  - SaveSystem (dynamic world state serialization)                                │
│  - ShaderCooker and PipelineCache for precompiled SPIR-V and persistent cache    │
│  - Result: Complete open-world game engine runtime (100% production ready)       │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 11 (OPTIONAL): Full Interactive 3D Editor Suite                           │
│  - Standalone editor application (EditorApp, EditorDockspace, EditorViewport)   │
│  - Viewport camera controls, TransformGizmo (Translate, Rotate, Scale)           │
│  - ObjectPicker raycast selection, Panels (Hierarchy, Inspector, Assets, Console)│
│  - SceneSerializer (Save/Load .scene JSON files)                                 │
│  - Note: Commenced only upon explicit request after Sprint 10 engine completion   │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### Sprint 8 Reference Inspiration

Use [AEspinosaDev/Vulkan-Engine](https://github.com/AEspinosaDev/Vulkan-Engine) as a design reference, not as a source to copy code or assets from:

* Its PBR direct-lighting and ambient/IBL examples (`resources/shaders/forward/physically_based.glsl`, `resources/shaders/VXGI/`) are useful visual references for the existing Jatayu lighting stack.
* Its organized shader, texture, mesh, and renderer example resources can inform a compact Sprint 8 integration scene: visible rigid bodies and terrain under Jatayu's existing shadows/IBL, with spatial audio reacting to movement and contact.
* Keep Sprint 8 focused on physics, character control, and spatial audio. Do not add a new lighting/GI pipeline or import external shaders/assets; use any further post-processing inspiration when planning Sprint 9.
* **Sprint 8 Hardening (Zero-Allocation Streaming at High Velocity):**
  * Eliminate transient main-thread GC allocations (`new PrioritizedLoadRequest`, list resize overhead, cell coordinate dictionary lookups, and event string interpolations) during high-speed motion (`StreamingManager.Update` and `OriginShift.TryRebase`).
  * Ensure `AllocationTracker.ZeroAllocationPercent` maintains a rock-solid 100% steady state even during the 100 m/s supersonic autopilot drone sweep across cell boundaries.

---

## 7. Change Log

| Entry | Sprint Affected | Modification Description |
| :--- | :---: | :--- |
| **CL-01** | Sprint 3b, 10, 11 | Moved `ImGuiVulkanBackend.cs` and `DebugOverlay.cs` forward from Sprint 10 to Sprint 3b. Moved full 13-file editor suite to an optional **Sprint 11**. |
| **CL-02** | Sprint 3a, 3b | Split Sprint 3 into **Sprint 3a** (Textures & Pipelines) and **Sprint 3b** (GPU Foundation, Reverse-Z, Diagnostics). |
| **CL-03** | Sprint 2, 3b | Corrected DepthBuffer status description to note that Reverse-Z conversion is pending Sprint 3b. |
| **CL-04** | Sprint 2, 5b, 7a | Designated Panini projection as demo-only for the cube sample; removed Panini from production camera and GPU-driven renderer descriptions. |
| **CL-05** | Sprint 5b, 7a | Re-specified `Frustum.cs` and `cull.comp` to use 5 planes plus a configurable `MaxDrawDistance` (default: 8,000 m). |
| **CL-06** | Sprint 4, 6a | Clarified cell packaging: world cells are binary chunk records inside contiguous `.jpak` archives (identified by GUID), eliminating standalone `.jcell` files. |
| **CL-07** | Sprint 3a, 7a | Standardized Hi-Z occlusion to **MIN** reduction for reverse-Z and added min-reduction sampler requirement (`VK_EXT_sampler_filter_minmax`) to `VulkanSampler.cs`. |
| **CL-08** | Sprint 6b | Added Texture Mip Streaming subsystem (`TextureStreamer.cs`, `MipResidency.cs`) and cooker mip tail splitting to catalog and roadmap. |
| **CL-09** | Sprint 7a, 7b | Split Sprint 7 into **Sprint 7a** (GPU-Driven Rendering) and **Sprint 7b** (Scalable Lighting, Foliage, Transparency, Particles, and LOD Dithering). |
| **CL-10** | Sprint 3a | Added Automated Testing, Validation & CI infrastructure files (`Jatayu.Tests`, `Jatayu.RenderSmoke`, `ci.yml`, `Directory.Build.props`, `RENDER_CONVENTIONS.md`). |
| **CL-11** | Sprint 3b, 5b | Added `NativeArena.cs`, `NativePool.cs`, `Settings.cs`, and `MaterialTable.cs` to the catalog. |
| **CL-12** | Core | Documented exact 5-step floating-origin rebasing procedure in Section 1.3. |
| **CL-13** | Core | Updated GPU frame budget table to 5.90 ms (providing ~15% headroom under 144 FPS / 6.94 ms target) and CPU frame budget to 5.20 ms. Defined Reference Scene in Section 5. |
| **CL-14** | Dashboard | Recounted all catalog files across 20 subsystem tables: 215 total planned files, 16 completed files (7.4% completion). |
| **CL-15** | Cross-Refs | Fixed broken cross-reference in Section 1.4 pointing to Reference Scene (Section 5) and OriginShift (Section 1.3). Removed all legacy document tags. |
| **CL-16** | Sprint 5b | Resolved bindless sequencing bug by moving `BindlessTable.cs` and basic `GeometryPool.cs` into Sprint 5b to support `MaterialTable` and `MipResidency`. Added `DynamicInstanceRing.cs`. |
| **CL-17** | Decisions | Recorded decisions D8 to D12 (MeshOptimizer binding, Jolt physics precision, Vulkan 1.3 hardware floor, platform targets, and reference GPU tier) with runtime verification marked "Pending: verify in Sprint 3a" where applicable. |
| **CL-18** | Decisions | Recorded decisions D1 (texture compression route) and D2 (ECS implementation candidate) with runtime verification marked "Pending: verify in Sprint 3a". |
| **CL-19** | Sprint 3b | Moved `ImGuiVulkanBackend.cs` and `DebugOverlay.cs` to `src/DebugUI/` (D13) to ensure engine never depends on editor folder. |
| **CL-20** | Sprint 3a, 4 | Formally permitted temporary synchronous upload and runtime PNG exception for Sprint 3a (D14), marked `// TEMPORARY(Sprint 3b/4)`. |
| **CL-21** | Sprint 6a, 6b | Added `tools/Jatayu.WorldGen/` (Sprint 6a), `TerrainHeightPyramid.cs` (Sprint 6b), `HlodCooker.cs`, and `ImpostorCooker.cs`. Formalized terrain tile 256m ownership and collider rules (D15). |
| **CL-22** | Sprint 7b, 10 | Added missing shaders (`particle.comp`, `skinning.comp`, `ibl_irradiance.comp`, `ibl_prefilter.comp`, `brdf_lut.comp`, `transparent_composite.frag`, `exposure_histogram.comp`, `ui.vert/frag`, `sdf_text.frag`), `FoliageCooker.cs`, `NavMeshCooker.cs`, `ShaderCooker.cs`, and `PipelineCache.cs`. |
| **CL-23** | Architecture | Separated shared data formats into `src/Jatayu.AssetFormats/` class library to decouple offline cooker from GPU/Vulkan dependencies. |
| **CL-24** | Sprint 5a, 5b | Split Sprint 5 into **Sprint 5a** (ECS & Engine Runtime) and **Sprint 5b** (Large-World Rendering Foundations, Bindless, Origin Shift). |
| **CL-25** | Sprint 6a, 6b | Split Sprint 6 into **Sprint 6a** (World Grid & Streaming) and **Sprint 6b** (Terrain, HLOD & Texture Streaming). |
| **CL-26** | World | Added `LeadSeconds` (default: 6 s) velocity-scaled streaming radius calculation to `WorldConfig` (D19) and foliage Ring A-only rule (D18). |
| **CL-27** | Sprint 3a | Replaced D10 with the exhaustive Vulkan 1.3 hardware floor specification. |
| **CL-28** | Sprint 3a | Added decision D20: hardware probe in `tests/Jatayu.RenderSmoke` running full test suite on D10 hardware or reduced validation suite on CI/software drivers. |
| **CL-29** | Sprint 10+ | Removed "meshlet-ready" and all references to meshlets from PLAN.md (D28). |
| **CL-30** | Sprint 6a | Moved `CellFormat.cs` and `CellCoord.cs` to `src/Jatayu.AssetFormats/World/`, added `WorldConstants.cs`, and removed duplicate `CookedCellRecord` from `CookedFormats.cs`. |
| **CL-31** | Sprint 5b | Updated floating-origin shift snap from 128m to 256m (terrain tile multiple) in Section 1.3 and `OriginShift.cs`. |
| **CL-32** | Sprint 6a | Updated `tools/Jatayu.WorldGen/Program.cs` catalog row to generate procedural placeholder glTF meshes into `assets/generated/`. |
| **CL-33** | Sprint 3b | Clarified `DebugOverlay.cs` scope for Sprint 3b to frame time, pass times, allocations, and VRAM only. |
| **CL-34** | Sprint 5b, 7a | Specified `ForwardRenderer3D.cs` role as basic forward in Sprint 5b, converted to GPU-driven indirect in Sprint 7a (D29). |
| **CL-35** | Sprint 6b | Added decision D21 (MipResidency dynamic allocation and bindless swap). |
| **CL-36** | Sprint 6b | Added decision D22 (unified `TerrainClipmap` renderer for all rings). |
| **CL-37** | Sprint 6a | Added decision D23 (900m streaming prefetch cap with Ring A 600m activation boundary). |
| **CL-38** | Sprint 7a | Added decision D24 (GPU culling across 5 views and LOD error table selection with dithering). |
| **CL-39** | Sprint 7a | Added decision D25 (previous-frame transforms for TAA motion vectors continuous across origin shifts). |
| **CL-40** | Sprint 5a | Added decision D26 (ECS fallback license; status: Pending: verify in Sprint 3a). |
| **CL-41** | Sprint 4 | Added decision D27 (BC7 encoder evaluation route; status: Pending: verify in Sprint 3a). |
| **CL-42** | Sprint 5b | Added decision D29 (Sprint 5b scope: ForwardRenderer3D basic, Skybox, DebugRenderer, ShadowMap, LightEnvironment). |
| **CL-43** | Sprint 4, 6a | Added decision D30 (shared formats dependency direction: `tools/` only reference `src/Jatayu.AssetFormats/`). |
| **CL-44** | Sprint 3a | Added Section 4.1 VERIFY checklist with file-reading package inspection and pending Sprint 3a verification markers. |
| **CL-45** | Decisions | Reworded CL-17 and CL-18 to state decisions D8-D12 and D1/D2 verification statuses are marked "Pending: verify in Sprint 3a", removing unverified claims. |
| **CL-46** | Physics, Core | Updated Section 1.3 step 3 to include Ring A static cell colliders in batched updates with D16 3.0 ms fallback. |
| **CL-47** | Cleanup | Removed leftover brief tags (P1-P8, P4, P6) from Sprint 6a box and Change Log entries CL-27 to CL-34. |
| **CL-48** | Roadmap | Fixed cosmetic typos in Sprint 3a box (closed parenthesis, added ShaderLoader) and Sprint 5b box (MaterialTable). |
| **CL-49** | Verification | Populated Section 4.1 with compact table of installed Ahwoo.BRUTAL.* packages read from Jatayu.csproj. |
| **CL-50** | Sprint 3b | Completed Sprint 3b: Implemented UploadManager, StagingRing, TimelineSemaphore, DeferredDeletionQueue, VramBudget, Reverse-Z depth conversion, NativeArena, NativePool, Profiler, AllocationTracker, GpuTimer, DebugNames, ImGuiVulkanBackend (dynamic rendering), DebugOverlay HUD, shaders, wired into Program.cs with 22/22 unit tests passing and hardware verification passing. |
| **CL-51** | Sprint 6a | Completed Sprint 6a: Implemented CellCoord (fast packing, FNV-1a GUIDs, distance metrics, neighbor iterator), CellFormat (binary structs for CookedCellHeader, instances, colliders, audio), WorldConstants, WorldConfig (rings, velocity lead, 900m prefetch cap), WorldCell lifecycle state machine, WorldGrid 2D sparse spatial hash grid, CellLoader memory-mapped decoder, CellCooker binary payload baker, tools/Jatayu.WorldGen/ procedural generator (.glb models & cell scatter into .jpak), StreamingManager (directional queue prioritization along velocity, 150m hysteresis, 1ms budget finalization), StreamingDebugOverlay 2D radar visualizer, and ForwardRenderer3D cell instance integration. 98/98 unit tests passing and hardware verification passing. |
| **CL-52** | Sprint 6b | Completed Sprint 6b: Implemented TerrainTile (256m tiles covering 2x2 cells, TileCoord = CellCoord / 2 per D15, bilinear height interpolation, origin shifting, .jtile binary codec), TerrainClipmap (concentric nested grid rings with continuous geomorphing, D22 unified renderer), TerrainMaterial (4-layer splat mapping, triplanar cliff blend parameters), TerrainHeightPyramid (Ring C far-field elevation mip pyramid), HeightfieldCooker (elevation bounds calculation, .jtile payload baking), HlodProxyCluster & HlodManager (Ring B 600m–2,000m proxy activation), HlodCooker (.jhlod binary cooker), ImpostorCooker (8-angle billboard atlas layout and view angle selection), TextureResidencyState & TextureStreamer (step-of-2 mip transitions per D21, permanent resident tail protection <= 64x64 per D5, atomic bindless swaps, VRAM pressure fallback), shaders (terrain.vert & terrain.frag), ForwardRenderer3D terrain pipeline and clipmap draw loop, and 15 new unit tests (113/113 passing) and RenderSmoke CHECK 7 verification passing. |
| **CL-53** | Sprint 7 | Planned Sprint 7 (GPU-Driven Rendering & Scalable Lighting): Structured into Phase 7a (InstanceBuffer, IndirectDrawBuffer, HiZBuilder, GpuCuller 5-view culling, multidraw indirect conversion) and Phase 7b (CascadedShadowMap, ClusteredLightCulling, IblProbe, FoliageSystem, TransparentPass, ParticleSystem). Updated progress tracking dashboard to 128 / 215 completed files (59.5%). |
| **CL-54** | Sprint 7a | Completed Sprint 7a: Implemented GPU-Driven Rendering Foundation. Unified 160-byte InstanceBuffer (Current & Previous transforms for continuous TAA velocity vectors per D25, BoundingCenter/Radius, MeshId, MaterialId, Flags, LodBias), multi-view IndirectDrawBuffer (5 views: Main Camera + 4 Cascades, 65,536 draws/view, vkCmdDrawIndexedIndirectCount support, CPU/GPU draw count reset, execution barrier), HiZBuilder with Reverse-Z MIN reduction sampler and compute shader hiz_build.comp, GpuCuller 5-view compute culler with cull.comp (5-plane frustum test, max draw distance clamp, LOD selection per D24, Reverse-Z Hi-Z occlusion test, atomic indirect draw compaction), multi-LOD GeometryPool with MeshLodRecord (48B) and MeshLodBuffer, and conversion of ForwardRenderer3D to vkCmdDrawIndexedIndirectCount with CPU draw fallback. Added GpuDrivenTests (121/121 unit tests passing) and hardware-verified CHECK 8 in Jatayu.RenderSmoke on NVIDIA GeForce RTX 5060 Ti. Updated dashboard to 134 / 215 files (62.3%). |
| **CL-55** | Sprint 7b | Completed and Accepted Sprint 7b: Scalable Lighting, Foliage, Particles & Transparency. End-to-end Vulkan runtime validation verified on NVIDIA GeForce RTX 5060 Ti: 4-cascade texel-snapped Reverse-Z shadows (2048x2048x4 D32_SFLOAT), 3D clustered light culling (16x8x24=3,072 clusters, 224 point lights + 32 spotlights), procedural environment, diffuse irradiance, GGX prefiltered specular IBL with 2D split-sum BRDF LUT, 47,707 procedural foliage grass instances with wind oscillation, 16,384 GPU compute particles with WBOIT accumulation/composition, and watertight 7-ring clipmap terrain with 200m flat origin clearing. Replaced dynamic window title updates with static title to prevent OS window message stalls and protect frame pacing. Measured reference scene: 755.3 FPS (1.32 ms/frame), 0.25 ms total GPU time (comfortably under 5.90 ms budget), 1.92 ms total CPU measured (submission 0.54 ms, under 1.0 ms budget). Stress mode (200,010 GPU instances): 2.87 ms total GPU time (under 5.90 ms budget), 0.25 ms CPU submission. 142/142 unit tests passing and all 9 RenderSmoke checks passing with zero validation or runtime errors. Sprint 7 is formally ACCEPTED. |
| **CL-56** | Sprint 8 | Added Vulkan-Engine as a conceptual reference for an integration scene using existing Jatayu lighting with physics and spatial audio; kept new lighting features and external code/assets out of Sprint 8. |
| **CL-57** | Sprint 7b | Cascaded shadow stability, texel snapping, quality presets, and cascade-dependent filter radius: Upgraded CSM texel snapping to round the sub-frustum bounding center in light space, eliminating sub-pixel crawl. Added ShadowQualityPreset (Standard, High, Ultra) and runtime diagnostics ([P] filter toggle, [G] cascade/depth/bias debug modes). Calibrated slope-scaled dynamic bias across terrain.frag and pbr.frag (capped at 0.002). Scaled 16-tap Poisson filter radius inversely with sqrt(cascadeScale) to prevent distant over-blurring. 148/148 unit tests passing and all 9 hardware RenderSmoke checks passing cleanly. |
| **CL-58** | Sprint 8 | Added Zero-Allocation Streaming Queue Pooling task to the end of Sprint 8 scope: eliminate transient main-thread GC allocations (e.g. PrioritizedLoadRequest queue items, dictionary queries, string interpolations) during high-speed/supersonic movement, ensuring AllocationTracker.ZeroAllocationPercent maintains a solid 100% steady state. |

