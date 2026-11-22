/* =================================================================================================
 * FILE: DescriptorLayout.cs
 * LOCATION: src/RHI/Vulkan/Descriptors/DescriptorLayout.cs
 * 
 * WHAT THIS FILE DOES:
 *   Wraps Vulkan descriptor set layouts (VkDescriptorSetLayout) and provides a fluent builder
 *   for configuring bindings (CombinedImageSampler, UniformBuffer, StorageBuffer) with specific
 *   shader stages (Vertex, Fragment, Compute).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Builder collects VkDescriptorSetLayoutBinding definitions.
 *   2. Builds VkDescriptorSetLayoutCreateInfo and creates native VkDescriptorSetLayout.
 *   3. Tracks binding descriptions for pipeline layout generation.
 *   4. Implements IDisposable to destroy the layout handle cleanly.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Descriptors;

/// <summary>
/// Encapsulates a Vulkan VkDescriptorSetLayout.
/// </summary>
public sealed class DescriptorLayout : IDisposable
{
    private readonly VulkanContext _context;
    private VkDescriptorSetLayout _layout;
    private bool _disposed;

    /// <summary>
    /// Gets the native Vulkan descriptor set layout handle.
    /// </summary>
    public VkDescriptorSetLayout Handle => _layout;

    /// <summary>
    /// Gets the array of bindings configured on this layout.
    /// </summary>
    public IReadOnlyList<VkDescriptorSetLayoutBinding> Bindings { get; }

    internal DescriptorLayout(VulkanContext context, VkDescriptorSetLayout layout, VkDescriptorSetLayoutBinding[] bindings)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _layout = layout;
        Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    /// <summary>
    /// Creates a fluent builder for constructing a DescriptorLayout.
    /// </summary>
    public static Builder Create() => new();

    /// <summary>
    /// Disposes the native Vulkan descriptor set layout.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_layout.VkHandle != 0)
            {
                _context.Device.DestroyDescriptorSetLayout(_layout, null);
                _layout = default;
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// Fluent builder for constructing DescriptorLayout instances.
    /// </summary>
    public sealed class Builder
    {
        private readonly List<VkDescriptorSetLayoutBinding> _bindings = [];

        /// <summary>
        /// Adds a binding to the descriptor layout.
        /// </summary>
        /// <param name="binding">Binding index matching shader layout(binding = X).</param>
        /// <param name="type">Type of descriptor (e.g. CombinedImageSampler, UniformBuffer).</param>
        /// <param name="stageFlags">Shader stages that access this binding.</param>
        /// <param name="count">Descriptor array count (defaults to 1).</param>
        public Builder AddBinding(
            int binding,
            VkDescriptorType type,
            VkShaderStageFlags stageFlags,
            int count = 1)
        {
            _bindings.Add(new VkDescriptorSetLayoutBinding
            {
                Binding = binding,
                DescriptorType = type,
                DescriptorCount = count,
                StageFlags = stageFlags,
            });
            return this;
        }

        /// <summary>
        /// Builds the native Vulkan descriptor set layout.
        /// </summary>
        public unsafe DescriptorLayout Build(
            VulkanContext context,
            VkDescriptorSetLayoutCreateFlags flags = VkDescriptorSetLayoutCreateFlags.None)
        {
            ArgumentNullException.ThrowIfNull(context);

            var bindingsArray = _bindings.ToArray();
            fixed (VkDescriptorSetLayoutBinding* pBindings = bindingsArray)
            {
                var createInfo = new VkDescriptorSetLayoutCreateInfo
                {
                    Flags = flags,
                    BindingCount = bindingsArray.Length,
                    Bindings = pBindings,
                };

                var layout = context.Device.CreateDescriptorSetLayout(in createInfo, null);
                return new DescriptorLayout(context, layout, bindingsArray);
            }
        }
    }
}
