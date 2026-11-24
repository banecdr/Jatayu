/* =================================================================================================
 * FILE: PackWriter.cs
 * LOCATION: tools/Jatayu.Cooker/PackWriter.cs
 * 
 * WHAT THIS FILE DOES:
 *   Constructs and serializes high-speed, contiguous memory-mapped .jpak asset packages.
 *   Writes assets sequentially with 16-byte alignment, records 64-bit AssetGuid table of contents,
 *   and prefixes the binary with a validated JpakHeader for sub-millisecond seek/streaming.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

/// <summary>
/// Packages multiple cooked binary assets into a single .jpak archive.
/// </summary>
public sealed class PackWriter : IDisposable
{
    private struct PendingEntry
    {
        public AssetGuid Guid;
        public AssetType Type;
        public uint Flags;
        public byte[] Data;
    }

    private readonly List<PendingEntry> _entries = new();
    private bool _disposed;

    /// <summary>
    /// Adds an asset payload to the pending package.
    /// </summary>
    public void AddEntry(AssetGuid guid, AssetType type, ReadOnlySpan<byte> data, uint flags = 0)
    {
        _entries.Add(new PendingEntry
        {
            Guid = guid,
            Type = type,
            Flags = flags,
            Data = data.ToArray(),
        });
    }

    /// <summary>
    /// Writes all added assets into a .jpak package on disk.
    /// </summary>
    public void WriteToFile(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_entries.Count == 0)
            throw new InvalidOperationException("Cannot write an empty JPAK package.");

        ulong totalPayloadBytes = 0;
        if ((ulong)_entries.Count > uint.MaxValue / (uint)Marshal.SizeOf<JpakEntry>())
            throw new InvalidOperationException("JPAK entry count exceeds the format limit.");
        foreach (var entry in _entries)
        {
            if (entry.Data.Length > int.MaxValue)
                throw new InvalidOperationException("JPAK payload exceeds the format limit.");
            if (totalPayloadBytes > ulong.MaxValue - (ulong)entry.Data.Length)
                throw new InvalidOperationException("JPAK package size exceeds the format limit.");
            totalPayloadBytes += (ulong)entry.Data.Length;
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        uint entryCount = (uint)_entries.Count;
        int headerSize = Marshal.SizeOf<JpakHeader>();
        int entrySize = Marshal.SizeOf<JpakEntry>();

        ulong tocOffset = (ulong)headerSize;
        ulong tocSize = (ulong)(entryCount * entrySize);
        ulong dataOffset = (tocOffset + tocSize + 15UL) & ~15UL; // 16-byte aligned
        if (dataOffset > long.MaxValue ||
            totalPayloadBytes > (ulong)long.MaxValue - dataOffset)
            throw new InvalidOperationException("JPAK package size exceeds the supported file limit.");

        var header = JpakHeader.Create(entryCount, tocOffset, dataOffset);

        // 1. Reserve header space
        stream.Seek((long)dataOffset, SeekOrigin.Begin);

        var writtenEntries = new JpakEntry[entryCount];
        for (int i = 0; i < _entries.Count; i++)
        {
            var item = _entries[i];
            long currentOffset = stream.Position;

            writer.Write(item.Data);

            // Pad to 16-byte alignment
            int remainder = (int)(stream.Position % 16);
            if (remainder != 0)
            {
                int pad = 16 - remainder;
                writer.Write(new byte[pad]);
            }

            writtenEntries[i] = new JpakEntry
            {
                Guid = item.Guid,
                Type = item.Type,
                Flags = item.Flags,
                Offset = (ulong)currentOffset,
                CompressedSize = (ulong)item.Data.Length,
                UncompressedSize = (ulong)item.Data.Length,
            };
        }

        // 2. Write TOC
        stream.Seek((long)tocOffset, SeekOrigin.Begin);
        Span<byte> entryBytes = stackalloc byte[entrySize];
        for (int i = 0; i < writtenEntries.Length; i++)
        {
            var entry = writtenEntries[i];
            MemoryMarshal.Write(entryBytes, in entry);
            writer.Write(entryBytes);
        }

        // 3. Write Master Header
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> headerBytes = stackalloc byte[headerSize];
        MemoryMarshal.Write(headerBytes, in header);
        writer.Write(headerBytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entries.Clear();
    }
}
