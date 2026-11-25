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

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Image-based lighting probe holding split-sum BRDF LUT, diffuse irradiance, and specular reflection maps.
/// </summary>
public sealed class IblProbe : IDisposable
{
    public const int BrdfLutResolution = 256;
    public const int IrradianceResolution = 64;
    public const int PrefilterResolution = 512;
    public const int PrefilterMipLevels = 5;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct PrefilterPushConstants
    {
        public float Roughness;
        public uint Resolution;
        public uint NumSamples;
        public float Padding;
    }

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private VkBuffer _stagingBuffer;
    private VmaAllocation _stagingAlloc;

    private GpuTexture _brdfLutTexture;
    private VkImageView _brdfLutStorageView;
    private VkSampler _brdfLutSampler;
    private GpuTexture _environmentTexture;
    private GpuTexture _irradianceTexture;
    private GpuTexture _prefilterTexture;
    private VkImageView _environmentStorageView;
    private VkImageView _irradianceStorageView;
    private readonly VkImageView[] _prefilterStorageViews = new VkImageView[PrefilterMipLevels];
    private VkSampler _environmentSampler;
    private VkSampler _irradianceSampler;
    private VkSampler _prefilterSampler;
    private VulkanPipeline? _brdfPipeline;
    private VulkanPipeline? _environmentPipeline;
    private VulkanPipeline? _irradiancePipeline;
    private VulkanPipeline? _prefilterPipeline;
    private DescriptorLayout? _brdfDescLayout;
    private DescriptorLayout? _environmentDescLayout;
    private DescriptorLayout? _convolutionDescLayout;
    private DescriptorAllocator? _descAllocator;
    private VkPipelineLayout _brdfPipelineLayout;
    private VkPipelineLayout _environmentPipelineLayout;
    private VkPipelineLayout _convolutionPipelineLayout;
    private bool _brdfLutGenerated;
    private bool _environmentMapsGenerated;
    private bool _disposed;

    /// <summary>
    /// Gets the 2D split-sum BRDF integration LUT texture (256x256 RG16F).
    /// </summary>
    public GpuTexture BrdfLutTexture => _brdfLutTexture;

    /// <summary>
    /// Gets the sampler for the BRDF LUT (linear clamp-to-edge).
    /// </summary>
    public VkSampler BrdfLutSampler => _brdfLutSampler;
    public GpuTexture IrradianceTexture => _irradianceTexture;
    public GpuTexture PrefilterTexture => _prefilterTexture;
    public VkSampler IrradianceSampler => _irradianceSampler;
    public VkSampler PrefilterSampler => _prefilterSampler;

    public unsafe IblProbe(VulkanContext context, GpuAllocator allocator)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        var device = _context.Device;

        _environmentTexture = CreateCubeTexture(PrefilterResolution, 1, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit);
        _irradianceTexture = CreateCubeTexture(IrradianceResolution, 1, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);
        _prefilterTexture = CreateCubeTexture(PrefilterResolution, PrefilterMipLevels, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);
        _environmentStorageView = CreateArrayView(_environmentTexture, 0, 1);
        _irradianceStorageView = CreateArrayView(_irradianceTexture, 0, 1);
        for (int mip = 0; mip < PrefilterMipLevels; mip++)
            _prefilterStorageViews[mip] = CreateArrayView(_prefilterTexture, mip, 1);

        _environmentSampler = CreateCubeSampler(0f);
        _irradianceSampler = CreateCubeSampler(0f);
        _prefilterSampler = CreateCubeSampler(PrefilterMipLevels - 1);

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

