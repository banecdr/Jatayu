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
    private uint _width;
    private uint _height;

    private GpuTexture _accumTexture;
    private GpuTexture _revealageTexture;
    private VkSampler _sampler;

    private readonly DescriptorLayout _compositeDescLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _compositeDescSet;
    private readonly VkPipelineLayout _compositePipelineLayout;
    private readonly VulkanPipeline _compositePipeline;
    private RgTextureHandle _accumHandle;
    private RgTextureHandle _revealageHandle;
    private RgTextureHandle _sceneColorHandle;
    private RgTextureHandle _sceneDepthHandle;
    private readonly Action<RenderPassBuilder> _accumulationSetup;
    private readonly Action<RenderPassBuilder> _compositeSetup;
    private readonly Action<RenderPassContext> _compositeExecute;
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
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Transparency targets must have positive dimensions.");
        _width = (uint)width;
        _height = (uint)height;

        var device = _context.Device;
        _accumulationSetup = SetupAccumulationPass;
        _compositeSetup = SetupCompositePass;
        _compositeExecute = ExecuteCompositePass;

        _accumTexture = CreateRenderTarget(width, height, VkFormat.R16G16B16A16SFloat);
        _revealageTexture = CreateRenderTarget(width, height, VkFormat.R8UNorm);

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

        string vertPath = ResolveShader("assets/shaders/transparent_composite.vert");
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

    private unsafe GpuTexture CreateRenderTarget(int width, int height, VkFormat format)
    {
        var imageInfo = new VkImageCreateInfo
        {
            ImageType = VkImageType._2D,
            Format = format,
            Extent = new VkExtent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
        _allocator.CreateImage(imageInfo, VmaMemoryUsage.GpuOnly, VmaAllocationCreateFlags.None, out var image, out var allocation, out _);
        var viewInfo = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = format,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        var view = _context.Device.CreateImageView(in viewInfo, null);
        return new GpuTexture(_context, _allocator, image, allocation, view, width, height, format, 1, VkImageLayout.Undefined);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Transparency targets must have positive dimensions.");
        if (_width == (uint)width && _height == (uint)height)
            return;

        var newAccum = CreateRenderTarget(width, height, VkFormat.R16G16B16A16SFloat);
        var newRevealage = CreateRenderTarget(width, height, VkFormat.R8UNorm);
        var oldAccum = _accumTexture;
        var oldRevealage = _revealageTexture;
        _accumTexture = newAccum;
        _revealageTexture = newRevealage;
        _width = (uint)width;
        _height = (uint)height;
        _descriptorAllocator.WriteImage(_compositeDescSet, 0, _sampler, _accumTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
        _descriptorAllocator.WriteImage(_compositeDescSet, 1, _sampler, _revealageTexture.ImageView, VkImageLayout.ShaderReadOnlyOptimal);
        oldAccum.Dispose();
        oldRevealage.Dispose();
    }

    /// <summary>
    /// Executes the fullscreen composite pass blending transparent accumulation over the scene color target.
    /// </summary>
    public void AddPasses(
        RenderGraph graph,
        RgTextureHandle sceneColor,
        RgTextureHandle sceneDepth,
        Action<RenderPassContext> renderTransparentGeometry)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(renderTransparentGeometry);
        if (graph.GetTextureWidth(sceneColor) != _width || graph.GetTextureHeight(sceneColor) != _height ||
            graph.GetTextureWidth(sceneDepth) != _width || graph.GetTextureHeight(sceneDepth) != _height)
            throw new InvalidOperationException("WBOIT targets must match the scene color and depth dimensions.");
        _sceneColorHandle = sceneColor;
        _sceneDepthHandle = sceneDepth;
        _accumHandle = graph.ImportTexture(
            "WboitAccumulation",
            _accumTexture.Image,
            _accumTexture.ImageView,
            _accumTexture.Format,
            _width,
            _height,
            VkImageLayout.Undefined);
        _revealageHandle = graph.ImportTexture(
            "WboitRevealage",
            _revealageTexture.Image,
            _revealageTexture.ImageView,
            _revealageTexture.Format,
            _width,
            _height,
            VkImageLayout.Undefined);

        graph.AddPass("WboitAccumulationPass", _accumulationSetup, renderTransparentGeometry);
        graph.AddPass("WboitCompositePass", _compositeSetup, _compositeExecute);
    }

    private void SetupAccumulationPass(RenderPassBuilder builder)
    {
        builder.SetColorAttachment(
            _accumHandle,
            VkAttachmentLoadOp.Clear,
            VkAttachmentStoreOp.Store,
            new VkClearColorValue { Float32 = new Brutal.Numerics.float4(0f, 0f, 0f, 0f) });
        builder.SetColorAttachment(
            _revealageHandle,
            VkAttachmentLoadOp.Clear,
            VkAttachmentStoreOp.Store,
            new VkClearColorValue { Float32 = new Brutal.Numerics.float4(1f, 0f, 0f, 0f) });
        builder.SetDepthAttachment(_sceneDepthHandle, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
    }

    private void SetupCompositePass(RenderPassBuilder builder)
    {
        builder.ReadTexture(_accumHandle);
        builder.ReadTexture(_revealageHandle);
        builder.SetColorAttachment(_sceneColorHandle, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
    }

    private void ExecuteCompositePass(RenderPassContext ctx)
    {
        var cmd = ctx.CommandBuffer;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _compositePipeline.Handle);

        Span<VkDescriptorSet> sets = [_compositeDescSet];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _compositePipelineLayout, 0, sets, Span<ByteSize32>.Empty);

        Span<VkViewport> viewports = [new VkViewport { Width = ctx.RenderArea.Width, Height = ctx.RenderArea.Height, MinDepth = 0f, MaxDepth = 1f }];
        Span<VkRect2D> scissors = [new VkRect2D(new VkExtent2D((int)ctx.RenderArea.Width, (int)ctx.RenderArea.Height))];
        cmd.SetViewport(0, viewports);
        cmd.SetScissor(0, scissors);
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
