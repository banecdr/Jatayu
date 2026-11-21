/* =================================================================================================
 * FILE: GameTime.cs
 * LOCATION: src/Core/Time/GameTime.cs
 * 
 * WHAT THIS FILE DOES:
 *   This is the master clock of the engine. It measures real-world time passing between frames,
 *   calculates how many frames per second (FPS) you are getting, and manages a "fixed-timestep 
 *   accumulator" so that physics engines (like Jolt Physics) run stably at a constant rate 
 *   without exploding or glitching.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. High-Precision Timing:
 *      Instead of using DateTime (which has poor precision on Windows), it uses CPU hardware ticks 
 *      via Stopwatch.GetTimestamp(). It multiplies tick differences by the reciprocal of the CPU 
 *      counter frequency to compute time in fractional seconds.
 *
 *   2. Variable Frame Delta (DeltaTime):
 *      Every frame when you call Update(), it measures the duration since the previous frame.
 *      If a frame takes unexpectedly long (like when dragging the window or hitting a breakpoint), 
 *      MaxDeltaTime clamps the duration so the game doesn't leap forward several seconds.
 *
 *   3. Fixed-Timestep Accumulator (The Jolt Physics Engine Loop):
 *      Physics engines calculate forces and collisions assuming a steady, predictable time step
 *      (e.g., 60 ticks per second). When DeltaTime arrives, it gets dumped into a "pool" called 
 *      the Accumulator. 
 *      StepFixed() checks if the pool has at least 1/60th of a second in it. If yes, it consumes
 *      that chunk and returns true, letting you step the physics world. If the game is running at 
 *      144 FPS, some render frames won't step physics at all. If the game drops to 30 FPS, one 
 *      render frame might run 2 physics steps to catch up.
 *
 *   4. Visual Interpolation (InterpolationAlpha):
 *      Because render frames and physics frames don't line up perfectly, InterpolationAlpha 
 *      gives you a number between 0.0 and 1.0 showing how far between the last physics tick and 
 *      the next physics tick the current frame is. The renderer uses this to smoothly blend 
 *      transforms so movement never stutters on high-refresh monitors.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - DeltaTime: Seconds elapsed since last frame, multiplied by TimeScale. Use this for general 
 *     things like moving a free-look camera or fading an effect.
 *   - UnscaledDeltaTime: Raw seconds elapsed since last frame. Ignores pause or slow-mo. Useful 
 *     for UI animations or FPS overlays that should keep moving even if game is paused.
 *   - TotalTime: Total seconds the engine has been running since start.
 *   - FrameCount: Total number of rendered frames since start.
 *   - Fps: Current frames per second, recalculated smoothly every 0.25 seconds.
 *   - AverageFrameTimeMs: Average time in milliseconds that one frame takes to execute.
 *   - TimeScale: Multiplier for speed (1.0 = normal, 0.5 = half speed slow-mo, 0.0 = paused).
 *   - MaxDeltaTime: Ceiling cap on DeltaTime (defaults to 0.1s / 100ms) to stop physics explosions.
 *   - FixedDeltaTime: Exact duration of each physics step (defaults to 1/60s = ~0.01667s).
 *   - Accumulator: Current reservoir of unspent physics time.
 *   - FixedFrameCount: Total number of fixed physics steps simulated since start.
 *   - InterpolationAlpha: Value from 0.0 to 1.0 indicating render progress between physics steps.
 *
 * HOW TO USE IT IN THE MAIN LOOP:
 *   var time = new GameTime();
 *   while (!window.ShouldClose)
 *   {
 *       time.Update();
 *
 *       // Run physics in fixed steps:
 *       while (time.StepFixed())
 *       {
 *           physicsWorld.Step(time.FixedDeltaTime);
 *       }
 *
 *       // Render with smoothing:
 *       renderer.Draw(time.InterpolationAlpha);
 *   }
 * ================================================================================================= */

using System.Diagnostics;

namespace Jatayu.Core.Time;

/// <summary>
/// Provides high-precision frame timing, variable delta tracking, FPS metrics,
/// and a fixed-timestep accumulator for deterministic physics (e.g., Jolt Physics).
/// </summary>
public sealed class GameTime
{
    private static readonly double TimestampFrequencyInv = 1.0 / Stopwatch.Frequency;

    private long _startTimestamp;
    private long _lastFrameTimestamp;
    private long _currentFrameTimestamp;

    // FPS metrics
    private double _fpsAccumulator;
    private int _fpsFrameCount;

    // --- Variable Frame Timing ---

    public float DeltaTime { get; private set; }
    public float UnscaledDeltaTime { get; private set; }
    public double TotalTime { get; private set; }
    public ulong FrameCount { get; private set; }
    public float Fps { get; private set; }
    public float AverageFrameTimeMs { get; private set; }
    public float TimeScale { get; set; } = 1.0f;
    public float MaxDeltaTime { get; set; } = 0.1f;

    // --- Fixed-Timestep Accumulator (for Physics & Determinism) ---

    public float FixedDeltaTime { get; set; } = 1.0f / 60.0f;
    public float Accumulator { get; private set; }
    public ulong FixedFrameCount { get; private set; }
    public float InterpolationAlpha => FixedDeltaTime > 0.0f ? Math.Clamp(Accumulator / FixedDeltaTime, 0.0f, 1.0f) : 1.0f;

    public GameTime()
    {
        Reset();
    }

    public void Reset()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        _lastFrameTimestamp = _startTimestamp;
        _currentFrameTimestamp = _startTimestamp;

        DeltaTime = 0.0f;
        UnscaledDeltaTime = 0.0f;
        TotalTime = 0.0;
        FrameCount = 0;
        Fps = 0.0f;
        AverageFrameTimeMs = 0.0f;
        _fpsAccumulator = 0.0;
        _fpsFrameCount = 0;

        Accumulator = 0.0f;
        FixedFrameCount = 0;
    }

    public void Update()
    {
        _currentFrameTimestamp = Stopwatch.GetTimestamp();
        long rawDeltaTicks = _currentFrameTimestamp - _lastFrameTimestamp;
        _lastFrameTimestamp = _currentFrameTimestamp;

        double rawDeltaSeconds = rawDeltaTicks * TimestampFrequencyInv;
        if (rawDeltaSeconds < 0.0)
            rawDeltaSeconds = 0.0;

        float unscaledDelta = (float)rawDeltaSeconds;
        if (unscaledDelta > MaxDeltaTime)
            unscaledDelta = MaxDeltaTime;

        UnscaledDeltaTime = unscaledDelta;
        DeltaTime = unscaledDelta * TimeScale;
        TotalTime = (_currentFrameTimestamp - _startTimestamp) * TimestampFrequencyInv;
        FrameCount++;

        // Feed the fixed-timestep accumulator
        Accumulator += DeltaTime;

        // Smoothed FPS calculation
        _fpsAccumulator += rawDeltaSeconds;
        _fpsFrameCount++;
        if (_fpsAccumulator >= 0.25)
        {
            Fps = (float)(_fpsFrameCount / _fpsAccumulator);
            AverageFrameTimeMs = (float)((_fpsAccumulator / _fpsFrameCount) * 1000.0);
            _fpsAccumulator = 0.0;
            _fpsFrameCount = 0;
        }
    }

    public bool StepFixed()
    {
        if (Accumulator >= FixedDeltaTime && FixedDeltaTime > 0.0f)
        {
            Accumulator -= FixedDeltaTime;
            FixedFrameCount++;
            return true;
        }

        return false;
    }
}
