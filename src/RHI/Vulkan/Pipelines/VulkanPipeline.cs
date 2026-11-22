/* =================================================================================================
 * FILE: VulkanPipeline.cs
 * LOCATION: src/RHI/Vulkan/Pipelines/VulkanPipeline.cs
 * 
 * WHAT THIS FILE DOES:
 *   Encapsulates native Vulkan graphics and compute pipelines (VkPipeline) along with their
 *   pipeline layout (VkPipelineLayout). Provides clean command buffer binding and safe disposal.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Holds native VkPipeline and VkPipelineLayout handles.
 *   2. Binds the pipeline to a command buffer via cmd.BindPipeline().
 *   3. Tracks whether this pipeline is Graphics or Compute (VkPipelineBindPoint).
 *   4. Destroys both the pipeline and its layout safely upon IDisposable.Dispose().
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Pipelines;

/// <summary>
/// Encapsulates a Vulkan VkPipeline and VkPipelineLayout.
/// </summary>
public sealed class VulkanPipeline : IDisposable
{
    private readonly VulkanContext _context;
    private VkPipeline _pipeline;
    private VkPipelineLayout _layout;
    private bool _disposed;

    /// <summary>
    /// Gets the native Vulkan pipeline handle.
    /// </summary>
    public VkPipeline Handle => _pipeline;

    /// <summary>
    /// Gets the native Vulkan pipeline layout handle.
    /// </summary>
    public VkPipelineLayout Layout => _layout;

    /// <summary>
    /// Gets the pipeline bind point (Graphics or Compute).
    /// </summary>
    public VkPipelineBindPoint BindPoint { get; }

    /// <summary>
    /// Initializes a new VulkanPipeline instance.
    /// </summary>
    public VulkanPipeline(
        VulkanContext context,
        VkPipeline pipeline,
        VkPipelineLayout layout,
        VkPipelineBindPoint bindPoint = VkPipelineBindPoint.Graphics)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipeline = pipeline;
        _layout = layout;
        BindPoint = bindPoint;
    }

    /// <summary>
    /// Binds this pipeline to the specified command buffer.
    /// </summary>
    public void Bind(CommandBuffer cmd)
    {
        cmd.BindPipeline(BindPoint, _pipeline);
    }

    /// <summary>
    /// Disposes the native Vulkan pipeline and its layout.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_pipeline.VkHandle != 0)
            {
                _context.Device.DestroyPipeline(_pipeline, null);
                _pipeline = default;
            }

            if (_layout.VkHandle != 0)
            {
                _context.Device.DestroyPipelineLayout(_layout, null);
                _layout = default;
            }

            _disposed = true;
        }
    }
}
