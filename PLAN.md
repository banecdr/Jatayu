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
 *   sub-millisecond CPU render submission and a 144 FPS budget (6.94 ms) on real scenes.
 *
 * HOW THE ENGINE ARCHITECTURE WORKS INTERNALLY:
 *   1. Core & Platform: Windowing (GLFW), high-precision GameTime clock, double-precision WorldPosition,
 *      origin rebasing, input actions, native memory pools, and multithreading Job System.
 *   2. Modern Vulkan 1.3 RHI: Hardware abstraction layer (VMA memory sub-allocation, multi-frame
 *      in-flight synchronization, swapchain recreation, dynamic rendering, timeline semaphores,
 *      async staging ring, deferred deletion queue, and bindless descriptor tables).
 *   3. Asset Pipeline & Offline Cooker: Tools cooker producing packed, memory-mapped archives (.jpak)
 *      with BC7/BC5 textures, mip streaming tails, meshlet/LOD geometry optimized via meshoptimizer,
 *      and packed binary cell records.
 *   4. World Grid & Region Streaming: 2D cell grid (128m cells), priority streaming, hysteresis radii,
 *      asynchronous loading jobs, main-thread GPU budget allocation, and HLOD proxies.
 *   5. Data-Oriented ECS & Scene: Archetype-based ECS for dynamic entities, cell-owned static instance
 *      arrays, camera-relative transformations, and spatial queries.
 *   6. GPU-Driven Renderer & Render Graph: Hi-Z depth pyramid occlusion culling (min-reduction),
 *      compute frustum (5-plane + max draw distance) / LOD culling, indirect multidraw
 *      (vkCmdDrawIndexedIndirectCount), cascaded shadow maps, clustered lighting, transient resource
 *      aliasing, and synchronization2 barriers.
 *   7. Environment Systems: Clipmap terrain with splat materials, GPU foliage instancing, atmospheric sky,
 *      time-of-day simulation, screen-space water, transparent passes, and GPU particles.
 *   8. Physics Engine: Jolt 3D rigid bodies, cell-streamed mesh/heightfield colliders, character controller,
 *      and deterministic origin-shift rebasing.
 *   9. Audio Engine: OpenAL 3D spatial sound emitters, cell-streamed audio sources, and origin-shift syncing.
 *  10. Diagnostics & Tooling: Embedded ImGui Vulkan dynamic rendering debug overlay (Sprint 3b),
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
                                       ┌──────────────────────────────┐
                                       │   RENDER GRAPH & PIPELINE    │
                                       │ (Pass DAG, Sync2 Barriers,   │
                                       │  Depth Pre-Pass, Hi-Z, Post) │
                                       └──────────────┬───────────────┘
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

### 1.1 Reverse-Z Depth & Infinite Far Plane (G7)
* **Depth buffer format:** `D32_SFLOAT`.
* **Clear value:** `0.0f` (near plane maps to `1.0f`, far plane maps to `0.0f`).
* **Depth comparison:** `VK_COMPARE_OP_GREATER_OR_EQUAL`.
* **Projection matrix:** Infinite far-plane perspective projection with inverted Y for Vulkan NDC:
  $$P_{00} = \frac{1}{\text{aspect} \cdot \tan(\text{fov} / 2)}, \quad P_{11} = -\frac{1}{\tan(\text{fov} / 2)}, \quad P_{22} = 0.0, \quad P_{23} = z_{\text{near}}, \quad P_{32} = -1.0$$
* Eliminates z-fighting across massive view distances (out to 50 km+).
* **Hi-Z Occlusion Convention:** Under reverse-Z, the farthest depth in any screen region is the **minimum** depth value. Hi-Z depth pyramids therefore use a **MIN** reduction filter, and sampling uses a min-reduction sampler (`VK_EXT_sampler_filter_minmax`, core in Vulkan 1.2).

### 1.2 Large-World Precision & Camera-Relative Rendering (G1)
* **Storage:** Absolute world positions are stored in double precision (`WorldPosition` with `double X, Y, Z`).
* **Camera-Relative CPU Transform:** Before uploading transforms to the GPU, object world positions are subtracted by the camera's double-precision position ($P_{\text{rel}} = P_{\text{obj}} - P_{\text{cam}}$) and cast to standard 32-bit `float3`.
* **Shaders:** Shaders never receive absolute world coordinates; view matrices have $(0, 0, 0)$ translation.
* **Culling Volume:** Frustum culling tests 5 planes (Left, Right, Bottom, Top, Near); far culling is governed by a separate configurable `MaxDrawDistance` (default: 8,000 m).
* **Panini Projection Scope:** Panini cylindrical projection is strictly a **demo-only option** for the cube sample (controlled via an off-by-default flag in push constants). It is excluded from the production GPU-driven rendering pipeline to avoid invalidating frustum culling, Hi-Z occlusion, and shadow projection.

### 1.3 Floating-Origin Rebasing Procedure (G1, G14)
When the camera travels beyond 2,000 m from the current physics/simulation origin, `OriginShift` coordinates a deterministic world rebase:
1. **Trigger Window:** Executes at a **frame boundary**, strictly after the physics step and before any queries, raycasts, or rendering commands for the next frame are recorded.
2. **Offset Snapping:** The shift offset is computed in double precision and snapped to an integer multiple of the world cell size (128 m), ensuring cell-local coordinates remain invariant.
3. **Subsystem Batch Updates:**
   - Jolt physics: Active and sleeping rigid bodies are translated by the negative offset; character controllers, kinematic bodies, and raycast caches are updated.
   - Spatial audio: AudioListener position and all active AudioSource emitter coordinates are translated.
   - ECS & Scene: Root entity transforms and active Camera `WorldPosition` are updated.
4. **Event Dispatch:** An `OriginShifted` event is broadcast through `EventBus` so any custom gameplay listeners can synchronize.
5. **Verification Rule:** Physics stacks must remain at rest across shifts with zero velocity spikes or millimeter-level drift.

### 1.4 Performance Discipline & Zero-Allocation Rule (G13)
* **Zero GC Heap Allocation in Hot Paths:** Per-frame render loops, culling, job submission, and physics ticking must produce **0 bytes of managed heap allocations** in steady state (`GC.GetAllocatedBytesForCurrentThread() == 0`).
* **Memory Primitives:** Structs, `Span<T>`, `ReadOnlySpan<T>`, `ArrayPool<T>`, `NativeMemory`, pooled buffers (`NativePool`), frame arenas (`NativeArena`), and pre-allocated arrays.
* **Disallowed in Hot Paths:** LINQ, closures, boxing, and per-frame string interpolations.
* **Target Frame Budget (6.94 ms for 144 FPS on Reference Scene):**

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
| **Debug Overlay (ImGui)** | 0.10 ms | Diagnostics HUD, metrics graphs |
| **Total CPU Budget** | **5.20 ms** | **Comfortably under 5.50 ms (192 FPS)** |

