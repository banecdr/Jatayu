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
    /// Compiles a GLSL shader file from disk and creates a native Vulkan VkShaderModule.
    /// </summary>
    public VkShaderModule CompileShaderModuleFromFile(
        Device device,
        string filePath,
        ShaderKind kind)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Shader source file not found: {filePath}", filePath);

        string source = File.ReadAllText(filePath);
        return CompileShaderModule(device, source, kind, Path.GetFileName(filePath));
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
