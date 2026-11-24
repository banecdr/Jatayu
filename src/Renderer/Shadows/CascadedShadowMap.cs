/* =================================================================================================
 * FILE: CascadedShadowMap.cs
 * LOCATION: src/Renderer/Shadows/CascadedShadowMap.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages a 4-cascade 2D array depth attachment for real-time directional sunlight shadows.
 *   Calculates practical logarithmic/linear split distributions, manages 2D array texture layers,
 *   and provides Reverse-Z depth comparison sampling.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Renderer.Shadows;

/// <summary>
/// Packed GPU uniform representation of 4-cascade shadow parameters.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 288)]
public struct GpuCascadeData
{
    public Matrix4x4 CascadeViewProj0;
    public Matrix4x4 CascadeViewProj1;
    public Matrix4x4 CascadeViewProj2;
    public Matrix4x4 CascadeViewProj3;
    public Vector4 CascadeSplits;      // X=split0, Y=split1, Z=split2, W=split3
    public Vector4 ShadowParams;       // X=resolution, Y=invResolution, Z=bias, W=pcfRadius
}

/// <summary>
/// 4-cascade directional shadow map backed by a Vulkan 2D Array depth image.
/// </summary>
public sealed class CascadedShadowMap : IDisposable
{
    public const int CascadeCount = 4;
    public const int DefaultResolution = 2048;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly int _resolution;

    private readonly ShadowCascade[] _cascades = new ShadowCascade[CascadeCount];
    private GpuTexture _depthTexture;
    private VkImageView _fullArrayView;
    private readonly VkImageView[] _layerViews = new VkImageView[CascadeCount];
    private VkSampler _comparisonSampler;
    private Vector4 _cascadeSplits;
    private bool _disposed;

    /// <summary>
    /// Gets the resolution (width and height) of each cascade in pixels.
    /// </summary>
    public int Resolution => _resolution;

    /// <summary>
    /// Gets the underlying GPU texture.
    /// </summary>
    public GpuTexture Texture => _depthTexture;

    /// <summary>
    /// Gets the 2D Array image view spanning all 4 cascade layers.
    /// </summary>
    public VkImageView FullArrayView => _fullArrayView;

    /// <summary>
    /// Gets the per-layer 2D image view for rendering to a specific cascade.
    /// </summary>
    public VkImageView GetLayerView(int cascadeIndex) => _layerViews[cascadeIndex];

    /// <summary>
    /// Gets the Reverse-Z depth comparison sampler.
    /// </summary>
    public VkSampler ComparisonSampler => _comparisonSampler;

    /// <summary>
    /// Gets the 4 cascade partitions.
    /// </summary>
    public ReadOnlySpan<ShadowCascade> Cascades => _cascades;

    /// <summary>
    /// Gets the cascade split distances (view space far plane for each cascade).
    /// </summary>
    public Vector4 CascadeSplits => _cascadeSplits;

    public unsafe CascadedShadowMap(VulkanContext context, GpuAllocator allocator, int resolution = DefaultResolution)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Shadow map resolution must be positive.");
        }
        _resolution = resolution;

        var device = _context.Device;

        // 1. Create 2D Array Depth Image (4 layers, D32_SFLOAT)
        var imageCreateInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = DepthConvention.DepthFormat,
            Extent = new VkExtent3D(resolution, resolution, 1),
            MipLevels = 1,
            ArrayLayers = CascadeCount,
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

        // 2. Create Full 2D Array Image View
        var arrayViewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2DArray,
            Format = DepthConvention.DepthFormat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = CascadeCount,
            },
        };
        _fullArrayView = device.CreateImageView(in arrayViewInfo, null);

        // 3. Create per-layer 2D views for render attachments
        for (int i = 0; i < CascadeCount; i++)
        {
            var layerViewInfo = new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = DepthConvention.DepthFormat,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = i,
                    LayerCount = 1,
                },
            };
            _layerViews[i] = device.CreateImageView(in layerViewInfo, null);
        }

        _depthTexture = new GpuTexture(_context, _allocator, image, allocation, _fullArrayView, resolution, resolution, DepthConvention.DepthFormat, 1, VkImageLayout.Undefined);

        // 4. Create Reverse-Z comparison sampler
        var samplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToBorder,
            AddressModeV = VkSamplerAddressMode.ClampToBorder,
            AddressModeW = VkSamplerAddressMode.ClampToBorder,
            CompareEnable = true,
            CompareOp = DepthConvention.DepthCompareOp, // VK_COMPARE_OP_GREATER_OR_EQUAL
            BorderColor = VkBorderColor.FloatOpaqueBlack, // 0.0f = infinite distance under Reverse-Z
            MinLod = 0.0f,
            MaxLod = 1.0f,
        };
        _comparisonSampler = device.CreateSampler(in samplerInfo, null);

        for (int i = 0; i < CascadeCount; i++)
        {
            _cascades[i].CascadeIndex = i;
        }
    }

    /// <summary>
    /// Computes the 4 cascade split distances and updates each cascade's light-space view-projection matrix.
    /// </summary>
    public void Update(
        Vector3 lightDirection,
        in Matrix4x4 cameraView,
        float fovRadians,
        float aspect,
        float nearPlane,
        float maxShadowDistance = 500.0f,
        float lambda = 0.75f)
    {
        _cascadeSplits = CalculateSplitDistances(nearPlane, maxShadowDistance, lambda);
        Span<float> splitDistances = stackalloc float[CascadeCount + 1];
        splitDistances[0] = nearPlane;
        splitDistances[1] = _cascadeSplits.X;
        splitDistances[2] = _cascadeSplits.Y;
        splitDistances[3] = _cascadeSplits.Z;
        splitDistances[4] = _cascadeSplits.W;

        for (int i = 0; i < CascadeCount; i++)
        {
            _cascades[i].Update(
                lightDirection,
                in cameraView,
                fovRadians,
                aspect,
                splitDistances[i],
                splitDistances[i + 1],
                _resolution
            );
        }
    }

    public static Vector4 CalculateSplitDistances(float nearPlane, float maxShadowDistance, float lambda = 0.75f)
    {
        if (!float.IsFinite(nearPlane) || nearPlane <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Near plane must be finite and positive.");
        }
        if (!float.IsFinite(maxShadowDistance) || maxShadowDistance <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(maxShadowDistance), "Maximum shadow distance must exceed the near plane.");
        }
        if (!float.IsFinite(lambda) || lambda < 0.0f || lambda > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(lambda), "Cascade split lambda must be between zero and one.");
        }

        Span<float> splits = stackalloc float[CascadeCount];
        for (int i = 1; i <= CascadeCount; i++)
        {
            float ratio = (float)i / CascadeCount;
            float logarithmic = nearPlane * MathF.Pow(maxShadowDistance / nearPlane, ratio);
            float uniform = nearPlane + (maxShadowDistance - nearPlane) * ratio;
            splits[i - 1] = (lambda * logarithmic) + ((1.0f - lambda) * uniform);
        }

        return new Vector4(splits[0], splits[1], splits[2], splits[3]);
    }

    public static int SelectCascadeIndex(float viewDepth, in Vector4 splits)
    {
        if (!float.IsFinite(viewDepth))
        {
            throw new ArgumentOutOfRangeException(nameof(viewDepth), "View depth must be finite.");
        }

        if (viewDepth > splits.W) return -1;
        if (viewDepth > splits.Z) return 3;
        if (viewDepth > splits.Y) return 2;
        if (viewDepth > splits.X) return 1;
        return 0;
    }

    public static Vector4 PackGpuShadowParams(int resolution, float shadowBias, float pcfRadius)
    {
        if (resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Shadow map resolution must be positive.");
        }

        return new Vector4(resolution, 1.0f / resolution, shadowBias, pcfRadius);
    }

    /// <summary>
    /// Extracts packed GPU cascade data for uniform buffer upload.
    /// </summary>
    public GpuCascadeData GetGpuData(float shadowBias = 0.001f, float pcfRadius = 1.0f)
    {
        return new GpuCascadeData
        {
            CascadeViewProj0 = _cascades[0].ViewProjection,
            CascadeViewProj1 = _cascades[1].ViewProjection,
            CascadeViewProj2 = _cascades[2].ViewProjection,
            CascadeViewProj3 = _cascades[3].ViewProjection,
            CascadeSplits = _cascadeSplits,
            ShadowParams = PackGpuShadowParams(_resolution, shadowBias, pcfRadius),
        };
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        if (_comparisonSampler.VkHandle != 0)
        {
            device.DestroySampler(_comparisonSampler, null);
        }

        for (int i = 0; i < CascadeCount; i++)
        {
            if (_layerViews[i].VkHandle != 0)
            {
                device.DestroyImageView(_layerViews[i], null);
            }
        }

        _depthTexture?.Dispose();
        _disposed = true;
    }
}