#### GPU Frame Budget (Target: 5.90 ms, leaving ~15% headroom for 144 FPS)
| GPU Pass | GPU Time Budget | Details |
| :--- | :--- | :--- |
| **Depth Pre-Pass & Hi-Z** | 0.50 ms | Reverse-Z pre-pass + min-reduction depth mip pyramid |
| **GPU Compute Culling** | 0.30 ms | Frustum + Hi-Z occlusion + indirect commands |
| **Shadow Cascades (CSM)** | 0.80 ms | 4 cascades with stable texel snapping & alpha-test |
| **Clustered Light Culling** | 0.30 ms | Compute light assignment to 3D cluster grid |
| **Opaque PBR + Terrain** | 2.00 ms | GPU-driven indirect multidraw & splat materials |
| **Foliage** | 0.50 ms | GPU-instanced wind vegetation with LOD cross-fade |
| **Water & Transparent** | 0.40 ms | Displacement waves, Fresnel, sorted transparency, particles |
| **Sky & Atmosphere** | 0.30 ms | Analytical scattering, aerial perspective fog |
| **Post-Processing Chain** | 0.70 ms | HDR tonemap (ACES), Bloom, TAA, SSAO |
| **Debug Overlay (ImGui)** | 0.10 ms | Real-time GPU metrics and streaming visualizer |
| **Total GPU Budget** | **5.90 ms** | **170 FPS (15% headroom under 6.94 ms target)** |

*Note: 1,000+ FPS claims apply only to trivial scenes (e.g. rotating cube). All open-world engine targets are measured against the Reference Scene defined in Section 7.*

### 1.5 Memory & GPU Resource Lifecycle (G4, G5)
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
| `src/Core/Math/WorldPosition.cs` | **PLANNED** | Double-precision 3D vector (`double X, Y, Z`), conversion to camera-relative `float3`, distance methods, SIMD alignment. |
| `src/Core/Math/OriginShift.cs` | **PLANNED** | Floating-origin coordinator. Snaps shift offset to cell multiples and broadcasts rebase events to Jolt, ECS, and audio at frame boundaries. |
| `src/Core/Memory/NativeArena.cs` | **PLANNED** | High-speed linear frame allocator allocating native memory with zero GC impact; reset once per frame. |
| `src/Core/Memory/NativePool.cs` | **PLANNED** | Pooled unmanaged buffer blocks recycling memory across streaming decodes and render passes. |
| `src/Core/Input/KeyCodes.cs` | **PLANNED** | Strongly typed C# enum mapping GLFW key and mouse codes into clean, game-ready names (`Key.W`, `Key.Space`, `MouseButton.Left`). |
| `src/Core/Input/Input.cs` | **PLANNED** | Central input polling system. Tracks current/previous button states, mouse delta, and scroll wheel. |
| `src/Core/Input/InputActions.cs` | **PLANNED** | Action/axis mapping abstraction with rebinding support ("MoveForward", "Jump", "LookX"). |
| `src/Core/Input/Gamepad.cs` | **PLANNED** | Gamepad/controller state reader (sticks, triggers, buttons) with deadzone normalization. |
| `src/Core/Events/EventBus.cs` | **PLANNED** | High-speed, allocation-free publish/subscribe event dispatcher for engine events (`WindowResized`, `OriginShifted`, `CellLoaded`). |
| `src/Core/Logging/Log.cs` | **PLANNED** | Formatted console & file logger wrapping `Ahwoo.BRUTAL.Logging` with colors and severity levels (`Info`, `Warn`, `Error`). |
| `src/Core/EngineConfig.cs` | **PLANNED** | Engine startup configuration record (Resolution, VSync, VRAM budget, CellSize, StreamingRadii, StagingRingMB, Cascades). |
| `src/Core/Config/Settings.cs` | **PLANNED** | User-facing graphics settings (shadow quality, draw distance, texture budget) stored in JSON. |
| `src/Core/Engine.cs` | **PLANNED** | Master engine coordinator. Initializes all subsystems, coordinates job threads, and runs the tick loop. |
| `src/Core/Save/SaveSystem.cs` | **PLANNED** | Serializes dynamic world state (player state, modified cell entities) separately from static cooked assets. |

### 2.2 Multithreading & Job System (`src/Core/Jobs/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Jobs/JobSystem.cs` | **PLANNED** | Work-stealing thread pool sized to physical cores minus 1-2. Supports priorities (`High`, `Normal`, `Background`) and dependencies. |
| `src/Core/Jobs/Job.cs` | **PLANNED** | Pooled unmanaged/struct job definition representing an executable delegate or function pointer. Zero allocation per task. |
| `src/Core/Jobs/JobHandle.cs` | **PLANNED** | Lightweight completion and dependency token for jobs with `.Wait()`, `.IsCompleted`, and `.Combine()`. |
| `src/Core/Jobs/ParallelFor.cs` | **PLANNED** | Batched parallel-for range partitioner for parallel execution of culling, animation, and data processing. |
| `src/Core/Jobs/MainThreadDispatcher.cs` | **PLANNED** | Lock-free queue for job results requiring main-thread execution (GPU resource finalization, scene mutation) with ms time budgeting. |

### 2.3 Diagnostics, Profiling & GPU Metrics (`src/Core/Profiling/` & `src/RHI/Vulkan/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Core/Profiling/Profiler.cs` | **PLANNED** | Lightweight CPU zone profiler with microsecond timing, frame markers, and optional Tracy integration. |
| `src/Core/Profiling/AllocationTracker.cs` | **PLANNED** | Per-frame heap allocation monitor (`GC.GetAllocatedBytesForCurrentThread()`) verifying zero GC allocations in hot paths. |
| `src/RHI/Vulkan/Profiling/GpuTimer.cs` | **PLANNED** | Hardware timestamp query pool wrapper. Measures per-pass GPU execution times with N-frame latency readback. |
| `src/RHI/Vulkan/Debug/DebugNames.cs` | **PLANNED** | Wraps `VK_EXT_debug_utils` to assign names and colors to Vulkan buffers, images, command labels, and queue regions. |

