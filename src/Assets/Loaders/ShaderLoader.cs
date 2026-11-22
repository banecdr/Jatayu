/* =================================================================================================
 * FILE: ShaderLoader.cs
 * LOCATION: src/Assets/Loaders/ShaderLoader.cs
 * 
 * WHAT THIS FILE DOES:
 *   Loads GLSL shader source code from disk and provides file system watching for live
 *   shader editing and hot-reloading during engine development.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Reads shader source text from disk with validation.
 *   2. Sets up a FileSystemWatcher targeting shader files (*.vert, *.frag, *.comp, *.glsl).
 *   3. Debounces file change events and broadcasts a ShaderChanged event.
 *   4. Implements IDisposable to stop and release the file watcher cleanly.
 * ================================================================================================= */

using System;
using System.IO;

namespace Jatayu.Assets.Loaders;

/// <summary>
/// Event arguments containing details of a modified shader file.
/// </summary>
public sealed class ShaderChangedEventArgs : EventArgs
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    public ShaderChangedEventArgs(string filePath)
    {
        FilePath = filePath;
    }
}

/// <summary>
/// Loads shader source files and watches for runtime file modifications to enable hot-reloading.
/// </summary>
public sealed class ShaderLoader : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastEventTime = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Triggered when a shader file in the watched directory is modified on disk.
    /// </summary>
    public event EventHandler<ShaderChangedEventArgs>? ShaderChanged;

    /// <summary>
    /// Initializes a new ShaderLoader instance.
    /// </summary>
    /// <param name="watchDirectory">Directory to watch for shader changes (or null to disable live watching).</param>
    public ShaderLoader(string? watchDirectory = null)
    {
        if (!string.IsNullOrEmpty(watchDirectory) && Directory.Exists(watchDirectory))
        {
            _watcher = new FileSystemWatcher(watchDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
    }

    /// <summary>
    /// Reads shader source code text from disk.
    /// </summary>
    public static string LoadSource(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Shader file not found: {filePath}", filePath);

        return File.ReadAllText(filePath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext is not (".vert" or ".frag" or ".comp" or ".glsl"))
            return;

        // Simple debounce (50ms) to avoid duplicate OS notifications
        var now = DateTime.UtcNow;
        if ((now - _lastEventTime).TotalMilliseconds < 50)
            return;

        _lastEventTime = now;
        ShaderChanged?.Invoke(this, new ShaderChangedEventArgs(e.FullPath));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            _disposed = true;
        }
    }
}
