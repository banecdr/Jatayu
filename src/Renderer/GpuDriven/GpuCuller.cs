// ==================================================================================================
// Jatayu Engine - GPU-Driven Architecture
// Module: GpuCuller.cs
// Role: Multi-view GPU compute culling dispatcher: 5-plane frustum, LOD selection, and Hi-Z occlusion (Sprint 7a).
// ==================================================================================================

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

namespace Jatayu.Renderer.GpuDriven;

/// <summary>
/// Uniform parameters passed to cull.comp per view (192 bytes, 16-byte aligned).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 192)]
public struct CullUniforms
{
    public Vector4 FrustumPlane0; // Left
    public Vector4 FrustumPlane1; // Right
    public Vector4 FrustumPlane2; // Bottom
    public Vector4 FrustumPlane3; // Top
    public Vector4 FrustumPlane4; // Near
    public Vector4 CameraPosAndMaxDist; // xyz = CamPos, w = MaxDrawDistance
    public Matrix4x4 ViewProj;          // Camera-relative ViewProj
    public Vector2 HiZScreenSize;       // Width, Height (0 if occlusion disabled)
    public Vector2 Padding;
}

/// <summary>
/// Push constants passed to cull.comp (20 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 20)]
public struct CullPushConstants
{
    public uint TotalInstanceCount;
    public uint ViewIndex;
    public uint MaxDrawsPerView;
    public uint BaseCommandOffset;
    public uint ShadowView;
}