### 2.4 Modern Vulkan 1.3 RHI (`src/RHI/Vulkan/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/RHI/Vulkan/Context/QueueFamilies.cs` | **DONE** | Hardware queue scanner. Identifies Graphics, Presentation, Compute, and dedicated Transfer queue families. |
| `src/RHI/Vulkan/Context/VulkanContext.cs` | **DONE** | Boots Vulkan 1.3 `VkInstance`, creates window surface, scores discrete GPU, enables Dynamic Rendering, builds logical `VkDevice`. |
| `src/RHI/Vulkan/Memory/GpuAllocator.cs` | **DONE** | High-performance VRAM manager wrapping AMD's Vulkan Memory Allocator (VMA). Sub-allocates GPU buffers and textures. |
| `src/RHI/Vulkan/Memory/GpuBuffer.cs` | **DONE** | Object-oriented GPU memory buffer (`VkBuffer` + `VmaAllocation`). Handles direct host pointer mapping and SIMD uploads. |
| `src/RHI/Vulkan/Swapchain/VulkanSwapchain.cs` | **DONE** | Presentation engine. Manages double/triple buffering, sRGB formats, image views, acquire/present, and dynamic resizing. |
| `src/RHI/Vulkan/Sync/FrameSync.cs` | **DONE** | Multi-frame concurrency ($N = 2$ or $3$). Ring buffer of semaphores and fences allowing CPU/GPU overlap. |
| `src/RHI/Vulkan/Textures/DepthBuffer.cs` | **DONE** | Hardware depth attachment image (`D32_SFLOAT`). (Reverse-Z conversion pending, Sprint 3b). |
| `src/RHI/Vulkan/Textures/VulkanSampler.cs` | **PLANNED** | Sampler wrapper (Linear, Trilinear, Anisotropic 16x, Clamp, Repeat, and Min-Reduction mode for Hi-Z via `VK_EXT_sampler_filter_minmax`). |
| `src/RHI/Vulkan/Textures/GpuTexture.cs` | **PLANNED** | 2D/Array GPU texture backed by VMA with mipmaps, format layouts, and image view management. |
| `src/RHI/Vulkan/Commands/CommandPoolManager.cs`| **PLANNED** | Command pool manager allocating primary and secondary command buffers per frame and per thread. |
| `src/RHI/Vulkan/Pipelines/ShaderCompiler.cs` | **PLANNED** | Google ShaderC wrapper compiling GLSL to SPIR-V bytecode with disk-based bytecode caching. |
| `src/RHI/Vulkan/Pipelines/VulkanPipeline.cs` | **PLANNED** | Encapsulates `VkPipeline` and `VkPipelineLayout` with dynamic rendering depth/color attachment states. |
| `src/RHI/Vulkan/Pipelines/PipelineBuilder.cs` | **PLANNED** | Fluent builder for graphics, compute, and mesh-shader pipelines without boilerplate structs. |
| `src/RHI/Vulkan/Descriptors/DescriptorLayout.cs` | **PLANNED** | Descriptor set layout wrapper for non-bindless passes (ImGui, compute dispatch tables). |
| `src/RHI/Vulkan/Descriptors/DescriptorAllocator.cs`| **PLANNED** | Pool allocator for `VkDescriptorSet` recycling sets across frames. |
| `src/RHI/Vulkan/Descriptors/BindlessTable.cs` | **PLANNED** | Global bindless descriptor array (`VK_EXT_descriptor_indexing`) binding thousands of textures to uniform shader arrays. |
| `src/RHI/Vulkan/Upload/UploadManager.cs` | **PLANNED** | Asynchronous GPU transfer coordinator using dedicated transfer queue, staging ring, and ownership transfers. |
| `src/RHI/Vulkan/Upload/StagingRing.cs` | **PLANNED** | Persistently mapped 64-256 MB circular staging buffer with head/tail tracking for zero-copy CPU-to-GPU uploads. |
| `src/RHI/Vulkan/Upload/UploadRequest.cs` | **PLANNED** | Struct describing buffer/image copy request, mip ranges, and target subresources. |
| `src/RHI/Vulkan/Sync/TimelineSemaphore.cs` | **PLANNED** | Vulkan 1.3 timeline semaphore wrapper for fine-grained async transfer queue synchronization. |
| `src/RHI/Vulkan/Memory/DeferredDeletionQueue.cs` | **PLANNED** | Retires GPU objects safely after $N$ in-flight frames have completed on the GPU timeline. |
| `src/RHI/Vulkan/Memory/VramBudget.cs` | **PLANNED** | Queries `VK_EXT_memory_budget` to track physical VRAM usage, budget limits, and streaming pressure. |

### 2.5 Asset Pipeline, Texture Streaming & Cooker (`src/Assets/` & `tools/Jatayu.Cooker/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Assets/AssetGuid.cs` | **PLANNED** | 64-bit fast hash/GUID identifying assets uniquely across packages and runtime scenes. |
| `src/Assets/PackReader.cs` | **PLANNED** | High-speed memory-mapped reader for `.jpak` binary asset packages with table-of-contents seek. |
| `src/Assets/CookedFormats.cs` | **PLANNED** | Shared binary structs: CookedMeshHeader, CookedTextureHeader, CookedMaterialRecord, CookedCellRecord. |
| `src/Assets/AssetManager.cs` | **PLANNED** | Async asset registry: resolves GUIDs to loaded assets, reference-counting, and evicting unused memory. |
| `src/Assets/Textures/TextureStreamer.cs` | **PLANNED** | Mip streaming coordinator: determines required mip levels based on screen coverage and issues upload/eviction requests. |
| `src/Assets/Textures/MipResidency.cs` | **PLANNED** | Tracks loaded mip ranges per texture and manages min-LOD clamps in bindless descriptor tables. |
| `src/Assets/Loaders/TextureLoader.cs` | **PLANNED** | Importer using `BRUTAL.Stb` and `BRUTAL.Ktx` for cooker tools and dev hot-reloading. |
| `src/Assets/Loaders/ModelLoader.cs` | **PLANNED** | glTF 2.0 importer using `BRUTAL.Gltf` parsing meshes, hierarchies, and materials for cooking. |
| `src/Assets/Loaders/ShaderLoader.cs` | **PLANNED** | Runtime shader file watcher for live editing and hot-reloading during development. |
| `tools/Jatayu.Cooker/Program.cs` | **PLANNED** | CLI asset cooker entry point (commands: cook-all, cook-models, cook-textures, cook-world). |
| `tools/Jatayu.Cooker/MeshCooker.cs` | **PLANNED** | Optimizes vertices with `Ahwoo.BRUTAL.MeshOptimizer` (cache, overdraw, fetch), builds LOD chain, quantizes. |
| `tools/Jatayu.Cooker/TextureCooker.cs` | **PLANNED** | Compresses textures to BC7/BC5/BC4 with full mipmaps; splits resident mip tail ($\le 64\times 64$) from streamable mips. |
| `tools/Jatayu.Cooker/MaterialCooker.cs` | **PLANNED** | Packages PBR material parameters and texture GUIDs into binary descriptor blobs. |
| `tools/Jatayu.Cooker/CellCooker.cs` | **PLANNED** | Bakes world cells into binary chunk records inside `.jpak`: static instance arrays, physics colliders, audio points. |
| `tools/Jatayu.Cooker/PackWriter.cs` | **PLANNED** | Packs cooked binary assets and cells into contiguous, memory-mapped `.jpak` archives with TOC headers. |
| `tools/Jatayu.Cooker/HeightfieldCooker.cs` | **PLANNED** | Processes 16-bit heightmaps into tiled terrain meshes, clipmap pyramids, and Jolt heightfield colliders. |

