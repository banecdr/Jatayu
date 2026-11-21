# Jatayu

<img width="1290" height="759" alt="image" src="https://github.com/user-attachments/assets/2480b1cf-67fa-4b98-ad92-51d61c472e00" />

A simple 3D Vulkan playground and testbed built with C# (.NET 10) and the BRUTAL library bindings (`Ahwoo.BRUTAL.*`).

Right now it sets up a basic Vulkan 1.3 render pipeline, draws a lit 3D cube with depth testing, and experiments with Panini projection to keep wide field-of-view angles from stretching near the screen edges.

## What's in here

- **Vulkan 1.3 setup**: Window creation via GLFW, swapchain with window resize handling, and depth buffering.
- **3D camera**: Basic orbit and zoom controls with perspective projection.
- **Panini projection**: Vertex shader projection that straightens out wide FOV views (toggle between 60° and 110°).
- **Shader compilation**: Compiles GLSL shaders (`cube.vert`, `cube.frag`) at startup using ShaderC.

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

## License

[GPL-2.0](LICENSE)
