/* =================================================================================================
 * FILE: RenderGraphResource.cs
 * LOCATION: src/Renderer/Graph/RenderGraphResource.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines virtual handles (RgTextureHandle, RgBufferHandle) and internal descriptor tracking
 *   for transient and imported render graph resources. Tracks layout, stage, and access masks
 *   across passes to automate Vulkan 1.3 synchronization2 barrier compilation.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;

namespace Jatayu.Renderer.Graph;

/// <summary>
/// Virtual handle representing a texture tracked within the render graph.
/// </summary>
public readonly record struct RgTextureHandle(uint Id, ushort Version = 1)
{
    public static readonly RgTextureHandle Invalid = new(0, 0);

    public bool IsValid => Id != 0;

    public override string ToString() => IsValid ? $"RgTexture({Id}v{Version})" : "RgTexture(Invalid)";
}

/// <summary>
/// Virtual handle representing a GPU buffer tracked within the render graph.
/// </summary>
public readonly record struct RgBufferHandle(uint Id, ushort Version = 1)
{
    public static readonly RgBufferHandle Invalid = new(0, 0);

    public bool IsValid => Id != 0;

    public override string ToString() => IsValid ? $"RgBuffer({Id}v{Version})" : "RgBuffer(Invalid)";
}

/// <summary>
/// Description used to declare a transient texture to be managed by the graph.
/// </summary>
public struct TextureDesc
{
    public string Name;
    public uint Width;
    public uint Height;
    public VkFormat Format;
    public VkImageUsageFlags Usage;
    public VkImageAspectFlags Aspect;

    public static TextureDesc Color2D(string name, uint width, uint height, VkFormat format, VkImageUsageFlags additionalUsage = VkImageUsageFlags.None) => new()
    {
        Name = name,
        Width = width,
        Height = height,
        Format = format,
        Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit | additionalUsage,
        Aspect = VkImageAspectFlags.ColorBit,
    };

    public static TextureDesc Depth2D(string name, uint width, uint height, VkFormat format = VkFormat.D32SFloat, VkImageUsageFlags additionalUsage = VkImageUsageFlags.None) => new()
    {
        Name = name,
        Width = width,
        Height = height,
        Format = format,
        Usage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit | additionalUsage,
        Aspect = VkImageAspectFlags.DepthBit,
    };
}

/// <summary>
/// Internal state tracked by the render graph for each texture resource.
/// </summary>
internal sealed class RgTextureResource
{
    public RgTextureHandle Handle { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsImported { get; set; }
    public VkImage Image { get; set; }
    public VkImageView ImageView { get; set; }
    public VkFormat Format { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public VkImageAspectFlags Aspect { get; set; }
    public VkImageUsageFlags Usage { get; set; }
    public int BaseArrayLayer { get; set; } = 0;
    public int LayerCount { get; set; } = 1;

    // Live synchronization state
    public VkImageLayout InitialLayout { get; set; }
    public VkImageLayout CurrentLayout { get; set; }
    public VkPipelineStageFlags2 CurrentStage { get; set; }
    public VkAccessFlags2 CurrentAccess { get; set; }

    public static VkImageAspectFlags DetermineAspect(VkFormat format)
    {
        return format switch
        {
            VkFormat.D32SFloat or VkFormat.D16UNorm => VkImageAspectFlags.DepthBit,
            VkFormat.D32SFloatS8UInt or VkFormat.D24UNormS8UInt => VkImageAspectFlags.DepthBit | VkImageAspectFlags.StencilBit,
            _ => VkImageAspectFlags.ColorBit,
        };
    }
}

/// <summary>
/// Internal state tracked by the render graph for each buffer resource.
/// </summary>
internal sealed class RgBufferResource
{
    public RgBufferHandle Handle { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsImported { get; set; }
    public VkBuffer Buffer { get; set; }
    public ulong Size { get; set; }
    public VkBufferUsageFlags Usage { get; set; }

    // Live synchronization state
    public VkPipelineStageFlags2 CurrentStage { get; set; }
    public VkAccessFlags2 CurrentAccess { get; set; }
}
