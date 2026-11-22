// ==================================================================================================
// Jatayu Engine - Core Input
// Module: InputActions.cs
// Role: Action and axis mapping abstraction supporting user rebindings and multi-device input queries.
// ==================================================================================================

using System.Runtime.CompilerServices;

namespace Jatayu.Core.Input;

/// <summary>
/// Defines an action binding that checks keyboard, mouse, or gamepad inputs.
/// </summary>
public sealed class ActionBinding
{
    public string Name { get; set; } = string.Empty;
    public Key PrimaryKey { get; set; } = Key.Unknown;
    public Key SecondaryKey { get; set; } = Key.Unknown;
    public MouseButton? MouseButton { get; set; }
    public GamepadButton? GamepadButton { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDown(int gamepadIndex = 0)
    {
        if (PrimaryKey != Key.Unknown && Input.IsKeyDown(PrimaryKey)) return true;
        if (SecondaryKey != Key.Unknown && Input.IsKeyDown(SecondaryKey)) return true;
        if (MouseButton.HasValue && Input.IsMouseButtonDown(MouseButton.Value)) return true;
        if (GamepadButton.HasValue && Input.GetGamepad(gamepadIndex).IsButtonDown(GamepadButton.Value)) return true;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPressed(int gamepadIndex = 0)
    {
        if (PrimaryKey != Key.Unknown && Input.IsKeyPressed(PrimaryKey)) return true;
        if (SecondaryKey != Key.Unknown && Input.IsKeyPressed(SecondaryKey)) return true;
        if (MouseButton.HasValue && Input.IsMouseButtonPressed(MouseButton.Value)) return true;
        if (GamepadButton.HasValue && Input.GetGamepad(gamepadIndex).IsButtonPressed(GamepadButton.Value)) return true;
        return false;
    }
}

/// <summary>
/// Defines an axis binding composed of positive/negative keys or gamepad analog axes.
/// </summary>
public sealed class AxisBinding
{
    public string Name { get; set; } = string.Empty;
    public Key PositiveKey { get; set; } = Key.Unknown;
    public Key NegativeKey { get; set; } = Key.Unknown;
    public GamepadAxis? GamepadAxis { get; set; }
    public float Scale { get; set; } = 1.0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetValue(int gamepadIndex = 0)
    {
        float value = 0.0f;
        if (PositiveKey != Key.Unknown && Input.IsKeyDown(PositiveKey)) value += 1.0f;
        if (NegativeKey != Key.Unknown && Input.IsKeyDown(NegativeKey)) value -= 1.0f;

        if (GamepadAxis.HasValue)
        {
            float gVal = Input.GetGamepad(gamepadIndex).GetAxis(GamepadAxis.Value);
            if (MathF.Abs(gVal) > MathF.Abs(value))
            {
                value = gVal;
            }
        }

        return value * Scale;
    }
}

/// <summary>
/// Central manager for configurable input actions and axes.
/// </summary>
public static class InputActions
{
    private static readonly Dictionary<string, ActionBinding> s_actions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AxisBinding> s_axes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers or updates an action binding.
    /// </summary>
    public static void MapAction(ActionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        s_actions[binding.Name] = binding;
    }

    /// <summary>
    /// Registers or updates an axis binding.
    /// </summary>
    public static void MapAxis(AxisBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        s_axes[binding.Name] = binding;
    }

    /// <summary>
    /// Checks if a mapped action is currently held down.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsActionDown(string actionName, int gamepadIndex = 0)
    {
        return s_actions.TryGetValue(actionName, out var binding) && binding.IsDown(gamepadIndex);
    }

    /// <summary>
    /// Checks if a mapped action was triggered down this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsActionPressed(string actionName, int gamepadIndex = 0)
    {
        return s_actions.TryGetValue(actionName, out var binding) && binding.IsPressed(gamepadIndex);
    }

    /// <summary>
    /// Gets the normalized float value (-1.0 to 1.0) of a mapped axis.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetAxis(string axisName, int gamepadIndex = 0)
    {
        return s_axes.TryGetValue(axisName, out var binding) ? binding.GetValue(gamepadIndex) : 0.0f;
    }

    /// <summary>
    /// Sets up default standard first-person / third-person game bindings.
    /// </summary>
    public static void SetupDefaults()
    {
        s_actions.Clear();
        s_axes.Clear();

        MapAction(new ActionBinding { Name = "Jump", PrimaryKey = Key.Space, GamepadButton = GamepadButton.A });
        MapAction(new ActionBinding { Name = "Sprint", PrimaryKey = Key.LeftShift, GamepadButton = GamepadButton.LeftThumb });
        MapAction(new ActionBinding { Name = "Crouch", PrimaryKey = Key.LeftControl, GamepadButton = GamepadButton.B });
        MapAction(new ActionBinding { Name = "Interact", PrimaryKey = Key.E, GamepadButton = GamepadButton.X });
        MapAction(new ActionBinding { Name = "Fire", MouseButton = MouseButton.Left, GamepadButton = GamepadButton.RightBumper });

        MapAxis(new AxisBinding { Name = "MoveX", PositiveKey = Key.D, NegativeKey = Key.A, GamepadAxis = GamepadAxis.LeftX });
        MapAxis(new AxisBinding { Name = "MoveY", PositiveKey = Key.W, NegativeKey = Key.S, GamepadAxis = GamepadAxis.LeftY });
        MapAxis(new AxisBinding { Name = "LookX", GamepadAxis = GamepadAxis.RightX });
        MapAxis(new AxisBinding { Name = "LookY", GamepadAxis = GamepadAxis.RightY });
    }
}
