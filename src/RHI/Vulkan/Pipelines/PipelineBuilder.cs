/* =================================================================================================
 * FILE: PipelineBuilder.cs
 * LOCATION: src/RHI/Vulkan/Pipelines/PipelineBuilder.cs
 * 
 * WHAT THIS FILE DOES:
 *   Provides a fluent, zero-boilerplate builder for creating Vulkan 1.3 graphics pipelines
 *   with Dynamic Rendering, modern state presets (rasterization, blending, depth-stencil),
 *   viewport configuration, and dynamic states.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Configures shader stages (Vertex, Fragment), vertex layout, input assembly, and rasterization.
 *   2. Attaches dynamic rendering color and depth attachment formats (VkPipelineRenderingCreateInfo).
 *   3. Creates the graphics pipeline via Device.CreateGraphicsPipeline().
 *   4. Returns an object-oriented VulkanPipeline instance.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.Memory;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Pipelines;

/// <summary>
/// Fluent builder for constructing Vulkan graphics pipelines with Dynamic Rendering.
/// </summary>
public sealed class PipelineBuilder
{
    private ShaderStages? _shaderStages;
    private VertexInput? _vertexInput;
    private PtrOwner<VkPipelineInputAssemblyStateCreateInfo> _inputAssembly = Presets.InputAssembly.TriangleList;
    private PtrOwner<VkPipelineRasterizationStateCreateInfo> _rasterization = Presets.Rasterization.Fill.CullBack;
    private PtrOwner<VkPipelineMultisampleStateCreateInfo> _multisample = Presets.Multisample.None;
    private PtrOwner<VkPipelineDepthStencilStateCreateInfo> _depthStencil = Presets.DepthStencil.DepthTestWrite;
    private VkPipelineDepthStencilStateCreateInfo _customDepthStencil = new()
    {
        DepthTestEnable = true,
        DepthWriteEnable = true,
        DepthCompareOp = VkCompareOp.GreaterOrEqual,
        DepthBoundsTestEnable = false,
        StencilTestEnable = false,
        MinDepthBounds = 0.0f,
        MaxDepthBounds = 1.0f,
    };
    private bool _useCustomDepthStencil = true;
    private PtrOwner<VkPipelineColorBlendStateCreateInfo> _colorBlend = Presets.BlendState.BlendNone;
    private VkPipelineLayout _layout;
    private VkFormat _colorFormat;
    private VkFormat[]? _colorFormats;
    private VkPipelineColorBlendAttachmentState[]? _colorBlendAttachments;
    private VkFormat _depthFormat = VkFormat.D32SFloat;
    private readonly List<VkDynamicState> _dynamicStates = [VkDynamicState.Viewport, VkDynamicState.Scissor];

    /// <summary>
    /// Creates a new PipelineBuilder.
    /// </summary>
    public static PipelineBuilder Create() => new();

    /// <summary>
    /// Sets the compiled shader stages.
    /// </summary>
    public PipelineBuilder SetShaderStages(ShaderStages stages)
    {
        _shaderStages = stages ?? throw new ArgumentNullException(nameof(stages));
        return this;
    }

    /// <summary>
    /// Sets the vertex input layout description.
    /// </summary>
    public PipelineBuilder SetVertexInput(VertexInput vertexInput)
    {
        _vertexInput = vertexInput;
        return this;
    }

    /// <summary>
    /// Sets the input assembly state.
    /// </summary>
    public PipelineBuilder SetInputAssembly(PtrOwner<VkPipelineInputAssemblyStateCreateInfo> inputAssembly)
    {
        _inputAssembly = inputAssembly;
        return this;
    }

    /// <summary>
    /// Sets the rasterization state.
    /// </summary>
    public PipelineBuilder SetRasterization(PtrOwner<VkPipelineRasterizationStateCreateInfo> rasterization)
    {
        _rasterization = rasterization;
        return this;
    }

    /// <summary>
    /// Sets the multisample state.
    /// </summary>
    public PipelineBuilder SetMultisample(PtrOwner<VkPipelineMultisampleStateCreateInfo> multisample)
    {
        _multisample = multisample;
        return this;
    }

    /// <summary>
    /// Sets the depth-stencil testing state using a Brutal preset.
    /// </summary>
    public PipelineBuilder SetDepthStencil(PtrOwner<VkPipelineDepthStencilStateCreateInfo> depthStencil)
    {
        _depthStencil = depthStencil;
        _useCustomDepthStencil = false;
        return this;
    }

    /// <summary>
    /// Configures depth testing and writing directly.
    /// Defaults to canonical Reverse-Z (GreaterOrEqual).
    /// </summary>
    public PipelineBuilder SetDepthTest(
        bool depthTestEnable = true,
        bool depthWriteEnable = true,
        VkCompareOp compareOp = VkCompareOp.GreaterOrEqual)
    {
        _customDepthStencil = new VkPipelineDepthStencilStateCreateInfo
        {
            DepthTestEnable = depthTestEnable,
            DepthWriteEnable = depthWriteEnable,
            DepthCompareOp = compareOp,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false,
            MinDepthBounds = 0.0f,
            MaxDepthBounds = 1.0f,
        };
        _useCustomDepthStencil = true;
        return this;
    }

    /// <summary>
    /// Sets the color blending state.
    /// </summary>
    public PipelineBuilder SetColorBlend(PtrOwner<VkPipelineColorBlendStateCreateInfo> colorBlend)
    {
        _colorBlend = colorBlend;
        return this;
    }

    /// <summary>
    /// Sets the dynamic rendering color attachment format.
    /// </summary>
    public PipelineBuilder SetColorAttachmentFormat(VkFormat format)
    {
        _colorFormat = format;
        _colorFormats = null;
        return this;
    }

    /// <summary>
    /// Sets the dynamic-rendering formats for a pass with multiple color attachments.
    /// </summary>
    public PipelineBuilder SetColorAttachmentFormats(params VkFormat[] formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Length == 0)
            throw new ArgumentException("At least one color attachment format is required.", nameof(formats));
        _colorFormats = (VkFormat[])formats.Clone();
        _colorFormat = formats[0];
        return this;
    }

    /// <summary>
    /// Sets independent blend states for each dynamic-rendering color attachment.
    /// </summary>
    public PipelineBuilder SetColorBlendAttachments(params VkPipelineColorBlendAttachmentState[] attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Length == 0)
            throw new ArgumentException("At least one color blend attachment is required.", nameof(attachments));
        _colorBlendAttachments = (VkPipelineColorBlendAttachmentState[])attachments.Clone();
        return this;
    }

    /// <summary>
    /// Sets the dynamic rendering depth attachment format.
    /// </summary>
    public PipelineBuilder SetDepthAttachmentFormat(VkFormat format)
    {
        _depthFormat = format;
        return this;
    }

    /// <summary>
    /// Sets the pipeline layout handle.
    /// </summary>
    public PipelineBuilder SetPipelineLayout(VkPipelineLayout layout)
    {
        _layout = layout;
        return this;
    }

    /// <summary>
    /// Sets dynamic states to be enabled for this pipeline.
    /// </summary>
    public PipelineBuilder SetDynamicStates(params VkDynamicState[] states)
    {
        _dynamicStates.Clear();
        _dynamicStates.AddRange(states);
        return this;
    }

    /// <summary>
    /// Builds and creates the native Vulkan graphics pipeline.
    /// </summary>
    public unsafe VulkanPipeline Build(VulkanContext context, VkPipelineCache cache = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_shaderStages == null)
            throw new InvalidOperationException("ShaderStages must be specified on PipelineBuilder.");

        if (_layout.VkHandle == 0)
            throw new InvalidOperationException("PipelineLayout must be set before building the pipeline.");

        var device = context.Device;

        // Dynamic rendering color and depth formats
        var colorFormats = _colorFormats ?? [_colorFormat];
        if (_colorBlendAttachments != null && _colorBlendAttachments.Length != colorFormats.Length)
            throw new InvalidOperationException("The number of color blend attachments must match the dynamic-rendering color formats.");

        // Dummy viewport/scissor (overridden dynamically via vkCmdSetViewport / vkCmdSetScissor)
        var viewport = new VkViewport { Width = 1, Height = 1, MaxDepth = 1 };
        var scissor = new VkRect2D(new VkExtent2D(1, 1));
        var viewportState = new VkPipelineViewportStateCreateInfo
        {
            ViewportCount = 1,
            Viewports = &viewport,
            ScissorCount = 1,
            Scissors = &scissor,
        };

        var dynamicStatesArray = _dynamicStates.ToArray();
        fixed (VkDynamicState* pDynamicStates = dynamicStatesArray)
        fixed (VkFormat* pColorFormats = colorFormats)
        fixed (VkPipelineColorBlendAttachmentState* pBlendAttachments = _colorBlendAttachments)
        {
            var renderingInfo = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = colorFormats.Length,
                ColorAttachmentFormats = pColorFormats,
                DepthAttachmentFormat = _depthFormat,
            };
            var customBlendInfo = new VkPipelineColorBlendStateCreateInfo
            {
                LogicOpEnable = false,
                LogicOp = VkLogicOp.Copy,
                AttachmentCount = _colorBlendAttachments?.Length ?? 0,
                Attachments = pBlendAttachments,
            };
            VkPipelineColorBlendStateCreateInfo* colorBlendState = _colorBlendAttachments == null
                ? (VkPipelineColorBlendStateCreateInfo*)_colorBlend
                : &customBlendInfo;

            var dynamicStateInfo = new VkPipelineDynamicStateCreateInfo
            {
                DynamicStateCount = dynamicStatesArray.Length,
                DynamicStates = pDynamicStates,
            };

            var vertexInputState = _vertexInput ?? new VertexInput(0, 0);

            fixed (VkPipelineDepthStencilStateCreateInfo* pCustomDepth = &_customDepthStencil)
            {
                var depthState = _useCustomDepthStencil ? pCustomDepth : (VkPipelineDepthStencilStateCreateInfo*)_depthStencil;

                var createInfo = new VkGraphicsPipelineCreateInfo
                {
                    Next = &renderingInfo,
                    StageCount = _shaderStages.Count,
                    Stages = _shaderStages,
                    VertexInputState = vertexInputState,
                    InputAssemblyState = _inputAssembly,
                    ViewportState = &viewportState,
                    RasterizationState = _rasterization,
                    MultisampleState = _multisample,
                    DepthStencilState = depthState,
                    ColorBlendState = colorBlendState,
                    DynamicState = &dynamicStateInfo,
                    Layout = _layout,
                };

                var pipeline = device.CreateGraphicsPipeline(cache, createInfo, null);
                return new VulkanPipeline(context, pipeline, _layout, VkPipelineBindPoint.Graphics);
            }
        }
    }

    /// <summary>
    /// Builds and creates a native Vulkan compute pipeline.
    /// </summary>
    public static unsafe VulkanPipeline CreateComputePipeline(
        VulkanContext context,
        VkPipelineLayout layout,
        VkShaderModule computeShaderModule,
        string entryPoint = "main",
        VkPipelineCache cache = default,
        bool ownsLayout = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (layout.VkHandle == 0)
            throw new ArgumentException("PipelineLayout must not be null.", nameof(layout));
        if (computeShaderModule.VkHandle == 0)
            throw new ArgumentException("ComputeShaderModule must not be null.", nameof(computeShaderModule));

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(entryPoint + "\0");
        fixed (byte* pName = nameBytes)
        {
            var stageInfo = new VkPipelineShaderStageCreateInfo
            {
                Stage = VkShaderStageFlags.ComputeBit,
                Module = computeShaderModule,
                Name = (byte*)pName,
            };

            var createInfo = new VkComputePipelineCreateInfo
            {
                Stage = stageInfo,
                Layout = layout,
            };

            VkPipeline pipeline;
            var res = context.Device.PInvoke.vkCreateComputePipelines.Invoke(
                context.Device.Handle,
                cache,
                1,
                &createInfo,
                null,
                &pipeline
            );

            if (res != VkResult.Success)
                throw new InvalidOperationException($"Failed to create compute pipeline: {res}");

            return new VulkanPipeline(context, pipeline, layout, VkPipelineBindPoint.Compute, ownsLayout);
        }
    }
}
