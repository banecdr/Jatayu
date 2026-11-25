# Jatayu

<img width="1284" height="754" alt="image" src="https://github.com/user-attachments/assets/0c7866d3-f8d1-4642-adc9-24608a2340d4" />

Jatayu is a C# 3D engine and interactive rendering testbed built on .NET 10, Vulkan 1.3, and the BRUTAL library bindings (`Ahwoo.BRUTAL.*`). It has grown beyond the original cube demo into a place to build and exercise rendering, world, physics, and audio systems.

It is an engine demo, not a finished game or standalone editor. The current scene brings these systems together:

- **Rendering:** physically based materials, atmospheric sky, reverse-Z depth, cascaded soft shadows, image-based lighting, clustered lights, and bindless textures.
- **GPU-driven drawing:** indirect draws with compute culling, including Hi-Z occlusion, plus a CPU drawing fallback for comparison.
- **Terrain and large worlds:** a seven-ring terrain clipmap, streamed world cells, double-precision world positions, and floating-origin shifts for stable rendering and physics far from the origin.
- **Physics and audio:** Jolt rigid bodies and a walk/sprint/jump character controller, alongside OpenAL positional sound. Audio falls back to silent mode when no device is available.
- **Tools and diagnostics:** an ImGui telemetry HUD, world-streaming radar, asset cooker, procedural world-generation tool, and automated tests.

The demo also includes optional GPU particles, a high-speed camera tour, and a 200,000-instance rendering stress mode. Foliage rendering is implemented but disabled in the default scene.

## Controls

| Key | Action |
| --- | --- |
| `Right Mouse Button` | Capture the mouse; look around and fly with `WASD` |
| `C` | Switch between orbit camera and free-fly camera |
| `X` | Toggle character mode (`WASD` to move, `Shift` to sprint, `Space` to jump) |
| `Space` | Jump in character mode; pause or resume auto-orbit otherwise |
| `F1` / `F2` | Toggle diagnostics HUD / world-streaming radar |
| `F3` | Toggle the high-speed drone tour |
| `F6` | Toggle the GPU particle fountain |
| `J` / `K` | Fire a physics cannonball / reset the physics demo |
| `T` | Teleport between the origin and the 50 km monolith scene |
| `P` | Toggle shadow filtering quality |
| `F` | Switch between 60° and 110° field of view |
| `V` | Toggle V-Sync |
| `Esc` | Release the mouse; press again to exit |

## Getting Started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A GPU with a Vulkan 1.3-capable driver

### Run

```bash
dotnet run
```

On Linux, the native windowing and audio dependencies used by the app must also be installed. Audio can run silently without an audio device.

To run the test suite:

```bash
dotnet test tests/Jatayu.Tests --configuration Release
```

### Build a Release package

On Windows, run:

```powershell
.\tools\Publish-Release.ps1
```

This builds and validates a self-contained Windows x64 package in `dist\Jatayu`.

To export the Linux x64 package, run:

```powershell
.\tools\Publish-Linux-Release.ps1
```

This builds and validates a self-contained Linux x64 package in `dist/Jatayu-linux-x64`. Run it on Linux with `./Jatayu`; the .NET runtime is bundled, but a Vulkan-capable GPU driver and the native windowing/audio system libraries are still required.

After a push to `main` passes CI, GitHub Actions creates a release and marks it as the latest. The release includes `Jatayu-windows-x64.zip` and `Jatayu-linux-x64.tar.gz`.

## License

[GPL-2.0](LICENSE)
