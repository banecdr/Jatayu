/* =================================================================================================
 * FILE: ParticleSystem.cs
 * LOCATION: src/Renderer/Particles/ParticleSystem.cs
 * 
 * WHAT THIS FILE DOES:
 *   GPU-simulated particle emitter and renderer.
 *   Simulates physics (integration, velocity, lifetime, gravity) via compute shader
 *   and renders camera-facing spherical billboard quads with Reverse-Z depth testing.
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;

namespace Jatayu.Renderer.Particles;

/// <summary>
/// Packed 64-byte particle structure (four 16-byte aligned vec4 blocks in std430).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct GpuParticle
{
    public Vector3 Position;
    public float Age;
    public Vector3 Velocity;
    public float Lifetime;
    public Vector4 Color;
    public float Size;
    public float Padding0;
    public float Padding1;
    public float Padding2;
}

/// <summary>
/// Push constants for particle compute simulation pass (48 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
public struct ParticleComputePushConstants
{
    public Vector4 EmitterPosition;
    public Vector4 GravityAndDrag;
    public float DeltaTime;
    public float TotalTime;
    public uint MaxParticles;
    public float DefaultLifetime;
}

/// <summary>
/// Push constants for particle graphics billboard rendering pass (144 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 144)]
public struct ParticleRenderPushConstants
{
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public Vector4 ScreenParams;
}

/// <summary>
/// GPU-driven particle system with compute physics and billboard rasterization.
/// </summary>
public sealed class ParticleSystem : IDisposable
{
    public const int DefaultMaxParticles = 16384;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly uint _maxParticles;

    private readonly GpuBuffer _particleBuffer;
    private readonly GpuBuffer _quadVertexBuffer;

    private readonly DescriptorLayout _computeDescLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _computeDescSet;
    private readonly VkPipelineLayout _computePipelineLayout;
    private readonly VulkanPipeline _computePipeline;

    private readonly VkPipelineLayout _renderPipelineLayout;
    private readonly VulkanPipeline _renderPipeline;

    private Vector3 _emitterPosition = new Vector3(0f, 1f, 0f);
    private Vector3 _gravity = new Vector3(0f, -4.9f, 0f);
    private float _drag = 0.05f;
    private float _defaultLifetime = 2.5f;
    private bool _disposed;

    public Vector3 EmitterPosition { get => _emitterPosition; set => _emitterPosition = value; }
    public uint MaxParticles => _maxParticles;

    public unsafe ParticleSystem(
        VulkanContext context,
        GpuAllocator allocator,
        ShaderCompiler shaderCompiler,
        VkFormat colorFormat,
        uint maxParticles = DefaultMaxParticles)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);
        _maxParticles = Math.Max(64, maxParticles);

        var device = _context.Device;

        // 1. Allocate GPU Particle Storage Buffer
        ulong bufferSize = (ulong)(Marshal.SizeOf<GpuParticle>() * _maxParticles);
        _particleBuffer = allocator.CreateBuffer(
            bufferSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );

        // Clear initial particle ages so they spawn on first tick
        Span<byte> zeros = stackalloc byte[(int)Math.Min(bufferSize, 1024)];
        zeros.Clear();

        // 2. Unit quad vertices (6 vertices = 2 triangles)
        float[] quadVertices =
        [
            -1.0f, -1.0f,
             1.0f, -1.0f,
             1.0f,  1.0f,
             1.0f,  1.0f,
            -1.0f,  1.0f,
            -1.0f, -1.0f,
        ];
        _quadVertexBuffer = allocator.CreateBuffer(
            (ulong)(sizeof(float) * quadVertices.Length),
            VkBufferUsageFlags.VertexBufferBit,
            VmaMemoryUsage.CpuToGpu
        );
        _quadVertexBuffer.SetData<float>(quadVertices);

        // 3. Build Compute Pipeline (particle.comp)
        _computeDescLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit | VkShaderStageFlags.VertexBit)
            .Build(_context);

        _descriptorAllocator = new DescriptorAllocator(_context, 2);

        _computeDescSet = _descriptorAllocator.Allocate(_computeDescLayout);
        _descriptorAllocator.WriteBuffer(_computeDescSet, 0, _particleBuffer.Handle, 0, _particleBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);

        var computePushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.ComputeBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<ParticleComputePushConstants>()),
        };

        var setLayout = _computeDescLayout.Handle;
        var computePipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PushConstantRanges = &computePushRange,
        };
        _computePipelineLayout = device.CreatePipelineLayout(in computePipeLayoutInfo, null);

        string compShaderPath = ResolveShader("assets/shaders/particle.comp");
        var compShader = shaderCompiler.CompileShaderModuleFromFile(device, compShaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);
        _computePipeline = PipelineBuilder.CreateComputePipeline(_context, _computePipelineLayout, compShader);

        // 4. Build Graphics Pipeline (particle.vert + particle.frag)
        var renderPushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<ParticleRenderPushConstants>()),
        };

        var renderPipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PushConstantRanges = &renderPushRange,
        };
        _renderPipelineLayout = device.CreatePipelineLayout(in renderPipeLayoutInfo, null);

        string vertShaderPath = ResolveShader("assets/shaders/particle.vert");
        string fragShaderPath = ResolveShader("assets/shaders/particle.frag");
        var vertShader = shaderCompiler.CompileShaderModuleFromFile(device, vertShaderPath, Brutal.ShaderCApi.ShaderKind.VertexShader);
        var fragShader = shaderCompiler.CompileShaderModuleFromFile(device, fragShaderPath, Brutal.ShaderCApi.ShaderKind.FragmentShader);

        using (var stages = new ShaderStages(device, [Presets.ShaderStage.Vertex(vertShader), Presets.ShaderStage.Fragment(fragShader)], null))
        {
            var vertexInput = new VertexInput(1, 1);
            vertexInput.AddBinding(0, new ByteSize(sizeof(float) * 2), VkVertexInputRate.Vertex);
            vertexInput.AddAttribute(0, 0, VkFormat.R32G32SFloat, new ByteSize(0));

            _renderPipeline = PipelineBuilder.Create()
                .SetShaderStages(stages)
                .SetVertexInput(vertexInput)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: true, depthWriteEnable: false, compareOp: DepthConvention.DepthCompareOp)
                .SetRasterization(Presets.Rasterization.Fill.CullNone)
                .SetColorBlend(Presets.BlendState.BlendColorAlpha)
                .SetPipelineLayout(_renderPipelineLayout)
                .Build(_context);
        }
    }

    /// <summary>
    /// Dispatches the compute shader to update particle physics.
    /// </summary>
    public unsafe void Simulate(CommandBuffer cmd, float deltaTime, float totalTime)
    {
        var device = _context.Device;

        device.PInvoke.vkCmdBindPipeline.Invoke(cmd.Handle, VkPipelineBindPoint.Compute, _computePipeline.Handle);

        var set = _computeDescSet;
        device.PInvoke.vkCmdBindDescriptorSets.Invoke(cmd.Handle, VkPipelineBindPoint.Compute, _computePipelineLayout, 0, 1, &set, 0, null);

        var pc = new ParticleComputePushConstants
        {
            EmitterPosition = new Vector4(_emitterPosition, 100.0f),
            GravityAndDrag = new Vector4(_gravity, _drag),
            DeltaTime = deltaTime,
            TotalTime = totalTime,
            MaxParticles = _maxParticles,
            DefaultLifetime = _defaultLifetime,
        };

        CommandBufferExtensions.PushConstants(
            cmd,
            _computePipelineLayout,
            VkShaderStageFlags.ComputeBit,
            new ByteSize(0),
            new ReadOnlySpan<ParticleComputePushConstants>(&pc, 1)
        );

        device.PInvoke.vkCmdDispatch.Invoke(cmd.Handle, (int)((_maxParticles + 63) / 64), 1, 1);

        // Execution barrier ensuring particle writes are visible to vertex shader
        var barrier = stackalloc VkBufferMemoryBarrier2[1];
        barrier[0] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.VertexShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderStorageReadBit,
            Buffer = _particleBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(_particleBuffer.SizeInBytes),
        };

        var depInfo = new VkDependencyInfo
        {
            BufferMemoryBarrierCount = 1,
            BufferMemoryBarriers = barrier,
        };
        device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd.Handle, &depInfo);
    }

    /// <summary>
    /// Renders the particles into the active render pass.
    /// </summary>
    public unsafe void Render(CommandBuffer cmd, in Matrix4x4 view, in Matrix4x4 proj, float screenWidth, float screenHeight)
    {
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _renderPipeline.Handle);

        Span<VkDescriptorSet> sets = [_computeDescSet];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _renderPipelineLayout, 0, sets, Span<ByteSize32>.Empty);

        Span<VkBuffer> vertexBuffers = [_quadVertexBuffer.Handle];
        Span<ByteSize64> vertexOffsets = [new ByteSize64(0)];
        cmd.BindVertexBuffers(0, vertexBuffers, vertexOffsets);

        var pc = new ParticleRenderPushConstants
        {
            View = view,
            Proj = proj,
            ScreenParams = new Vector4(screenWidth, screenHeight, 0.1f, 0.5f),
        };

        CommandBufferExtensions.PushConstants(
            cmd,
            _renderPipelineLayout,
            VkShaderStageFlags.VertexBit,
            new ByteSize(0),
            new ReadOnlySpan<ParticleRenderPushConstants>(&pc, 1)
        );

        cmd.Draw(6, (int)_maxParticles, 0, 0);
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

        _computePipeline?.Dispose();
        _renderPipeline?.Dispose();
        if (_computePipelineLayout.VkHandle != 0) device.DestroyPipelineLayout(_computePipelineLayout, null);
        if (_renderPipelineLayout.VkHandle != 0) device.DestroyPipelineLayout(_renderPipelineLayout, null);
        _descriptorAllocator?.Dispose();
        _computeDescLayout?.Dispose();

        _particleBuffer?.Dispose();
        _quadVertexBuffer?.Dispose();

        _disposed = true;
    }
}
