/* =================================================================================================
 * FILE: DebugOverlay.cs
 * LOCATION: src/DebugUI/DebugOverlay.cs
 * 
 * WHAT THIS FILE DOES:
 *   Real-time telemetry HUD overlay rendered on top of the 3D scene using Dear ImGui.
 *   Visualizes engine frame times, CPU/GPU pass profiles, 0-byte GC allocations, and VRAM budget.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Jatayu.Core.Profiling;
using Jatayu.Core.Time;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Profiling;

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
        VramBudget? vramBudget,
        IReadOnlyList<Profiler.ZoneTiming> cpuZones,
        IReadOnlyList<GpuTimer.GpuPassTiming> gpuPasses)
    {
        if (!_isOpen) return;

        // Position HUD in top-left with slight transparency
        ImGui.SetNextWindowPos(new float2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.75f);

        if (ImGui.Begin("Jatayu Engine Diagnostics (Sprint 3b)"u8, ref _isOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // -------------------------------------------------------------------------------------
            // 1. ENGINE PERFORMANCE & FRAME RATE
            // -------------------------------------------------------------------------------------
            float fps = gameTime.Fps;
            float dtMs = gameTime.AverageFrameTimeMs > 0 ? gameTime.AverageFrameTimeMs : gameTime.DeltaTime * 1000f;
            var frameColor = dtMs <= 6.94f ? _green : (dtMs <= 16.67f ? _yellow : _red);

            ImGui.TextColored(frameColor, $"FPS: {fps:F1} ({dtMs:F2} ms)");
            ImGui.Text("Target: 144 FPS / 6.94 ms Budget"u8);
            ImGui.Separator();

            // -------------------------------------------------------------------------------------
            // 2. ZERO-ALLOCATION DISCIPLINE
            // -------------------------------------------------------------------------------------
            long gcAllocated = AllocationTracker.LastFrameAllocatedBytes;
            var allocColor = gcAllocated == 0 ? _green : _red;

            ImGui.Text("Steady-State GC Allocations:"u8);
            ImGui.SameLine();
            ImGui.TextColored(allocColor, $"{gcAllocated} B");
            ImGui.Text($"Zero-Alloc Frame Rate: {AllocationTracker.ZeroAllocationPercent:F1}%");
            ImGui.Separator();

            // -------------------------------------------------------------------------------------
            // 3. VRAM BUDGET & TELEMETRY
            // -------------------------------------------------------------------------------------
            if (vramBudget != null)
            {
                var stats = vramBudget.QueryStats();
                double usedMb = stats.TotalAllocatedBytes / (1024.0 * 1024.0);
                double budgetMb = stats.TotalBudgetBytes / (1024.0 * 1024.0);
                float fraction = stats.BudgetUsagePercent;

                ImGui.Text($"VRAM: {usedMb:F0} MB / {budgetMb:F0} MB ({fraction * 100f:F1}%)");
                ImGui.ProgressBar(fraction, new float2(280f, 0f), ""u8);
                ImGui.Separator();
            }

            // -------------------------------------------------------------------------------------
            // 4. CPU PASS TIMINGS (PROFILER)
            // -------------------------------------------------------------------------------------
            if (cpuZones.Count > 0 && ImGui.CollapsingHeader("CPU Pass Timings"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int i = 0; i < cpuZones.Count; i++)
                {
                    var zone = cpuZones[i];
                    ImGui.Text($"  {zone.Name}:");
                    ImGui.SameLine(180f);
                    ImGui.TextColored(_cyan, $"{zone.DurationMs:F3} ms");
                }
            }

            // -------------------------------------------------------------------------------------
            // 5. GPU PASS TIMINGS (GPU TIMER)
            // -------------------------------------------------------------------------------------
            if (gpuPasses.Count > 0 && ImGui.CollapsingHeader("GPU Pass Timings"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int i = 0; i < gpuPasses.Count; i++)
                {
                    var pass = gpuPasses[i];
                    ImGui.Text($"  {pass.Name}:");
                    ImGui.SameLine(180f);
                    ImGui.TextColored(_cyan, $"{pass.DurationMs:F3} ms");
                }
            }

            // -------------------------------------------------------------------------------------
            // 6. RENDER CONVENTIONS
            // -------------------------------------------------------------------------------------
            if (ImGui.CollapsingHeader("Active Pipeline Features"u8))
            {
                ImGui.Text("  Vulkan: 1.3 Dynamic Rendering"u8);
                ImGui.Text("  Depth: Reverse-Z (Infinite Far)"u8);
                ImGui.Text("  Clear Depth: 0.0f (GreaterOrEqual)"u8);
                ImGui.Text("  Uploads: Async Dedicated Transfer"u8);
                ImGui.Text("  Staging: Circular Persistently Mapped"u8);
            }
        }
        ImGui.End();
    }
}
