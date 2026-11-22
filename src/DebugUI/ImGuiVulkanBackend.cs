/* =================================================================================================
 * FILE: ImGuiVulkanBackend.cs
 * LOCATION: src/DebugUI/ImGuiVulkanBackend.cs
 * 
 * WHAT THIS FILE DOES:
 *   Custom Vulkan 1.3 Dynamic Rendering backend for Dear ImGui.
 *   Renders ImGui draw lists directly inside vkCmdBeginRendering passes with ZERO legacy
 *   VkRenderPass or VkFramebuffer objects.
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.Loaders;
using Jatayu.Core;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.RHI.Vulkan.Textures;
using Jatayu.RHI.Vulkan.Upload;

namespace Jatayu.DebugUI;

/// <summary>
/// Vulkan 1.3 Dynamic Rendering backend for Dear ImGui.
/// </summary>
public sealed class ImGuiVulkanBackend : IDisposable
{
    private struct ImGuiPushConstants
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly GlfwBackend _glfwBackend;
    private readonly DescriptorLayout _descriptorLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _fontDescriptorSet;
    private readonly VulkanSampler _fontSampler;
    private readonly GpuTexture _fontTexture;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VulkanPipeline _pipeline;

    private GpuBuffer? _vertexBuffer;
    private GpuBuffer? _indexBuffer;
    private int _vertexCount;
    private int _indexCount;
    private bool _disposed;

    /// <summary>
    /// Initializes the Dear ImGui context and builds the Vulkan 1.3 Dynamic Rendering pipeline.
    /// </summary>
    public unsafe ImGuiVulkanBackend(
        VulkanContext context,
        GpuAllocator allocator,
        UploadManager uploadManager,
        Window window,
        VkFormat colorAttachmentFormat)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(uploadManager);
        ArgumentNullException.ThrowIfNull(window);

        // 1. Initialize ImGui Context & GLFW input backend
        ImGui.CreateContext();
        ImGui.StyleColorsDark();

        _glfwBackend = new GlfwBackend(window.GlfwHandle, GlfwBackend.ClientApi.Vulkan);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // Ensure default font is added so the font atlas is initialized
        if (io.Fonts.Fonts.Count == 0)
        {
            ImFontAtlasExtensions.AddFontDefault(io.Fonts, default);
        }

        // 2. Build font texture and upload via UploadManager
        var texData = io.Fonts.TexData;
        int fontW = texData.Width;
        int fontH = texData.Height;
        var pixelsPtr = (byte*)texData.Pixels.Pointer;
        var fontSpan = new ReadOnlySpan<byte>(pixelsPtr, fontW * fontH * 4);
        var fontImageData = new LoadedImageData(fontW, fontH, 4, ImagePixelFormat.R8G8B8A8_Unorm, fontSpan.ToArray());

        _fontTexture = uploadManager.CreateTextureFromImageData(fontImageData, out var fontUploadVal);
        uploadManager.WaitForUpload(fontUploadVal);

        // 3. Create Font Sampler & Descriptors
        _fontSampler = VulkanSampler.CreateLinearClamp(context);

        _descriptorLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit)
            .Build(context);

        _descriptorAllocator = new DescriptorAllocator(context);
        _fontDescriptorSet = _descriptorAllocator.Allocate(_descriptorLayout);
        _descriptorAllocator.WriteImage(
            _fontDescriptorSet,
            0,
            _fontSampler,
            _fontTexture,
            VkImageLayout.ShaderReadOnlyOptimal
        );

        texData.TexID = new ImTextureID((nint)_fontDescriptorSet.VkHandle);

        // 4. Compile ImGui shaders
        using var shaderCompiler = new ShaderCompiler("assets/shaders/cache");
        var vertShader = shaderCompiler.CompileShaderModuleFromFile(context.Device, "assets/shaders/imgui.vert", ShaderKind.VertexShader);
        var fragShader = shaderCompiler.CompileShaderModuleFromFile(context.Device, "assets/shaders/imgui.frag", ShaderKind.FragmentShader);

        using var shaderStages = new ShaderStages(
            context.Device,
            [
                Presets.ShaderStage.Vertex(vertShader),
                Presets.ShaderStage.Fragment(fragShader),
            ],
            null
        );

        var pushConstantRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)sizeof(ImGuiPushConstants)),
        };

        _pipelineLayout = context.Device.CreatePipelineLayout([_descriptorLayout.Handle], [pushConstantRange], null);

        // 5. Create Vertex Input State for ImDrawVert (pos: float2, uv: float2, col: uint)
        var vertexInput = new VertexInput(1, 3);
        vertexInput.AddBinding(0, new ByteSize((uint)sizeof(ImDrawVert)), VkVertexInputRate.Vertex);
        vertexInput.AddAttribute(0, 0, VkFormat.R32G32SFloat, new ByteSize(0));
        vertexInput.AddAttribute(1, 0, VkFormat.R32G32SFloat, new ByteSize(8));
        vertexInput.AddAttribute(2, 0, VkFormat.R8G8B8A8UNorm, new ByteSize(16));

        // 6. Build Vulkan 1.3 Dynamic Rendering Pipeline
        _pipeline = PipelineBuilder.Create()
            .SetShaderStages(shaderStages)
            .SetVertexInput(vertexInput)
            .SetColorAttachmentFormat(colorAttachmentFormat)
            .SetDepthTest(depthTestEnable: false, depthWriteEnable: false)
            .SetRasterization(Presets.Rasterization.Fill.CullNone)
            .SetColorBlend(Presets.BlendState.BlendColorAlpha)
            .SetPipelineLayout(_pipelineLayout)
            .Build(context);
    }

    /// <summary>
    /// Starts a new ImGui frame. Call before any ImGui UI commands.
    /// </summary>
    public void NewFrame()
    {
        ThrowIfDisposed();
        _glfwBackend.NewFrame();
        ImGui.NewFrame();
    }

    /// <summary>
    /// Renders ImGui draw commands into an active Vulkan 1.3 Dynamic Rendering pass.
    /// </summary>
    public unsafe void Render(CommandBuffer cmd)
    {
        ThrowIfDisposed();

        ImGui.Render();
        var drawData = ImGui.GetDrawData();

        if (drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0)
            return;

        int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0)
            return;

        // 1. Ensure vertex and index buffer capacity
        EnsureBufferCapacity(drawData.TotalVtxCount, drawData.TotalIdxCount);

        // 2. Upload vertex and index data sequentially into host-visible mapped buffers
        var vtxDst = (ImDrawVert*)_vertexBuffer!.MappedData;
        var idxDst = (ushort*)_indexBuffer!.MappedData;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            var vtxSpan = cmdList.VtxBuffer.Span;
            var idxSpan = cmdList.IdxBuffer.Span;

            fixed (ImDrawVert* pVtx = vtxSpan)
            fixed (ushort* pIdx = idxSpan)
            {
                Buffer.MemoryCopy(
                    pVtx,
                    vtxDst,
                    (long)_vertexBuffer.SizeInBytes,
                    (long)(vtxSpan.Length * sizeof(ImDrawVert))
                );
                Buffer.MemoryCopy(
                    pIdx,
                    idxDst,
                    (long)_indexBuffer.SizeInBytes,
                    (long)(idxSpan.Length * sizeof(ushort))
                );
            }

            vtxDst += vtxSpan.Length;
            idxDst += idxSpan.Length;
        }

        // 3. Bind Graphics Pipeline
        _pipeline.Bind(cmd);

        // 4. Bind Vertex and Index buffers
        cmd.BindVertexBuffers(0, [_vertexBuffer.Handle], [new ByteSize64(0)]);
        cmd.BindIndexBuffer(_indexBuffer.Handle, new ByteSize64(0), VkIndexType.UInt16);

        // 5. Set Viewport matching framebuffer dimensions
        var viewport = new VkViewport
        {
            X = 0,
            Y = 0,
            Width = fbWidth,
            Height = fbHeight,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
        cmd.SetViewport(0, [viewport]);

        // 6. Push Scale and Translation constants
        var pushConstants = new ImGuiPushConstants
        {
            Scale = new Vector2(2.0f / drawData.DisplaySize.X, 2.0f / drawData.DisplaySize.Y),
            Translate = new Vector2(-1.0f - drawData.DisplayPos.X * (2.0f / drawData.DisplaySize.X),
                                    -1.0f - drawData.DisplayPos.Y * (2.0f / drawData.DisplaySize.Y))
        };

        cmd.PushConstants(
            _pipelineLayout,
            VkShaderStageFlags.VertexBit,
            0,
            new ReadOnlySpan<byte>(&pushConstants, sizeof(ImGuiPushConstants))
        );

        // 7. Bind font descriptor set (Set 0)
        cmd.BindDescriptorSets(
            VkPipelineBindPoint.Graphics,
            _pipelineLayout,
            0,
            [_fontDescriptorSet],
            []
        );

        // 8. Render Draw Lists
        int globalVtxOffset = 0;
        int globalIdxOffset = 0;
        var clipOff = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            var cmdSpan = cmdList.CmdBuffer.Span;

            for (int cmd_i = 0; cmd_i < cmdSpan.Length; cmd_i++)
            {
                ref readonly var pcmd = ref cmdSpan[cmd_i];

                // Project clip rect into framebuffer coordinates
                var clipMin = new Vector2((pcmd.ClipRect.X - clipOff.X) * clipScale.X, (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                var clipMax = new Vector2((pcmd.ClipRect.Z - clipOff.X) * clipScale.X, (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                if (clipMin.X < 0.0f) clipMin.X = 0.0f;
                if (clipMin.Y < 0.0f) clipMin.Y = 0.0f;
                if (clipMax.X > fbWidth) clipMax.X = fbWidth;
                if (clipMax.Y > fbHeight) clipMax.Y = fbHeight;
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
                    continue;

                var scissor = new VkRect2D
                {
                    Offset = new VkOffset2D((int)clipMin.X, (int)clipMin.Y),
                    Extent = new VkExtent2D((int)(clipMax.X - clipMin.X), (int)(clipMax.Y - clipMin.Y))
                };
                cmd.SetScissor(0, [scissor]);

                cmd.DrawIndexed(
                    (int)pcmd.ElemCount,
                    1,
                    (int)(pcmd.IdxOffset + (uint)globalIdxOffset),
                    (int)(pcmd.VtxOffset + (uint)globalVtxOffset),
                    0
                );
            }

            globalIdxOffset += cmdList.IdxBuffer.Count;
            globalVtxOffset += cmdList.VtxBuffer.Count;
        }
    }

    private unsafe void EnsureBufferCapacity(int vertexCount, int indexCount)
    {
        if (_vertexBuffer == null || _vertexCount < vertexCount)
        {
            _vertexBuffer?.Dispose();
            _vertexCount = vertexCount + 5000;
            _vertexBuffer = _allocator.CreateBuffer(
                (ulong)(_vertexCount * sizeof(ImDrawVert)),
                VkBufferUsageFlags.VertexBufferBit,
                VmaMemoryUsage.CpuToGpu,
                VmaAllocationCreateFlags.MappedBit
            );
        }

        if (_indexBuffer == null || _indexCount < indexCount)
        {
            _indexBuffer?.Dispose();
            _indexCount = indexCount + 10000;
            _indexBuffer = _allocator.CreateBuffer(
                (ulong)(_indexCount * sizeof(ushort)),
                VkBufferUsageFlags.IndexBufferBit,
                VmaMemoryUsage.CpuToGpu,
                VmaAllocationCreateFlags.MappedBit
            );
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _context.WaitIdle();

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _pipeline.Dispose();

        if (_pipelineLayout.VkHandle != 0)
            _context.Device.DestroyPipelineLayout(_pipelineLayout, null);

        _descriptorAllocator.Dispose();
        _descriptorLayout.Dispose();
        _fontSampler.Dispose();
        _fontTexture.Dispose();
        _glfwBackend.Dispose();
    }
}