### 2.6 World Grid & Region Streaming (`src/World/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/World/WorldConfig.cs` | **PLANNED** | World configuration (CellSize: 128m, LoadRadius: 600m, UnloadRadius: 750m, MaxDrawDistance: 8000m, Ring distances). |
| `src/World/CellCoord.cs` | **PLANNED** | Integer coordinate pair `(int X, int Z)` with fast hash, distance metrics, and neighbor iterators. |
| `src/World/WorldCell.cs` | **PLANNED** | Cell state machine (`Unloaded` → `LoadingIO` → `LoadingDecode` → `ReadyToFinalize` → `Active` → `Unloading`). Owns static instance data. |
| `src/World/WorldGrid.cs` | **PLANNED** | Sparse 2D hash grid storing active and dormant cells. Fast spatial lookup by `WorldPosition`. |
| `src/World/StreamingManager.cs` | **PLANNED** | Central streaming controller: evaluates player position/velocity, prioritizes loading queue, enforces time budget. |
| `src/World/CellLoader.cs` | **PLANNED** | Job system worker reading cooked cell records from `.jpak`, parsing instances, and preparing GPU upload payloads. |
| `src/World/CellFormat.cs` | **PLANNED** | Binary structure specifications for packed cell records (Header, InstanceTable, ColliderTable, AudioTable). |
| `src/World/Hlod.cs` | **PLANNED** | Hierarchical LOD manager: swaps distant cell clusters with simplified proxy meshes and impostors. |
| `src/World/StreamingDebugOverlay.cs` | **PLANNED** | Visual debug grid displaying cell states (Green=Active, Yellow=Loading, Red=Unloading), memory, and queue depths. |

#### Distance Rings Configuration Table (`WorldConfig`)
| Ring | Distance Range | Content & Subsystems Active | Physics Colliders |
| :--- | :--- | :--- | :--- |
| **Ring A (Near)** | $0\,\text{m} \le d \le 600\,\text{m}$ | Full detail static meshes, dynamic ECS entities, foliage, audio emitters | **Yes** (Jolt bodies active) |
| **Ring B (Mid)** | $600\,\text{m} < d \le 2,000\,\text{m}$ | HLOD proxy meshes, simplified materials, audio emitters | **No** (Colliders unstreamed) |
| **Ring C (Far)** | $2,000\,\text{m} < d \le 8,000\,\text{m}$ | Terrain clipmap geometry, sky/atmosphere, distant water | **No** (Visual only) |
*Streaming Hysteresis: Cells load when entering 600 m radius and unload only after crossing 750 m (150 m hysteresis band).*

### 2.7 Terrain System (`src/Terrain/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Terrain/TerrainTile.cs` | **PLANNED** | Represents a 256m terrain chunk (covering 2x2 cells): height data, normal maps, splat weight textures, Jolt shape handle. |
| `src/Terrain/TerrainClipmap.cs` | **PLANNED** | GPU clipmap / quadtree LOD renderer with continuous geomorphing to prevent popping and edge cracks out to 8 km. |
| `src/Terrain/TerrainMaterial.cs` | **PLANNED** | 4-8 layer splat-map material shader parameters with triplanar blending for steep cliff faces. |

### 2.8 Render Graph & Frame Scheduling (`src/Renderer/Graph/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/Graph/RenderGraph.cs` | **PLANNED** | Frame render graph DAG. Passes declare read/write dependencies; compiles barriers, layout transitions, and aliases memory. |
| `src/Renderer/Graph/RenderPass.cs` | **PLANNED** | Execution node in the render graph with setup and execute callbacks receiving command buffers. |
| `src/Renderer/Graph/RenderGraphResource.cs` | **PLANNED** | Virtual handle for transient/imported textures and buffers tracked by the graph for synchronization. |
| `src/Renderer/Graph/BarrierBuilder.cs` | **PLANNED** | Automatic `VkDependencyInfo` and `VkImageMemoryBarrier2` generator using Vulkan 1.3 `synchronization2`. |

### 2.9 GPU-Driven Rendering (`src/Renderer/GpuDriven/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/GpuDriven/GeometryPool.cs` | **PLANNED** | Monolithic shared vertex/index GPU memory pool enabling draw calls across all static meshes without rebinding buffers. |
| `src/Renderer/GpuDriven/InstanceBuffer.cs` | **PLANNED** | GPU storage buffer holding per-instance data: camera-relative float transform, mesh ID, material ID, bounding sphere. |
| `src/Renderer/GpuDriven/IndirectDrawBuffer.cs` | **PLANNED** | Indirect command buffer populated by compute shader for `vkCmdDrawIndexedIndirectCount` draws. |
| `src/Renderer/GpuDriven/GpuCuller.cs` | **PLANNED** | Compute shader dispatcher: executes 5-plane frustum culling, max draw distance clamp, and Hi-Z min-reduction occlusion. |
| `src/Renderer/GpuDriven/HiZBuilder.cs` | **PLANNED** | Generates hierarchical depth mip pyramid (Hi-Z) from the depth pre-pass using min-reduction filters. |

