/* =================================================================================================
 * FILE: TransparentPass.cs
 * LOCATION: src/Renderer/Transparent/TransparentPass.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages order-independent transparency (WBOIT) and forward alpha blending.
 *   Allocates accumulation and revealage render targets, and executes fullscreen
 *   transparent composition over the main HDR scene color target.
 * ================================================================================================= */

using System;
using System.IO;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Graph;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.RHI.Vulkan.Textures;

namespace Jatayu.Renderer.Transparent;

/// <summary>
/// Order-independent transparency compositing pass.
/// </summary>
public sealed class TransparentPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly uint _width;
    private readonly uint _height;

    private GpuTexture _accumTexture;
    private GpuTexture _revealageTexture;
    private VkSampler _sampler;

    private readonly DescriptorLayout _compositeDescLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _compositeDescSet;
    private readonly VkPipelineLayout _compositePipelineLayout;
    private readonly VulkanPipeline _compositePipeline;
    private bool _disposed;

    public GpuTexture AccumTexture => _accumTexture;
    public GpuTexture RevealageTexture => _revealageTexture;

    public unsafe TransparentPass(
        VulkanContext context,
        GpuAllocator allocator,
        ShaderCompiler shaderCompiler,
        VkFormat colorFormat,
        int width,
        int height)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);
        _width = (uint)width;
        _height = (uint)height;

        var device = _context.Device;

        // 1. Allocate Accumulation target (RGBA16F, clear to (0,0,0,0))
        var accumInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R16G16B16A16SFloat,
            Extent = new VkExtent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
        allocator.CreateImage(accumInfo, VmaMemoryUsage.GpuOnly, VmaAllocationCreateFlags.None, out var accumImg, out var accumAlloc, out _);
        var accumView = device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = accumImg,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R16G16B16A16SFloat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
        _accumTexture = new GpuTexture(_context, _allocator, accumImg, accumAlloc, accumView, width, height, VkFormat.R16G16B16A16SFloat, 1, VkImageLayout.Undefined);

        // 2. Allocate Revealage target (R8_UNORM, clear to 1.0)
        var revealInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = VkFormat.R8UNorm,
            Extent = new VkExtent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
        allocator.CreateImage(revealInfo, VmaMemoryUsage.GpuOnly, VmaAllocationCreateFlags.None, out var revealImg, out var revealAlloc, out _);
        var revealView = device.CreateImageView(new VkImageViewCreateInfo
        {
            Image = revealImg,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R8UNorm,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        }, null);
        _revealageTexture = new GpuTexture(_context, _allocator, revealImg, revealAlloc, revealView, width, height, VkFormat.R8UNorm, 1, VkImageLayout.Undefined);

        // 3. Bilinear clamp sampler
        _sampler = device.CreateSampler(new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
        }, null);

        // 4. Build Fullscreen Composite Pipeline (transparent_composite.frag)
        _compositeDescLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit)
            .AddBinding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit)
            .Build(_context);

        _descriptorAllocator = new DescriptorAllocator(_context, 2);

        _compositeDescSet = _descriptorAllocator.Allocate(_compositeDescLayout);
        _descriptorAllocator.WriteImage(_compositeDescSet, 0, _sampler, _accumTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
        _descriptorAllocator.WriteImage(_compositeDescSet, 1, _sampler, _revealageTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);

        var setLayout = _compositeDescLayout.Handle;
        var pipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
        };
        _compositePipelineLayout = device.CreatePipelineLayout(in pipeLayoutInfo, null);

        string vertPath = ResolveShader("assets/shaders/skybox.vert"); // Reuses fullscreen procedural triangle
        string fragPath = ResolveShader("assets/shaders/transparent_composite.frag");
        var vertModule = shaderCompiler.CompileShaderModuleFromFile(device, vertPath, Brutal.ShaderCApi.ShaderKind.VertexShader);
        var fragModule = shaderCompiler.CompileShaderModuleFromFile(device, fragPath, Brutal.ShaderCApi.ShaderKind.FragmentShader);

        using (var stages = new ShaderStages(device, [Presets.ShaderStage.Vertex(vertModule), Presets.ShaderStage.Fragment(fragModule)], null))
        {
            _compositePipeline = PipelineBuilder.Create()
                .SetShaderStages(stages)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthTest(depthTestEnable: false, depthWriteEnable: false)
                .SetRasterization(Presets.Rasterization.Fill.CullNone)
                .SetColorBlend(Presets.BlendState.BlendColorAlpha)
                .SetPipelineLayout(_compositePipelineLayout)
                .Build(_context);
        }
    }

    /// <summary>
    /// Executes the fullscreen composite pass blending transparent accumulation over the scene color target.
    /// </summary>
    public void Composite(CommandBuffer cmd)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _compositePipeline.Handle);

        Span<VkDescriptorSet> sets = [_compositeDescSet];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _compositePipelineLayout, 0, sets, Span<ByteSize32>.Empty);

        // Fullscreen procedural triangle (3 vertices, no buffers bound)
        cmd.Draw(3, 1, 0, 0);
    }

    private static string ResolveShader(string relPath)
    {
        if (File.Exists(relPath)) return relPath;
        string fromBase = Path.Combine(AppContext.BaseDirectory, relPath);
        if (File.Exists(fromBase)) return fromBase;
        return relPath;
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        _compositePipeline?.Dispose();
        _descriptorAllocator?.Dispose();
        _compositeDescLayout?.Dispose();

        if (_sampler.VkHandle != 0) device.DestroySampler(_sampler, null);
        _accumTexture?.Dispose();
        _revealageTexture?.Dispose();

        _disposed = true;
    }
}
