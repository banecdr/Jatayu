/* =================================================================================================
 * FILE: FoliageSystem.cs
 * LOCATION: src/Renderer/Vegetation/FoliageSystem.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages GPU-instanced foliage rendering (grass, shrubs, trees) within Ring A (d <= 300m).
 *   Maintains instance storage buffers, wind physics parameters, procedural blade geometry,
 *   and records instanced draw calls with zero managed allocations per frame.
 * ================================================================================================= */

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.AssetFormats.World;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;

namespace Jatayu.Renderer.Vegetation;

/// <summary>
/// Push constants submitted to the foliage rendering pipeline (96 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct FoliagePushConstants
{
    public Matrix4x4 ViewProj;
    public Vector4 WindParams;         // X=WindDir.X, Y=WindDir.Y, Z=Speed, W=Strength
    public Vector4 CameraRelOrigin;    // Camera-relative origin of the cell chunk
    public float Time;
    public float MaxDistance;
    public float FadeRange;
    public float Padding;
}

/// <summary>
/// High-density vegetation instancing system with procedural wind oscillation and distance dither.
/// </summary>
public sealed class FoliageSystem : IDisposable
{
    public const int MaxFoliageInstances = 131072; // 128k instances = 2 MB VRAM
    public const float DefaultMaxDistance = 300.0f;
    public const float DefaultFadeRange = 50.0f;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private readonly GpuBuffer _instanceBuffer;
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;
    private readonly uint _indexCount;

    private readonly DescriptorLayout _descriptorLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _descriptorSet;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VulkanPipeline _pipeline;

    private uint _activeInstanceCount;
    private Vector2 _windDirection = Vector2.Normalize(new Vector2(0.8f, 0.6f));
    private float _windSpeed = 2.5f;
    private float _windStrength = 0.35f;
    private bool _disposed;

    public uint ActiveInstanceCount => _activeInstanceCount;
    public Vector2 WindDirection { get => _windDirection; set => _windDirection = Vector2.Normalize(value); }
    public float WindSpeed { get => _windSpeed; set => _windSpeed = value; }
    public float WindStrength { get => _windStrength; set => _windStrength = value; }

