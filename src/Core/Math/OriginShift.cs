// ==================================================================================================
// Jatayu Engine - Core Large-World Math
// Module: OriginShift.cs
// Role: Floating-origin coordinator executing deterministic 256m-snapped world rebases (Section 1.3).
// ==================================================================================================

using Jatayu.Core.Events;
using Jatayu.Core.Logging;

namespace Jatayu.Core.Mathematics;

/// <summary>
/// Coordinates deterministic floating-origin rebasing when the camera travels beyond 2,000 meters
/// from the simulation origin. Snaps shift offsets to 256m (terrain tile multiple) to keep
/// cell-local coordinates and terrain data invariant.
/// </summary>
public sealed class OriginShift
{
    /// <summary>
    /// Default threshold distance from simulation origin before triggering a rebase (2,000 meters).
    /// </summary>
    public const double DefaultThresholdDistance = 2000.0;

    /// <summary>
    /// Tile dimension to which shift offsets are snapped (256.0 meters = 2x2 128m cells).
    /// </summary>
    public const double SnapTileDimension = 256.0;

    private WorldPosition _currentOrigin = WorldPosition.Zero;
    private double _thresholdDistance = DefaultThresholdDistance;

    /// <summary>
    /// Gets the accumulated total origin offset applied since engine start.
    /// </summary>
    public WorldPosition CurrentOrigin => _currentOrigin;

    /// <summary>
    /// Gets or sets the distance threshold in meters that triggers an origin shift.
    /// </summary>
    public double ThresholdDistance
    {
        get => _thresholdDistance;
        set => _thresholdDistance = System.Math.Max(500.0, value);
    }

    /// <summary>
    /// Evaluates camera position and executes a deterministic origin shift if threshold is exceeded.
    /// Executes strictly at a frame boundary (Section 1.3).
    /// </summary>
    /// <param name="cameraPosition">The camera position relative to the current simulation origin.</param>
    /// <param name="deltaShift">Out parameter returning the exact translation delta applied.</param>
    /// <returns>True if an origin shift occurred, false otherwise.</returns>
    public bool CheckAndShift(in WorldPosition cameraPosition, out WorldPosition deltaShift)
    {
        deltaShift = WorldPosition.Zero;

        double distanceSq = cameraPosition.X * cameraPosition.X + cameraPosition.Z * cameraPosition.Z;
        if (distanceSq < _thresholdDistance * _thresholdDistance)
        {
            return false;
        }

        // Calculate offset snapped to 256m terrain tile multiple
        double rawDx = cameraPosition.X;
        double rawDz = cameraPosition.Z;

        double snappedDx = System.Math.Round(rawDx / SnapTileDimension) * SnapTileDimension;
        double snappedDz = System.Math.Round(rawDz / SnapTileDimension) * SnapTileDimension;

        if (System.Math.Abs(snappedDx) < 0.001 && System.Math.Abs(snappedDz) < 0.001)
        {
            return false;
        }

        deltaShift = new WorldPosition(snappedDx, 0.0, snappedDz);
        _currentOrigin += deltaShift;

        Log.Info($"[OriginShift] Triggered floating origin rebase: Delta=({snappedDx:F1}, 0.0, {snappedDz:F1})m | New Origin={_currentOrigin}");

        // Broadcast event through EventBus so physics, audio, and gameplay systems synchronize
        EventBus.Publish(new OriginShiftedEvent(snappedDx, 0.0, snappedDz));

        return true;
    }

    /// <summary>
    /// Resets the simulation origin to world zero.
    /// </summary>
    public void Reset()
    {
        _currentOrigin = WorldPosition.Zero;
    }
}