### 2.10 3D Graphics, Lighting & Environment (`src/Renderer/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/Camera/Camera3D.cs` | **DONE** | 3D Perspective camera. Computes View, Projection, and camera-relative transformation matrices. |
| `src/Renderer/Camera/CameraController.cs` | **DONE** | Smooth camera controller supporting first-person free-look, orbit, pitch/yaw, and keyboard navigation. |
| `src/Renderer/Camera/Frustum.cs` | **PLANNED** | Extracts 5 bounding planes (Left, Right, Bottom, Top, Near) plus configurable max draw distance for CPU-side coarse culling. |
| `src/Renderer/Camera/DepthConvention.cs` | **PLANNED** | Canonical depth settings and matrices (Reverse-Z flag, infinite projection builder, clear depth constants). |
| `src/Renderer/Mesh/Vertex3D.cs` | **DONE** | Standard 3D PBR vertex struct: Position (`float3`), Normal (`float3`), Tangent (`float4`), UV (`float2`), and Color (`float4`). |
| `src/Renderer/Mesh/Mesh.cs` | **PLANNED** | GPU geometry container holding vertex/index buffer slices, triangle count, and bounding box. |
| `src/Renderer/Mesh/Model.cs` | **PLANNED** | Multi-mesh 3D model container containing sub-meshes, materials, and local node hierarchies. |
| `src/Renderer/Material/Material.cs` | **PLANNED** | PBR material binding albedo, normal, metallic-roughness, AO, emissive textures and scalar factors. |
| `src/Renderer/Material/MaterialTable.cs` | **PLANNED** | GPU storage buffer containing all active material parameters indexed by material ID for bindless draws. |
| `src/Renderer/Lighting/DirectionalLight.cs` | **PLANNED** | Sunlight parameters: Direction, Color, Intensity, Cascaded shadow configuration. |
| `src/Renderer/Lighting/PointLight.cs` | **PLANNED** | Point light parameters: Camera-relative position, Color, Intensity, Attenuation radius. |
| `src/Renderer/Lighting/SpotLight.cs` | **PLANNED** | Spot light parameters: Position, Direction, Inner/Outer cone angles, Color, Intensity. |
| `src/Renderer/Lighting/LightEnvironment.cs` | **PLANNED** | Scene lighting container holding sun light, ambient sky light, and active clustered light arrays. |
| `src/Renderer/Lighting/ClusteredLightCulling.cs`| **PLANNED** | 3D view-frustum cluster grid allocator and compute pass assigning point/spot lights to 3D clusters. |
| `src/Renderer/Lighting/IblProbe.cs` | **PLANNED** | Image-based lighting probe: diffuse irradiance cubemap, specular prefiltered reflection map, BRDF LUT. |
| `src/Renderer/Shadows/ShadowMap.cs` | **PLANNED** | Depth render target and light view-projection for single directional/spot shadow passes. |
| `src/Renderer/Shadows/CascadedShadowMap.cs` | **PLANNED** | 4-cascade shadow map renderer with texel snapping to prevent edge shimmering during camera translation. |
| `src/Renderer/Shadows/ShadowCascade.cs` | **PLANNED** | Individual cascade bounds, projection matrix, split distance, and depth bias calculations. |
| `src/Renderer/Vegetation/FoliageSystem.cs` | **PLANNED** | GPU instancing manager for grass, shrubs, and trees with wind vertex animation and distance LOD dithering. |
| `src/Renderer/Vegetation/FoliageInstanceData.cs`| **PLANNED** | Packed instance representation (16 bytes: position offset, scale, rotation, color tint). |
| `src/Renderer/Transparent/TransparentPass.cs` | **PLANNED** | Sorted or weighted-blended order-independent transparency pass for glass, effects, and particles. |
| `src/Renderer/Particles/ParticleSystem.cs` | **PLANNED** | GPU particle simulation (compute update, indirect draw) with soft particle depth fading. |
| `src/Renderer/Sky/Atmosphere.cs` | **PLANNED** | Analytical Rayleigh/Mie atmospheric scattering shader simulation and aerial perspective fog. |
| `src/Renderer/Sky/TimeOfDay.cs` | **PLANNED** | 24-hour sun/moon orbit simulation driving directional light vector, sky color, and ambient tones. |
| `src/Renderer/Skybox/Skybox.cs` | **PLANNED** | Procedural atmospheric or cubemap background drawn at reverse-Z infinity ($Z = 0.0$). |
| `src/Renderer/Water/WaterRenderer.cs` | **PLANNED** | Planar/tiled water surface with depth absorption, Gerstner wave displacement, normal blending, and screen refraction. |
| `src/Renderer/ForwardRenderer3D.cs` | **PLANNED** | High-performance GPU-driven forward renderer coordinating render graph passes and presenting to swapchain. |
| `src/Renderer/Debug/DebugRenderer.cs` | **PLANNED** | 3D wireframe line, box, sphere, and frustum renderer for visualizing physics colliders and streaming bounds. |

### 2.11 Post-Processing Pipeline (`src/Renderer/PostProcess/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Renderer/PostProcess/PostProcessChain.cs` | **PLANNED** | Orchestrates post-process passes via render graph attachments in HDR `R16G16B16A16_SFLOAT`. |
| `src/Renderer/PostProcess/Tonemap.cs` | **PLANNED** | ACES / AgX filmic tonemapping, auto-exposure adaptation, color grading LUT, and gamma correction. |
| `src/Renderer/PostProcess/Bloom.cs` | **PLANNED** | Downsample/upsample progressive blur pyramid with threshold knee and energy conservation. |
| `src/Renderer/PostProcess/Taa.cs` | **PLANNED** | Temporal Anti-Aliasing with jittered projection matrices, motion vector reprojection, and variance clipping. |
| `src/Renderer/PostProcess/Ssao.cs` | **PLANNED** | Screen-Space Ambient Occlusion (or GTAO) with depth-aware bilateral blur. |

### 2.12 Scene & Entity Architecture / ECS (`src/Scene/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Scene/Transform.cs` | **PLANNED** | Local float transform (Position, Rotation, Scale) + `WorldPosition` reference for root entities. |
| `src/Scene/Entity.cs` | **PLANNED** | Lightweight numeric entity ID handle indexing into archetype component storage. |
| `src/Scene/Scene.cs` | **PLANNED** | Container managing active ECS world, dynamic entities, spatial hierarchy, and render submission. |
| `src/Scene/Components/MeshRenderer.cs` | **PLANNED** | Struct component referencing mesh ID and material ID for dynamic entities. |
| `src/Scene/Components/LightComponent.cs` | **PLANNED** | Struct component attaching directional, point, or spot light properties to an entity. |
| `src/Scene/Components/CameraComponent.cs` | **PLANNED** | Struct component marking the active viewport camera entity. |
| `src/Scene/Components/RigidBodyComponent.cs` | **PLANNED** | Struct component binding an entity to a Jolt simulation body handle. |

### 2.13 Physics Engine (`src/Physics/` via BRUTAL.Jolt)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Physics/PhysicsEngine.cs` | **PLANNED** | Master Jolt physics wrapper. Manages broadphase layers, gravity, origin rebasing, and fixed-timestep ticking. |
| `src/Physics/PhysicsBody.cs` | **PLANNED** | 3D rigid body wrapper (Static, Kinematic, Dynamic; Velocity, Restitution, Friction, Mass). |
| `src/Physics/Colliders/Collider.cs` | **PLANNED** | Base class for collision shapes. |
| `src/Physics/Colliders/BoxCollider.cs` | **PLANNED** | Box collision shape defined by half-extents. |
| `src/Physics/Colliders/SphereCollider.cs` | **PLANNED** | Sphere collision shape defined by radius. |
| `src/Physics/Colliders/CapsuleCollider.cs` | **PLANNED** | Capsule collision shape for characters. |
| `src/Physics/Colliders/MeshCollider.cs` | **PLANNED** | Static mesh / convex hull collider streamed per world cell. |
| `src/Physics/CharacterController.cs` | **PLANNED** | Kinematic virtual character controller for player movement, stairs, slope limits, and jumping. |
| `src/Physics/RaycastHit.cs` | **PLANNED** | Raycast query result struct containing Hit, Position, Normal, Distance, and Body ID. |

### 2.14 Audio Engine (`src/Audio/` via BRUTAL.OpenAl)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Audio/AudioEngine.cs` | **PLANNED** | OpenAL context, device management, master gain, and 3D spatial coordinate updates. |
| `src/Audio/SoundClip.cs` | **PLANNED** | Shared in-memory decoded audio buffer (WAV/OGG) reference-counted across emitters. |
| `src/Audio/AudioSource.cs` | **PLANNED** | 3D positional audio emitter with attenuation curves, pitch, volume, and looping. |
| `src/Audio/AudioListener.cs` | **PLANNED** | Player audio receptor syncing world position and forward/up vectors with active camera. |

