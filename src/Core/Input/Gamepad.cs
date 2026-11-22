// ==================================================================================================
// Jatayu Engine - Core Input
// Module: Gamepad.cs
// Role: Gamepad state representation with stick deadzone normalization and trigger handling.
// ==================================================================================================

using System.Numerics;

namespace Jatayu.Core.Input;

/// <summary>
/// Represents the polled state of a single connected gamepad controller.
/// </summary>
public sealed class Gamepad
{
    private const int ButtonCount = 15;
    private const int AxisCount = 6;

    private readonly bool[] _currentButtons = new bool[ButtonCount];
    private readonly bool[] _previousButtons = new bool[ButtonCount];
    private readonly float[] _axes = new float[AxisCount];

    /// <summary>
    /// Gets or sets the radial deadzone for thumbsticks (default: 0.15f).
    /// </summary>
    public float StickDeadzone { get; set; } = 0.15f;

    /// <summary>
    /// Gets or sets the trigger activation deadzone (default: 0.05f).
    /// </summary>
    public float TriggerDeadzone { get; set; } = 0.05f;

    /// <summary>
    /// Gets whether this controller index is currently connected.
    /// </summary>
    public bool IsConnected { get; internal set; }

    /// <summary>
    /// Checks if a button is currently held down.
    /// </summary>
    public bool IsButtonDown(GamepadButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < ButtonCount && _currentButtons[idx];
    }

    /// <summary>
    /// Checks if a button was pressed down in the current frame.
    /// </summary>
    public bool IsButtonPressed(GamepadButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < ButtonCount && _currentButtons[idx] && !_previousButtons[idx];
    }

    /// <summary>
    /// Checks if a button was released in the current frame.
    /// </summary>
    public bool IsButtonReleased(GamepadButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < ButtonCount && !_currentButtons[idx] && _previousButtons[idx];
    }

    /// <summary>
    /// Gets the raw axis value between -1.0f and 1.0f (or 0.0f to 1.0f for triggers).
    /// </summary>
    public float GetAxis(GamepadAxis axis)
    {
        int idx = (int)axis;
        return idx >= 0 && idx < AxisCount ? _axes[idx] : 0.0f;
    }

    /// <summary>
    /// Gets the normalized left thumbstick 2D vector with radial deadzone applied.
    /// </summary>
    public Vector2 GetLeftStick()
    {
        return ApplyRadialDeadzone(GetAxis(GamepadAxis.LeftX), GetAxis(GamepadAxis.LeftY), StickDeadzone);
    }

    /// <summary>
    /// Gets the normalized right thumbstick 2D vector with radial deadzone applied.
    /// </summary>
    public Vector2 GetRightStick()
    {
        return ApplyRadialDeadzone(GetAxis(GamepadAxis.RightX), GetAxis(GamepadAxis.RightY), StickDeadzone);
    }

    /// <summary>
    /// Updates button and axis states during input polling.
    /// </summary>
    internal void UpdateState(ReadOnlySpan<byte> buttons, ReadOnlySpan<float> axes)
    {
        Array.Copy(_currentButtons, _previousButtons, ButtonCount);

        int btnLen = Math.Min(buttons.Length, ButtonCount);
        for (int i = 0; i < btnLen; i++)
        {
            _currentButtons[i] = buttons[i] != 0;
        }

        int axisLen = Math.Min(axes.Length, AxisCount);
        for (int i = 0; i < axisLen; i++)
        {
            _axes[i] = axes[i];
        }
    }

    internal void Reset()
    {
        Array.Clear(_currentButtons);
        Array.Clear(_previousButtons);
        Array.Clear(_axes);
        IsConnected = false;
    }

    private static Vector2 ApplyRadialDeadzone(float x, float y, float deadzone)
    {
        Vector2 v = new(x, y);
        float length = v.Length();
        if (length <= deadzone)
            return Vector2.Zero;

        // Rescale smoothly from 0 to 1 beyond deadzone
        float normalizedLength = Math.Min(1.0f, (length - deadzone) / (1.0f - deadzone));
        return (v / length) * normalizedLength;
    }
}
