/* =================================================================================================
 * FILE: ShaderCooker.cs
 * LOCATION: tools/Jatayu.Cooker/ShaderCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Offline batch compilation of GLSL shaders (.vert, .frag, .comp) into SPIR-V bytecode (.spv).
 *   Enables production/Release exports to ship zero loose GLSL source files and bypass runtime
 *   ShaderC compilation overhead.
 * ================================================================================================= */

using System;
using System.IO;
using System.Text;
using Brutal.ShaderCApi;

namespace Jatayu.Cooker;

public static class ShaderCooker
{
    public static int CookAllShaders(string inputDir, string? outputDir = null, bool force = false)
    {
        outputDir ??= inputDir;

        if (!Directory.Exists(inputDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ShaderCooker] Input directory does not exist: {inputDir}");
            Console.ResetColor();
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        var shaderFiles = Directory.GetFiles(inputDir, "*.*", SearchOption.TopDirectoryOnly);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vert", ".frag", ".comp" };

        var targetFiles = shaderFiles
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToArray();

        if (targetFiles.Length == 0)
        {
            Console.WriteLine($"[ShaderCooker] No shader files (.vert, .frag, .comp) found in {inputDir}");
            return 0;
        }

        Console.WriteLine($"[ShaderCooker] Found {targetFiles.Length} shaders in '{inputDir}'. Compiling to SPIR-V...");

        // Check newest timestamp among include files (e.g. .glsl)
        DateTime newestIncludeTime = DateTime.MinValue;
        foreach (var file in Directory.GetFiles(inputDir, "*.glsl", SearchOption.TopDirectoryOnly))
        {
            var wt = File.GetLastWriteTimeUtc(file);
            if (wt > newestIncludeTime) newestIncludeTime = wt;
        }

        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();

        int compiledCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        foreach (var shaderFile in targetFiles)
        {
            string fileName = Path.GetFileName(shaderFile);
            string ext = Path.GetExtension(shaderFile).ToLowerInvariant();
            string outSpvName = fileName + ".spv";
            string outSpvPath = Path.Combine(outputDir, outSpvName);

            ShaderKind? kind = ext switch
            {
                ".vert" => ShaderKind.VertexShader,
                ".frag" => ShaderKind.FragmentShader,
                ".comp" => ShaderKind.ComputeShader,
                _ => null
            };

            if (kind == null) continue;

            DateTime srcTime = File.GetLastWriteTimeUtc(shaderFile);
            if (!force && File.Exists(outSpvPath))
            {
                DateTime spvTime = File.GetLastWriteTimeUtc(outSpvPath);
                if (spvTime >= srcTime && spvTime >= newestIncludeTime)
                {
                    skippedCount++;
                    continue;
                }
            }

            try
            {
                string source = File.ReadAllText(shaderFile);
                source = ResolveIncludes(source, inputDir);
                byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

                using var result = compiler.Compile(
                    sourceBytes,
                    kind.Value,
                    "shader"u8,
                    "main"u8,
                    options
                );

                if (result.GetCompilationStatus() != CompilationStatus.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ShaderCooker ERROR] Failed compiling {fileName} ({kind.Value}):\n{result.GetErrorMessage()}");
                    Console.ResetColor();
                    errorCount++;
                    continue;
                }

                int length = (int)result.GetLength();
                byte[] spirv = new byte[length];
                unsafe
                {
                    fixed (byte* pDst = spirv)
                    {
                        Buffer.MemoryCopy(result.GetBytes(), pDst, length, length);
                    }
                }

                File.WriteAllBytes(outSpvPath, spirv);
                Console.WriteLine($"  [OK] {fileName} -> {outSpvName} ({length:N0} bytes)");
                compiledCount++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ShaderCooker EXCEPTION] Error processing {fileName}: {ex.Message}");
                Console.ResetColor();
                errorCount++;
            }
        }

        Console.WriteLine($"[ShaderCooker] Complete: {compiledCount} compiled, {skippedCount} up-to-date, {errorCount} errors.");
        return errorCount > 0 ? 1 : 0;
    }

    private static string ResolveIncludes(string source, string baseDir)
    {
        if (!source.Contains("#include")) return source;

        var lines = source.Split('\n');
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#include \"") && trimmed.EndsWith("\""))
            {
                string includeRel = trimmed.Substring(10, trimmed.Length - 11);
                string fullPath = Path.Combine(baseDir, includeRel);
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
}