### 2.15 AI, Navigation & Animation (`src/AI/`, `src/Animation/`, `src/UI/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/AI/NavMesh.cs` | **PLANNED** | World navigation mesh manager supporting tiled navmesh queries across streamed cells. |
| `src/AI/NavMeshAgent.cs` | **PLANNED** | AI pathfinding agent following waypoints with avoidance and velocity control. |
| `src/AI/NavMeshTile.cs` | **PLANNED** | Cell-sized baked navigation tile loaded/unloaded alongside world cells. |
| `src/Animation/Skeleton.cs` | **PLANNED** | Bone hierarchy, bind poses, and inverse bind matrices parsed from glTF. |
| `src/Animation/AnimationClip.cs` | **PLANNED** | Skeletal animation keyframes (translation, rotation, scale) with interpolation. |
| `src/Animation/SkinningSystem.cs` | **PLANNED** | Computes final bone matrix palettes and dispatches GPU compute/vertex skinning. |
| `src/UI/Canvas.cs` | **PLANNED** | In-game HUD/UI container with anchor layout system independent of editor ImGui. |
| `src/UI/FontRenderer.cs` | **PLANNED** | Signed Distance Field (SDF) text rendering pipeline using `Ahwoo.BRUTAL.FreeType`. |

### 2.16 Developer Tooling, Diagnostics & Standalone Editor (`src/Editor/`)
| File Path | Status | Plain-English Role & Key Responsibilities |
| :--- | :---: | :--- |
| `src/Editor/ImGuiVulkanBackend.cs` | **PLANNED** | Custom Vulkan 1.3 Dynamic Rendering backend for Dear ImGui. Zero legacy render passes. (Sprint 3b). |
| `src/Editor/DebugOverlay.cs` | **PLANNED** | Lightweight diagnostics HUD: FPS, CPU/GPU pass times, heap allocations, VRAM bar, streaming map, camera free-fly. (Sprint 3b). |
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

### 2.17 Automated Testing, Validation & CI Infrastructure
| File Path | Status | Purpose & Role |
| :--- | :---: | :--- |
| `tests/Jatayu.Tests/Jatayu.Tests.csproj` | **PLANNED** | xUnit unit test suite covering math, origin shift, job system, staging ring, pack reader, and cell state machines without GPU requirements. (Sprint 3a). |
| `tests/Jatayu.RenderSmoke/Program.cs` | **PLANNED** | Automated offscreen/hidden-window Vulkan smoke test verifying zero validation errors and frame budget thresholds. (Sprint 3a). |
| `.github/workflows/ci.yml` | **PLANNED** | Continuous integration pipeline: build, test, and run validation smoke tests. (Sprint 3a). |
| `Directory.Build.props` | **PLANNED** | Solution-wide compiler settings (`TreatWarningsAsErrors=true`, nullable enabled, analyzers). (Sprint 3a). |
| `docs/RENDER_CONVENTIONS.md` | **PLANNED** | Canonical reference documenting depth, handedness, matrix layout, units, and shader bindings. (Sprint 3a). |

### 2.18 Asset Shaders (`assets/shaders/`)
| File Path | Status | Purpose |
| :--- | :---: | :--- |
| `assets/shaders/triangle.vert` | **DONE** | 2D test vertex shader. |
| `assets/shaders/triangle.frag` | **DONE** | 2D test fragment shader. |
| `assets/shaders/cube.vert` | **DONE** | 3D cube vertex shader (push constants: MVP + demo-only Panini option). |
| `assets/shaders/cube.frag` | **DONE** | 3D cube fragment shader with directional diffuse normal shading. |
| `assets/shaders/pbr.vert` | **PLANNED** | Camera-relative vertex shader evaluating world normals, tangents, and shadow coordinates. |
| `assets/shaders/pbr.frag` | **PLANNED** | Cook-Torrance microfacet specular BRDF, metallic-roughness, normal mapping, cascaded shadows. |
| `assets/shaders/shadow.vert` | **PLANNED** | Depth-only vertex shader for directional cascaded shadow map passes. |
| `assets/shaders/shadow.frag` | **PLANNED** | Depth-only fragment shader supporting alpha-testing for foliage shadow casting. |
| `assets/shaders/skybox.vert` | **PLANNED** | Fullscreen triangle vertex shader rendering background at reverse-Z depth = 0.0. |
| `assets/shaders/skybox.frag` | **PLANNED** | Procedural gradient sky or prefiltered cubemap shader. |
| `assets/shaders/debug_line.vert`| **PLANNED** | 3D debug wireframe vertex shader. |
| `assets/shaders/debug_line.frag`| **PLANNED** | Unlit flat-color fragment shader for physics debug lines and bounds. |
| `assets/shaders/cull.comp` | **PLANNED** | Compute shader for 5-plane frustum culling, max draw distance clamp, Hi-Z min-reduction occlusion, and indirect command generation. |
| `assets/shaders/hiz_build.comp` | **PLANNED** | Compute shader generating hierarchical depth mip pyramid using MIN reduction for reverse-Z. |
| `assets/shaders/light_cull.comp`| **PLANNED** | Compute shader dividing frustum into 3D clusters and assigning point/spot light indices. |
| `assets/shaders/particle.vert` | **PLANNED** | GPU particle billboard vertex shader with size attenuation. |
| `assets/shaders/particle.frag` | **PLANNED** | Soft-particle fragment shader with depth fade against depth buffer. |
| `assets/shaders/lod_dither.glsl`| **PLANNED** | GLSL include file providing screen-door stipple transparency for seamless LOD cross-fading. |
| `assets/shaders/terrain.vert` | **PLANNED** | Terrain clipmap vertex shader with continuous geomorphing and normal generation from heightmaps. |
| `assets/shaders/terrain.frag` | **PLANNED** | Multi-layer splat material shader with triplanar projection for cliff slopes. |
| `assets/shaders/foliage.vert` | **PLANNED** | GPU-instanced vegetation vertex shader with procedural wind vertex oscillation. |
| `assets/shaders/foliage.frag` | **PLANNED** | Foliage fragment shader with alpha-to-coverage and subsurface scattering approximation. |
| `assets/shaders/atmosphere.frag`| **PLANNED** | Analytical Rayleigh/Mie scattering atmosphere and sun disc shader. |
| `assets/shaders/water.vert` | **PLANNED** | Water surface displacement shader using Gerstner waves. |
| `assets/shaders/water.frag` | **PLANNED** | Water lighting with depth color gradient, normal blending, Fresnel reflection and refraction. |
| `assets/shaders/post_tonemap.frag`| **PLANNED** | ACES / AgX filmic tonemapping, vignette, and dithering shader. |
| `assets/shaders/post_bloom.frag`| **PLANNED** | Dual-filtering progressive blur upsample/downsample shader. |
| `assets/shaders/post_taa.frag` | **PLANNED** | Temporal anti-aliasing shader with history reprojection and neighborhood clamping. |
| `assets/shaders/post_ssao.frag` | **PLANNED** | Screen-space ambient occlusion shader with depth-aware spatial filtering. |

---

## 3. Progress Tracking Dashboard

- **Total Planned Files:** 194
- **Total Completed Files:** 16
- **Current Completion:** **8.2% (Sprint 2 Complete, Sprint 3a Ready)**