    public unsafe FoliageSystem(
        VulkanContext context,
        GpuAllocator allocator,
        ShaderCompiler shaderCompiler,
        VkFormat colorFormat)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);

        var device = _context.Device;

        // 1. Allocate Instance Storage Buffer
        ulong instBufferSize = (ulong)(Marshal.SizeOf<FoliageInstanceData>() * MaxFoliageInstances);
        _instanceBuffer = allocator.CreateBuffer(
            instBufferSize,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );

        // 2. Generate procedural 3-blade crossed star grass clump mesh (15 vertices, 9 triangles)
        // Positions (x, y, z), Normals (nx, ny, nz), UVs (u, v), Color/Flex (flex, 0, 0, 1)
        float[] bladeVertices =
        [
            // Blade 0: Angle 0 deg (along X, facing +Z)
            -0.025f, 0.00f, 0.00000f,    0.0f, 0.0f, 1.0f,   0.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
             0.025f, 0.00f, 0.00000f,    0.0f, 0.0f, 1.0f,   1.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
            -0.016f, 0.18f, 0.00000f,    0.0f, 0.0f, 1.0f,   0.15f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
             0.016f, 0.18f, 0.00000f,    0.0f, 0.0f, 1.0f,   0.85f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
             0.000f, 0.35f, 0.00000f,    0.0f, 0.0f, 1.0f,   0.50f, 1.0f,  1.0f, 0.0f, 0.0f, 1.0f,

            // Blade 1: Angle 60 deg
            -0.0125f, 0.00f, -0.02165f,  -0.866f, 0.0f, 0.5f,   0.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
             0.0125f, 0.00f,  0.02165f,  -0.866f, 0.0f, 0.5f,   1.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
            -0.0080f, 0.18f, -0.01386f,  -0.866f, 0.0f, 0.5f,   0.15f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
             0.0080f, 0.18f,  0.01386f,  -0.866f, 0.0f, 0.5f,   0.85f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
             0.0000f, 0.35f,  0.00000f,  -0.866f, 0.0f, 0.5f,   0.50f, 1.0f,  1.0f, 0.0f, 0.0f, 1.0f,

            // Blade 2: Angle 120 deg
             0.0125f, 0.00f, -0.02165f,  -0.866f, 0.0f, -0.5f,  0.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
            -0.0125f, 0.00f,  0.02165f,  -0.866f, 0.0f, -0.5f,  1.00f, 0.0f,  0.0f, 0.0f, 0.0f, 1.0f,
             0.0080f, 0.18f, -0.01386f,  -0.866f, 0.0f, -0.5f,  0.15f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
            -0.0080f, 0.18f,  0.01386f,  -0.866f, 0.0f, -0.5f,  0.85f, 0.5f,  0.5f, 0.0f, 0.0f, 1.0f,
             0.0000f, 0.35f,  0.00000f,  -0.866f, 0.0f, -0.5f,  0.50f, 1.0f,  1.0f, 0.0f, 0.0f, 1.0f,
        ];

        ushort[] bladeIndices =
        [
            0, 1, 2,  2, 1, 3,  2, 3, 4,
            5, 6, 7,  7, 6, 8,  7, 8, 9,
            10, 11, 12,  12, 11, 13,  12, 13, 14
        ];
        _indexCount = (uint)bladeIndices.Length;

        _vertexBuffer = allocator.CreateBuffer(
            (ulong)(sizeof(float) * bladeVertices.Length),
            VkBufferUsageFlags.VertexBufferBit,
            VmaMemoryUsage.CpuToGpu
        );
        _vertexBuffer.SetData<float>(bladeVertices);

        _indexBuffer = allocator.CreateBuffer(
            (ulong)(sizeof(ushort) * bladeIndices.Length),
            VkBufferUsageFlags.IndexBufferBit,
            VmaMemoryUsage.CpuToGpu
        );
        _indexBuffer.SetData<ushort>(bladeIndices);

        // 3. Build Descriptor Layout & Allocator
        _descriptorLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageBuffer, VkShaderStageFlags.VertexBit)
            .Build(_context);

        _descriptorAllocator = new DescriptorAllocator(_context, 2);

        _descriptorSet = _descriptorAllocator.Allocate(_descriptorLayout);
        _descriptorAllocator.WriteBuffer(_descriptorSet, 0, _instanceBuffer.Handle, 0, _instanceBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);

        // 4. Build Pipeline Layout with Push Constants
        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<FoliagePushConstants>()),
        };

        var setLayout = _descriptorLayout.Handle;
        var pipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PushConstantRanges = &pushRange,
        };
        _pipelineLayout = device.CreatePipelineLayout(in pipeLayoutInfo, null);

        // 5. Build Graphics Pipeline (Two-sided, Reverse-Z depth test, Alpha-to-coverage/Dither)
        string vertPath = ResolveShader("assets/shaders/foliage.vert");
        string fragPath = ResolveShader("assets/shaders/foliage.frag");

        var vertModule = shaderCompiler.CompileShaderModuleFromFile(device, vertPath, Brutal.ShaderCApi.ShaderKind.VertexShader);
        var fragModule = shaderCompiler.CompileShaderModuleFromFile(device, fragPath, Brutal.ShaderCApi.ShaderKind.FragmentShader);

        using (var stages = new ShaderStages(device, [Presets.ShaderStage.Vertex(vertModule), Presets.ShaderStage.Fragment(fragModule)], null))
        {
            // Vertex input: pos (vec3), norm (vec3), uv (vec2), color (vec4) = 48 bytes stride
            uint stride = 48;
            var vertexInput = new VertexInput(1, 4);
            vertexInput.AddBinding(0, new ByteSize(stride), VkVertexInputRate.Vertex);
            vertexInput.AddAttribute(0, 0, VkFormat.R32G32B32SFloat, new ByteSize(0));
            vertexInput.AddAttribute(1, 0, VkFormat.R32G32B32SFloat, new ByteSize(12));
            vertexInput.AddAttribute(2, 0, VkFormat.R32G32SFloat, new ByteSize(24));
            vertexInput.AddAttribute(3, 0, VkFormat.R32G32B32A32SFloat, new ByteSize(32));

            _pipeline = PipelineBuilder.Create()
                .SetShaderStages(stages)
                .SetVertexInput(vertexInput)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: true, depthWriteEnable: true, compareOp: DepthConvention.DepthCompareOp)
                .SetRasterization(Presets.Rasterization.Fill.CullNone) // Two-sided grass blades
                .SetColorBlend(Presets.BlendState.BlendNone)
                .SetPipelineLayout(_pipelineLayout)
                .Build(_context);
        }
    }

    /// <summary>
    /// Uploads foliage instance data to GPU storage buffer.
    /// </summary>
    public void SetInstances(ReadOnlySpan<FoliageInstanceData> instances)
    {
        _activeInstanceCount = (uint)Math.Min(instances.Length, MaxFoliageInstances);
        if (_activeInstanceCount > 0)
        {
            _instanceBuffer.SetData(instances[..(int)_activeInstanceCount]);
        }
    }

    /// <summary>
    /// Records instanced foliage rendering commands.
    /// </summary>
    public unsafe void Render(
        CommandBuffer cmd,
        in Matrix4x4 viewProj,
        in Vector3 cameraRelOrigin,
        float time,
        float maxDistance = DefaultMaxDistance,
        float fadeRange = DefaultFadeRange)
    {
        if (_activeInstanceCount == 0) return;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline.Handle);

        Span<VkDescriptorSet> sets = [_descriptorSet];
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pipelineLayout, 0, sets, Span<ByteSize32>.Empty);

        Span<VkBuffer> vertexBuffers = [_vertexBuffer.Handle];
        Span<ByteSize64> vertexOffsets = [new ByteSize64(0)];
        cmd.BindVertexBuffers(0, vertexBuffers, vertexOffsets);
        cmd.BindIndexBuffer(_indexBuffer.Handle, new ByteSize64(0), VkIndexType.UInt16);

        var pc = new FoliagePushConstants
        {
            ViewProj = viewProj,
            WindParams = new Vector4(_windDirection.X, _windDirection.Y, _windSpeed, _windStrength),
            CameraRelOrigin = new Vector4(cameraRelOrigin.X, cameraRelOrigin.Y, cameraRelOrigin.Z, 0f),
            Time = time,
            MaxDistance = maxDistance,
            FadeRange = fadeRange,
            Padding = 0f,
        };

        CommandBufferExtensions.PushConstants(
            cmd,
            _pipelineLayout,
            VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            new ByteSize(0),
            new ReadOnlySpan<FoliagePushConstants>(&pc, 1)
        );

        cmd.DrawIndexed((int)_indexCount, (int)_activeInstanceCount, 0, 0, 0);
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

        _pipeline?.Dispose();
        if (_pipelineLayout.VkHandle != 0) device.DestroyPipelineLayout(_pipelineLayout, null);
        _descriptorAllocator?.Dispose();
        _descriptorLayout?.Dispose();

        _instanceBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();

        _disposed = true;
    }
}
