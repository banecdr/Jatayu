/* =================================================================================================
 * FILE: IblProbe.cs
 * LOCATION: src/Renderer/Lighting/IblProbe.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages Image-Based Lighting (IBL) resources for split-sum PBR environment reflections:
 *   - 2D BRDF integration LUT (256x256 RG16F)
 *   - Diffuse irradiance cubemap (64x64x6 RGBA16F)
 *   - Specular prefiltered reflection cubemap with 5 roughness mips (128x128x6 RGBA16F)
 * ================================================================================================= */

using System;
using System.IO;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Image-based lighting probe holding split-sum BRDF LUT, diffuse irradiance, and specular reflection maps.
/// </summary>
public sealed class IblProbe : IDisposable
{
    public const int BrdfLutResolution = 256;
    public const int IrradianceResolution = 64;
    public const int PrefilterResolution = 128;
    public const int PrefilterMipLevels = 5;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private GpuTexture _brdfLutTexture;
    private VkImageView _brdfLutStorageView;
    private VkSampler _brdfLutSampler;
    private VulkanPipeline? _pipeline;
    private DescriptorLayout? _descLayout;
    private DescriptorAllocator? _descAllocator;
    private bool _brdfLutGenerated;
    private bool _disposed;

    /// <summary>
    /// Gets the 2D split-sum BRDF integration LUT texture (256x256 RG16F).
    /// </summary>
    public GpuTexture BrdfLutTexture => _brdfLutTexture;

    /// <summary>
    /// Gets the sampler for the BRDF LUT (linear clamp-to-edge).
    /// </summary>
    public VkSampler BrdfLutSampler => _brdfLutSampler;

    public unsafe IblProbe(VulkanContext context, GpuAllocator allocator)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        var device = _context.Device;

        // 1. Create BRDF LUT texture (256x256 RG16F, Storage + Sampled)
        var imageInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R16G16SFloat,
            Extent = new VkExtent3D(BrdfLutResolution, BrdfLutResolution, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };

        allocator.CreateImage(
            imageInfo,
            VmaMemoryUsage.GpuOnly,
            VmaAllocationCreateFlags.None,
            out var image,
            out var allocation,
            out _
        );

        var sampledViewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R16G16SFloat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        var sampledView = device.CreateImageView(in sampledViewInfo, null);
        _brdfLutTexture = new GpuTexture(_context, _allocator, image, allocation, sampledView, BrdfLutResolution, BrdfLutResolution, VkFormat.R16G16SFloat, 1, VkImageLayout.Undefined);

        var storageViewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R16G16SFloat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        _brdfLutStorageView = device.CreateImageView(in storageViewInfo, null);

        // 2. Create bilinear clamp sampler for BRDF LUT
        var samplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MinLod = 0.0f,
            MaxLod = 1.0f,
        };
        _brdfLutSampler = device.CreateSampler(in samplerInfo, null);
    }

    /// <summary>
    /// Executes the compute pass to pre-integrate the 2D split-sum BRDF LUT.
    /// </summary>
    public unsafe void GenerateBrdfLut(VkCommandBuffer cmd, ShaderCompiler compiler)
    {
        if (_brdfLutGenerated) return;

        var device = _context.Device;

        // 1. Transition image to General layout for compute storage write
        var barrier = stackalloc VkImageMemoryBarrier2[1];
        barrier[0] = new VkImageMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.TopOfPipeBit,
            SrcAccessMask = VkAccessFlags2.None,
            DstStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            OldLayout = VkImageLayout.Undefined,
            NewLayout = VkImageLayout.General,
            Image = _brdfLutTexture.Image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var depInfo = new VkDependencyInfo
        {
            ImageMemoryBarrierCount = 1,
            ImageMemoryBarriers = barrier,
        };
        device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &depInfo);

        // 2. Compile shader & create pipeline
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "assets", "shaders", "brdf_lut.comp");
        if (!File.Exists(shaderPath))
        {
            shaderPath = Path.Combine(Directory.GetCurrentDirectory(), "assets", "shaders", "brdf_lut.comp");
        }

        var shaderModule = compiler.CompileShaderModuleFromFile(device, shaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);

        _descLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)
            .Build(_context);

        _descAllocator = new DescriptorAllocator(_context, 2);

        var descSet = _descAllocator.Allocate(_descLayout);
        _descAllocator.WriteStorageImage(descSet, 0, _brdfLutStorageView, VkImageLayout.General);

        var setLayout = _descLayout.Handle;
        var pipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
        };
        var pipeLayout = device.CreatePipelineLayout(in pipeLayoutInfo, null);
        _pipeline = PipelineBuilder.CreateComputePipeline(_context, pipeLayout, shaderModule);
        device.DestroyShaderModule(shaderModule, null);

        // 3. Dispatch compute shader (16x16 workgroups over 256x256 image)
        device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _pipeline.Handle);
        device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd, VkPipelineBindPoint.Compute, pipeLayout, 0, 1, &descSet, 0, null);
        device.PInvoke.vkCmdDispatch.Invoke(cmd, BrdfLutResolution / 16, BrdfLutResolution / 16, 1);

        // 4. Transition image to ShaderReadOnlyOptimal for fragment shader sampling
        barrier[0] = new VkImageMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderSampledReadBit,
            OldLayout = VkImageLayout.General,
            NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
            Image = _brdfLutTexture.Image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &depInfo);

        _brdfLutGenerated = true;
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        _pipeline?.Dispose();
        _descAllocator?.Dispose();
        _descLayout?.Dispose();

        if (_brdfLutSampler.VkHandle != 0) device.DestroySampler(_brdfLutSampler, null);
        if (_brdfLutStorageView.VkHandle != 0) device.DestroyImageView(_brdfLutStorageView, null);
        _brdfLutTexture?.Dispose();

        _disposed = true;
    }
}