| Subsystem | Completed / Planned | Status |
| :--- | :---: | :--- |
| **Core Platform, Memory & Config** | 2 / 16 | In Progress |
| **Multithreading & Jobs** | 0 / 5 | Planned (Sprint 4) |
| **Diagnostics & Profiling** | 0 / 4 | Planned (Sprint 3b) |
| **Modern Vulkan 1.3 RHI** | 7 / 22 | **DepthBuffer Complete** |
| **Asset Pipeline, Mip Streaming & Cooker** | 0 / 16 | Planned (Sprint 3a-6) |
| **World Grid & Streaming** | 0 / 9 | Planned (Sprint 6) |
| **Terrain System** | 0 / 3 | Planned (Sprint 6) |
| **Render Graph** | 0 / 4 | Planned (Sprint 4) |
| **GPU-Driven Rendering** | 0 / 5 | Planned (Sprint 7a) |
| **3D Graphics, Lighting & Environment** | 3 / 28 | **Camera3D & Vertex3D Complete** |
| **Post-Processing** | 0 / 5 | Planned (Sprint 9) |
| **Scene & ECS Architecture** | 0 / 7 | Planned (Sprint 5) |
| **Physics (BRUTAL.Jolt)** | 0 / 9 | Planned (Sprint 8) |
| **Audio (BRUTAL.OpenAL)** | 0 / 4 | Planned (Sprint 8) |
| **AI, Navigation & Animation** | 0 / 8 | Planned (Sprint 10) |
| **Editor & Tooling Overlay** | 0 / 15 | Planned (Sprint 3b, 11) |
| **Testing, Validation & CI** | 0 / 5 | Planned (Sprint 3a) |
| **Asset Shaders** | 4 / 29 | **cube.vert, cube.frag Complete** |

---

## 4. Architectural Decisions Log & Resolutions

* **D1 (Texture Compression in Cooker):**
  * *Resolution:* Use a native `bc7enc` (or `ispc_texcomp`) encoder wrapped via P/Invoke within `tools/Jatayu.Cooker/`. Stb and Ktx alone in BRUTAL do not encode BC7/BC5 offline. Provide a fast low-quality iteration mode for rapid development and high-quality production mode.
* **D2 (ECS Implementation):**
  * *Resolution:* Adopt **Arch** (MIT license). Archetype-based ECS with struct components, zero-allocation queries (`QueryDescription`), native memory chunks, parallel query support, and verified .NET 10 compatibility. A 100k entity query spike will be verified in Sprint 5.
* **D3 (Cell Sizing):**
  * *Resolution:* **128 m** world cells; terrain tiles cover $2 \times 2$ cells (256 m). Configurable in `WorldConfig`.
* **D4 (Distance Streaming Rings):**
  * *Resolution:*
    * Ring A ($0 \le d \le 600\,\text{m}$): Full detail meshes, colliders, audio, foliage.
    * Ring B ($600\,\text{m} < d \le 2,000\,\text{m}$): HLOD proxy meshes, audio/foliage, no physics colliders.
    * Ring C ($2,000\,\text{m} < d \le 8,000\,\text{m}$): Terrain clipmap and sky/atmosphere only.
    * Load radius = 600 m; unload radius = 750 m (150 m hysteresis band).
* **D5 (Texture Residency Strategy):**
  * *Resolution:* Mip streaming via `TextureStreamer`. Small mip tails ($\le 64\times 64$) are permanently resident; streamable higher mips are loaded/evicted dynamically under `VramBudget` constraints.
* **D6 (Max Draw Distance):**
  * *Resolution:* **8,000 m (8 km)** default, configurable in `WorldConfig`. Governs the far boundary of the 5-plane frustum cull and terrain clipmap outer ring.
* **D7 (Automated Testing Framework):**
  * *Resolution:* **xUnit** in `tests/Jatayu.Tests/` targeting .NET 10 for all non-GPU engine logic, combined with `tests/Jatayu.RenderSmoke/` for headless/offscreen Vulkan validation smoke tests.

---

## 5. Reference Scene for All Performance Measurements