/// <summary>
/// GPU compute culler dispatching 5-view frustum, LOD, and Hi-Z occlusion culling with zero CPU draw loop overhead.
/// </summary>
public sealed class GpuCuller : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private readonly DescriptorLayout _descriptorLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet[] _viewDescriptorSets;

    private readonly GpuBuffer _uniformBuffer;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VulkanPipeline _pipeline;

    private bool _disposed;

    /// <summary>
    /// Initializes the GPU culler compute pipeline and descriptors.
    /// </summary>
    public unsafe GpuCuller(
        VulkanContext context,
        GpuAllocator allocator,
        ShaderCompiler shaderCompiler,
        int maxViews = IndirectDrawBuffer.DefaultViewCount)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);

        // 1. Create uniform buffer for all view parameters
        ulong uniformBufferSize = (ulong)(Marshal.SizeOf<CullUniforms>() * maxViews);
        _uniformBuffer = _allocator.CreateBuffer(
            uniformBufferSize,
            VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );

        // 2. Build Descriptor Layout
        _descriptorLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)         // InstanceBuffer
            .AddBinding(1, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)         // IndirectDrawBuffer
            .AddBinding(2, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)         // DrawCountBuffer
            .AddBinding(3, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)         // MeshLodBuffer
            .AddBinding(4, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit) // HiZPyramid
            .AddBinding(5, VkDescriptorType.UniformBuffer, VkShaderStageFlags.ComputeBit)        // CullUniforms
            .Build(_context);

        // 3. Allocate per-view descriptor sets
        _descriptorAllocator = new DescriptorAllocator(_context, maxSetsPerPool: maxViews + 2);
        _viewDescriptorSets = new VkDescriptorSet[maxViews];
        for (int i = 0; i < maxViews; i++)
        {
            _viewDescriptorSets[i] = _descriptorAllocator.Allocate(_descriptorLayout);
        }

        // 4. Build Pipeline Layout with Push Constants
        var pushRange = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.ComputeBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<CullPushConstants>()),
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

        // 5. Compile compute shader & create compute pipeline
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "assets", "shaders", "cull.comp");
        if (!File.Exists(shaderPath))
        {
            shaderPath = Path.Combine(Directory.GetCurrentDirectory(), "assets", "shaders", "cull.comp");
        }

        var computeShader = shaderCompiler.CompileShaderModuleFromFile(_context.Device, shaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);
        _pipeline = PipelineBuilder.CreateComputePipeline(_context, _pipelineLayout, computeShader);
    }

    /// <summary>
    /// Dispatches compute culling for the specified view, testing frustum, LOD selection, and Hi-Z occlusion.
    /// </summary>
    public unsafe void CullView(
        VkCommandBuffer cmd,
        int viewIndex,
        in Frustum frustum,
        Vector3 cameraPosition,
        float maxDrawDistance,
        Matrix4x4 viewProj,
        InstanceBuffer instanceBuffer,
        IndirectDrawBuffer indirectBuffer,
        GeometryPool geometryPool,
        HiZBuilder? hizBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(instanceBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        ArgumentNullException.ThrowIfNull(geometryPool);

        if (instanceBuffer.TotalCount == 0)
            return;

        if (viewIndex < 0 || viewIndex >= _viewDescriptorSets.Length)
            throw new ArgumentOutOfRangeException(nameof(viewIndex));

        // 1. Pack CullUniforms
        var pLeft = frustum.GetPlane(Frustum.PlaneIndex.Left);
        var pRight = frustum.GetPlane(Frustum.PlaneIndex.Right);
        var pBottom = frustum.GetPlane(Frustum.PlaneIndex.Bottom);
        var pTop = frustum.GetPlane(Frustum.PlaneIndex.Top);
        var pNear = frustum.GetPlane(Frustum.PlaneIndex.Near);

        var uniforms = new CullUniforms
        {
            FrustumPlane0 = new Vector4(pLeft.Normal, pLeft.Distance),
            FrustumPlane1 = new Vector4(pRight.Normal, pRight.Distance),
            FrustumPlane2 = new Vector4(pBottom.Normal, pBottom.Distance),
            FrustumPlane3 = new Vector4(pTop.Normal, pTop.Distance),
            FrustumPlane4 = new Vector4(pNear.Normal, pNear.Distance),
            CameraPosAndMaxDist = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, maxDrawDistance),
            ViewProj = viewProj,
            HiZScreenSize = hizBuilder != null ? new Vector2(hizBuilder.Width, hizBuilder.Height) : Vector2.Zero,
            Padding = Vector2.Zero,
        };

        ulong uniformOffset = (ulong)(viewIndex * Marshal.SizeOf<CullUniforms>());
        Span<CullUniforms> uniformSpan = stackalloc CullUniforms[1] { uniforms };
        _uniformBuffer.SetData<CullUniforms>(uniformSpan, offsetInBytes: uniformOffset);

        // 2. Bind Descriptors for this view
        var set = _viewDescriptorSets[viewIndex];
        _descriptorAllocator.WriteBuffer(set, 0, instanceBuffer.Buffer.Handle, descriptorType: VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(set, 1, indirectBuffer.CommandBuffer.Handle, descriptorType: VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(set, 2, indirectBuffer.CountBuffer.Handle, descriptorType: VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(set, 3, geometryPool.MeshLodBuffer.Handle, descriptorType: VkDescriptorType.StorageBuffer);

        if (hizBuilder != null)
        {
            _descriptorAllocator.WriteImage(
                set,
                4,
                hizBuilder.Sampler.Handle,
                hizBuilder.FullPyramidView,
                VkImageLayout.ShaderReadOnlyOptimal
            );
        }

        _descriptorAllocator.WriteBuffer(
            set,
            5,
            _uniformBuffer.Handle,
            offset: uniformOffset,
            range: (ulong)Marshal.SizeOf<CullUniforms>(),
            descriptorType: VkDescriptorType.UniformBuffer
        );

        // 3. Bind Compute Pipeline & Descriptor Set
        _context.Device.PInvoke.vkCmdBindPipeline.Invoke(
            cmd,
            VkPipelineBindPoint.Compute,
            _pipeline.Handle
        );

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

        // 4. Push Constants
        uint baseCmdOffset = (uint)(viewIndex * indirectBuffer.MaxDrawsPerView);
        var push = new CullPushConstants
        {
            TotalInstanceCount = (uint)instanceBuffer.TotalCount,
            ViewIndex = (uint)viewIndex,
            MaxDrawsPerView = (uint)indirectBuffer.MaxDrawsPerView,
            BaseCommandOffset = baseCmdOffset,
            ShadowView = viewIndex == 0 ? 0u : 1u,
        };

        _context.Device.PInvoke.vkCmdPushConstants.Invoke(
            cmd,
            _pipelineLayout,
            VkShaderStageFlags.ComputeBit,
            new ByteSize32(0),
            (ByteSize32)sizeof(CullPushConstants),
            &push
        );

        // 5. Dispatch Compute (64 instances per workgroup)
        uint groupsX = ((uint)instanceBuffer.TotalCount + 63) / 64;
        _context.Device.PInvoke.vkCmdDispatch.Invoke(cmd, (int)groupsX, 1, 1);
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            _pipeline.Dispose();
            _descriptorLayout.Dispose();
            _descriptorAllocator.Dispose();
            _uniformBuffer.Dispose();
            _disposed = true;
        }
    }
}
