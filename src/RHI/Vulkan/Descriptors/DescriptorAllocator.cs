/* =================================================================================================
 * FILE: DescriptorAllocator.cs
 * LOCATION: src/RHI/Vulkan/Descriptors/DescriptorAllocator.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages Vulkan descriptor pools (VkDescriptorPool) and provides scalable allocation of
 *   descriptor sets (VkDescriptorSet) for textures, uniform buffers, and storage buffers without
 *   running out of pool capacity.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Configures pool sizes for common descriptor types (CombinedImageSampler, UniformBuffer, StorageBuffer).
 *   2. Allocates VkDescriptorPool instances with VkDescriptorPoolCreateFlags.None.
 *   3. Allocates descriptor sets via vkAllocateDescriptorSets.
 *   4. Provides WriteImage() and WriteBuffer() helpers to update descriptor sets via vkUpdateDescriptorSets.
 *   5. Implements ResetPools() to quickly recycle all descriptor sets between frames.
 *   6. Cleans up all native pools upon disposal.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.RHI.Vulkan.Descriptors;

/// <summary>
/// Pool allocator managing Vulkan descriptor pools and descriptor sets.
/// </summary>
public sealed class DescriptorAllocator : IDisposable
{
    private readonly VulkanContext _context;
    private readonly List<VkDescriptorPool> _pools = [];
    private VkDescriptorPool _currentPool;
    private bool _disposed;

    /// <summary>
    /// Gets the default max sets per descriptor pool.
    /// </summary>
    public int MaxSetsPerPool { get; }

    /// <summary>
    /// Pool size ratios defining how many descriptors of each type are allocated per pool.
    /// </summary>
    public readonly struct PoolSizeRatio
    {
        public VkDescriptorType Type { get; }
        public float Ratio { get; }

        public PoolSizeRatio(VkDescriptorType type, float ratio)
        {
            Type = type;
            Ratio = ratio;
        }
    }

    private static readonly PoolSizeRatio[] DefaultRatios =
    [
        new(VkDescriptorType.CombinedImageSampler, 4.0f),
        new(VkDescriptorType.UniformBuffer, 2.0f),
        new(VkDescriptorType.StorageBuffer, 2.0f),
        new(VkDescriptorType.SampledImage, 4.0f),
        new(VkDescriptorType.Sampler, 2.0f),
    ];

    /// <summary>
    /// Initializes a new DescriptorAllocator.
    /// </summary>
    public DescriptorAllocator(VulkanContext context, int maxSetsPerPool = 1000)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        MaxSetsPerPool = maxSetsPerPool;
        _currentPool = CreatePool();
        _pools.Add(_currentPool);
    }

    private unsafe VkDescriptorPool CreatePool()
    {
        var poolSizes = new VkDescriptorPoolSize[DefaultRatios.Length];
        for (int i = 0; i < DefaultRatios.Length; i++)
        {
            poolSizes[i] = new VkDescriptorPoolSize
            {
                Type = DefaultRatios[i].Type,
                DescriptorCount = Math.Max(1, (int)(DefaultRatios[i].Ratio * MaxSetsPerPool)),
            };
        }

        fixed (VkDescriptorPoolSize* pPoolSizes = poolSizes)
        {
            var createInfo = new VkDescriptorPoolCreateInfo
            {
                MaxSets = MaxSetsPerPool,
                PoolSizeCount = poolSizes.Length,
                PoolSizes = pPoolSizes,
                Flags = VkDescriptorPoolCreateFlags.None,
            };

            return _context.Device.CreateDescriptorPool(in createInfo, null);
        }
    }

    /// <summary>
    /// Allocates a new descriptor set using the specified layout.
    /// </summary>
    public unsafe VkDescriptorSet Allocate(DescriptorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var layoutHandle = layout.Handle;
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            DescriptorPool = _currentPool,
            DescriptorSetCount = 1,
            SetLayouts = &layoutHandle,
        };

        VkDescriptorSet set = default;
        var result = _context.Device.PInvoke.vkAllocateDescriptorSets.Invoke(
            _context.Device.Handle,
            &allocInfo,
            &set
        );

        if (result == VkResult.Success)
            return set;

        // Pool is exhausted, create a new pool and retry
        _currentPool = CreatePool();
        _pools.Add(_currentPool);
        allocInfo.DescriptorPool = _currentPool;

        result = _context.Device.PInvoke.vkAllocateDescriptorSets.Invoke(
            _context.Device.Handle,
            &allocInfo,
            &set
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"Failed to allocate Vulkan descriptor set: {result}");

        return set;
    }

    /// <summary>
    /// Updates a descriptor set binding with a combined image sampler.
    /// </summary>
    public unsafe void WriteImage(
        VkDescriptorSet set,
        int binding,
        VulkanSampler sampler,
        GpuTexture texture,
        VkImageLayout layout = VkImageLayout.ShaderReadOnlyOptimal)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(texture);
        WriteImage(set, binding, sampler.Handle, texture.ImageView, layout);
    }

    /// <summary>
    /// Updates a descriptor set binding with a combined image sampler from raw Vulkan handles.
    /// </summary>
    public unsafe void WriteImage(
        VkDescriptorSet set,
        int binding,
        VkSampler sampler,
        VkImageView imageView,
        VkImageLayout layout = VkImageLayout.ShaderReadOnlyOptimal)
    {
        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = imageView,
            ImageLayout = layout,
        };

        var write = new VkWriteDescriptorSet
        {
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &imageInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(
            _context.Device.Handle,
            1,
            &write,
            0,
            null
        );
    }

    /// <summary>
    /// Updates a descriptor set binding with a storage image.
    /// </summary>
    public unsafe void WriteStorageImage(
        VkDescriptorSet set,
        int binding,
        VkImageView imageView,
        VkImageLayout layout = VkImageLayout.General)
    {
        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = default,
            ImageView = imageView,
            ImageLayout = layout,
        };

        var write = new VkWriteDescriptorSet
        {
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.StorageImage,
            ImageInfo = &imageInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(
            _context.Device.Handle,
            1,
            &write,
            0,
            null
        );
    }

    /// <summary>
    /// Updates a descriptor set binding with a storage or uniform buffer.
    /// </summary>
    public unsafe void WriteBuffer(
        VkDescriptorSet set,
        int binding,
        VkBuffer buffer,
        ulong offset = 0,
        ulong range = ~0UL,
        VkDescriptorType descriptorType = VkDescriptorType.StorageBuffer)
    {
        var bufferInfo = new VkDescriptorBufferInfo
        {
            Buffer = buffer,
            Offset = new ByteSize64(offset),
            Range = new ByteSize64(range),
        };

        var write = new VkWriteDescriptorSet
        {
            DstSet = set,
            DstBinding = binding,
            DstArrayElement = 0,
            DescriptorCount = 1,
            DescriptorType = descriptorType,
            BufferInfo = &bufferInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(
            _context.Device.Handle,
            1,
            &write,
            0,
            null
        );
    }

    /// <summary>
    /// Resets all allocated descriptor pools. Invalidates all previously allocated descriptor sets.
    /// </summary>
    public void ResetPools()
    {
        foreach (var pool in _pools)
        {
            _context.Device.ResetDescriptorPool(pool, 0);
        }
    }

    /// <summary>
    /// Disposes all managed Vulkan descriptor pools.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            foreach (var pool in _pools)
            {
                if (pool.VkHandle != 0)
                {
                    _context.Device.DestroyDescriptorPool(pool, null);
                }
            }
            _pools.Clear();
            _currentPool = default;
            _disposed = true;
        }
    }
}