    private unsafe GpuTexture CreateCubeTexture(int resolution, int mipLevels, VkImageUsageFlags usage)
    {
        var imageInfo = new VkImageCreateInfo
        {
            Flags = VkImageCreateFlags.CubeCompatibleBit,
            ImageType = VkImageType._2D,
            Format = VkFormat.R16G16B16A16SFloat,
            Extent = new VkExtent3D(resolution, resolution, 1),
            MipLevels = mipLevels,
            ArrayLayers = 6,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = usage,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
        _allocator.CreateImage(imageInfo, VmaMemoryUsage.GpuOnly, VmaAllocationCreateFlags.None, out var image, out var allocation, out _);

        var viewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType.Cube,
            Format = VkFormat.R16G16B16A16SFloat,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = 6,
            },
        };
        var cubeView = _context.Device.CreateImageView(in viewInfo, null);
        return new GpuTexture(_context, _allocator, image, allocation, cubeView, resolution, resolution, VkFormat.R16G16B16A16SFloat, mipLevels);
    }

    private unsafe VkImageView CreateArrayView(GpuTexture texture, int baseMip, int mipCount)
    {
        var viewInfo = new VkImageViewCreateInfo
        {
            Image = texture.Image,
            ViewType = VkImageViewType._2DArray,
            Format = texture.Format,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = baseMip,
                LevelCount = mipCount,
                BaseArrayLayer = 0,
                LayerCount = 6,
            },
        };
        return _context.Device.CreateImageView(in viewInfo, null);
    }

    private unsafe VkSampler CreateCubeSampler(float maxLod)
    {
        var info = new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
            MinLod = 0,
            MaxLod = maxLod,
        };
        return _context.Device.CreateSampler(in info, null);
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

        _brdfDescLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)
            .Build(_context);

        _descAllocator ??= new DescriptorAllocator(_context, 16);

        var descSet = _descAllocator.Allocate(_brdfDescLayout);
        _descAllocator.WriteStorageImage(descSet, 0, _brdfLutStorageView, VkImageLayout.General);

        var setLayout = _brdfDescLayout.Handle;
        var pipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
        };
        _brdfPipelineLayout = device.CreatePipelineLayout(in pipeLayoutInfo, null);
        _brdfPipeline = PipelineBuilder.CreateComputePipeline(_context, _brdfPipelineLayout, shaderModule);
        device.DestroyShaderModule(shaderModule, null);

        // 3. Dispatch compute shader (16x16 workgroups over 256x256 image)
        device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _brdfPipeline.Handle);
        device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd, VkPipelineBindPoint.Compute, _brdfPipelineLayout, 0, 1, &descSet, 0, null);
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

    /// <summary>
    /// Generates a procedural sky environment, diffuse irradiance cube, and roughness-prefiltered specular cube.
    /// </summary>
    public unsafe void GenerateEnvironmentMaps(VkCommandBuffer cmd, ShaderCompiler compiler)
    {
        if (_environmentMapsGenerated)
            return;

        ArgumentNullException.ThrowIfNull(compiler);
        var device = _context.Device;

        _environmentDescLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)
            .Build(_context);
        _convolutionDescLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit)
            .AddBinding(1, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)
            .Build(_context);
        _descAllocator ??= new DescriptorAllocator(_context, 16);

        var envSet = _descAllocator.Allocate(_environmentDescLayout);
        _descAllocator.WriteStorageImage(envSet, 0, _environmentStorageView, VkImageLayout.General);

        var irradianceSet = _descAllocator.Allocate(_convolutionDescLayout);
        _descAllocator.WriteImage(irradianceSet, 0, _environmentSampler, _environmentTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
        _descAllocator.WriteStorageImage(irradianceSet, 1, _irradianceStorageView, VkImageLayout.General);

        var prefilterSets = new VkDescriptorSet[PrefilterMipLevels];
        for (int mip = 0; mip < PrefilterMipLevels; mip++)
        {
            prefilterSets[mip] = _descAllocator.Allocate(_convolutionDescLayout);
            _descAllocator.WriteImage(prefilterSets[mip], 0, _environmentSampler, _environmentTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
            _descAllocator.WriteStorageImage(prefilterSets[mip], 1, _prefilterStorageViews[mip], VkImageLayout.General);
        }

        var envLayout = _environmentDescLayout.Handle;
        _environmentPipelineLayout = device.CreatePipelineLayout(new[] { envLayout }, Array.Empty<VkPushConstantRange>(), null);

        var convolutionLayout = _convolutionDescLayout.Handle;
        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.ComputeBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<PrefilterPushConstants>()),
        };
        _convolutionPipelineLayout = device.CreatePipelineLayout([convolutionLayout], [pushRange], null);

        _environmentPipeline = CreateComputePipeline(compiler, "ibl_environment.comp", _environmentPipelineLayout, ownsLayout: true);
        _irradiancePipeline = CreateComputePipeline(compiler, "ibl_irradiance.comp", _convolutionPipelineLayout, ownsLayout: true);
        _prefilterPipeline = CreateComputePipeline(compiler, "ibl_prefilter.comp", _convolutionPipelineLayout, ownsLayout: false);

        bool loadedHdr = TryLoadHdrCubemap(cmd);
        if (!loadedHdr)
        {
            TransitionImage(cmd, _environmentTexture.Image, VkImageLayout.Undefined, VkImageLayout.General,
                VkPipelineStageFlags2.TopOfPipeBit, VkAccessFlags2.None,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
                0, 1);

            device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _environmentPipeline.Handle);
            device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd, VkPipelineBindPoint.Compute, _environmentPipelineLayout, 0, 1, &envSet, 0, null);
            device.PInvoke.vkCmdDispatch.Invoke(cmd, (PrefilterResolution + 15) / 16, (PrefilterResolution + 15) / 16, 6);
            TransitionImage(cmd, _environmentTexture.Image, VkImageLayout.General, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit,
                0, 1);
        }

        TransitionImage(cmd, _irradianceTexture.Image, VkImageLayout.Undefined, VkImageLayout.General,
            VkPipelineStageFlags2.TopOfPipeBit, VkAccessFlags2.None,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
            0, 1);
        TransitionImage(cmd, _prefilterTexture.Image, VkImageLayout.Undefined, VkImageLayout.General,
            VkPipelineStageFlags2.TopOfPipeBit, VkAccessFlags2.None,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
            0, PrefilterMipLevels);

        device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _irradiancePipeline.Handle);
        device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd, VkPipelineBindPoint.Compute, _convolutionPipelineLayout, 0, 1, &irradianceSet, 0, null);
        device.PInvoke.vkCmdDispatch.Invoke(cmd, (IrradianceResolution + 15) / 16, (IrradianceResolution + 15) / 16, 6);

        device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _prefilterPipeline.Handle);
        for (int mip = 0; mip < PrefilterMipLevels; mip++)
        {
            uint resolution = (uint)Math.Max(1, PrefilterResolution >> mip);
            var set = prefilterSets[mip];
            device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd, VkPipelineBindPoint.Compute, _convolutionPipelineLayout, 0, 1, &set, 0, null);
            var push = new PrefilterPushConstants
            {
                Roughness = (float)mip / (PrefilterMipLevels - 1),
                Resolution = resolution,
                NumSamples = (mip == 0) ? 32u : 256u,
            };
            device.PInvoke.vkCmdPushConstants.Invoke(
                cmd,
                _convolutionPipelineLayout,
                VkShaderStageFlags.ComputeBit,
                new ByteSize32(0),
                (ByteSize32)sizeof(PrefilterPushConstants),
                &push
            );
            device.PInvoke.vkCmdDispatch.Invoke(cmd, (int)((resolution + 15) / 16), (int)((resolution + 15) / 16), 6);
        }

        TransitionImage(cmd, _irradianceTexture.Image, VkImageLayout.General, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
            VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderSampledReadBit,
            0, 1);
        TransitionImage(cmd, _prefilterTexture.Image, VkImageLayout.General, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderStorageWriteBit,
            VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderSampledReadBit,
            0, PrefilterMipLevels);

        _environmentMapsGenerated = true;
    }

    private unsafe VulkanPipeline CreateComputePipeline(
        ShaderCompiler compiler,
        string shaderName,
        VkPipelineLayout layout,
        bool ownsLayout = true)
    {
        string shaderPath = ResolveShader(shaderName);
        var shaderModule = compiler.CompileShaderModuleFromFile(_context.Device, shaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);
        try
        {
            return PipelineBuilder.CreateComputePipeline(_context, layout, shaderModule, ownsLayout: ownsLayout);
        }
        finally
        {
            _context.Device.DestroyShaderModule(shaderModule, null);
        }
    }

    internal static string ResolveShader(string shaderName, string? shaderAssetRoot = null)
    {
        string path = Path.Combine("assets", "shaders", shaderName);
        if (shaderAssetRoot is not null)
            path = Path.Combine(shaderAssetRoot, path);
        return ShaderCompiler.ResolveShaderFile(path);
    }

    private unsafe void TransitionImage(
        VkCommandBuffer cmd,
        VkImage image,
        VkImageLayout oldLayout,
        VkImageLayout newLayout,
        VkPipelineStageFlags2 srcStage,
        VkAccessFlags2 srcAccess,
        VkPipelineStageFlags2 dstStage,
        VkAccessFlags2 dstAccess,
        int baseMip,
        int mipCount)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = baseMip,
                LevelCount = mipCount,
                BaseArrayLayer = 0,
                LayerCount = 6,
            },
        };
        var dependency = new VkDependencyInfo
        {
            ImageMemoryBarrierCount = 1,
            ImageMemoryBarriers = &barrier,
        };
        _context.Device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &dependency);
    }

    private unsafe bool TryLoadHdrCubemap(VkCommandBuffer cmd)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "textures", "HDRI", "sunflowers_puresky_cubemap_512.jtex"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "textures", "HDRI", "sunflowers_puresky_cubemap_512.jtex"),
            "assets/textures/HDRI/sunflowers_puresky_cubemap_512.jtex"
        ];

        string? validPath = null;
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                validPath = p;
                break;
            }
        }

        if (validPath == null)
            return false;

        try
        {
            using var fs = File.OpenRead(validPath);
            int headerSize = Marshal.SizeOf<Jatayu.AssetFormats.CookedTextureHeader>();
            Span<byte> headerBytes = stackalloc byte[headerSize];
            fs.ReadExactly(headerBytes);
            var header = MemoryMarshal.Read<Jatayu.AssetFormats.CookedTextureHeader>(headerBytes);

            if (!header.IsValid || header.Width != (uint)PrefilterResolution || header.Height != (uint)PrefilterResolution || header.Format != Jatayu.AssetFormats.CookedTextureFormat.Rgba16Float)
                return false;

            long payloadSize = fs.Length - headerSize;
            long expectedSize = 6L * PrefilterResolution * PrefilterResolution * 4 * 2;
            if (payloadSize != expectedSize)
                return false;

            _allocator.CreateBuffer(
                (ulong)payloadSize,
                VkBufferUsageFlags.TransferSrcBit,
                VmaMemoryUsage.CpuToGpu,
                VmaAllocationCreateFlags.MappedBit,
                out _stagingBuffer,
                out _stagingAlloc,
                out var allocInfo
            );

            byte* pDst = (byte*)allocInfo.PMappedData;
            var dstSpan = new Span<byte>(pDst, (int)payloadSize);
            fs.ReadExactly(dstSpan);
            _allocator.FlushAllocation(_stagingAlloc);

            TransitionImage(cmd, _environmentTexture.Image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags2.TopOfPipeBit, VkAccessFlags2.None,
                VkPipelineStageFlags2.TransferBit, VkAccessFlags2.TransferWriteBit,
                0, 1);

            ulong faceBytes = (ulong)(PrefilterResolution * PrefilterResolution * 4 * 2);
            var copyRegions = stackalloc VkBufferImageCopy[6];
            for (int f = 0; f < 6; f++)
            {
                copyRegions[f] = new VkBufferImageCopy
                {
                    BufferOffset = new ByteSize64((ulong)f * faceBytes),
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new VkImageSubresourceLayers
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = f,
                        LayerCount = 1,
                    },
                    ImageOffset = new VkOffset3D(0, 0, 0),
                    ImageExtent = new VkExtent3D(PrefilterResolution, PrefilterResolution, 1),
                };
            }

            _context.Device.PInvoke.vkCmdCopyBufferToImage.Invoke(
                cmd,
                _stagingBuffer,
                _environmentTexture.Image,
                VkImageLayout.TransferDstOptimal,
                6,
                copyRegions
            );

            TransitionImage(cmd, _environmentTexture.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags2.TransferBit, VkAccessFlags2.TransferWriteBit,
                VkPipelineStageFlags2.ComputeShaderBit, VkAccessFlags2.ShaderSampledReadBit,
                0, 1);

            Console.WriteLine($"[IblProbe] Successfully loaded 512x512 HDR cubemap from: {validPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IblProbe Warning] Failed to load HDR cubemap from '{validPath}': {ex.Message}");
            return false;
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        _brdfPipeline?.Dispose();
        _environmentPipeline?.Dispose();
        _irradiancePipeline?.Dispose();
        _prefilterPipeline?.Dispose();
        _descAllocator?.Dispose();
        _brdfDescLayout?.Dispose();
        _environmentDescLayout?.Dispose();
        _convolutionDescLayout?.Dispose();

        if (_stagingBuffer.VkHandle != 0)
        {
            _allocator.DestroyBuffer(_stagingBuffer, _stagingAlloc);
            _stagingBuffer = default;
        }

        if (_brdfLutSampler.VkHandle != 0) device.DestroySampler(_brdfLutSampler, null);
        if (_environmentSampler.VkHandle != 0) device.DestroySampler(_environmentSampler, null);
        if (_irradianceSampler.VkHandle != 0) device.DestroySampler(_irradianceSampler, null);
        if (_prefilterSampler.VkHandle != 0) device.DestroySampler(_prefilterSampler, null);
        if (_brdfLutStorageView.VkHandle != 0) device.DestroyImageView(_brdfLutStorageView, null);
        if (_environmentStorageView.VkHandle != 0) device.DestroyImageView(_environmentStorageView, null);
        if (_irradianceStorageView.VkHandle != 0) device.DestroyImageView(_irradianceStorageView, null);
        foreach (var storageView in _prefilterStorageViews)
        {
            if (storageView.VkHandle != 0) device.DestroyImageView(storageView, null);
        }
        _environmentTexture?.Dispose();
        _irradianceTexture?.Dispose();
        _prefilterTexture?.Dispose();
        _brdfLutTexture?.Dispose();

        _disposed = true;
    }
}
