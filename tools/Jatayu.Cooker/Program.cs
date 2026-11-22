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

    private static void PrintUsage()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  cook-texture <inputFile> <outputFile>   : Bakes an image into .jtex with mipmaps & resident tail");
        Console.WriteLine("  inspect <packagePath.jpak>              : Inspects .jpak Table of Contents header");
    }
}
