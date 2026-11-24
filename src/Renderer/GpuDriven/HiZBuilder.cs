// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: HiZBuilder.cs
// Role: Hierarchical depth mip pyramid (Hi-Z) builder using MIN reduction for Reverse-Z occlusion culling (Sprint 7a).
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// Push constants for hiz_build.comp (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct HiZPushConstants
{
    public uint PrevWidth;
    public uint PrevHeight;
    public uint DestWidth;
    public uint DestHeight;
}

/// <summary>
/// Generates and manages the hierarchical-Z depth mip pyramid for Reverse-Z occlusion culling.
/// </summary>
public sealed class HiZBuilder : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _mipLevels;

    private readonly GpuTexture _hizTexture;
    private readonly VkImageView[] _storageViews;
    private readonly VkImageView[] _sampledViews;
    private readonly VkImageView _fullPyramidView;
    private readonly VulkanSampler _minReductionSampler;

    private readonly DescriptorLayout _descriptorLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VulkanPipeline _pipeline;

    private readonly VkDescriptorSet[] _descriptorSets;
    private bool _disposed;

    /// <summary>
    /// Gets the full pyramid image view sampling all mip levels with min reduction.
    /// </summary>
    public VkImageView FullPyramidView => _fullPyramidView;

    /// <summary>
    /// Gets the min-reduction texture sampler.
    /// </summary>
    public VulkanSampler Sampler => _minReductionSampler;

    /// <summary>
    /// Gets the base resolution width.
    /// </summary>
    public uint Width => _width;

    /// <summary>
    /// Gets the base resolution height.
    /// </summary>
    public uint Height => _height;

    /// <summary>
    /// Gets the total number of mip levels.
    /// </summary>
    public uint MipLevels => _mipLevels;

    /// <summary>
    /// Initializes the Hi-Z pyramid builder and pre-compiles the min-reduction compute shader.
    /// </summary>
    public unsafe HiZBuilder(
        VulkanContext context,
        GpuAllocator allocator,
        ShaderCompiler shaderCompiler,
        uint width,
        uint height)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(_width, _height))) + 1;

        // 1. Allocate HiZ pyramid texture in R32_SFLOAT
        _hizTexture = GpuTexture.Create2D(
            _context,
            _allocator,
            (int)_width,
            (int)_height,
            VkFormat.R32SFloat,
            VkImageUsageFlags.SampledBit | VkImageUsageFlags.StorageBit | VkImageUsageFlags.TransferDstBit,
            (int)_mipLevels
        );

        // 2. Create min-reduction linear clamp sampler
        _minReductionSampler = VulkanSampler.CreateMinReductionLinearClamp(_context);

        // 3. Create per-level storage and sampled views
        _storageViews = new VkImageView[_mipLevels];
        _sampledViews = new VkImageView[_mipLevels];

        for (uint i = 0; i < _mipLevels; i++)
        {
            var subresource = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = (int)i,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };

            var storageViewInfo = new VkImageViewCreateInfo
            {
                Image = _hizTexture.Image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R32SFloat,
                SubresourceRange = subresource,
            };
            _storageViews[i] = _context.Device.CreateImageView(in storageViewInfo, null);

            var sampledViewInfo = new VkImageViewCreateInfo
            {
                Image = _hizTexture.Image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R32SFloat,
                SubresourceRange = subresource,
            };
            _sampledViews[i] = _context.Device.CreateImageView(in sampledViewInfo, null);
        }

        // Full pyramid view for culling
        var fullSubresource = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = (int)_mipLevels,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        var fullViewInfo = new VkImageViewCreateInfo
        {
            Image = _hizTexture.Image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R32SFloat,
            SubresourceRange = fullSubresource,
        };
        _fullPyramidView = _context.Device.CreateImageView(in fullViewInfo, null);

        // 4. Create descriptor layout
        _descriptorLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit)
            .AddBinding(1, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)
            .Build(_context);

        // 5. Create descriptor allocator and allocate per-level descriptor sets
        _descriptorAllocator = new DescriptorAllocator(_context, maxSetsPerPool: (int)(_mipLevels + 4));
        _descriptorSets = new VkDescriptorSet[_mipLevels];
        for (int i = 0; i < _mipLevels; i++)
        {
            _descriptorSets[i] = _descriptorAllocator.Allocate(_descriptorLayout);
        }

        // 6. Build pipeline layout with push constants
        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.ComputeBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<HiZPushConstants>()),
        };

        var setLayout = _descriptorLayout.Handle;
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PushConstantRanges = &pushRange,
        };

        _pipelineLayout = _context.Device.CreatePipelineLayout(in pipelineLayoutInfo, null);

        // 7. Compile compute shader & create compute pipeline
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "assets", "shaders", "hiz_build.comp");
        if (!File.Exists(shaderPath))
        {
            shaderPath = Path.Combine(Directory.GetCurrentDirectory(), "assets", "shaders", "hiz_build.comp");
        }

        var computeShader = shaderCompiler.CompileShaderModuleFromFile(_context.Device, shaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);
        _pipeline = PipelineBuilder.CreateComputePipeline(_context, _pipelineLayout, computeShader);
    }

    /// <summary>
    /// Executes the downsample passes to generate the Hi-Z pyramid from the provided depth buffer.
    /// </summary>
    public unsafe void Build(VkCommandBuffer cmd, DepthBuffer depthBuffer)
    {
        ArgumentNullException.ThrowIfNull(depthBuffer);

        // Bind source depth to level 0 if not already bound or depth buffer changed
        _descriptorAllocator.WriteImage(
            _descriptorSets[0],
            0,
            _minReductionSampler.Handle,
            depthBuffer.ImageView,
            VkImageLayout.DepthStencilReadOnlyOptimal
        );
        _descriptorAllocator.WriteStorageImage(
            _descriptorSets[0],
            1,
            _storageViews[0],
            VkImageLayout.General
        );

        // Bind subsequent levels (reading sampled level i-1, writing storage level i)
        for (int i = 1; i < _mipLevels; i++)
        {
            _descriptorAllocator.WriteImage(
                _descriptorSets[i],
                0,
                _minReductionSampler.Handle,
                _sampledViews[i - 1],
                VkImageLayout.ShaderReadOnlyOptimal
            );
            _descriptorAllocator.WriteStorageImage(
                _descriptorSets[i],
                1,
                _storageViews[i],
                VkImageLayout.General
            );
        }

        // Bind pipeline
        _context.Device.PInvoke.vkCmdBindPipeline.Invoke(
            cmd,
            VkPipelineBindPoint.Compute,
            _pipeline.Handle
        );

        uint prevW = (uint)depthBuffer.Width;
        uint prevH = (uint)depthBuffer.Height;
        uint currW = _width;
        uint currH = _height;

        for (uint i = 0; i < _mipLevels; i++)
        {
            // Transition destination mip level to GENERAL layout for imageStore
            TransitionMipLevel(
                cmd,
                i,
                VkImageLayout.Undefined,
                VkImageLayout.General,
                VkAccessFlags2.None,
                VkAccessFlags2.ShaderStorageWriteBit,
                VkPipelineStageFlags2.TopOfPipeBit,
                VkPipelineStageFlags2.ComputeShaderBit
            );

            // Bind descriptor set for this mip
            var set = _descriptorSets[i];
            _context.Device.PInvoke.vkCmdBindDescriptorSets.Invoke(
                cmd,
                VkPipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &set,
                0,
                null
            );

            // Push resolution constants
            var push = new HiZPushConstants
            {
                PrevWidth = prevW,
                PrevHeight = prevH,
                DestWidth = currW,
                DestHeight = currH,
            };

            _context.Device.PInvoke.vkCmdPushConstants.Invoke(
                cmd,
                _pipelineLayout,
                VkShaderStageFlags.ComputeBit,
                new ByteSize32(0),
                (ByteSize32)sizeof(HiZPushConstants),
                &push
            );

            // Dispatch compute
            uint groupsX = (currW + 15) / 16;
            uint groupsY = (currH + 15) / 16;
            _context.Device.PInvoke.vkCmdDispatch.Invoke(cmd, (int)groupsX, (int)groupsY, 1);

            // Transition destination mip level to SHADER_READ_ONLY_OPTIMAL for subsequent sampling
            TransitionMipLevel(
                cmd,
                i,
                VkImageLayout.General,
                VkImageLayout.ShaderReadOnlyOptimal,
                VkAccessFlags2.ShaderStorageWriteBit,
                VkAccessFlags2.ShaderSampledReadBit,
                VkPipelineStageFlags2.ComputeShaderBit,
                VkPipelineStageFlags2.ComputeShaderBit
            );

            prevW = currW;
            prevH = currH;
            currW = Math.Max(1, currW / 2);
            currH = Math.Max(1, currH / 2);
        }
    }

    private unsafe void TransitionMipLevel(
        VkCommandBuffer cmd,
        uint mipLevel,
        VkImageLayout oldLayout,
        VkImageLayout newLayout,
        VkAccessFlags2 srcAccess,
        VkAccessFlags2 dstAccess,
        VkPipelineStageFlags2 srcStage,
        VkPipelineStageFlags2 dstStage)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = _hizTexture.Image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = (int)mipLevel,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var depInfo = new VkDependencyInfo
        {
            ImageMemoryBarrierCount = 1,
            ImageMemoryBarriers = &barrier,
        };

        _context.Device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &depInfo);
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            _pipeline.Dispose();

            for (int i = 0; i < _mipLevels; i++)
            {
                if (_storageViews[i].VkHandle != 0)
                    _context.Device.DestroyImageView(_storageViews[i], null);
                if (_sampledViews[i].VkHandle != 0)
                    _context.Device.DestroyImageView(_sampledViews[i], null);
            }

            if (_fullPyramidView.VkHandle != 0)
                _context.Device.DestroyImageView(_fullPyramidView, null);

            _minReductionSampler.Dispose();
            _descriptorLayout.Dispose();
            _descriptorAllocator.Dispose();
            _hizTexture.Dispose();

            _disposed = true;
        }
    }
}
