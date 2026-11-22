// ==================================================================================================
// Jatayu Engine - 3D Materials & Shading
// Module: MaterialTable.cs
// Role: Monolithic GPU storage buffer containing all active PBR materials indexed by material ID.
// ==================================================================================================

using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Upload;

namespace Jatayu.Renderer.Material;

/// <summary>
/// GPU storage buffer holding all active material definitions indexed by integer material ID.
/// Enables shaders to fetch arbitrary material properties dynamically in bindless and forward passes.
/// </summary>
public sealed class MaterialTable : IDisposable
{
    public const int DefaultCapacity = 2048;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly int _capacity;

    private readonly GpuMaterialData[] _cpuMaterials;
    private GpuBuffer _gpuBuffer;
    private int _count;
    private bool _isDirty;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying GPU storage buffer.
    /// </summary>
    public GpuBuffer Buffer => _gpuBuffer;

    /// <summary>
    /// Gets the number of registered materials.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Constructs a new MaterialTable backed by a GPU storage buffer.
    /// </summary>
    public MaterialTable(VulkanContext context, GpuAllocator allocator, int capacity = DefaultCapacity)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _capacity = capacity;

        _cpuMaterials = new GpuMaterialData[capacity];

        ulong bufferSize = (ulong)(Marshal.SizeOf<GpuMaterialData>() * capacity);
        _gpuBuffer = _allocator.CreateBuffer(
            bufferSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );

        // Register default material at slot 0
        RegisterMaterial(new Material { Name = "Default" });
    }

    /// <summary>
    /// Registers a new material and returns its allocated ID.
    /// </summary>
    public uint RegisterMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (_count >= _capacity)
        {
            throw new InvalidOperationException($"MaterialTable capacity ({_capacity}) exceeded.");
        }

        uint id = (uint)_count++;
        _cpuMaterials[id] = material.ToGpuData();
        _isDirty = true;
        return id;
    }

    /// <summary>
    /// Updates the GPU material parameters at a specific ID slot.
    /// </summary>
    public void UpdateMaterial(uint id, in GpuMaterialData data)
    {
        if (id >= _count)
            throw new ArgumentOutOfRangeException(nameof(id));

        _cpuMaterials[id] = data;
        _isDirty = true;
    }

    /// <summary>
    /// Flushes dirty materials to the GPU storage buffer via UploadManager.
    /// </summary>
    public void Flush(UploadManager uploadManager)
    {
        if (!_isDirty || _count == 0) return;

        ReadOnlySpan<GpuMaterialData> slice = _cpuMaterials.AsSpan(0, _count);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(slice);

        uploadManager.UploadBuffer(_gpuBuffer, 0, bytes);
        _isDirty = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _gpuBuffer.Dispose();
            _disposed = true;
        }
    }
}
