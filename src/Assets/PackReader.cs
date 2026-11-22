/* =================================================================================================
 * FILE: PackReader.cs
 * LOCATION: src/Assets/PackReader.cs
 * 
 * WHAT THIS FILE DOES:
 *   High-speed memory-mapped reader for .jpak binary packages.
 *   Maps the archive file into process memory, parses the Table of Contents (TOC) into a fast
 *   hash dictionary keyed by 64-bit AssetGuid, and provides zero-copy Span slices for sub-millisecond
 *   asset retrieval.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Assets;

/// <summary>
/// High-speed memory-mapped reader for seeking and extracting assets from .jpak packages.
/// </summary>
public sealed class PackReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Dictionary<AssetGuid, JpakEntry> _entries;
    private unsafe byte* _basePointer;
    private bool _disposed;

    /// <summary>
    /// Gets the file path of this opened package.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the number of indexed assets in this package.
    /// </summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Opens and memory-maps a .jpak package.
    /// </summary>
    public unsafe PackReader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Package not found: {filePath}", filePath);

        FilePath = filePath;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePointer);

        int headerSize = Marshal.SizeOf<JpakHeader>();
        var header = Marshal.PtrToStructure<JpakHeader>((nint)_basePointer);
        if (!header.IsValid)
            throw new InvalidDataException($"Invalid .jpak header in {filePath} (Magic: 0x{header.Magic:X8}, Version: {header.Version})");

        _entries = new Dictionary<AssetGuid, JpakEntry>((int)header.EntryCount);

        int entrySize = Marshal.SizeOf<JpakEntry>();
        byte* tocPtr = _basePointer + header.TocOffset;

        for (uint i = 0; i < header.EntryCount; i++)
        {
            var entry = Marshal.PtrToStructure<JpakEntry>((nint)(tocPtr + (i * entrySize)));
            _entries[entry.Guid] = entry;
        }
    }

    /// <summary>
    /// Checks if an asset exists in this package by GUID.
    /// </summary>
    public bool Contains(AssetGuid guid) => _entries.ContainsKey(guid);

    /// <summary>
    /// Gets the TOC entry record for an asset by GUID.
    /// </summary>
    public bool TryGetEntry(AssetGuid guid, out JpakEntry entry) => _entries.TryGetValue(guid, out entry);

    /// <summary>
    /// Returns a zero-copy memory slice of the asset payload directly from the memory-mapped file.
    /// </summary>
    public unsafe ReadOnlySpan<byte> ReadAssetBytes(AssetGuid guid)
    {
        if (!_entries.TryGetValue(guid, out var entry))
            throw new KeyNotFoundException($"Asset GUID {guid} not found in package {FilePath}");

        return new ReadOnlySpan<byte>(_basePointer + entry.Offset, (int)entry.CompressedSize);
    }

    /// <summary>
    /// Copies the asset payload into a new managed byte[] array.
    /// </summary>
    public byte[] ReadAssetArray(AssetGuid guid)
    {
        var span = ReadAssetBytes(guid);
        return span.ToArray();
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_basePointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePointer = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
        _entries.Clear();
    }
}
