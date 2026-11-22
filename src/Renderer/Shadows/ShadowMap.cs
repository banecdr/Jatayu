// ==================================================================================================
// Jatayu Engine - 3D Lighting & Shadows
// Module: ShadowMap.cs
// Role: Depth render target and comparison sampler for real-time directional shadow mapping (Reverse-Z).
// ==================================================================================================

using System.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Renderer.Shadows;

/// <summary>
/// Depth render target buffer and Reverse-Z comparison sampler for directional shadows.
/// </summary>
public sealed class ShadowMap : IDisposable
{
    public const int DefaultResolution = 2048;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly int _resolution;

    private GpuTexture _depthTexture;
    private VkSampler _comparisonSampler;
    private Matrix4x4 _lightViewProjection;
    private bool _disposed;

    /// <summary>
    /// Gets the resolution (width and height) of the shadow map in pixels.
    /// </summary>
    public int Resolution => _resolution;

    /// <summary>
    /// Gets the underlying GPU texture attachment.
    /// </summary>
    public GpuTexture Texture => _depthTexture;

    /// <summary>
    /// Gets the native VkImageView for depth rendering.
    /// </summary>
    public VkImageView ImageView => _depthTexture.ImageView;

    /// <summary>
    /// Gets the Reverse-Z depth comparison sampler (VK_COMPARE_OP_GREATER_OR_EQUAL).
    /// </summary>
    public VkSampler ComparisonSampler => _comparisonSampler;

    /// <summary>
    /// Gets or sets the light view-projection matrix used to render this shadow map.
    /// </summary>
    public Matrix4x4 LightViewProjection
    {
        get => _lightViewProjection;
        set => _lightViewProjection = value;
    }

    /// <summary>
    /// Constructs a new ShadowMap depth attachment.
    /// </summary>
    public unsafe ShadowMap(VulkanContext context, GpuAllocator allocator, int resolution = DefaultResolution)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _resolution = resolution;

        // 1. Allocate D32_SFLOAT depth target with Sampled + DepthAttachment flags
        var imageCreateInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = DepthConvention.DepthFormat,
            Extent = new VkExtent3D(resolution, resolution, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };

        allocator.CreateImage(
            imageCreateInfo,
            VmaMemoryUsage.GpuOnly,
            VmaAllocationCreateFlags.None,
            out var image,
            out var allocation,
            out _
        );

        var viewCreateInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = DepthConvention.DepthFormat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var imageView = _context.Device.CreateImageView(viewCreateInfo, null);

        _depthTexture = new GpuTexture(
            context,
            allocator,
            image,
            allocation,
            imageView,
            resolution,
            resolution,
            DepthConvention.DepthFormat
        );

        // 2. Create Reverse-Z comparison sampler
        var samplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToBorder,
            AddressModeV = VkSamplerAddressMode.ClampToBorder,
            AddressModeW = VkSamplerAddressMode.ClampToBorder,
            BorderColor = VkBorderColor.FloatOpaqueWhite, // 1.0f outside border under Reverse-Z
            CompareEnable = true,
            CompareOp = DepthConvention.DepthCompareOp, // VK_COMPARE_OP_GREATER_OR_EQUAL
            MinLod = 0.0f,
            MaxLod = 1.0f,
        };

        _comparisonSampler = _context.Device.CreateSampler(samplerInfo, null);
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_comparisonSampler.VkHandle != 0)
            {
                _context.Device.DestroySampler(_comparisonSampler, null);
                _comparisonSampler = default;
            }

            _depthTexture.Dispose();
            _disposed = true;
        }
    }
}
