/* =================================================================================================
 * FILE: ClusteredLightCulling.cs
 * LOCATION: src/Renderer/Lighting/ClusteredLightCulling.cs
 * 
 * WHAT THIS FILE DOES:
 *   Orchestrates 3D cluster grid partitioning and GPU compute culling for dynamic Point and Spot lights.
 *   Partitions the view frustum into 16 x 8 x 24 (3,072) clusters with logarithmic depth slicing,
 *   enabling O(1) light assignment during forward shading.
 * ================================================================================================= */

using System;
using System.IO;
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

namespace Jatayu.Renderer.Lighting;

/// <summary>
/// Offset and count record for a single cluster.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct ClusterRecord
{
    public uint Offset;
    public uint Count;
}

/// <summary>
/// Packed 176-byte uniform block for clustered light assignment compute shader.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 176)]
public struct ClusterUniforms
{
    public Matrix4x4 InvProj;
    public Matrix4x4 View;
    public Vector4 ScreenSizeAndNearFar; // X=Width, Y=Height, Z=NearZ, W=FarZ
    public Vector4 ClusterGridDim;      // X=16, Y=8, Z=24, W=TotalClusters
    public uint PointLightCount;
    public uint SpotLightCount;
    public uint MaxLightsPerCluster;
    public uint Padding;
}

/// <summary>
/// Manages the 3D clustered light grid allocation and compute dispatch.
/// </summary>
public sealed class ClusteredLightCulling : IDisposable
{
    public const int ClustersX = 16;
    public const int ClustersY = 8;
    public const int ClustersZ = 24;
    public const int TotalClusters = ClustersX * ClustersY * ClustersZ; // 3,072 clusters
    public const int MaxLights = 1024;
    public const int MaxIndices = 65536;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private readonly GpuBuffer _uniformBuffer;
    private readonly GpuBuffer _pointLightBuffer;
    private readonly GpuBuffer _spotLightBuffer;
    private readonly GpuBuffer _clusterRecordBuffer;
    private readonly GpuBuffer _lightIndexBuffer;

    private readonly DescriptorLayout _descriptorLayout;
    private readonly DescriptorAllocator _descriptorAllocator;
    private readonly VkDescriptorSet _descriptorSet;
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VulkanPipeline _pipeline;

    private uint _activePointLightCount;
    private uint _activeSpotLightCount;
    private bool _disposed;

    public GpuBuffer ClusterRecordBuffer => _clusterRecordBuffer;
    public GpuBuffer LightIndexBuffer => _lightIndexBuffer;
    public GpuBuffer PointLightBuffer => _pointLightBuffer;
    public GpuBuffer SpotLightBuffer => _spotLightBuffer;
    public uint ActivePointLightCount => _activePointLightCount;
    public uint ActiveSpotLightCount => _activeSpotLightCount;

    public unsafe ClusteredLightCulling(VulkanContext context, GpuAllocator allocator, ShaderCompiler shaderCompiler)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(shaderCompiler);

        // 1. Allocate GPU buffers
        _uniformBuffer = allocator.CreateBuffer(
            (ulong)Marshal.SizeOf<ClusterUniforms>(),
            VkBufferUsageFlags.UniformBufferBit,
            VmaMemoryUsage.CpuToGpu,
            VmaAllocationCreateFlags.MappedBit
        );

        _pointLightBuffer = allocator.CreateBuffer(
            (ulong)(Marshal.SizeOf<PointLight>() * MaxLights),
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );

        _spotLightBuffer = allocator.CreateBuffer(
            (ulong)(Marshal.SizeOf<SpotLight>() * MaxLights),
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.CpuToGpu
        );

        _clusterRecordBuffer = allocator.CreateBuffer(
            (ulong)(Marshal.SizeOf<ClusterRecord>() * TotalClusters),
            VkBufferUsageFlags.StorageBufferBit,
            VmaMemoryUsage.GpuOnly
        );

        // Index 0 stores the atomic globalIndexCounter, followed by indices
        _lightIndexBuffer = allocator.CreateBuffer(
            (ulong)(sizeof(uint) + (sizeof(uint) * MaxIndices)),
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit,
            VmaMemoryUsage.GpuOnly
        );

