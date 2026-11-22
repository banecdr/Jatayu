/* =================================================================================================
 * FILE: Program.cs
 * LOCATION: tools/Jatayu.Cooker/Program.cs
 * 
 * WHAT THIS FILE DOES:
 *   CLI Entry Point for the Jatayu Offline Asset Cooker Tool.
 *   Provides command-line verbs for batch asset processing, MeshOptimizer geometry optimization,
 *   texture downsampling/resident tail extraction, and packaging into contiguous .jpak containers.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("                JATAYU OFFLINE ASSET COOKER & PACKAGER (SPRINT 4)              ");
        Console.WriteLine("================================================================================");

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "cook-texture":
                    if (args.Length < 3) { Console.WriteLine("Usage: cook-texture <inputFile> <outputFile>"); return 1; }
                    return ExecuteCookTexture(args[1], args[2]);

                case "cook-model":
                    if (args.Length < 3) { Console.WriteLine("Usage: cook-model <inputGltf> <outputDir>"); return 1; }
                    return ExecuteCookModel(args[1], args[2]);

                case "cook-pak":
                    if (args.Length < 3) { Console.WriteLine("Usage: cook-pak <outputJpak> <inputDir>"); return 1; }
                    return ExecuteCookPak(args[1], args[2]);

                case "inspect":
                    if (args.Length < 2) { Console.WriteLine("Usage: inspect <packagePath.jpak>"); return 1; }
                    return ExecuteInspect(args[1]);

                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Error] Unknown cooker command: '{command}'");
                    Console.ResetColor();
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Cooker Exception]: {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return 1;
        }
    }

    private static int ExecuteCookTexture(string inputFile, string outputFile)
    {
        Console.WriteLine($"[Cooker] Cooking texture: {inputFile} -> {outputFile}");
        var guid = AssetGuid.FromPath(inputFile);
        var bytes = TextureCooker.CookImageFile(inputFile, guid);
        File.WriteAllBytes(outputFile, bytes);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Cooker] Successfully baked {bytes.Length:N0} bytes with GUID: {guid}");
        Console.ResetColor();
        return 0;
    }

    private static int ExecuteInspect(string jpakPath)
    {
        if (!File.Exists(jpakPath))
        {
            Console.WriteLine($"[Error] Package file not found: {jpakPath}");
            return 1;
        }

        using var fs = File.OpenRead(jpakPath);
        using var reader = new BinaryReader(fs);

        int headerSize = Marshal.SizeOf<JpakHeader>();
        Span<byte> headerBytes = stackalloc byte[headerSize];
        fs.ReadExactly(headerBytes);
        var header = MemoryMarshal.Read<JpakHeader>(headerBytes);

        if (!header.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Invalid .jpak header (Magic: 0x{header.Magic:X8}, Version: {header.Version})");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"[Package] File: {Path.GetFileName(jpakPath)}");
        Console.WriteLine($"  Entries      : {header.EntryCount}");
        Console.WriteLine($"  TOC Offset   : {header.TocOffset} bytes");
        Console.WriteLine($"  Data Offset  : {header.DataOffset} bytes");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("  #   | GUID             | Type      | Offset     | Compressed | Uncompressed");
        Console.WriteLine("--------------------------------------------------------------------------------");

        fs.Seek((long)header.TocOffset, SeekOrigin.Begin);
        int entrySize = Marshal.SizeOf<JpakEntry>();
        Span<byte> entryBytes = stackalloc byte[entrySize];

        for (int i = 0; i < header.EntryCount; i++)
        {
            fs.ReadExactly(entryBytes);
            var entry = MemoryMarshal.Read<JpakEntry>(entryBytes);
            Console.WriteLine($"  {i,3} | {entry.Guid} | {entry.Type,-9} | {entry.Offset,10} | {entry.CompressedSize,10} | {entry.UncompressedSize,12}");
        }

        Console.WriteLine("================================================================================");
        return 0;
    }

    private static int ExecuteCookModel(string gltfPath, string outputDir)
    {
        if (!File.Exists(gltfPath))
        {
            Console.WriteLine($"[Error] glTF model file not found: {gltfPath}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"[Cooker] Loading glTF model: {gltfPath}");
        var rawModel = Jatayu.AssetFormats.Loaders.ModelLoader.LoadFromFile(gltfPath);

        var modelName = Path.GetFileNameWithoutExtension(gltfPath);
        Console.WriteLine($"[Cooker] Loaded model '{modelName}' with {rawModel.Submeshes.Count} submeshes");
        for (int i = 0; i < rawModel.Submeshes.Count; i++)
        {
            var submesh = rawModel.Submeshes[i];
            var submeshName = string.IsNullOrEmpty(submesh.Name) ? $"submesh_{i}" : submesh.Name;
            var guid = AssetGuid.FromPath($"{modelName}/{submeshName}");
            var outputPath = Path.Combine(outputDir, $"{guid}.jmesh");

            Console.WriteLine($"  Baking submesh {i}: '{submeshName}' ({submesh.Vertices.Length} verts, {submesh.Indices.Length} indices) -> {Path.GetFileName(outputPath)}");
            var bytes = MeshCooker.CookRawSubmesh(submesh, guid);
            File.WriteAllBytes(outputPath, bytes);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Cooker] Successfully cooked all submeshes for '{modelName}'");
        Console.ResetColor();
        return 0;
    }

    private static int ExecuteCookPak(string outputJpak, string inputDir)
    {
        if (!Directory.Exists(inputDir))
        {
            Console.WriteLine($"[Error] Input asset directory not found: {inputDir}");
            return 1;
        }

        using var packWriter = new PackWriter();
        var files = Directory.GetFiles(inputDir, "*.*", SearchOption.AllDirectories);
        Console.WriteLine($"[Cooker] Packing {files.Length} files from '{inputDir}' into '{outputJpak}'");

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            AssetType type = ext switch
            {
                ".jmesh" => AssetType.Mesh,
                ".jtex" => AssetType.Texture,
                ".jmat" => AssetType.Material,
                _ => AssetType.Unknown,
            };

            if (type == AssetType.Unknown)
                continue;

            var filenameWithoutExt = Path.GetFileNameWithoutExtension(file);
            AssetGuid guid;
            if (ulong.TryParse(filenameWithoutExt, System.Globalization.NumberStyles.HexNumber, null, out var guidVal))
            {
                guid = new AssetGuid(guidVal);
            }
            else
            {
                guid = AssetGuid.FromPath(file);
            }

            var bytes = File.ReadAllBytes(file);
            packWriter.AddEntry(guid, type, bytes);
            Console.WriteLine($"  Added {type,-8} {guid} ({bytes.Length:N0} bytes)");
        }

        packWriter.WriteToFile(outputJpak);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Cooker] Successfully generated archive: {outputJpak}");
        Console.ResetColor();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  cook-texture <inputFile> <outputFile>   : Bakes an image into .jtex with mipmaps & resident tail");
        Console.WriteLine("  cook-model <inputGltf> <outputDir>      : Bakes glTF meshes into optimized .jmesh files");
        Console.WriteLine("  cook-pak <outputJpak> <inputDir>        : Bundles cooked assets into a .jpak package");
        Console.WriteLine("  inspect <packagePath.jpak>              : Inspects .jpak Table of Contents header");
    }
}
