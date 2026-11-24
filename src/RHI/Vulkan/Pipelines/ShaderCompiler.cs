/* =================================================================================================
 * FILE: ShaderCompiler.cs
 * LOCATION: src/RHI/Vulkan/Pipelines/ShaderCompiler.cs
 * 
 * WHAT THIS FILE DOES:
 *   Wraps Google ShaderC compiler (Brutal.ShaderCApi) to compile GLSL shader source code into
 *   SPIR-V bytecode with disk-based bytecode caching to eliminate runtime compilation hitches.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Initializes ShaderC compiler instance and options (Brutal.ShaderCApi.Compiler / CompileOptions).
 *   2. Compiles vertex, fragment, and compute GLSL source strings into native SPIR-V binaries.
 *   3. Hashes shader source and checks on-disk cache in cacheDir (e.g. assets/shaders/cache/).
 *   4. Creates native Vulkan VkShaderModule handles using ShaderModuleExtensions.FromSpirv.
 *   5. Implements IDisposable to free native compiler allocations.
 * ================================================================================================= */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Brutal.ShaderCApi;
using Brutal.Strings;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Jatayu.RHI.Vulkan.Pipelines;

/// <summary>
/// Compiles GLSL shaders into Vulkan SPIR-V bytecode and creates VkShaderModule handles.
/// </summary>
public sealed class ShaderCompiler : IDisposable
{
    private Compiler _compiler;
    private CompileOptions _options;
    private readonly string? _cacheDirectory;
    private bool _disposed;

