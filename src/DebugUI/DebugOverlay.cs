/* =================================================================================================
 * FILE: DebugOverlay.cs
 * LOCATION: src/DebugUI/DebugOverlay.cs
 * 
 * WHAT THIS FILE DOES:
 *   Real-time telemetry HUD overlay rendered on top of the 3D scene using Dear ImGui.
 *   Provides comprehensive diagnostic visualization for Sprint 5b:
 *     - Frame rates, DeltaTime, V-Sync, and VRAM memory telemetry.
 *     - 64-bit Large-World precision and 256m tile-snapped OriginShift tracking.
 *     - PBR Material parameters and active Bindless descriptor indexing slots.
 *     - Directional sunlight vector, illuminance, and 16-tap PCF soft shadows.
 *     - Reverse-Z infinite depth conventions and Panini projection parameters.
 *     - Live CPU and GPU pass profile timers.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Jatayu.Core.Mathematics;
using Jatayu.Core.Profiling;
using Jatayu.Core.Time;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Lighting;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Profiling;
using System.Text.Unicode;

namespace Jatayu.DebugUI;

/// <summary>
/// Real-time engine diagnostics and performance telemetry HUD overlay.
/// </summary>
public sealed class DebugOverlay
{
    private bool _isOpen = true;
    private readonly float4 _green = new(0.2f, 0.9f, 0.3f, 1.0f);
    private readonly float4 _yellow = new(0.95f, 0.85f, 0.2f, 1.0f);
    private readonly float4 _red = new(0.95f, 0.3f, 0.2f, 1.0f);
    private readonly float4 _cyan = new(0.2f, 0.8f, 0.95f, 1.0f);
    private readonly float4 _orange = new(1.0f, 0.6f, 0.2f, 1.0f);
    private readonly float4 _gold = new(1.0f, 0.84f, 0.0f, 1.0f);

    /// <summary>
    /// Gets or sets whether the diagnostic HUD is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    /// <summary>
    /// Renders the performance telemetry overlay for the current frame.
    /// </summary>
    public void Draw(
        GameTime gameTime,
        Camera3D camera,
        bool vsync,
        VramBudget? vramBudget,
        IReadOnlyList<Profiler.ZoneTiming> cpuZones,
        IReadOnlyList<GpuTimer.GpuPassTiming> gpuPasses,
        OriginShift? originShift = null,
        LightEnvironment? lightEnv = null,
        int visibleEntities = 0,
        int totalEntities = 0,
        int shadowCasters = 0,
        bool atLargeWorld = false,
        bool debugWireframe = false,
        bool sunOrbiting = false)
    {
        if (!_isOpen) return;

        // Position HUD in top-left with slight transparency
        ImGui.SetNextWindowPos(new float2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.80f);

        if (ImGui.Begin("Jatayu Engine Diagnostics - Sprint 5b (Large-World & PBR)"u8, ref _isOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Span<byte> buf = stackalloc byte[256];
            int w;

            // -------------------------------------------------------------------------------------
            // 1. ENGINE PERFORMANCE & VRAM TELEMETRY
            // -------------------------------------------------------------------------------------
            float fps = gameTime.Fps;
            float dtMs = gameTime.AverageFrameTimeMs > 0 ? gameTime.AverageFrameTimeMs : gameTime.DeltaTime * 1000f;
            var frameColor = dtMs <= 6.94f ? _green : (dtMs <= 16.67f ? _yellow : _red);

            if (Utf8.TryWrite(buf, $"FPS: {fps:F1} ({dtMs:F2} ms)", out w))
            {
                buf[w] = 0;
                ImGui.TextColored(frameColor, buf[..w]);
            }
            ImGui.SameLine(180f);
            if (Utf8.TryWrite(buf, $"Frame: #{gameTime.FrameCount}", out w))
            {
                buf[w] = 0;
                ImGui.Text(buf[..w]);
            }

            if (vsync)
                ImGui.TextColored(_green, "V-Sync: ON (Locked to display) [V]"u8);
            else
                ImGui.TextColored(_yellow, "V-Sync: OFF (Uncapped FPS) [V]"u8);

            long gcAllocated = AllocationTracker.LastFrameAllocatedBytes;
            var allocColor = gcAllocated == 0 ? _green : _red;
            ImGui.Text("Steady-State GC Allocations:"u8);
            ImGui.SameLine();
            if (Utf8.TryWrite(buf, $"{gcAllocated} B", out w))
            {
                buf[w] = 0;
                ImGui.TextColored(allocColor, buf[..w]);
            }
            if (Utf8.TryWrite(buf, $"Zero-Alloc Frame Rate: {AllocationTracker.ZeroAllocationPercent:F1}%", out w))
            {
                buf[w] = 0;
                ImGui.Text(buf[..w]);
            }

            if (vramBudget != null)
            {
                var stats = vramBudget.QueryStats();
                double usedMb = stats.TotalAllocatedBytes / (1024.0 * 1024.0);
                double budgetMb = stats.TotalBudgetBytes / (1024.0 * 1024.0);
                float fraction = stats.BudgetUsagePercent;

                if (Utf8.TryWrite(buf, $"VRAM: {usedMb:F0} MB / {budgetMb:F0} MB ({fraction * 100f:F1}%)", out w))
                {
                    buf[w] = 0;
                    ImGui.Text(buf[..w]);
                }
                ImGui.ProgressBar(fraction, new float2(320f, 0f), ""u8);
            }
            ImGui.Separator();

            // -------------------------------------------------------------------------------------
            // 2. 64-BIT LARGE-WORLD & FLOATING-ORIGIN REBASE
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("Large-World Precision (50,000m) & OriginShift"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (atLargeWorld)
                {
                    ImGui.TextColored(_gold, "Location: [50,000m Monolith Cluster] (Toggle: [T])"u8);
                }
                else
                {
                    ImGui.TextColored(_cyan, "Location: [World Origin Scene (0m)] (Toggle: [T])"u8);
                }

                if (camera != null)
                {
                    var pos = camera.Position;
                    var tgt = camera.Target;
                    float dist = Vector3.Distance(pos, tgt);
                    if (Utf8.TryWrite(buf, $"Camera Pos:   ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}) m", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    if (Utf8.TryWrite(buf, $"Camera Target:({tgt.X:F1}, {tgt.Y:F1}, {tgt.Z:F1}) m | Dist: {dist:F1} m", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    if (Utf8.TryWrite(buf, $"Camera FOV:   {camera.FovDegrees:F0}° (Toggle: [F])", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }

                    ImGui.Text("Shadows:       [P] Toggle Ultra HQ (16-tap Poisson) vs Standard"u8);
                }

                if (originShift != null)
                {
                    var orig = originShift.CurrentOrigin;
                    if (Utf8.TryWrite(buf, $"Floating Origin: ({orig.X:F1}, {orig.Y:F1}, {orig.Z:F1}) m", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    if (Utf8.TryWrite(buf, $"Shift Threshold: {originShift.ThresholdDistance:F0} m (Snapped to 256m tiles)", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                }
            }

            // -------------------------------------------------------------------------------------
            // 3. SCENE ENTITIES & PBR MATERIALS
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("Scene ECS & PBR Materials"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (Utf8.TryWrite(buf, $"Entities: {totalEntities} Total | {visibleEntities} In Frustum | {shadowCasters} Shadow Casters", out w))
                {
                    buf[w] = 0;
                    ImGui.Text(buf[..w]);
                }
                ImGui.Text("Active PBR Materials:"u8);
                ImGui.TextColored(_gold, "  #1 Gold:          Metallic 1.00, Roughness 0.15"u8);
                ImGui.TextColored(_orange, "  #2 Copper:        Metallic 0.95, Roughness 0.25"u8);
                ImGui.TextColored(new float4(0.9f, 0.9f, 0.95f, 1.0f), "  #3 Chrome:        Metallic 1.00, Roughness 0.05"u8);
                ImGui.TextColored(new float4(0.85f, 0.4f, 0.2f, 1.0f), "  #4 Terracotta:    Metallic 0.00, Roughness 0.85"u8);
                ImGui.TextColored(_cyan, "  #5 Neon Blue:     Emissive 4.0 lux, Glow (0.2, 0.8, 1.0)"u8);
                ImGui.TextColored(_green, "  #6 Checkerboard:  Bindless Slot #0 (Anisotropic 16x)"u8);
                ImGui.TextColored(new float4(0.5f, 0.55f, 0.6f, 1.0f), "  #7 Ground Slate:  Roughness 0.90 (200m x 200m Plane)"u8);
            }

            // -------------------------------------------------------------------------------------
            // 4. LIGHTING, SOFT SHADOWS & SKYBOX
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("Lighting & Shadows"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (lightEnv != null)
                {
                    var dir = lightEnv.Sun.Direction;
                    if (Utf8.TryWrite(buf, $"Sun Direction: ({dir.X:F2}, {dir.Y:F2}, {dir.Z:F2})", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    if (Utf8.TryWrite(buf, $"Sun Light Orbit: {(sunOrbiting ? "ANIMATING" : "PAUSED")} (Toggle: [L])", out w))
                    {
                        buf[w] = 0;
                        ImGui.TextColored(sunOrbiting ? _green : _yellow, buf[..w]);
                    }
                    if (Utf8.TryWrite(buf, $"Sun Illuminance: {lightEnv.Sun.Intensity:F0} Lux", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    ImGui.Text("Shadow Map:    2048x2048 D32_SFLOAT (Reverse-Z Comparison, 16-tap PCF)"u8);
                    ImGui.Text("Skybox:        Reverse-Z Procedural Atmosphere (Z = 0.0 Infinity)"u8);
                }
            }

            // -------------------------------------------------------------------------------------
            // 5. 3D DEBUG DIAGNOSTICS
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("3D Wireframe Diagnostics"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (Utf8.TryWrite(buf, $"3D Wireframes: {(debugWireframe ? "VISIBLE" : "HIDDEN")} (Toggle: [B])", out w))
                {
                    buf[w] = 0;
                    ImGui.TextColored(debugWireframe ? _green : _yellow, buf[..w]);
                }
                ImGui.Text("  Axes:   Red = +X, Green = +Y, Blue = +Z (3m origin gizmo)"u8);
                ImGui.Text("  Bounds: Yellow/Orange/Cyan AABB wireframe bounding boxes"u8);
            }

            // -------------------------------------------------------------------------------------
            // 6. CPU & GPU PASS TIMINGS
            // -------------------------------------------------------------------------------------
            if (cpuZones.Count > 0 && ImGui.CollapsingHeader("CPU Pass Timings"u8))
            {
                for (int i = 0; i < cpuZones.Count; i++)
                {
                    var zone = cpuZones[i];
                    if (Utf8.TryWrite(buf, $"  {zone.Name}:", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    ImGui.SameLine(180f);
                    if (Utf8.TryWrite(buf, $"{zone.DurationMs:F3} ms", out w))
                    {
                        buf[w] = 0;
                        ImGui.TextColored(_cyan, buf[..w]);
                    }
                }
            }

            if (gpuPasses.Count > 0 && ImGui.CollapsingHeader("GPU Pass Timings"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int i = 0; i < gpuPasses.Count; i++)
                {
                    var pass = gpuPasses[i];
                    if (Utf8.TryWrite(buf, $"  {pass.Name}:", out w))
                    {
                        buf[w] = 0;
                        ImGui.Text(buf[..w]);
                    }
                    ImGui.SameLine(180f);
                    if (Utf8.TryWrite(buf, $"{pass.DurationMs:F3} ms", out w))
                    {
                        buf[w] = 0;
                        ImGui.TextColored(_cyan, buf[..w]);
                    }
                }
            }

            // -------------------------------------------------------------------------------------
            // 7. KEYBOARD CONTROLS CHEATSHEET
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("Interactive Controls Cheatsheet"u8))
            {
                ImGui.Text("  [G]           : Toggle GPU-Driven Indirect Multi-Draw (Hi-Z Occlusion)"u8);
                ImGui.Text("  [K]           : Toggle Procedural Wind Grass Field"u8);
                ImGui.Text("  [J]           : Toggle GPU Particle Fountain"u8);
                ImGui.Text("  [P]           : Toggle Ultra High-Quality Shadows (16-tap Poisson vs 9-tap)"u8);
                ImGui.Text("  [T]           : Teleport between 0m Origin and 50,000m Cluster"u8);
                ImGui.Text("  [C]           : Toggle 6-DOF Flycam vs Orbit camera"u8);
                ImGui.Text("  [F3]          : Toggle 100 m/s Supersonic Drone Tour"u8);
                ImGui.Text("  [F4]          : Toggle Terrain Clipmap LOD Ring Colors"u8);
                ImGui.Text("  [L]           : Toggle Sun Light Orbit animation"u8);
                ImGui.Text("  [B]           : Toggle 3D Wireframe Debug bounds & axes"u8);
                ImGui.Text("  [F]           : Toggle FOV (Standard 60° <-> Ultra-Wide 110°)"u8);
                ImGui.Text("  [V]           : Toggle V-Sync live"u8);
                ImGui.Text("  [F1] / [F2]   : Toggle Telemetry HUD / Streaming Radar"u8);
                ImGui.Text("  [ESC]         : Graceful shutdown"u8);
            }
        }
        ImGui.End();
    }
}
