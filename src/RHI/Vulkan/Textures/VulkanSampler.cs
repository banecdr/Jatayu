/* =================================================================================================
 * FILE: VulkanSampler.cs
 * LOCATION: src/RHI/Vulkan/Textures/VulkanSampler.cs
 * 
 * WHAT THIS FILE DOES:
 *   Encapsulates Vulkan texture samplers (VkSampler) with pre-configured filtering modes:
 *   Linear, Trilinear, 16x Anisotropic filtering, Clamp-to-edge, Repeat, and the mandatory
 *   VK_EXT_sampler_filter_minmax Min-Reduction mode required for Hi-Z occlusion culling.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Allocates and configures VkSamplerCreateInfo with anisotropic and mipmap parameters.
 *   2. For Min-Reduction (Hi-Z), chains VkSamplerReductionModeCreateInfo with ReductionMode = Min.
 *   3. Calls device.CreateSampler to create the native hardware sampler object.
 *   4. Implements clean disposal via device.DestroySampler.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Textures;

/// <summary>
/// Encapsulates a Vulkan hardware texture sampler (VkSampler) and standard engine sampler presets.
/// </summary>
public sealed class VulkanSampler : IDisposable
{
    private readonly VulkanContext _context;
    private VkSampler _sampler;
    private bool _disposed;

    /// <summary>
    /// Gets the native Vulkan sampler handle.
    /// </summary>
    public VkSampler Handle => _sampler;

    /// <summary>
    /// Initializes a new VulkanSampler from a configured VkSamplerCreateInfo descriptor.
    /// </summary>
    public unsafe VulkanSampler(VulkanContext context, in VkSamplerCreateInfo createInfo)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var info = createInfo;
        _sampler = _context.Device.CreateSampler(in info, null);
    }

    /// <summary>
    /// Creates a bilinear sampler with clamp-to-edge addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateLinearClamp(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MipLodBias = 0.0f,
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 0.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a bilinear sampler with repeat addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateLinearRepeat(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.Repeat,
            AddressModeV = VkSamplerAddressMode.Repeat,
            AddressModeW = VkSamplerAddressMode.Repeat,
            MipLodBias = 0.0f,
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 0.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a trilinear sampler with linear mip-mapping and clamp-to-edge addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateTrilinearClamp(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MipLodBias = 0.0f,
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 1000.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a trilinear sampler with linear mip-mapping and repeat addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateTrilinearRepeat(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.Repeat,
            AddressModeV = VkSamplerAddressMode.Repeat,
            AddressModeW = VkSamplerAddressMode.Repeat,
            MipLodBias = 0.0f,
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 1000.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a high-fidelity 16x anisotropic sampler with clamp-to-edge addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateAnisotropic16xClamp(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MipLodBias = 0.0f,
            AnisotropyEnable = true,
            MaxAnisotropy = 16.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 1000.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a high-fidelity 16x anisotropic sampler with repeat addressing.
    /// </summary>
    public static unsafe VulkanSampler CreateAnisotropic16xRepeat(VulkanContext context)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.Repeat,
            AddressModeV = VkSamplerAddressMode.Repeat,
            AddressModeW = VkSamplerAddressMode.Repeat,
            MipLodBias = 0.0f,
            AnisotropyEnable = true,
            MaxAnisotropy = 16.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 1000.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Creates a Min-Reduction sampler for Hi-Z occlusion depth pyramids using VK_EXT_sampler_filter_minmax.
    /// In Reverse-Z, min reduction selects the nearest depth (closest surface).
    /// </summary>
    public static unsafe VulkanSampler CreateMinReductionLinearClamp(VulkanContext context)
    {
        var reductionInfo = new VkSamplerReductionModeCreateInfo
        {
            ReductionMode = VkSamplerReductionMode.Min,
        };

        var info = new VkSamplerCreateInfo
        {
            Next = &reductionInfo,
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MipLodBias = 0.0f,
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = false,
            MinLod = 0.0f,
            MaxLod = 1000.0f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };

        return new VulkanSampler(context, info);
    }

    /// <summary>
    /// Disposes the native Vulkan sampler.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_sampler.VkHandle != 0)
            {
                _context.Device.DestroySampler(_sampler, null);
                _sampler = default;
            }
            _disposed = true;
        }
    }
}