        // 2. Build Descriptor Layout
        _descriptorLayout = DescriptorLayout.Create()
            .AddBinding(0, VkDescriptorType.UniformBuffer, VkShaderStageFlags.ComputeBit)
            .AddBinding(1, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
            .AddBinding(2, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
            .AddBinding(3, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
            .AddBinding(4, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit)
            .Build(_context);

        _descriptorAllocator = new DescriptorAllocator(_context, 4);

        _descriptorSet = _descriptorAllocator.Allocate(_descriptorLayout);

        // Bind buffers to descriptor set
        _descriptorAllocator.WriteBuffer(_descriptorSet, 0, _uniformBuffer.Handle, 0, (ulong)Marshal.SizeOf<ClusterUniforms>(), VkDescriptorType.UniformBuffer);
        _descriptorAllocator.WriteBuffer(_descriptorSet, 1, _pointLightBuffer.Handle, 0, _pointLightBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(_descriptorSet, 2, _spotLightBuffer.Handle, 0, _spotLightBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(_descriptorSet, 3, _clusterRecordBuffer.Handle, 0, _clusterRecordBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);
        _descriptorAllocator.WriteBuffer(_descriptorSet, 4, _lightIndexBuffer.Handle, 0, _lightIndexBuffer.SizeInBytes, VkDescriptorType.StorageBuffer);

        // 3. Build Pipeline Layout & Pipeline
        var setLayout = _descriptorLayout.Handle;
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            SetLayoutCount = 1,
            SetLayouts = &setLayout,
        };
        _pipelineLayout = _context.Device.CreatePipelineLayout(in pipelineLayoutInfo, null);

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "assets", "shaders", "light_cull.comp");
        if (!File.Exists(shaderPath))
        {
            shaderPath = Path.Combine(Directory.GetCurrentDirectory(), "assets", "shaders", "light_cull.comp");
        }

        var computeShader = shaderCompiler.CompileShaderModuleFromFile(_context.Device, shaderPath, Brutal.ShaderCApi.ShaderKind.ComputeShader);
        _pipeline = PipelineBuilder.CreateComputePipeline(_context, _pipelineLayout, computeShader);
    }

    /// <summary>
    /// Uploads dynamic point and spot light arrays to GPU storage buffers.
    /// </summary>
    public void UpdateLights(ReadOnlySpan<PointLight> pointLights, ReadOnlySpan<SpotLight> spotLights)
    {
        _activePointLightCount = (uint)Math.Min(pointLights.Length, MaxLights);
        if (_activePointLightCount > 0)
        {
            _pointLightBuffer.SetData(pointLights[..(int)_activePointLightCount]);
        }

        _activeSpotLightCount = (uint)Math.Min(spotLights.Length, MaxLights);
        if (_activeSpotLightCount > 0)
        {
            _spotLightBuffer.SetData(spotLights[..(int)_activeSpotLightCount]);
        }
    }

    /// <summary>
    /// Records the clustered light culling compute dispatch into the command buffer.
    /// </summary>
    public unsafe void Cull(
        VkCommandBuffer cmd,
        in Matrix4x4 view,
        in Matrix4x4 proj,
        float screenWidth,
        float screenHeight,
        float nearZ,
        float farZ)
    {
        Matrix4x4.Invert(proj, out var invProj);

        var uniforms = new ClusterUniforms
        {
            InvProj = invProj,
            View = view,
            ScreenSizeAndNearFar = new Vector4(screenWidth, screenHeight, nearZ, farZ),
            ClusterGridDim = new Vector4(ClustersX, ClustersY, ClustersZ, TotalClusters),
            PointLightCount = _activePointLightCount,
            SpotLightCount = _activeSpotLightCount,
            MaxLightsPerCluster = 64,
            Padding = 0,
        };

        _uniformBuffer.SetData(new ReadOnlySpan<ClusterUniforms>(&uniforms, 1));

        // 1. Reset global index counter to 0 via vkCmdFillBuffer
        _context.Device.PInvoke.vkCmdFillBuffer.Invoke(
            cmd,
            _lightIndexBuffer.Handle,
            new ByteSize64(0),
            new ByteSize64(sizeof(uint)),
            0
        );

        // Barrier ensuring counter reset is visible to compute shader
        var fillBarrier = stackalloc VkBufferMemoryBarrier2[1];
        fillBarrier[0] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.TransferBit,
            SrcAccessMask = VkAccessFlags2.TransferWriteBit,
            DstStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderStorageReadBit | VkAccessFlags2.ShaderStorageWriteBit,
            Buffer = _lightIndexBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(sizeof(uint)),
        };

        var depInfo = new VkDependencyInfo
        {
            BufferMemoryBarrierCount = 1,
            BufferMemoryBarriers = fillBarrier,
        };
        _context.Device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &depInfo);

        // 2. Dispatch compute shader (workgroup 16x8x1, dispatched across Z=24)
        _context.Device.PInvoke.vkCmdBindPipeline.Invoke(cmd, VkPipelineBindPoint.Compute, _pipeline.Handle);

        var set = _descriptorSet;
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

        _context.Device.PInvoke.vkCmdDispatch.Invoke(cmd, 1, 1, ClustersZ);

        // 3. Execution barrier ensuring cluster records and light indices are visible to fragment shading
        var outBarriers = stackalloc VkBufferMemoryBarrier2[2];
        outBarriers[0] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderStorageReadBit,
            Buffer = _clusterRecordBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(_clusterRecordBuffer.SizeInBytes),
        };
        outBarriers[1] = new VkBufferMemoryBarrier2
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderStorageWriteBit,
            DstStageMask = VkPipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderStorageReadBit,
            Buffer = _lightIndexBuffer.Handle,
            Offset = new ByteSize64(0),
            Size = new ByteSize64(_lightIndexBuffer.SizeInBytes),
        };

        var outDepInfo = new VkDependencyInfo
        {
            BufferMemoryBarrierCount = 2,
            BufferMemoryBarriers = outBarriers,
        };
        _context.Device.PInvoke.vkCmdPipelineBarrier2.Invoke(cmd, &outDepInfo);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        _pipeline?.Dispose();
        if (_pipelineLayout.VkHandle != 0) device.DestroyPipelineLayout(_pipelineLayout, null);
        _descriptorAllocator?.Dispose();
        _descriptorLayout?.Dispose();

        _uniformBuffer?.Dispose();
        _pointLightBuffer?.Dispose();
        _spotLightBuffer?.Dispose();
        _clusterRecordBuffer?.Dispose();
        _lightIndexBuffer?.Dispose();

        _disposed = true;
    }
}