To maintain rigorous, objective performance validation across sprints, all frame-budget claims are evaluated against a standardized **Reference Scene**:
* **World Dimensions:** 16 km $\times$ 16 km streamed world.
* **Geometry:** Procedural clipmap terrain, 200,000 visible static instances (rocks, buildings, props), 50,000 foliage instances in view.
* **Lighting:** 1 directional sunlight with 4 cascaded shadow map splits, 256 active clustered point/spot lights.
* **Camera Dynamics:** Camera traversing the world at 100 m/s across multiple cell boundaries.
* **Resolution & Presentation:** 1920 $\times$ 1080 resolution, V-Sync OFF.
* **Hardware Reporting:** Every measurement report must state the GPU model, driver version, CPU, and measured scene. (Until the Reference Scene is fully built, intermediate sprint reports will explicitly state the interim test scene used).

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
│ SPRINT 3a: Textures and Pipelines [NEXT]                                         │
│  - GpuTexture, VulkanSampler (including min-reduction mode for Hi-Z)             │
│  - TextureLoader (importer), ShaderCompiler (with SPIR-V disk cache)             │
│  - VulkanPipeline & PipelineBuilder (Dynamic Rendering color/depth)              │
│  - CommandPoolManager per-frame allocation, DescriptorLayout, DescriptorAlloc    │
│  - Test projects (Jatayu.Tests, Jatayu.RenderSmoke), CI workflow, Build props    │
│  - docs/RENDER_CONVENTIONS.md canonical documentation                            │
│  - Result: Textured cube rendered via modular pipeline, validation-clean, CI     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 3b: GPU Foundation and Diagnostics                                        │
│  - UploadManager, StagingRing (64-256 MB), UploadRequest, TimelineSemaphore      │
│  - DeferredDeletionQueue (safe resource retirement after N frames in flight)     │
│  - VramBudget (VK_EXT_memory_budget query wrapper)                               │
│  - Reverse-Z conversion (DepthConvention, Camera3D, DepthBuffer, shaders)       │
│  - NativeArena, NativePool zero-allocation native memory backings                │
│  - Profiler, GpuTimer (timestamps), DebugNames (VK_EXT_debug_utils)              │
│  - AllocationTracker (enforces zero-allocation steady state)                     │
│  - ImGuiVulkanBackend & DebugOverlay (FPS, pass timings, allocations, VRAM)      │
│  - Result: Textured cube with async uploads, reverse-Z, live diagnostics HUD     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 4: Jobs, Cooking & Render Graph                                           │
│  - G3: JobSystem, Job, JobHandle, ParallelFor, MainThreadDispatcher              │
│  - G5: Cooker tool (Jatayu.Cooker), MeshCooker (meshoptimizer), TextureCooker    │
│  - G5: PackWriter/PackReader (.jpak memory-mapped archives), CookedFormats       │
│  - G10: RenderGraph (Pass DAG, synchronization2 barriers, depth pre-pass, opaque)│
│  - ModelLoader as offline importer; AssetManager for cooked runtime assets       │
│  - Result: glTF model cooked offline, streamed from .jpak, rendered via graph    │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 5: Scene, ECS, Input & Large-World Foundations                            │
│  - G12: Archetype ECS architecture with struct components, Scene & Entity rework │
│  - G1: WorldPosition (double), Camera-Relative Rendering, OriginShift (5.4)      │
│  - Frustum culling (5 planes + max draw distance clamp)                          │
│  - Input system (KeyCodes, Input polling, InputActions, Gamepad)                 │
│  - Engine & EngineConfig, Settings, EventBus, Log                                │
│  - PBR shaders (pbr.vert, pbr.frag), Material, MaterialTable, DirectionalLight   │
│  - Result: PBR scene rendered at 50,000m from origin with zero vertex jitter     │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 6: World Grid, Streaming, Terrain & Texture Streaming                     │
│  - G2: WorldConfig (128m cells, 600m/750m radii), CellCoord, WorldCell           │
│  - G2: StreamingManager (priority queue, hysteresis radii, budgeted loading)    │
│  - G2: CellCooker, CellLoader, HLOD proxy mesh management, StreamingDebugOverlay │
│  - G8: TerrainTile (256m), TerrainClipmap (geomorphing), TerrainMaterial splat   │
│  - D5: TextureStreamer and MipResidency mip streaming subsystem                  │
│  - Result: Traverse 16 km x 16 km world at 100 m/s with no hitch > 2ms           │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 7a: GPU-Driven Rendering                                                  │
│  - G6: BindlessTable (descriptor indexing), GeometryPool, InstanceBuffer        │
│  - G6: Compute culling (cull.comp: 5-plane frustum + Hi-Z min-reduction)         │
│  - G6: HiZBuilder (min-reduction depth pyramid)                                  │
│  - G6: IndirectDrawBuffer (vkCmdDrawIndexedIndirectCount)                       │
│  - Result: 200,000 visible instances in < 4ms GPU, < 1ms CPU render submission   │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 7b: Scalable Lighting, Foliage & Transparency                             │
│  - G9: CascadedShadowMap (4 cascades, stable texel snapping, alpha-test shadow)  │
│  - G9: ClusteredLightCulling (3D cluster grid compute pass), SpotLight, IBL      │
│  - G8: FoliageSystem (GPU instanced vegetation, wind vertex oscillation)         │
│  - LOD cross-fade stipple dithering (lod_dither.glsl)                            │
│  - TransparentPass (order-independent transparency) & ParticleSystem             │
│  - Result: Reference scene at budget with shadows, 256 lights, foliage, particles│
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 8: Physics, Character & Spatial Audio                                     │
│  - Jolt PhysicsEngine integration with streaming per-cell colliders              │
│  - Terrain heightfield physics shapes generated per cell                         │
│  - Kinematic CharacterController (walk, sprint, jump, stairs, slopes)            │
│  - Raycasts and collision event dispatch to ECS systems                          │
│  - OpenAL AudioEngine: SoundClip, AudioSource, AudioListener, cell emitters      │
│  - Origin shift integration (batch body update, listener/emitter shift)          │
│  - Result: Walk/jump across streamed world with collisions & 3D sound; 3 shifts  │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 9: Atmosphere, Water & Post-Processing Pipeline                           │
│  - G8: Analytical Atmosphere sky shader, TimeOfDay sun/moon orbit, aerial fog    │
│  - G8: WaterRenderer (displacement waves, Fresnel, depth color gradient)         │
│  - G11: PostProcessChain (HDR R16G16B16A16_SFLOAT target)                        │
│  - G11: ACES/AgX Tonemapping, Bloom down/upsample pyramid, TAA, SSAO/GTAO        │
│  - Result: Cinematic open-world lighting and post-processing within 5.90 ms GPU  │
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         │
                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ SPRINT 10: Gameplay Support & Release Readiness (Engine Complete!)               │
│  - G14: NavMesh tile streaming & NavMeshAgent pathfinding                        │
│  - G14: Skeletal animation system & GPU compute skinning                         │
│  - G14: Runtime Canvas UI & FreeType SDF font rendering                          │
│  - G14: SaveSystem (dynamic world state serialization)                           │
│  - G14: Release shader pipeline with persistent VkPipelineCache disk caching     │
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

---

## 7. Change Log

| Entry | Sprint Affected | Modification Description |
| :--- | :---: | :--- |
| **CL-01** | Sprint 3b, 10, 11 | Moved `ImGuiVulkanBackend.cs` and `DebugOverlay.cs` forward from Sprint 10 to Sprint 3b to provide an immediate diagnostics HUD. Moved full 13-file editor suite to an optional **Sprint 11**. |
| **CL-02** | Sprint 3a, 3b | Split Sprint 3 into **Sprint 3a** (Textures & Pipelines) and **Sprint 3b** (GPU Foundation, Reverse-Z, Diagnostics). |
| **CL-03** | Sprint 2, 3b | Corrected DepthBuffer status description to note that Reverse-Z conversion is pending Sprint 3b. |
| **CL-04** | Sprint 2, 5, 7a | Designated Panini projection as demo-only for the cube sample; removed Panini from production camera and GPU-driven renderer descriptions. |
| **CL-05** | Sprint 5, 7a | Re-specified `Frustum.cs` and `cull.comp` to use 5 planes plus a configurable `MaxDrawDistance` (default: 8,000 m). |
| **CL-06** | Sprint 4, 6 | Clarified cell packaging: world cells are binary chunk records inside contiguous `.jpak` archives (identified by GUID), eliminating standalone `.jcell` files. |
| **CL-07** | Sprint 3a, 7a | Standardized Hi-Z occlusion to **MIN** reduction for reverse-Z and added min-reduction sampler requirement (`VK_EXT_sampler_filter_minmax`) to `VulkanSampler.cs`. |
| **CL-08** | Sprint 6 | Added Texture Mip Streaming subsystem (`TextureStreamer.cs`, `MipResidency.cs`) and cooker mip tail splitting to catalog and roadmap. |
| **CL-09** | Sprint 7a, 7b | Split Sprint 7 into **Sprint 7a** (GPU-Driven Rendering) and **Sprint 7b** (Scalable Lighting, Foliage, Transparency, Particles, and LOD Dithering). |
| **CL-10** | Sprint 3a | Added Automated Testing, Validation & CI infrastructure files (`Jatayu.Tests`, `Jatayu.RenderSmoke`, `ci.yml`, `Directory.Build.props`, `RENDER_CONVENTIONS.md`). |
| **CL-11** | Sprint 3b, 5 | Added `NativeArena.cs`, `NativePool.cs`, `Settings.cs`, and `MaterialTable.cs` to the catalog. |
| **CL-12** | Core | Documented exact 5-step floating-origin rebasing procedure in Section 1.3. |
| **CL-13** | Core | Updated GPU frame budget table to 5.90 ms (providing ~15% headroom under 144 FPS / 6.94 ms target) and CPU frame budget to 5.20 ms. Defined Reference Scene in Section 5. |
| **CL-14** | Dashboard | Recounted all catalog files across 18 subsystem tables: 194 total planned files, 16 completed files (8.2% completion). |
