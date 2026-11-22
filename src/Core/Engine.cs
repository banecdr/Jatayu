// ==================================================================================================
// Jatayu Engine - Master Engine Architecture
// Module: Engine.cs
// Role: Master engine coordinator managing lifecycle, subsystem initialization, and zero-allocation game loop.
// ==================================================================================================

using Brutal.GlfwApi;
using Jatayu.Assets;
using Jatayu.Core.Config;
using Jatayu.Core.Events;
using Jatayu.Core.Input;
using Jatayu.Core.Jobs;
using Jatayu.Core.Logging;
using Jatayu.Core.Profiling;
using Jatayu.Core.Time;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Upload;
using Jatayu.Scene;

namespace Jatayu.Core;

/// <summary>
/// Master engine coordinator for Jatayu Engine.
/// Initializes all subsystems, coordinates multithreading jobs, updates the active scene ECS,
/// and drives the zero-allocation game loop.
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly EngineConfig _config;
    private readonly Settings _settings;

    private Window? _window;
    private VulkanContext? _vulkanContext;
    private GpuAllocator? _gpuAllocator;
    private JobSystem? _jobSystem;
    private UploadManager? _uploadManager;
    private AssetManager? _assetManager;
    private Scene.Scene? _activeScene;
    private GameTime? _time;

    private bool _isRunning;
    private bool _isDisposed;

    /// <summary>
    /// Gets the engine startup configuration.
    /// </summary>
    public EngineConfig Config => _config;

    /// <summary>
    /// Gets the user settings instance.
    /// </summary>
    public Settings Settings => _settings;

    /// <summary>
    /// Gets the native GLFW window.
    /// </summary>
    public Window Window => _window ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the active Vulkan hardware context.
    /// </summary>
    public VulkanContext Context => _vulkanContext ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the GPU memory allocator.
    /// </summary>
    public GpuAllocator Allocator => _gpuAllocator ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the multithreaded job scheduler.
    /// </summary>
    public JobSystem Jobs => _jobSystem ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the runtime asset manager.
    /// </summary>
    public AssetManager Assets => _assetManager ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the asynchronous GPU upload manager.
    /// </summary>
    public UploadManager Uploads => _uploadManager ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the active ECS scene.
    /// </summary>
    public Scene.Scene Scene => _activeScene ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Gets the high-precision game clock.
    /// </summary>
    public GameTime Time => _time ?? throw new InvalidOperationException("Engine not initialized.");

    /// <summary>
    /// Constructs a new Engine instance with the specified configuration.
    /// </summary>
    public Engine(EngineConfig? config = null)
    {
        _config = config ?? EngineConfig.Default;
        _settings = Settings.Load();
    }

    /// <summary>
    /// Initializes all core engine subsystems, RHI, and the active ECS scene.
    /// </summary>
    public void Initialize()
    {
        Log.InitializeFileLogging(_config.LogFilePath);
        Log.Info("==================================================================");
        Log.Info($"Initializing Jatayu Engine ({_config.WindowTitle})");
        Log.Info("==================================================================");

        // 1. Multithreading Job System
        _jobSystem = new JobSystem();
        Log.Info($"JobSystem online with {_jobSystem.WorkerCount} worker threads.");

        // 2. Input System & Default Actions
        InputActions.SetupDefaults();
        Log.Info("Input subsystem and default action bindings initialized.");

        // 3. Desktop Window
        _window = new Window(
            title: _config.WindowTitle,
            width: _config.WindowWidth,
            height: _config.WindowHeight,
            resizable: true,
            vsync: _config.VSync
        );
        Log.Info($"Window created: {_config.WindowWidth}x{_config.WindowHeight} (VSync: {_config.VSync})");

        // 4. Vulkan 1.3 Hardware Context
        _vulkanContext = new VulkanContext(_window);
        Log.Info($"Vulkan 1.3 Context initialized: {_vulkanContext.DeviceName}");

        // 5. GPU Allocator (VMA)
        _gpuAllocator = new GpuAllocator(_vulkanContext);

        // 6. Upload Manager & Staging Ring
        _uploadManager = new UploadManager(_vulkanContext, _gpuAllocator, (ulong)_config.StagingRingSizeMB * 1024 * 1024);

        // 7. Runtime Asset Manager
        _assetManager = new AssetManager();

        // 8. High-Precision Timing Clock
        _time = new GameTime();

        // 9. Active ECS Scene
        _activeScene = new Scene.Scene();
        Log.Info("ECS Scene initialized with zero-allocation Arch archetype backing.");
    }

    /// <summary>
    /// Executes the main engine game loop until shutdown is requested.
    /// </summary>
    /// <param name="onUpdate">Optional update callback invoked each frame with delta time in seconds.</param>
    /// <param name="onRender">Optional render callback invoked each frame.</param>
    public void Run(Action<float>? onUpdate = null, Action? onRender = null)
    {
        if (_window == null)
            Initialize();

        _isRunning = true;

        Log.Info("Entering engine main loop.");

        while (_isRunning && !_window!.ShouldClose)
        {
            AllocationTracker.BeginFrame();
            Profiler.BeginFrame();

            // 1. Process OS window events and poll input states
            using (Profiler.Scope("PollEvents"))
            {
                Input.Input.BeginFrame();
                _window.PollEvents();
            }

            if (_window.IsMinimized)
            {
                Profiler.EndFrame();
                AllocationTracker.EndFrame();
                Thread.Sleep(16);
                continue;
            }

            // 2. Tick high-precision game clock
            using (Profiler.Scope("GameTime"))
            {
                _time!.Update();
            }
            float dt = _time!.DeltaTime;

            using (Profiler.Scope("Frame"))
            {
                // 3. Drain main thread dispatcher within 2.0 ms budget
                using (Profiler.Scope("MainThreadDispatcher"))
                {
                    MainThreadDispatcher.Default.Drain(2.0);
                }

                // 4. Gameplay & ECS update tick
                using (Profiler.Scope("Update"))
                {
                    onUpdate?.Invoke(dt);
                }

                // 5. Render submission
                using (Profiler.Scope("Render"))
                {
                    onRender?.Invoke();
                }
            }

            Profiler.EndFrame();
            AllocationTracker.EndFrame();
        }

        Log.Info("Exiting engine main loop.");
    }

    /// <summary>
    /// Requests the engine to exit on the next frame boundary.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
    }

    /// <summary>
    /// Cleans up and disposes all engine resources deterministically.
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            Log.Info("Shutting down Jatayu Engine subsystems...");

            if (_vulkanContext != null && !_vulkanContext.Device.IsNull())
            {
                _vulkanContext.WaitIdle();
            }

            _activeScene?.Dispose();
            _assetManager?.Dispose();
            _uploadManager?.Dispose();
            _gpuAllocator?.Dispose();
            _vulkanContext?.Dispose();
            _window?.Dispose();

            _jobSystem?.Dispose();
            Log.Shutdown();

            _isDisposed = true;
        }
    }
}
