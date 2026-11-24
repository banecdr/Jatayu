// ==================================================================================================
// Jatayu Engine - Core Input
// Module: Input.cs
// Role: Central input polling system tracking keyboard, mouse, and gamepads with zero per-frame heap allocations.
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Jatayu.Core.Input;

/// <summary>
/// Central static input subsystem for Jatayu Engine.
/// Maintains instantaneous, pressed, and released states for keyboard, mouse, and gamepads.
/// </summary>
public static class Input
{
    private const int MaxKeys = 512;
    private const int MaxMouseButtons = 8;
    private const int MaxGamepads = 4;

    private static readonly bool[] s_currentKeys = new bool[MaxKeys];
    private static readonly bool[] s_previousKeys = new bool[MaxKeys];

    private static readonly bool[] s_currentMouse = new bool[MaxMouseButtons];
    private static readonly bool[] s_previousMouse = new bool[MaxMouseButtons];

    private static Vector2 s_mousePosition;
    private static Vector2 s_previousMousePosition;
    private static Vector2 s_mouseDelta;
    private static Vector2 s_scrollDelta;

    private static readonly Gamepad[] s_gamepads = new Gamepad[MaxGamepads];

    static Input()
    {
        for (int i = 0; i < MaxGamepads; i++)
        {
            s_gamepads[i] = new Gamepad();
        }
    }

    /// <summary>
    /// Gets the current cursor position in window screen coordinates.
    /// </summary>
    public static Vector2 MousePosition => s_mousePosition;

    /// <summary>
    /// Gets the mouse delta movement between the current and previous frames.
    /// </summary>
    public static Vector2 MouseDelta => s_mouseDelta;

    /// <summary>
    /// Gets the mouse scroll wheel delta in the current frame.
    /// </summary>
    public static Vector2 ScrollDelta => s_scrollDelta;

    /// <summary>
    /// Returns true if the key is currently held down.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKeyDown(Key key)
    {
        int idx = (int)key;
        return idx >= 0 && idx < MaxKeys && s_currentKeys[idx];
    }

    /// <summary>
    /// Returns true if the key was pressed this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKeyPressed(Key key)
    {
        int idx = (int)key;
        return idx >= 0 && idx < MaxKeys && s_currentKeys[idx] && !s_previousKeys[idx];
    }

    /// <summary>
    /// Returns true if the key was released this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsKeyReleased(Key key)
    {
        int idx = (int)key;
        return idx >= 0 && idx < MaxKeys && !s_currentKeys[idx] && s_previousKeys[idx];
    }

    /// <summary>
    /// Returns true if the mouse button is currently held down.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMouseButtonDown(MouseButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < MaxMouseButtons && s_currentMouse[idx];
    }

    /// <summary>
    /// Returns true if the mouse button was pressed this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMouseButtonPressed(MouseButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < MaxMouseButtons && s_currentMouse[idx] && !s_previousMouse[idx];
    }

    /// <summary>
    /// Returns true if the mouse button was released this frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMouseButtonReleased(MouseButton button)
    {
        int idx = (int)button;
        return idx >= 0 && idx < MaxMouseButtons && !s_currentMouse[idx] && s_previousMouse[idx];
    }

    /// <summary>
    /// Gets the gamepad controller at index (0 to 3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Gamepad GetGamepad(int index)
    {
        if (index < 0 || index >= MaxGamepads)
            throw new ArgumentOutOfRangeException(nameof(index), "Gamepad index must be between 0 and 3.");

        return s_gamepads[index];
    }

    private static bool s_mouseInitialized;

    /// <summary>
    /// Prepares input state at the beginning of each frame.
    /// Copies current keys to previous keys and resets per-frame deltas.
    /// </summary>
    public static void BeginFrame()
    {
        Array.Copy(s_currentKeys, s_previousKeys, MaxKeys);
        Array.Copy(s_currentMouse, s_previousMouse, MaxMouseButtons);

        s_mouseDelta = Vector2.Zero;
        s_scrollDelta = Vector2.Zero;
    }

    /// <summary>
    /// Records raw keyboard event from window callback.
    /// </summary>
    public static void RecordKeyEvent(int keyCode, bool isDown)
    {
        if (keyCode >= 0 && keyCode < MaxKeys)
        {
            s_currentKeys[keyCode] = isDown;
        }
    }

    /// <summary>
    /// Records raw mouse button event from window callback.
    /// </summary>
    public static void RecordMouseButton(int buttonCode, bool isDown)
    {
        if (buttonCode >= 0 && buttonCode < MaxMouseButtons)
        {
            s_currentMouse[buttonCode] = isDown;
        }
    }

    /// <summary>
    /// Records cursor position event from window callback.
    /// </summary>
    public static void RecordMouseMove(float x, float y)
    {
        var newPos = new Vector2(x, y);
        if (s_mouseInitialized)
        {
            s_mouseDelta += newPos - s_mousePosition;
        }
        else
        {
            s_mouseInitialized = true;
        }
        s_mousePosition = newPos;
    }

    /// <summary>
    /// Resets the accumulated mouse movement delta for the current frame.
    /// </summary>
    public static void ResetMouseDelta()
    {
        s_mouseDelta = Vector2.Zero;
    }

    /// <summary>
    /// Records mouse scroll wheel event from window callback.
    /// </summary>
    public static void RecordScroll(float xOffset, float yOffset)
    {
        s_scrollDelta += new Vector2(xOffset, yOffset);
    }

    /// <summary>
    /// Resets all input states.
    /// </summary>
    public static void Reset()
    {
        Array.Clear(s_currentKeys);
        Array.Clear(s_previousKeys);
        Array.Clear(s_currentMouse);
        Array.Clear(s_previousMouse);
        s_mousePosition = Vector2.Zero;
        s_previousMousePosition = Vector2.Zero;
        s_mouseDelta = Vector2.Zero;
        s_scrollDelta = Vector2.Zero;
        s_mouseInitialized = false;

        for (int i = 0; i < MaxGamepads; i++)
        {
            s_gamepads[i].Reset();
        }
    }
}
