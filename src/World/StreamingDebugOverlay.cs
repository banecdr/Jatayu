/* =================================================================================================
 * FILE: StreamingDebugOverlay.cs
 * LOCATION: src/World/StreamingDebugOverlay.cs
 * 
 * WHAT THIS FILE DOES:
 *   Visual 2D streaming mini-map and diagnostics HUD rendered with Dear ImGui:
 *   - 2D grid visualizer displaying cells around the player (Green=Active, Yellow=Loading,
 *     Cyan=ReadyToFinalize, Red=Unloading, Dark=Unloaded).
 *   - Player position indicator and velocity vector arrow.
 *   - Real-time telemetry: Active cell counts, in-flight background tasks, effective load radius (m),
 *     player speed (m/s), and streaming frame execution time (ms).
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Text.Unicode;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Jatayu.AssetFormats.World;
using Jatayu.Core.Mathematics;

namespace Jatayu.World;

/// <summary>
/// Diagnostic 2D grid visualizer and streaming telemetry HUD.
/// </summary>
public sealed class StreamingDebugOverlay
{
    private bool _isOpen = true;

    private readonly float4 _colGreen = new(0.2f, 0.85f, 0.3f, 1.0f);
    private readonly float4 _colYellow = new(0.95f, 0.85f, 0.2f, 1.0f);
    private readonly float4 _colCyan = new(0.2f, 0.8f, 0.95f, 1.0f);
    private readonly float4 _colRed = new(0.95f, 0.3f, 0.2f, 1.0f);
    private readonly float4 _colDark = new(0.2f, 0.2f, 0.25f, 0.8f);
    private readonly float4 _colPlayer = new(1.0f, 0.4f, 0.9f, 1.0f);

    public bool IsVisible
    {
        get => _isOpen;
        set => _isOpen = value;
    }

    /// <summary>
    /// Renders the 2D streaming grid and metrics overlay.
    /// </summary>
    public void Draw(
        StreamingManager streamingManager,
        in WorldPosition playerPos,
        in Vector3 playerVelocity)
    {
        if (!_isOpen || streamingManager == null)
            return;

        Span<byte> buf = stackalloc byte[256];
        int w;

        ImGui.SetNextWindowSize(new float2(380f, 480f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("World Streaming Radar"u8, ref _isOpen))
        {
            var config = streamingManager.Config;
            var grid = streamingManager.Grid;

            // 1. Telemetry Metrics
            if (ImGui.CollapsingHeader("Streaming Metrics"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (Utf8.TryWrite(buf, $"Speed:           {streamingManager.CurrentSpeedMps:F1} m/s ({streamingManager.CurrentSpeedMps * 3.6f:F1} km/h)", out w))
                    ImGui.Text(buf[..w]);

                if (Utf8.TryWrite(buf, $"Effective Radius: {streamingManager.EffectiveLoadRadius:F0} m (Base: {config.LoadRadiusRingA:F0}m, Cap: {config.PrefetchRadiusCap:F0}m)", out w))
                    ImGui.Text(buf[..w]);

                if (Utf8.TryWrite(buf, $"Active Cells:    {streamingManager.ActiveCellCount} / {grid.TotalCellCount}", out w))
                    ImGui.TextColored(_colGreen, buf[..w]);

                if (Utf8.TryWrite(buf, $"In-Flight Loads: {streamingManager.InFlightCount}", out w))
                    ImGui.TextColored(streamingManager.InFlightCount > 0 ? _colYellow : _colCyan, buf[..w]);

                if (Utf8.TryWrite(buf, $"Pending Finalize:{streamingManager.ReadyToFinalizeCount}", out w))
                    ImGui.Text(buf[..w]);

                if (Utf8.TryWrite(buf, $"Streaming Tick:  {streamingManager.LastStreamingTimeMs:F3} ms", out w))
                    ImGui.TextColored(_colCyan, buf[..w]);
            }

            // 2. 2D Streaming Mini-Map
            if (ImGui.CollapsingHeader("2D Cell Grid Map"u8, ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text("Legend: Green=Active, Yellow=Loading, Cyan=Finalizing, Dark=Dormant"u8);

                var playerCoord = CellCoord.FromWorld(playerPos.X, playerPos.Z, config.CellSize);

                const int viewRadius = 6; // 13x13 grid view
                const float cellSizePx = 20f;
                const float cellSpacingPx = 2f;

                float mapWidth = (viewRadius * 2 + 1) * (cellSizePx + cellSpacingPx);
                float mapHeight = (viewRadius * 2 + 1) * (cellSizePx + cellSpacingPx);

                var cursor = ImGui.GetCursorScreenPos();

                for (int rz = -viewRadius; rz <= viewRadius; rz++)
                {
                    for (int rx = -viewRadius; rx <= viewRadius; rx++)
                    {
                        var cellCoord = new CellCoord(playerCoord.X + rx, playerCoord.Z + rz);

                        float px = cursor.X + (rx + viewRadius) * (cellSizePx + cellSpacingPx);
                        float py = cursor.Y + (rz + viewRadius) * (cellSizePx + cellSpacingPx);

                        float4 color = _colDark;
                        if (grid.TryGetCell(cellCoord, out var cell))
                        {
                            color = cell.State switch
                            {
                                CellState.Active => _colGreen,
                                CellState.LoadingIO or CellState.LoadingDecode => _colYellow,
                                CellState.ReadyToFinalize => _colCyan,
                                CellState.Unloading => _colRed,
                                _ => _colDark,
                            };
                        }

                        // Highlight player cell
                        if (rx == 0 && rz == 0)
                        {
                            color = _colPlayer;
                        }

                        ImGui.GetWindowDrawList().AddRectFilled(
                            new float2(px, py),
                            new float2(px + cellSizePx, py + cellSizePx),
                            ImGui.ColorConvertFloat4ToU32(color)
                        );
                    }
                }

                // Advance layout cursor past custom draw area
                ImGui.Dummy(new float2(mapWidth, mapHeight + 10f));
            }
        }
        ImGui.End();
    }
}
