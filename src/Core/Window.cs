/* =================================================================================================
 * FILE: Window.cs
 * LOCATION: src/Core/Window.cs
 * 
 * WHAT THIS FILE DOES:
 *   This is the operating system window wrapper for the engine. It opens an OS desktop window 
 *   using GLFW (via BRUTAL.Glfw), intercepts input and resize events from the operating system, 
 *   controls V-Sync configuration, and instructs GLFW to NOT create an OpenGL context so Vulkan 
 *   can take direct control of the window's surface.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. GLFW Initialization & Vulkan Hints:
 *      Before opening a window, it calls Glfw.Init(). It then sets GlfwWindowHint.ClientApi to 0. 
 *      Normally GLFW defaults to setting up OpenGL. Telling it 0 means "no graphics API", which 
 *      is the required setting for Vulkan.
 *
 *   2. Window Creation & Centering:
 *      It creates the native OS window using Glfw.CreateWindow(). Once created, it queries the 
 *      connected monitors and centers the window on the primary screen so it doesn't open in an 
 *      awkward corner.
 *
 *   3. Framebuffer Size vs Window Size (High-DPI / 4K Scaling):
 *      On modern monitors (like Retina displays or Windows 4K screens set to 150% scaling), 
 *      the physical pixel count (FramebufferSize) is different from the OS window coordinates (Size). 
 *      Vulkan renders to physical pixels, so this class tracks FramebufferWidth and FramebufferHeight 
 *      and fires the Resized event whenever the physical pixel resolution changes.
 *
 *   4. Minimization Detection:
 *      When you minimize a window, its framebuffer size drops to 0x0. If Vulkan tries to create 
 *      a swapchain with zero dimensions, the graphics driver crashes. This class tracks 
 *      IsMinimized so the renderer can simply pause drawing until the window is restored.
 *
 *   5. V-Sync (Vertical Synchronization) Option:
 *      In Vulkan, V-Sync is not a simple GLFW flag; it is governed by the swapchain's present mode 
 *      (FifoKHR for V-Sync ON, vs ImmediateKHR/MailboxKHR for V-Sync OFF). This class provides a 
 *      VSync property and a VSyncChanged event. When you toggle VSync at runtime, the event tells 
 *      the Vulkan renderer to rebuild the swapchain with the requested sync mode.
 *
 *   6. Event Polling:
 *      Operating systems require windows to regularly "pump" messages (mouse moves, keyboard hits, 
 *      close button clicks). PollEvents() handles this each frame.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Title: The text shown in the window's title bar.
 *   - Width, Height: The logical dimensions of the window in screen units.
 *   - FramebufferWidth, FramebufferHeight: The actual physical pixel dimensions used by Vulkan.
 *   - AspectRatio: Width divided by height (e.g. 1.777 for 16:9). Used by camera projection matrices.
 *   - VSync: True to synchronize frame presentation with monitor refresh rate (caps FPS, prevents tearing).
 *            False for uncapped framerate (allows testing raw GPU throughput).
 *   - ShouldClose: True if the user clicked the [X] button or pressed Alt+F4. The main loop checks this.
 *   - IsMinimized: True if the window is iconified/minimized. The renderer checks this to pause rendering.
 *   - GlfwHandle: The raw GlfwWindow instance. The Vulkan subsystem needs this to create a VkSurfaceKHR.
 *   - Resized: An event fired whenever the window's physical framebuffer size changes.
 *   - FocusChanged: An event fired when the window gains or loses input focus.
 *   - VSyncChanged: An event fired when the VSync setting is toggled so Vulkan can rebuild its swapchain.
 *
 * HOW TO USE IT IN THE MAIN LOOP:
 *   using var window = new Window("My 3D Engine", 1920, 1080, vsync: true);
 *   while (!window.ShouldClose)
 *   {
 *       window.PollEvents();
 *       if (!window.IsMinimized)
 *       {
 *           renderer.Draw();
 *       }
 *   }
 * ================================================================================================= */

using Brutal.GlfwApi;
using Brutal.Numerics;
using Jatayu.Core.Events;
using EngineInput = Jatayu.Core.Input.Input;

namespace Jatayu.Core;

