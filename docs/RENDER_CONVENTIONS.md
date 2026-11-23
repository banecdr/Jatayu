# Jatayu Engine - Rendering Conventions and Coordinate Standards

This document establishes the binding rendering, mathematical, coordinate, and descriptor conventions for the Jatayu Engine codebase. All engine subsystems and shaders must strictly adhere to these standards.

---

## 1. World & Camera Coordinate Conventions
- **Units**: 1.0 unit = 1.0 meter.
- **Handedness & Axes**:
  - Right-handed 3D coordinate system.
  - **+X**: Right (East)
  - **+Y**: Up (Elevation)
  - **+Z**: Towards Viewer / Backwards (or South). Looking down **-Z** in camera space.
- **Large World Positions & Origin Shifts**:
  - Absolute world coordinates use 64-bit double precision (`WorldPosition` with `double X, Y, Z`).
  - Rendering is **camera-relative**: the CPU subtracts the camera's double-precision position from entity world coordinates and casts the delta to single-precision `float` (`Vector3`) before uploading to GPU instance/constant buffers.
  - Shaders never receive or compute in absolute world coordinates.
  - Origin shifts occur in deterministic snaps of 256 meters (matching the terrain tile grid) when the camera travels beyond 2,000 meters from the simulation origin.
  - **Static Instance Transform Invariance (World Streaming)**: Static mesh instances in streaming cells store transforms cell-locally (`float3` position relative to that cell's double-precision origin). When an origin shift triggers, only the per-cell origin vectors are updated on the CPU; static instance transform buffers in VRAM remain invariant and are **never re-uploaded** on an origin shift.
  - **Physics Simulation (Jolt Single Precision)**: Verified in D9 probe that `BRUTAL.Jolt` is built in Single Precision (`float3`). The 2,000 m origin snapping threshold strictly bounds all active rigid body coordinates to within $[-2000\text{ m}, +2000\text{ m}]$, avoiding catastrophic IEEE 754 precision loss without requiring double-precision physics builds.

---

## 2. Vulkan NDC & Depth Conventions (Reverse-Z)
- **Vulkan Normalized Device Coordinates (NDC)**:
  - **X**: `[-1.0, 1.0]` (Left to Right)
  - **Y**: `[-1.0, 1.0]` (Top to Bottom in standard Vulkan; handled via negative viewport height `Y = height, Height = -height` or projection matrix inversion so +Y remains Up in world space).
  - **Z**: `[0.0, 1.0]` (Direct3D/Vulkan style depth range).
- **Reverse-Z Configuration**:
  - **Format**: `VK_FORMAT_D32_SFLOAT` (mandatory hardware floor).
  - **Near Plane Depth**: `1.0` (at $z = z_{near}$).
  - **Far Plane Depth**: `0.0` (at $z = \infty$).
  - **Clear Depth**: `0.0f`.
  - **Depth Compare Op**: `VK_COMPARE_OP_GREATER_OR_EQUAL` (`VkCompareOp.GreaterOrEqual`).
  - **Projection Matrix**: Infinite far plane perspective projection:
    $$P_{00} = \frac{1}{\tan(fov_x / 2)}, \quad P_{11} = -\frac{1}{\tan(fov_y / 2)}, \quad P_{22} = 0, \quad P_{23} = -1, \quad P_{32} = z_{near}, \quad P_{33} = 0$$
  - **Far Culling**: Uses a dedicated `MaxDrawDistance` (default: 8,000 meters) rather than a finite projection far plane.
  - **Hi-Z Occlusion**: Downsampled depth pyramids use minimum reduction with a min-reduction sampler (`VK_SAMPLER_REDUCTION_MODE_MIN` via `VK_EXT_sampler_filter_minmax`).

---

## 3. Matrix & Vector Mathematics
- **Memory Layout**: Row-major in `System.Numerics.Matrix4x4`.
- **Shader Interface**: Vector-matrix multiplication semantics are standardized in GLSL:
  - Position transformation: `gl_Position = uPushConstants.MVP * vec4(vPosition, 1.0);`
- **Frustum Culling**: 5-plane frustum extraction (Left, Right, Bottom, Top, Near) plus spherical/radial distance check against `MaxDrawDistance`.

---

## 4. Vulkan Descriptor & Binding Hierarchy
- **Set 0: Frame Context (Global)**
  - Dynamic camera view-projection matrices, camera world delta, ambient/sky environment, shadow cascade splits and matrices.
- **Set 1: Pass & Resource Tables (Bindless)**
  - Unbounded descriptor arrays for bindless 2D textures (`sampler2D textures[]`) and sampler tables (`sampler samplers[]`).
  - Read-only storage buffers for materials, meshlets/geometries, and transforms.
- **Set 2: Pass Specific / Post-Processing**
  - G-Buffer attachments, shadow depth maps, compute acceleration structures.
- **Push Constants**:
  - Fast, small per-draw metadata ($\le 128$ bytes) containing draw call indices, material IDs, and camera relative offsets.

---

## 5. Performance & Allocation Rules
- **Steady-State Hot Paths**: Zero managed heap allocations per frame. No LINQ, closures, boxing, or per-frame string concatenation.
- **Object Retirement**: Retired GPU resources (textures, buffers, views) must be deferred via `DeferredDeletionQueue` to avoid destroying in-flight resources across $N$ frames in flight.
- **Validation Layers**: Zero Vulkan validation layer warnings or errors permitted in debug builds.
