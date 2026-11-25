# Jatayu

<img width="1284" height="754" alt="image" src="https://github.com/user-attachments/assets/0c7866d3-f8d1-4642-adc9-24608a2340d4" />




A simple 3D Vulkan playground and testbed built with C# (.NET 10) and the BRUTAL library bindings (`Ahwoo.BRUTAL.*`).

Right now it sets up a basic Vulkan 1.3 render pipeline, draws a lit 3D cube with depth testing, and experiments with Panini projection to keep wide field-of-view angles from stretching near the screen edges.

## What's in here

- **Vulkan 1.3 setup**: Window creation via GLFW, swapchain with window resize handling, and depth buffering.
- **3D camera**: Basic orbit and zoom controls with perspective projection.
- **Panini projection**: Vertex shader projection that straightens out wide FOV views (toggle between 60° and 110°).
- **Shader loading**: Debug uses GLSL sources with ShaderC; Release loads offline-compiled SPIR-V.

## Controls

| Key | Action |
| --- | --- |
| `Space` | Pause / resume rotation |
| `Arrow Keys` | Orbit camera around object |
| `W` / `S` | Zoom in / zoom out |
| `P` | Switch Panini projection (Off / Medium / Full) |
| `F` | Toggle FOV (60° standard / 110° wide) |
| `1` / `2` | Adjust Panini curve parameter |
| `V` | Toggle VSync |
| `C` | Change background color |
| `Esc` | Exit |

## Getting Started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A GPU with Vulkan 1.3 driver support

### Run

```bash
dotnet run
```

### Build a Release package

On Windows, run:

```powershell
.\tools\Publish-Release.ps1
```

This stages a self-contained `win-x64` publish and replaces `dist\Jatayu` only after validation succeeds. The package contains `Jatayu.exe`, precompiled SPIR-V files under `assets\shaders`, and `assets\packages\world.jpak` when that source package exists. Release shader binaries are generated under `obj` rather than the source tree; no loose GLSL, settings file, shader cache, PDB, or DLL sidecar is included.

The managed and application-native dependencies are bundled into the single-file executable; .NET extracts native libraries to its runtime extraction cache as needed, so `assets\libs` is not required. A Vulkan-capable Windows GPU driver remains a system prerequisite. Debug builds continue copying GLSL sources to the normal `bin\Debug` output for development.

To export the Linux x64 package, run:

```powershell
.\tools\Publish-Linux-Release.ps1
```

This creates `dist/Jatayu-linux-x64` without changing the Windows package. It contains the self-contained `Jatayu` executable, precompiled SPIR-V shaders, and the optional world package. On Linux, run `./Jatayu`; the .NET runtime is bundled, but a Vulkan 1.3-capable GPU driver and the Linux window/audio system libraries required by the native dependencies must be installed. The Ubuntu CI workflow also publishes this package as the `Jatayu-linux-x64` build artifact. If the package was exported on Windows, set the executable bit after copying it to Linux with `chmod +x Jatayu`.

## License

[GPL-2.0](LICENSE)
