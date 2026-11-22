/* =================================================================================================
 * FILE: RenderPass.cs
 * LOCATION: src/Renderer/Graph/RenderPass.cs
 * 
 * WHAT THIS FILE DOES:
 *   Execution node in the render graph DAG. Defines resource read/write declarations during setup,
 *   attachment configuration for Vulkan 1.3 Dynamic Rendering, and the execution callback.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace Jatayu.Renderer.Graph;

/// <summary>
/// Declares an attachment target for Vulkan 1.3 Dynamic Rendering.
/// </summary>
public struct RenderPassColorAttachment
{
    public RgTextureHandle Handle;
    public VkAttachmentLoadOp LoadOp;
    public VkAttachmentStoreOp StoreOp;
    public VkClearColorValue ClearValue;
}

/// <summary>
/// Declares a depth/stencil attachment target for Vulkan 1.3 Dynamic Rendering.
/// </summary>
public struct RenderPassDepthAttachment
{
    public RgTextureHandle Handle;
    public VkAttachmentLoadOp LoadOp;
    public VkAttachmentStoreOp StoreOp;
    public float ClearDepth;
    public bool HasAttachment;
}

/// <summary>
/// Resource dependency record tracking a read or write access required by a pass.
/// </summary>
public struct ResourceAccessDeclaration
{
    public RgTextureHandle TextureHandle;
    public RgBufferHandle BufferHandle;
    public bool IsWrite;
    public VkPipelineStageFlags2 Stage;
    public VkAccessFlags2 Access;
    public VkImageLayout ExpectedLayout;
}

/// <summary>
/// Execution context supplied to a render pass execute callback.
/// </summary>
public sealed class RenderPassContext
{
    private readonly RenderGraph _graph;

    public CommandBuffer CommandBuffer { get; internal set; }
    public VkExtent2D RenderArea { get; internal set; }

    internal RenderPassContext(RenderGraph graph)
    {
        _graph = graph;
    }

    /// <summary>
    /// Gets the underlying native Vulkan image handle for a virtual texture.
    /// </summary>
    public VkImage GetImage(RgTextureHandle handle) => _graph.GetNativeImage(handle);

    /// <summary>
    /// Gets the underlying native Vulkan image view handle for a virtual texture.
    /// </summary>
    public VkImageView GetImageView(RgTextureHandle handle) => _graph.GetNativeImageView(handle);

    /// <summary>
    /// Gets the texture width in pixels.
    /// </summary>
    public uint GetWidth(RgTextureHandle handle) => _graph.GetTextureWidth(handle);

    /// <summary>
    /// Gets the texture height in pixels.
    /// </summary>
    public uint GetHeight(RgTextureHandle handle) => _graph.GetTextureHeight(handle);
}

/// <summary>
/// Fluent builder used during pass setup to declare resource reads, writes, and render targets.
/// </summary>
public sealed class RenderPassBuilder
{
    internal readonly RenderPass Pass;

    internal RenderPassBuilder(RenderPass pass)
    {
        Pass = pass;
    }

    /// <summary>
    /// Declares a texture read requirement with expected stage, access mask, and image layout.
    /// </summary>
    public RenderPassBuilder ReadTexture(
        RgTextureHandle handle,
        VkPipelineStageFlags2 stage = VkPipelineStageFlags2.FragmentShaderBit,
        VkAccessFlags2 access = VkAccessFlags2.ShaderSampledReadBit,
        VkImageLayout layout = VkImageLayout.ShaderReadOnlyOptimal)
    {
        Pass.ResourceDeclarations.Add(new ResourceAccessDeclaration
        {
            TextureHandle = handle,
            IsWrite = false,
            Stage = stage,
            Access = access,
            ExpectedLayout = layout,
        });
        return this;
    }

    /// <summary>
    /// Declares a texture write requirement with expected stage, access mask, and image layout.
    /// </summary>
    public RenderPassBuilder WriteTexture(
        RgTextureHandle handle,
        VkPipelineStageFlags2 stage = VkPipelineStageFlags2.ColorAttachmentOutputBit,
        VkAccessFlags2 access = VkAccessFlags2.ColorAttachmentWriteBit,
        VkImageLayout layout = VkImageLayout.ColorAttachmentOptimal)
    {
        Pass.ResourceDeclarations.Add(new ResourceAccessDeclaration
        {
            TextureHandle = handle,
            IsWrite = true,
            Stage = stage,
            Access = access,
            ExpectedLayout = layout,
        });
        return this;
    }

    /// <summary>
    /// Configures a color attachment for Dynamic Rendering. Automatically registers a write dependency.
    /// </summary>
    public RenderPassBuilder SetColorAttachment(
        RgTextureHandle handle,
        VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear,
        VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store,
        VkClearColorValue clearValue = default)
    {
        Pass.ColorAttachments.Add(new RenderPassColorAttachment
        {
            Handle = handle,
            LoadOp = loadOp,
            StoreOp = storeOp,
            ClearValue = clearValue,
        });

        WriteTexture(
            handle,
            VkPipelineStageFlags2.ColorAttachmentOutputBit,
            VkAccessFlags2.ColorAttachmentWriteBit,
            VkImageLayout.ColorAttachmentOptimal
        );

        return this;
    }

    /// <summary>
    /// Configures a depth attachment for Dynamic Rendering. Automatically registers a write dependency.
    /// </summary>
    public RenderPassBuilder SetDepthAttachment(
        RgTextureHandle handle,
        VkAttachmentLoadOp loadOp = VkAttachmentLoadOp.Clear,
        VkAttachmentStoreOp storeOp = VkAttachmentStoreOp.Store,
        float clearDepth = 0.0f)
    {
        Pass.DepthAttachment = new RenderPassDepthAttachment
        {
            Handle = handle,
            LoadOp = loadOp,
            StoreOp = storeOp,
            ClearDepth = clearDepth,
            HasAttachment = true,
        };

        WriteTexture(
            handle,
            VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit,
            VkAccessFlags2.DepthStencilAttachmentWriteBit,
            VkImageLayout.DepthAttachmentOptimal
        );

        return this;
    }

    /// <summary>
    /// Marks this pass as having external side effects (e.g. presenting to swapchain or external outputs),
    /// preventing it from being culled during DAG optimization.
    /// </summary>
    public RenderPassBuilder HasSideEffects(bool sideEffects = true)
    {
        Pass.HasSideEffects = sideEffects;
        return this;
    }
}

/// <summary>
/// A node in the render graph execution DAG.
/// </summary>
public sealed class RenderPass
{
    public string Name { get; }
    public int PassIndex { get; internal set; }
    public bool HasSideEffects { get; internal set; }
    public bool IsCulled { get; internal set; }

    public List<ResourceAccessDeclaration> ResourceDeclarations { get; } = [];
    public List<RenderPassColorAttachment> ColorAttachments { get; } = [];
    public RenderPassDepthAttachment DepthAttachment;

    public Action<RenderPassContext>? ExecuteCallback { get; set; }

    public bool IsDynamicRenderingPass => ColorAttachments.Count > 0 || DepthAttachment.HasAttachment;

    public RenderPass(string name, int passIndex)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        PassIndex = passIndex;
    }

    internal void Reset()
    {
        HasSideEffects = false;
        IsCulled = false;
        ResourceDeclarations.Clear();
        ColorAttachments.Clear();
        DepthAttachment = default;
        ExecuteCallback = null;
    }
}