    /// <summary>
    /// Initializes a new ShaderCompiler instance with optional disk caching.
    /// </summary>
    /// <param name="cacheDirectory">Optional directory path to cache compiled SPIR-V bytecode.</param>
    public ShaderCompiler(string? cacheDirectory = null)
    {
        _compiler = ShaderC.CreateCompiler();
        _options = ShaderC.CreateCompilerOptions();
        _cacheDirectory = cacheDirectory;

        if (!string.IsNullOrEmpty(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    /// <summary>
    /// Compiles GLSL source code to SPIR-V bytecode.
    /// </summary>
    public unsafe byte[] CompileToSpirv(string source, ShaderKind kind, string sourceFileName = "shader.glsl")
    {
        ArgumentNullException.ThrowIfNull(source);

        source = ResolveIncludes(source, sourceFileName);

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        // Check on-disk cache if directory is configured
        string? cacheFilePath = GetCacheFilePath(sourceBytes, kind, sourceFileName);
        if (cacheFilePath != null && File.Exists(cacheFilePath))
        {
            return File.ReadAllBytes(cacheFilePath);
        }

        using var result = _compiler.Compile(
            sourceBytes,
            kind,
            "shader"u8,
            "main"u8,
            _options
        );

        var status = result.GetCompilationStatus();
        if (status != CompilationStatus.Success)
        {
            string error = result.GetErrorMessage();
            throw new InvalidOperationException($"Shader compilation failed for '{sourceFileName}' ({kind}):\n{error}");
        }

        int length = (int)result.GetLength();
        byte[] spirv = new byte[length];
        fixed (byte* pDst = spirv)
        {
            Buffer.MemoryCopy(result.GetBytes(), pDst, length, length);
        }

        // Save to cache
        if (cacheFilePath != null)
        {
            try
            {
                File.WriteAllBytes(cacheFilePath, spirv);
            }
            catch
            {
                // Cache write failure is non-fatal
            }
        }

        return spirv;
    }

    /// <summary>
    /// Compiles GLSL source code and creates a native Vulkan VkShaderModule.
    /// </summary>
    public unsafe VkShaderModule CompileShaderModule(
        Device device,
        string source,
        ShaderKind kind,
        string sourceFileName = "shader.glsl")
    {
        ArgumentNullException.ThrowIfNull(device);

        byte[] spirv = CompileToSpirv(source, kind, sourceFileName);
        return ShaderModuleExtensions.FromSpirv(device, spirv, null);
    }

    /// <summary>
    /// Loads a pre-compiled SPIR-V shader module directly, or compiles GLSL source from disk.
    /// In Release builds (or when loose GLSL is absent), loads companion .spv files directly
    /// with zero ShaderC compilation overhead.
    /// </summary>
    public VkShaderModule CompileShaderModuleFromFile(
        Device device,
        string filePath,
        ShaderKind kind)
    {
        ArgumentNullException.ThrowIfNull(device);

        string resolvedPath = ResolveShaderFile(filePath);

        if (resolvedPath.EndsWith(".spv", StringComparison.OrdinalIgnoreCase))
        {
            byte[] spirv = File.ReadAllBytes(resolvedPath);
            return ShaderModuleExtensions.FromSpirv(device, spirv, null);
        }

        string source = File.ReadAllText(resolvedPath);
        return CompileShaderModule(device, source, kind, Path.GetFileName(resolvedPath));
    }

    /// <summary>
    /// Resolves a shader path by checking direct paths, companion .spv binaries, and base/parent directories.
    /// In Release builds, prefers pre-compiled .spv binaries for fast startup.
    /// In Debug builds, prefers raw GLSL sources to enable live shader iteration.
    /// </summary>
    public static string ResolveShaderFile(string filePath)
    {
        string spvCandidate = filePath.EndsWith(".spv", StringComparison.OrdinalIgnoreCase)
            ? filePath
            : filePath + ".spv";
        string baseSpv = Path.Combine(AppContext.BaseDirectory, spvCandidate);
        string baseSrc = Path.Combine(AppContext.BaseDirectory, filePath);

#if !DEBUG
        // Release builds must load the assets shipped beside the executable.
        if (File.Exists(baseSpv)) return baseSpv;
        if (File.Exists(baseSrc)) return baseSrc;
        if (File.Exists(spvCandidate)) return spvCandidate;
        if (File.Exists(filePath)) return filePath;
#else
        // Debug build priority: prefer loose source for hot-reloading, fall back to .spv
        if (File.Exists(filePath)) return filePath;
        if (File.Exists(spvCandidate)) return spvCandidate;
        if (File.Exists(baseSrc)) return baseSrc;
        if (File.Exists(baseSpv)) return baseSpv;
#endif

        // Search upward directory tree (up to 5 parents)
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && dir != null; i++)
        {
            string candSpv = Path.Combine(dir, spvCandidate);
            string candSrc = Path.Combine(dir, filePath);

#if !DEBUG
            if (File.Exists(candSpv)) return candSpv;
            if (File.Exists(candSrc)) return candSrc;
#else
            if (File.Exists(candSrc)) return candSrc;
            if (File.Exists(candSpv)) return candSpv;
#endif
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            $"Shader not found: neither source '{filePath}' nor pre-compiled binary '{spvCandidate}' could be located.", filePath);
    }

    private string? GetCacheFilePath(byte[] sourceBytes, ShaderKind kind, string fileName)
    {
        if (string.IsNullOrEmpty(_cacheDirectory))
            return null;

        var hash = SHA256.HashData(sourceBytes);
        string hashStr = Convert.ToHexString(hash)[..16];
        string safeName = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(_cacheDirectory, $"{safeName}_{kind}_{hashStr}.spv");
    }

    private static string ResolveIncludes(string source, string sourceFileName)
    {
        if (!source.Contains("#include")) return source;

        var lines = source.Split('\n');
        var sb = new StringBuilder();
        string baseDir = "assets/shaders";
        if (File.Exists(sourceFileName))
        {
            baseDir = Path.GetDirectoryName(sourceFileName) ?? baseDir;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#include \"") && trimmed.EndsWith("\""))
            {
                string includeRel = trimmed.Substring(10, trimmed.Length - 11);
                string fullPath = Path.Combine(baseDir, includeRel);
                if (!File.Exists(fullPath))
                {
                    fullPath = Path.Combine(AppContext.BaseDirectory, "assets", "shaders", includeRel);
                }
                if (File.Exists(fullPath))
                {
                    sb.AppendLine(File.ReadAllText(fullPath));
                    continue;
                }
            }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Disposes native ShaderC compiler handles.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _options.Dispose();
            _compiler.Dispose();
            _disposed = true;
        }
    }
}