/// <summary>
/// Manages the desktop window lifecycle, OS event pumping, framebuffer sizing, and V-Sync configuration for Vulkan.
/// </summary>
public sealed class Window : IDisposable
{
    private static bool s_glfwInitialized;
    private string _title = "BRUTAL Engine";
    private GlfwWindow _window;
    private bool _vsync;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying BRUTAL GlfwWindow handle.
    /// Needed by Vulkan to create a VkSurfaceKHR.
    /// </summary>
    public GlfwWindow GlfwHandle => _window;

    /// <summary>
    /// Title text displayed in the window's title bar.
    /// Setting this property updates the OS window title immediately.
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (_window != null && !_window.IsNull())
            {
                _window.Title = value;
            }
        }
    }

    /// <summary>
    /// Logical width of the window in screen coordinates.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// Logical height of the window in screen coordinates.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// Physical width of the framebuffer in pixels.
    /// Use this for Vulkan swapchain and viewport creation.
    /// </summary>
    public int FramebufferWidth { get; private set; }

    /// <summary>
    /// Physical height of the framebuffer in pixels.
    /// Use this for Vulkan swapchain and viewport creation.
    /// </summary>
    public int FramebufferHeight { get; private set; }

    /// <summary>
    /// Aspect ratio of the framebuffer (Width / Height).
    /// </summary>
    public float AspectRatio => FramebufferHeight > 0 ? (float)FramebufferWidth / FramebufferHeight : 1.0f;

    /// <summary>
    /// Gets or sets whether Vertical Synchronization (V-Sync) is enabled.
    /// When enabled (true), frames are synchronized with the monitor refresh rate to prevent tearing.
    /// When disabled (false), framerate is uncapped.
    /// Changing this at runtime fires <see cref="VSyncChanged"/> to rebuild the Vulkan swapchain.
    /// </summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync != value)
            {
                _vsync = value;
                VSyncChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Returns true if the user clicked the window's close button or requested a close.
    /// </summary>
    public bool ShouldClose => _window.ShouldClose;

    /// <summary>
    /// Returns true if the window is currently minimized to the taskbar.
    /// When true, rendering should be skipped to prevent zero-size swapchain crashes.
    /// </summary>
    public bool IsMinimized { get; private set; }

    /// <summary>
    /// Event fired when the physical framebuffer is resized.
    /// Passes (int newWidth, int newHeight) in physical pixels.
    /// </summary>
    public event Action<int, int>? Resized;

    /// <summary>
    /// Event fired when window focus is gained or lost.
    /// </summary>
    public event Action<bool>? FocusChanged;

    /// <summary>
    /// Event fired when V-Sync is toggled, signaling Vulkan to update its swapchain presentation mode.
    /// </summary>
    public event Action<bool>? VSyncChanged;

    /// <summary>
    /// Event fired when a keyboard key is pressed, released, or repeated.
    /// Passes (GlfwKey key, GlfwInputAction action).
    /// </summary>
    /// <summary>
    /// Event fired when a keyboard key is pressed, released, or repeated.
    /// Passes (GlfwKey key, GlfwKeyAction action).
    /// </summary>
    public event Action<GlfwKey, GlfwKeyAction>? KeyAction;

    /// <summary>
    /// Checks if a keyboard key is currently held down.
    /// </summary>
    /// <param name="key">The GLFW key code to check.</param>
    /// <returns>True if the key is currently pressed, false otherwise.</returns>
    public bool IsKeyDown(GlfwKey key) => _window.GetKey(key) == GlfwKeyAction.Press;

    /// <summary>
    /// Creates and displays a new GLFW window configured specifically for Vulkan.
    /// </summary>
    /// <param name="title">Title shown on the window header.</param>
    /// <param name="width">Initial width in screen coordinates.</param>
    /// <param name="height">Initial height in screen coordinates.</param>
    /// <param name="resizable">Whether the user can resize the window with their mouse.</param>
    /// <param name="vsync">Whether V-Sync is enabled by default (defaults to true).</param>
    public Window(string title = "BRUTAL Engine", int width = 1280, int height = 720, bool resizable = true, bool vsync = true)
    {
        _title = title;
        Width = width;
        Height = height;
        _vsync = vsync;

        EnsureGlfwInitialized();

        // Configure GLFW hints for Vulkan:
        // CRITICAL: ClientApi = 0 tells GLFW NOT to create an OpenGL context.
        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        Glfw.WindowHint(GlfwWindowHint.Resizable, resizable ? 1 : 0);

        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
        {
            Title = title,
            Size = new int2(width, height)
        });

        if (_window.IsNull())
            throw new InvalidOperationException("Failed to create GLFW window.");

        // Read physical framebuffer size (handles High-DPI / Retina monitors)
        var fbSize = _window.FramebufferSize;
        FramebufferWidth = fbSize.X;
        FramebufferHeight = fbSize.Y;

        // Center window on primary monitor
        var monitors = Glfw.GetMonitors();
        if (monitors.Length > 0)
        {
            _window.CenterToMonitor(monitors[0]);
        }

        // Wire up GLFW events
        _window.OnFramebufferSize += HandleFramebufferResize;
        _window.OnFocus += HandleFocus;
        _window.OnIconify += HandleIconify;
        _window.OnKey += HandleKey;
        _window.OnMouseButton += HandleMouseButton;
        _window.OnCursorPos += HandleCursorPos;
        _window.OnScroll += HandleScroll;
    }

    private void HandleKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        EngineInput.RecordKeyEvent((int)key, action != GlfwKeyAction.Release);
        KeyAction?.Invoke(key, action);
    }

    private void HandleMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        EngineInput.RecordMouseButton((int)button, action == GlfwButtonAction.Press);
    }

    private void HandleCursorPos(GlfwWindow window, double2 pos)
    {
        EngineInput.RecordMouseMove((float)pos.X, (float)pos.Y);
    }

    private void HandleScroll(GlfwWindow window, double2 offset)
    {
        EngineInput.RecordScroll((float)offset.X, (float)offset.Y);
    }

    private static void EnsureGlfwInitialized()
    {
        if (!s_glfwInitialized)
        {
            if (!Glfw.Init())
                throw new InvalidOperationException("Failed to initialize GLFW.");

            s_glfwInitialized = true;
        }
    }

    private void HandleFramebufferResize(GlfwWindow window, int2 newSize)
    {
        FramebufferWidth = newSize.X;
        FramebufferHeight = newSize.Y;
        IsMinimized = (newSize.X == 0 || newSize.Y == 0);

        if (!IsMinimized)
        {
            Width = window.Width;
            Height = window.Height;
            Resized?.Invoke(newSize.X, newSize.Y);
            EventBus.Publish(new WindowResizedEvent(newSize.X, newSize.Y));
        }
    }

    private void HandleFocus(GlfwWindow window, GlfwFocusEvent focusEvent)
    {
        FocusChanged?.Invoke(focusEvent == GlfwFocusEvent.GainFocus);
    }

    private void HandleIconify(GlfwWindow window, GlfwIconifyEvent iconifyEvent)
    {
        IsMinimized = (iconifyEvent == GlfwIconifyEvent.Iconified);
    }

    /// <summary>
    /// Pumps all pending OS events (mouse, keyboard, window moves).
    /// Call this once per frame at the beginning of your main loop.
    /// </summary>
    public void PollEvents()
    {
        Glfw.PollEvents();
    }

    /// <summary>
    /// Waits until at least one event is received before returning.
    /// Useful for saving CPU/GPU when the engine is paused or minimized.
    /// </summary>
    public void WaitEvents()
    {
        Glfw.WaitEvents();
    }

    /// <summary>
    /// Requests the window to close.
    /// </summary>
    public void Close()
    {
        _window.ShouldClose = true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_window != null && !_window.IsNull())
            {
                _window.OnFramebufferSize -= HandleFramebufferResize;
                _window.OnFocus -= HandleFocus;
                _window.OnIconify -= HandleIconify;
                _window.OnKey -= HandleKey;
                _window.OnMouseButton -= HandleMouseButton;
                _window.OnCursorPos -= HandleCursorPos;
                _window.OnScroll -= HandleScroll;
                _window.Dispose();
            }

            _disposed = true;
        }
    }
}
