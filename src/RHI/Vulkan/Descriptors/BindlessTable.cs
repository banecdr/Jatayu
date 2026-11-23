// ==================================================================================================
// Jatayu Engine - Modern Vulkan 1.3 RHI
// Module: BindlessTable.cs
// Role: Global bindless descriptor indexing table binding thousands of textures (VK_EXT_descriptor_indexing).
// ==================================================================================================

using System.Runtime.CompilerServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Descriptors;

/// <summary>
/// Global bindless descriptor array allowing thousands of textures to be accessed via uniform indices in shaders.
/// Built on Vulkan 1.2 core descriptor indexing (partially bound + update-after-bind).
/// </summary>
public sealed class BindlessTable : IDisposable
{
    public const uint DefaultMaxTextures = 4096;

    private readonly VulkanContext _context;
    private readonly uint _maxTextures;

    private VkDescriptorPool _pool;
    private VkDescriptorSetLayout _layout;
    private VkDescriptorSet _descriptorSet;

    private readonly Queue<uint> _freeSlots;
    private readonly bool[] _activeSlots;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the allocated bindless descriptor set.
    /// </summary>
    public VkDescriptorSet DescriptorSet => _descriptorSet;

    /// <summary>
    /// Gets the bindless descriptor set layout.
    /// </summary>
    public VkDescriptorSetLayout Layout => _layout;

    /// <summary>
    /// Gets the maximum texture capacity of this bindless table.
    /// </summary>
    public uint MaxTextures => _maxTextures;

    /// <summary>
    /// Constructs and initializes the global bindless table.
    /// </summary>
    public unsafe BindlessTable(VulkanContext context, uint maxTextures = DefaultMaxTextures)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _maxTextures = maxTextures;

        _freeSlots = new Queue<uint>((int)maxTextures);
        _activeSlots = new bool[maxTextures];

        for (uint i = 0; i < maxTextures; i++)
        {
            _freeSlots.Enqueue(i);
        }

        // 1. Create Descriptor Set Layout with UpdateAfterBind & PartiallyBound
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = (int)maxTextures,
            StageFlags = VkShaderStageFlags.FragmentBit | VkShaderStageFlags.ComputeBit,
        };

        var bindingFlags = VkDescriptorBindingFlags.PartiallyBoundBit | VkDescriptorBindingFlags.UpdateAfterBindBit;
        var flagsInfo = new VkDescriptorSetLayoutBindingFlagsCreateInfo
        {
            BindingCount = 1,
            BindingFlags = &bindingFlags,
        };

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            Next = &flagsInfo,
            Flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1,
            Bindings = &binding,
        };

        _layout = _context.Device.CreateDescriptorSetLayout(in layoutInfo, null);

        // 2. Create Descriptor Pool with UpdateAfterBind
        var poolSize = new VkDescriptorPoolSize
        {
            Type = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = (int)maxTextures,
        };

        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            Flags = VkDescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PoolSizes = &poolSize,
        };

        _pool = _context.Device.CreateDescriptorPool(in poolInfo, null);

        // 3. Allocate the global descriptor set
        var localLayout = _layout;
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            SetLayouts = &localLayout,
        };

        VkDescriptorSet set = default;
        if (_context.Device.PInvoke.vkAllocateDescriptorSets.Invoke(_context.Device.Handle, &allocInfo, &set) != VkResult.Success)
        {
            throw new InvalidOperationException("Failed to allocate bindless descriptor set.");
        }
        _descriptorSet = set;
    }

    /// <summary>
    /// Registers a combined image sampler into the bindless table.
    /// Updates the descriptor set in-place with zero CPU overhead.
    /// </summary>
    /// <returns>The allocated bindless texture slot index.</returns>
    public unsafe uint RegisterTexture(VkImageView imageView, VkSampler sampler)
    {
        uint slot;
        lock (_lock)
        {
            if (_freeSlots.Count == 0)
            {
                throw new InvalidOperationException($"BindlessTable capacity of {_maxTextures} textures exceeded.");
            }
            slot = _freeSlots.Dequeue();
            _activeSlots[slot] = true;
        }

        // Update descriptor in-place
        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = imageView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var write = new VkWriteDescriptorSet
        {
            DstSet = _descriptorSet,
            DstBinding = 0,
            DstArrayElement = (int)slot,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &imageInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(_context.Device.Handle, 1, &write, 0, null);

        return slot;
    }

    /// <summary>
    /// Updates an existing slot in-place with a new image view and sampler (D21 atomic swap).
    /// </summary>
    public unsafe void UpdateSlot(uint slot, VkImageView imageView, VkSampler sampler)
    {
        if (slot >= _maxTextures)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = sampler,
            ImageView = imageView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var write = new VkWriteDescriptorSet
        {
            DstSet = _descriptorSet,
            DstBinding = 0,
            DstArrayElement = (int)slot,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &imageInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(_context.Device.Handle, 1, &write, 0, null);
    }

    /// <summary>
    /// Unregisters a texture slot and reclaims it for reuse.
    /// </summary>
    public void UnregisterTexture(uint slot)
    {
        if (slot >= _maxTextures) return;

        lock (_lock)
        {
            if (_activeSlots[slot])
            {
                _activeSlots[slot] = false;
                _freeSlots.Enqueue(slot);
            }
        }
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_layout.VkHandle != 0)
            {
                _context.Device.DestroyDescriptorSetLayout(_layout, null);
                _layout = default;
            }

            if (_pool.VkHandle != 0)
            {
                _context.Device.DestroyDescriptorPool(_pool, null);
                _pool = default;
            }

            _disposed = true;
        }
    }
}
