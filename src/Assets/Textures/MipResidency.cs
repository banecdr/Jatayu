/* =================================================================================================
 * FILE: MipResidency.cs
 * LOCATION: src/Assets/Textures/MipResidency.cs
 * 
 * WHAT THIS FILE DOES:
 *   Tracks loaded mip levels per texture and coordinates steps-of-2 residency transitions (D21).
 *   - Guarantees resident mip tail (mips <= 64x64) is permanently loaded.
 *   - Dispatches step-of-2 mip reallocations and bindless slot swaps to minimize churn.
 *   - Retires old VkImage instances safely through DeferredDeletionQueue after N frames in-flight.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Jatayu.AssetFormats;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Assets.Textures;

/// <summary>
/// Tracks the active residency state and bindless slot for a streaming texture.
/// </summary>
public sealed class TextureResidencyState
{
    public AssetGuid TextureGuid { get; }
    public uint FullWidth { get; }
    public uint FullHeight { get; }
    public uint TotalMipLevels { get; }
    public uint ResidentTailMips { get; }
    public VkSampler Sampler { get; set; }

    /// <summary>
    /// Current top mip level resident in VRAM (0 = full resolution, higher = lower resolution).
    /// </summary>
    public uint CurrentTopMip { get; set; }

    /// <summary>
    /// Target mip level requested based on distance / texel density heuristics.
    /// </summary>
    public uint TargetTopMip { get; set; }

    /// <summary>
    /// Active bindless descriptor slot.
    /// </summary>
    public uint BindlessSlot { get; set; }

    /// <summary>
    /// Currently allocated GPU texture resource.
    /// </summary>
    public GpuTexture? CurrentGpuTexture { get; set; }

    public TextureResidencyState(
        AssetGuid textureGuid,
        uint fullWidth,
        uint fullHeight,
        uint totalMipLevels,
        uint residentTailMips,
        uint initialTopMip,
        uint bindlessSlot)
    {
        TextureGuid = textureGuid;
        FullWidth = fullWidth;
        FullHeight = fullHeight;
        TotalMipLevels = totalMipLevels;
        ResidentTailMips = residentTailMips;
        CurrentTopMip = initialTopMip;
        TargetTopMip = initialTopMip;
        BindlessSlot = bindlessSlot;
    }

    /// <summary>
    /// Computes the step-of-2 transition target to limit VRAM churn (D21).
    /// </summary>
    public uint ComputeStepTransition(uint desiredTarget)
    {
        // Must never exceed the resident tail boundary
        uint maxAllowedTopMip = Math.Max(0, TotalMipLevels - ResidentTailMips);
        desiredTarget = Math.Min(desiredTarget, maxAllowedTopMip);

        if (desiredTarget == CurrentTopMip)
            return CurrentTopMip;

        // Step by at most 2 mip levels per residency change
        if (desiredTarget < CurrentTopMip)
        {
            // Increasing resolution (lowering top mip index)
            uint step = Math.Min(2u, CurrentTopMip - desiredTarget);
            return CurrentTopMip - step;
        }
        else
        {
            // Decreasing resolution (raising top mip index)
            uint step = Math.Min(2u, desiredTarget - CurrentTopMip);
            return CurrentTopMip + step;
        }
    }
}
