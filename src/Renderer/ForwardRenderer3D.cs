// ==================================================================================================
// Jatayu Engine - 3D Forward Rendering
// Module: ForwardRenderer3D.cs
// Role: Coordinates 3D forward rendering passes (Shadows, Skybox, PBR Opaque, Debug Wireframe)
//       with large-world floating-origin coordinate precision, Reverse-Z depth, and Bindless texturing.
// ==================================================================================================

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Memory;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Core.Mathematics;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.Debug;
using Jatayu.Renderer.GpuDriven;
using Jatayu.Renderer.Graph;
using Jatayu.Renderer.Lighting;
using Jatayu.Renderer.Material;
using Jatayu.Renderer.Mesh;
using Jatayu.Renderer.Shadows;
using Jatayu.Renderer.Skybox;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Descriptors;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;
using Jatayu.Scene;
using Jatayu.Scene.Components;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Jatayu.World;

namespace Jatayu.Renderer;

/// <summary>
/// Push constants submitted to the PBR vertex & fragment shaders (196 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 196)]
public struct PbrPushConstants
{
    public Matrix4x4 ModelMatrix;
    public Matrix4x4 ViewProjMatrix;
    public Matrix4x4 LightViewProj;
    public uint MaterialId;
}

/// <summary>
/// Push constants submitted to the shadow depth shader (128 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 128)]
public struct ShadowPushConstants
{
    public Matrix4x4 LightViewProj;
    public Matrix4x4 ModelMatrix;
}

/// <summary>
/// Push constants submitted to the skybox procedural shader (64 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct SkyboxPushConstants
{
    public Matrix4x4 InvViewProj;
}

/// <summary>
/// Push constants submitted to the debug line wireframe shader (64 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct DebugLinePushConstants
{
    public Matrix4x4 ViewProjMatrix;
}

/// <summary>
/// Main 3D Forward Renderer orchestrating real-time directional shadows, procedural skybox,
/// Cook-Torrance GGX PBR opaque rendering, and immediate-mode 3D wireframe diagnostics.
/// Built from the ground up for zero managed allocations on hot paths and large-world coordinate precision.
/// </summary>
public sealed class ForwardRenderer3D : IDisposable
{
    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;
    private readonly ShaderCompiler _shaderCompiler;
    private readonly bool _ownsShaderCompiler;

    // GPU Uniform Buffers
    private readonly GpuBuffer _lightEnvironmentBuffer;

    // Descriptor Set Management for Set 0 (Frame / Scene Resources)
    private VkDescriptorSetLayout _frameDescriptorLayout;
    private VkDescriptorPool _frameDescriptorPool;
    private VkDescriptorSet _frameDescriptorSet;

    // Pipeline Layouts
    private VkPipelineLayout _shadowPipelineLayout;
    private VkPipelineLayout _skyboxPipelineLayout;
    private VkPipelineLayout _pbrPipelineLayout;
    private VkPipelineLayout _debugPipelineLayout;

    // Graphics Pipelines
    private VulkanPipeline _shadowPipeline = null!;
    private VulkanPipeline _skyboxPipeline = null!;
    private VulkanPipeline _pbrPipeline = null!;
    private VulkanPipeline _debugPipeline = null!;

    // Reusable culling lists to eliminate GC allocations
    private readonly List<RenderItem> _visibleItems = new(1024);
    private readonly List<RenderItem> _shadowCasters = new(1024);

    public int VisibleItemCount => _visibleItems.Count;
    public int ShadowCasterCount => _shadowCasters.Count;

    private static readonly Arch.Core.QueryDescription CullQuery = new Arch.Core.QueryDescription().WithAll<Transform, MeshRenderer>();

    // Preallocated Vulkan command recording arrays (0 allocations per frame)
    private readonly VkViewport[] _viewports = new VkViewport[1];
    private readonly VkRect2D[] _scissors = new VkRect2D[1];
    private readonly VkDescriptorSet[] _pbrDescriptorSets = new VkDescriptorSet[2];
    private readonly VkBuffer[] _vertexBufferArray = new VkBuffer[1];
    private readonly ByteSize64[] _vertexOffsetArray = new ByteSize64[1];

    private readonly Skybox.Skybox _skybox = new();
    private readonly Frustum _frustum = new() { MaxDrawDistance = 100000.0f };

    // Preallocated render pass callbacks (0 delegate/closure allocations per frame)
    private readonly Action<RenderPassBuilder> _shadowPassSetup;
    private readonly Action<RenderPassContext> _shadowPassExecute;
    private readonly Action<RenderPassBuilder> _skyboxPassSetup;
    private readonly Action<RenderPassContext> _skyboxPassExecute;
    private readonly Action<RenderPassBuilder> _pbrPassSetup;
    private readonly Action<RenderPassContext> _pbrPassExecute;
    private readonly Action<RenderPassBuilder> _debugPassSetup;
    private readonly Action<RenderPassContext> _debugPassExecute;

    // Per-frame pass arguments stored on instance to avoid lambda captures
    private RgTextureHandle _currentShadowDepthHandle;
    private RgTextureHandle _currentColorTarget;
    private RgTextureHandle _currentDepthTarget;
    private ShadowMap _currentShadowMap = null!;
    private BindlessTable _currentBindlessTable = null!;
    private Matrix4x4 _currentShadowLightVp;
    private Matrix4x4 _currentVp;
    private Matrix4x4 _currentInvVp;
    private Matrix4x4 _currentCameraViewProj;
    private uint _currentDebugVertexCount;
    private GpuBuffer _currentDebugBuffer = null!;

    private bool _disposed;

    private readonly struct RenderItem
    {
        public readonly Mesh.Mesh Mesh;
        public readonly Matrix4x4 CameraRelativeModel;
        public readonly uint MaterialId;

        public RenderItem(Mesh.Mesh mesh, in Matrix4x4 cameraRelativeModel, uint materialId)
        {
            Mesh = mesh;
            CameraRelativeModel = cameraRelativeModel;
            MaterialId = materialId;
        }
    }

    private struct CullJob : Arch.Core.IForEach<Transform, MeshRenderer>
    {
        public double CamX;
        public double CamY;
        public double CamZ;
        public Frustum Frustum;
        public List<RenderItem> VisibleItems;
        public List<RenderItem> ShadowCasters;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(ref Transform t, ref MeshRenderer mr)
        {
            if (!mr.IsVisible || mr.Mesh == null)
                return;

            Vector3 relPos = new(
                (float)(t.WorldX - CamX),
                (float)(t.WorldY - CamY),
                (float)(t.WorldZ - CamZ)
            );

            Matrix4x4 cameraRelativeModel =
                Matrix4x4.CreateScale(t.Scale) *
                Matrix4x4.CreateFromQuaternion(t.Rotation) *
                Matrix4x4.CreateTranslation(relPos);

            float maxScale = MathF.Max(t.Scale.X, MathF.Max(t.Scale.Y, t.Scale.Z));
            float radius = mr.Mesh.BoundingRadius * maxScale;

            if (Frustum.IsSphereVisible(relPos, radius))
            {
                VisibleItems.Add(new RenderItem(mr.Mesh, cameraRelativeModel, mr.MaterialId));
            }

            if (mr.CastShadows)
            {
                ShadowCasters.Add(new RenderItem(mr.Mesh, cameraRelativeModel, mr.MaterialId));
            }
        }
    }

    /// <summary>
    /// Constructs and initializes the 3D forward renderer, compiling GLSL shaders and building graphics pipelines.
    /// </summary>
    public unsafe ForwardRenderer3D(
        VulkanContext context,
        GpuAllocator allocator,
        BindlessTable bindlessTable,
        ShaderCompiler? shaderCompiler = null,
        VkFormat colorFormat = VkFormat.B8G8R8A8UNorm)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        ArgumentNullException.ThrowIfNull(bindlessTable);

        if (shaderCompiler != null)
        {
            _shaderCompiler = shaderCompiler;
            _ownsShaderCompiler = false;
        }
        else
        {
            _shaderCompiler = new ShaderCompiler("assets/shaders/cache");
            _ownsShaderCompiler = true;
        }

        // 1. Allocate LightEnvironment uniform buffer (128 bytes, CPU-to-GPU mapped)
        _lightEnvironmentBuffer = _allocator.CreateBuffer(
            (ulong)Marshal.SizeOf<GpuLightEnvironmentData>(),
            VkBufferUsageFlags.UniformBufferBit,
            VmaMemoryUsage.CpuToGpu,
            VmaAllocationCreateFlags.MappedBit
        );

        // 2. Initialize Set 0 Descriptor Set Layout (LightEnv UBO + ShadowMap Sampler + MaterialTable SSBO)
        CreateDescriptorSetLayout();

        // 3. Create Pipeline Layouts
        CreatePipelineLayouts(bindlessTable.Layout);

        // 4. Compile Shaders & Build Pipelines
        BuildPipelines(colorFormat);

        // 5. Preallocate RenderPass Callbacks (0 allocations per frame)
        _shadowPassSetup = SetupShadowPass;
        _shadowPassExecute = ExecuteShadowPass;
        _skyboxPassSetup = SetupSkyboxPass;
        _skyboxPassExecute = ExecuteSkyboxPass;
        _pbrPassSetup = SetupPbrPass;
        _pbrPassExecute = ExecutePbrPass;
        _debugPassSetup = SetupDebugPass;
        _debugPassExecute = ExecuteDebugPass;
    }

    private unsafe void CreateDescriptorSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[3];

        // Binding 0: Uniform buffer for LightEnvironment
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };

        // Binding 1: Combined image comparison sampler for ShadowMap
        bindings[1] = new VkDescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };

        // Binding 2: Storage buffer for MaterialTable
        bindings[2] = new VkDescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            BindingCount = 3,
            Bindings = bindings,
        };

        _frameDescriptorLayout = _context.Device.CreateDescriptorSetLayout(in layoutInfo, null);

        // Create descriptor pool
        var poolSizes = stackalloc VkDescriptorPoolSize[3];
        poolSizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 };
        poolSizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 };
        poolSizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 };

        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            MaxSets = 2,
            PoolSizeCount = 3,
            PoolSizes = poolSizes,
        };

        _frameDescriptorPool = _context.Device.CreateDescriptorPool(in poolInfo, null);

        var localLayout = _frameDescriptorLayout;
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            DescriptorPool = _frameDescriptorPool,
            DescriptorSetCount = 1,
            SetLayouts = &localLayout,
        };

        VkDescriptorSet set = default;
        if (_context.Device.PInvoke.vkAllocateDescriptorSets.Invoke(_context.Device.Handle, &allocInfo, &set) != VkResult.Success)
        {
            throw new InvalidOperationException("Failed to allocate frame descriptor set for ForwardRenderer3D.");
        }
        _frameDescriptorSet = set;
    }

    private unsafe void CreatePipelineLayouts(VkDescriptorSetLayout bindlessLayout)
    {
        var device = _context.Device;

        // Shadow Pipeline Layout: 128 bytes push constants
        var shadowPushConstant = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<ShadowPushConstants>()),
        };
        _shadowPipelineLayout = device.CreatePipelineLayout([], [shadowPushConstant], null);

        // Skybox Pipeline Layout: 64 bytes push constants
        var skyboxPushConstant = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<SkyboxPushConstants>()),
        };
        _skyboxPipelineLayout = device.CreatePipelineLayout([], [skyboxPushConstant], null);

        // Debug Line Pipeline Layout: 64 bytes push constants
        var debugPushConstant = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<DebugLinePushConstants>()),
        };
        _debugPipelineLayout = device.CreatePipelineLayout([], [debugPushConstant], null);

        // PBR Pipeline Layout: Set 0 (Frame) + Set 1 (Bindless) + 196 bytes push constants
        var pbrPushConstant = new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            Offset = new ByteSize32(0),
            Size = new ByteSize32((uint)Marshal.SizeOf<PbrPushConstants>()),
        };
        _pbrPipelineLayout = device.CreatePipelineLayout([_frameDescriptorLayout, bindlessLayout], [pbrPushConstant], null);
    }

    private unsafe void BuildPipelines(VkFormat colorFormat)
    {
        var device = _context.Device;

        // 1. Build Shadow Depth Pipeline
        var shadowVert = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/shadow.vert"), ShaderKind.VertexShader);
        using (var shadowStages = new ShaderStages(device, [Presets.ShaderStage.Vertex(shadowVert)], null))
        {
            var shadowVertexInput = new VertexInput(1, 1);
            shadowVertexInput.AddBinding(0, new ByteSize((uint)sizeof(Vertex3D)), VkVertexInputRate.Vertex);
            shadowVertexInput.AddAttribute(0, 0, VkFormat.R32G32B32SFloat, new ByteSize(0));

            _shadowPipeline = PipelineBuilder.Create()
                .SetShaderStages(shadowStages)
                .SetVertexInput(shadowVertexInput)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: true, depthWriteEnable: true, compareOp: DepthConvention.DepthCompareOp)
                .SetRasterization(Presets.Rasterization.Fill.CullBack)
                .SetPipelineLayout(_shadowPipelineLayout)
                .Build(_context);
        }

        // 2. Build Skybox Pipeline
        var skyboxVert = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/skybox.vert"), ShaderKind.VertexShader);
        var skyboxFrag = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/skybox.frag"), ShaderKind.FragmentShader);
        using (var skyboxStages = new ShaderStages(device, [Presets.ShaderStage.Vertex(skyboxVert), Presets.ShaderStage.Fragment(skyboxFrag)], null))
        {
            _skyboxPipeline = PipelineBuilder.Create()
                .SetShaderStages(skyboxStages)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: false, depthWriteEnable: false)
                .SetRasterization(Presets.Rasterization.Fill.CullNone)
                .SetColorBlend(Presets.BlendState.BlendNone)
                .SetPipelineLayout(_skyboxPipelineLayout)
                .Build(_context);
        }

        // 3. Build PBR Opaque Pipeline
        var pbrVert = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/pbr.vert"), ShaderKind.VertexShader);
        var pbrFrag = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/pbr.frag"), ShaderKind.FragmentShader);
        using (var pbrStages = new ShaderStages(device, [Presets.ShaderStage.Vertex(pbrVert), Presets.ShaderStage.Fragment(pbrFrag)], null))
        {
            var pbrVertexInput = new VertexInput(1, 4);
            pbrVertexInput.AddBinding(0, new ByteSize((uint)sizeof(Vertex3D)), VkVertexInputRate.Vertex);
            pbrVertexInput.AddAttribute(0, 0, VkFormat.R32G32B32SFloat, new ByteSize(0));  // Position
            pbrVertexInput.AddAttribute(1, 0, VkFormat.R32G32B32SFloat, new ByteSize(12)); // Color
            pbrVertexInput.AddAttribute(2, 0, VkFormat.R32G32B32SFloat, new ByteSize(24)); // Normal
            pbrVertexInput.AddAttribute(3, 0, VkFormat.R32G32SFloat, new ByteSize(36));    // TexCoord

            _pbrPipeline = PipelineBuilder.Create()
                .SetShaderStages(pbrStages)
                .SetVertexInput(pbrVertexInput)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: true, depthWriteEnable: true, compareOp: DepthConvention.DepthCompareOp)
                .SetRasterization(Presets.Rasterization.Fill.CullBack)
                .SetColorBlend(Presets.BlendState.BlendNone)
                .SetPipelineLayout(_pbrPipelineLayout)
                .Build(_context);
        }

        // 4. Build Debug Line Pipeline
        var debugVert = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/debug_line.vert"), ShaderKind.VertexShader);
        var debugFrag = _shaderCompiler.CompileShaderModuleFromFile(device, ResolveShaderPath("assets/shaders/debug_line.frag"), ShaderKind.FragmentShader);
        using (var debugStages = new ShaderStages(device, [Presets.ShaderStage.Vertex(debugVert), Presets.ShaderStage.Fragment(debugFrag)], null))
        {
            var debugVertexInput = new VertexInput(1, 2);
            debugVertexInput.AddBinding(0, new ByteSize((uint)sizeof(DebugLineVertex)), VkVertexInputRate.Vertex);
            debugVertexInput.AddAttribute(0, 0, VkFormat.R32G32B32SFloat, new ByteSize(0));
            debugVertexInput.AddAttribute(1, 0, VkFormat.R32G32B32A32SFloat, new ByteSize(12));

            _debugPipeline = PipelineBuilder.Create()
                .SetShaderStages(debugStages)
                .SetVertexInput(debugVertexInput)
                .SetInputAssembly(Presets.InputAssembly.LineList)
                .SetColorAttachmentFormat(colorFormat)
                .SetDepthAttachmentFormat(DepthConvention.DepthFormat)
                .SetDepthTest(depthTestEnable: true, depthWriteEnable: false, compareOp: DepthConvention.DepthCompareOp)
                .SetRasterization(Presets.Rasterization.Fill.CullNone)
                .SetColorBlend(Presets.BlendState.BlendNone)
                .SetPipelineLayout(_debugPipelineLayout)
                .Build(_context);
        }
    }

    private unsafe void UpdateLightBuffer(in GpuLightEnvironmentData data)
    {
        fixed (GpuLightEnvironmentData* pData = &data)
        {
            _lightEnvironmentBuffer.SetData(new ReadOnlySpan<GpuLightEnvironmentData>(pData, 1));
        }
    }

    /// <summary>
    /// Records all 3D forward rendering passes (Shadows, Skybox, PBR Opaque, Debug) into the render graph.
    /// </summary>
    public unsafe void Render(
        RenderGraph graph,
        Scene.Scene scene,
        Camera3D camera,
        LightEnvironment lightEnv,
        ShadowMap shadowMap,
        BindlessTable bindlessTable,
        MaterialTable materialTable,
        GeometryPool geometryPool,
        DynamicInstanceRing? instanceRing,
        DebugRenderer? debugRenderer,
        RgTextureHandle colorTarget,
        RgTextureHandle depthTarget,
        OriginShift? originShift = null,
        WorldGrid? worldGrid = null,
        Func<AssetGuid, Mesh.Mesh?>? meshResolver = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(lightEnv);
        ArgumentNullException.ThrowIfNull(shadowMap);
        ArgumentNullException.ThrowIfNull(bindlessTable);
        ArgumentNullException.ThrowIfNull(materialTable);
        ArgumentNullException.ThrowIfNull(geometryPool);

        // 1. Update LightEnvironment GPU buffer (centered around camera-relative origin Vector3.Zero)
        var lightGpuData = lightEnv.ToGpuData(Vector3.Zero);
        UpdateLightBuffer(in lightGpuData);

        Matrix4x4 shadowLightVp = lightGpuData.LightViewProj;

        // 2. Update Set 0 Descriptors
        var bufferInfo = new VkDescriptorBufferInfo
        {
            Buffer = _lightEnvironmentBuffer.Handle,
            Offset = new ByteSize64(0),
            Range = new ByteSize64((ulong)sizeof(GpuLightEnvironmentData)),
        };

        var shadowImageInfo = new VkDescriptorImageInfo
        {
            Sampler = shadowMap.ComparisonSampler,
            ImageView = shadowMap.ImageView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var materialBufferInfo = new VkDescriptorBufferInfo
        {
            Buffer = materialTable.Buffer.Handle,
            Offset = new ByteSize64(0),
            Range = new ByteSize64((ulong)(sizeof(GpuMaterialData) * materialTable.Count)),
        };

        var writes = stackalloc VkWriteDescriptorSet[3];
        writes[0] = new VkWriteDescriptorSet
        {
            DstSet = _frameDescriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.UniformBuffer,
            BufferInfo = &bufferInfo,
        };
        writes[1] = new VkWriteDescriptorSet
        {
            DstSet = _frameDescriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.CombinedImageSampler,
            ImageInfo = &shadowImageInfo,
        };
        writes[2] = new VkWriteDescriptorSet
        {
            DstSet = _frameDescriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.StorageBuffer,
            BufferInfo = &materialBufferInfo,
        };

        _context.Device.PInvoke.vkUpdateDescriptorSets.Invoke(_context.Device.Handle, 3, writes, 0, null);

        // 3. Camera-Relative Frustum Culling & Matrix Extraction
        _visibleItems.Clear();
        _shadowCasters.Clear();

        Matrix4x4 cameraRelativeView = camera.ViewMatrix;
        cameraRelativeView.M41 = 0;
        cameraRelativeView.M42 = 0;
        cameraRelativeView.M43 = 0;
        Matrix4x4 vp = cameraRelativeView * camera.ProjectionMatrix;

        _frustum.Update(vp, Vector3.Zero);

        Vector3 camPos = camera.Position;
        double camX = camPos.X;
        double camY = camPos.Y;
        double camZ = camPos.Z;

        // Query ECS world for renderable entities with zero allocations
        var cullJob = new CullJob
        {
            CamX = camX,
            CamY = camY,
            CamZ = camZ,
            Frustum = _frustum,
            VisibleItems = _visibleItems,
            ShadowCasters = _shadowCasters,
        };
        scene.World.InlineQuery<CullJob, Transform, MeshRenderer>(in CullQuery, ref cullJob);

        // Query WorldGrid for static instances owned by active streaming cells (temporary draw path for Sprint 6a)
        if (worldGrid != null && meshResolver != null)
        {
            var cameraWorldPos = new WorldPosition(camX, camY, camZ);
            var activeCells = worldGrid.ActiveCells;
            for (int c = 0; c < activeCells.Count; c++)
            {
                var cell = activeCells[c];
                cell.GetCameraRelativeBounds(cameraWorldPos, out var cellRelCenter, out var cellRadius);
                if (!_frustum.IsSphereVisible(cellRelCenter, cellRadius))
                    continue;

                var instances = cell.StaticInstances;
                for (int i = 0; i < instances.Length; i++)
                {
                    ref readonly var inst = ref instances[i];
                    double objWorldX = cell.Origin.X + inst.PosX;
                    double objWorldY = cell.Origin.Y + inst.PosY;
                    double objWorldZ = cell.Origin.Z + inst.PosZ;

                    Vector3 relPos = new(
                        (float)(objWorldX - camX),
                        (float)(objWorldY - camY),
                        (float)(objWorldZ - camZ)
                    );

                    float maxScale = MathF.Max(inst.ScaleX, MathF.Max(inst.ScaleY, inst.ScaleZ));
                    float radius = inst.BoundingRadius * maxScale;

                    if (_frustum.IsSphereVisible(relPos, radius))
                    {
                        var mesh = meshResolver(inst.MeshGuid);
                        if (mesh != null)
                        {
                            var rot = new Quaternion(inst.RotX, inst.RotY, inst.RotZ, inst.RotW);
                            var scale = new Vector3(inst.ScaleX, inst.ScaleY, inst.ScaleZ);
                            Matrix4x4 cameraRelativeModel =
                                Matrix4x4.CreateScale(scale) *
                                Matrix4x4.CreateFromQuaternion(rot) *
                                Matrix4x4.CreateTranslation(relPos);

                            var item = new RenderItem(mesh, cameraRelativeModel, inst.MaterialId);
                            _visibleItems.Add(item);

                            if ((inst.Flags & StaticInstanceFlags.CastShadow) != 0)
                            {
                                _shadowCasters.Add(item);
                            }
                        }
                    }
                }
            }
        }

        // 4. Import Shadow Map depth target into RenderGraph
        var shadowDepthHandle = graph.ImportTexture(
            "ShadowMapDepth",
            shadowMap.Texture.Image,
            shadowMap.ImageView,
            DepthConvention.DepthFormat,
            (uint)shadowMap.Resolution,
            (uint)shadowMap.Resolution,
            VkImageLayout.Undefined
        );

        // Store frame arguments in fields to allow zero-allocation cached delegate execution
        _currentShadowDepthHandle = shadowDepthHandle;
        _currentColorTarget = colorTarget;
        _currentDepthTarget = depthTarget;
        _currentShadowMap = shadowMap;
        _currentBindlessTable = bindlessTable;
        _currentShadowLightVp = shadowLightVp;
        _currentVp = vp;
        _currentInvVp = _skybox.ComputeInverseViewProjection(camera.ViewMatrix, camera.ProjectionMatrix);

        // 5. Register Shadow Pass
        if (_shadowCasters.Count > 0 && lightEnv.Sun.CastsShadows)
        {
            graph.AddPass("ShadowPass", _shadowPassSetup, _shadowPassExecute);
        }

        // 6. Register Skybox Pass (Renders at Reverse-Z infinity: Z = 0.0)
        graph.AddPass("SkyboxPass", _skyboxPassSetup, _skyboxPassExecute);

        // 7. Register PBR Opaque Pass
        graph.AddPass("PbrOpaquePass", _pbrPassSetup, _pbrPassExecute);

        // 8. Register Debug Wireframe Pass
        if (debugRenderer != null && debugRenderer.VertexCount > 0)
        {
            debugRenderer.Flush(out var vertexCount, out var debugBuffer);

            if (vertexCount > 0)
            {
                _currentDebugVertexCount = (uint)vertexCount;
                _currentDebugBuffer = debugBuffer;
                _currentCameraViewProj = camera.ViewProjection;
                graph.AddPass("DebugPass", _debugPassSetup, _debugPassExecute);
            }

            debugRenderer.Clear();
        }
    }

    private void SetupShadowPass(RenderPassBuilder builder)
    {
        builder.SetDepthAttachment(
            _currentShadowDepthHandle,
            VkAttachmentLoadOp.Clear,
            VkAttachmentStoreOp.Store,
            DepthConvention.ClearDepth
        );
    }

    private unsafe void ExecuteShadowPass(RenderPassContext ctx)
    {
        var cmd = ctx.CommandBuffer;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _shadowPipeline.Handle);

        _viewports[0] = new VkViewport
        {
            X = 0f,
            Y = 0f,
            Width = _currentShadowMap.Resolution,
            Height = _currentShadowMap.Resolution,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        cmd.SetViewport(0, _viewports);
        _scissors[0] = new VkRect2D(new VkExtent2D(_currentShadowMap.Resolution, _currentShadowMap.Resolution));
        cmd.SetScissor(0, _scissors);

        for (int i = 0; i < _shadowCasters.Count; i++)
        {
            var item = _shadowCasters[i];
            var pc = new ShadowPushConstants
            {
                LightViewProj = _currentShadowLightVp,
                ModelMatrix = item.CameraRelativeModel,
            };

            CommandBufferExtensions.PushConstants(
                cmd,
                _shadowPipelineLayout,
                VkShaderStageFlags.VertexBit,
                new ByteSize(0),
                new ReadOnlySpan<ShadowPushConstants>(&pc, 1)
            );

            _vertexBufferArray[0] = item.Mesh.VertexBuffer.Handle;
            _vertexOffsetArray[0] = new ByteSize64(item.Mesh.VertexOffset);
            cmd.BindVertexBuffers(0, _vertexBufferArray, _vertexOffsetArray);
            cmd.BindIndexBuffer(item.Mesh.IndexBuffer.Handle, new ByteSize64(item.Mesh.IndexOffset), item.Mesh.IndexType);
            cmd.DrawIndexed((int)item.Mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    private void SetupSkyboxPass(RenderPassBuilder builder)
    {
        var clearColor = new VkClearColorValue { Float32 = new float4(0.12f, 0.32f, 0.72f, 1.0f) };
        builder.SetColorAttachment(_currentColorTarget, VkAttachmentLoadOp.Clear, VkAttachmentStoreOp.Store, clearColor);
        builder.SetDepthAttachment(_currentDepthTarget, VkAttachmentLoadOp.Clear, VkAttachmentStoreOp.Store, DepthConvention.ClearDepth);
    }

    private unsafe void ExecuteSkyboxPass(RenderPassContext ctx)
    {
        var cmd = ctx.CommandBuffer;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _skyboxPipeline.Handle);

        _viewports[0] = new VkViewport
        {
            X = 0f,
            Y = 0f,
            Width = (float)ctx.RenderArea.Width,
            Height = (float)ctx.RenderArea.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        cmd.SetViewport(0, _viewports);
        _scissors[0] = new VkRect2D(ctx.RenderArea);
        cmd.SetScissor(0, _scissors);

        var pc = new SkyboxPushConstants { InvViewProj = _currentInvVp };

        CommandBufferExtensions.PushConstants(
            cmd,
            _skyboxPipelineLayout,
            VkShaderStageFlags.VertexBit,
            new ByteSize(0),
            new ReadOnlySpan<SkyboxPushConstants>(&pc, 1)
        );

        // Fullscreen procedural triangle (3 vertices, no buffers bound)
        cmd.Draw(3, 1, 0, 0);
    }

    private void SetupPbrPass(RenderPassBuilder builder)
    {
        builder.SetColorAttachment(_currentColorTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
        builder.SetDepthAttachment(_currentDepthTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
        builder.ReadTexture(_currentShadowDepthHandle, VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderSampledReadBit, VkImageLayout.ShaderReadOnlyOptimal);
    }

    private unsafe void ExecutePbrPass(RenderPassContext ctx)
    {
        var cmd = ctx.CommandBuffer;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pbrPipeline.Handle);

        _viewports[0] = new VkViewport
        {
            X = 0f,
            Y = 0f,
            Width = (float)ctx.RenderArea.Width,
            Height = (float)ctx.RenderArea.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        cmd.SetViewport(0, _viewports);
        _scissors[0] = new VkRect2D(ctx.RenderArea);
        cmd.SetScissor(0, _scissors);

        // Bind Set 0 (Frame) and Set 1 (Bindless Textures)
        _pbrDescriptorSets[0] = _frameDescriptorSet;
        _pbrDescriptorSets[1] = _currentBindlessTable.DescriptorSet;
        cmd.BindDescriptorSets(
            VkPipelineBindPoint.Graphics,
            _pbrPipelineLayout,
            0,
            _pbrDescriptorSets,
            Span<ByteSize32>.Empty
        );

        for (int i = 0; i < _visibleItems.Count; i++)
        {
            var item = _visibleItems[i];
            var pc = new PbrPushConstants
            {
                ModelMatrix = item.CameraRelativeModel,
                ViewProjMatrix = _currentVp,
                LightViewProj = _currentShadowLightVp,
                MaterialId = item.MaterialId,
            };

            CommandBufferExtensions.PushConstants(
                cmd,
                _pbrPipelineLayout,
                VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                new ByteSize(0),
                new ReadOnlySpan<PbrPushConstants>(&pc, 1)
            );

            _vertexBufferArray[0] = item.Mesh.VertexBuffer.Handle;
            _vertexOffsetArray[0] = new ByteSize64(item.Mesh.VertexOffset);
            cmd.BindVertexBuffers(0, _vertexBufferArray, _vertexOffsetArray);
            cmd.BindIndexBuffer(item.Mesh.IndexBuffer.Handle, new ByteSize64(item.Mesh.IndexOffset), item.Mesh.IndexType);
            cmd.DrawIndexed((int)item.Mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    private void SetupDebugPass(RenderPassBuilder builder)
    {
        builder.SetColorAttachment(_currentColorTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
        builder.SetDepthAttachment(_currentDepthTarget, VkAttachmentLoadOp.Load, VkAttachmentStoreOp.Store);
    }

    private unsafe void ExecuteDebugPass(RenderPassContext ctx)
    {
        var cmd = ctx.CommandBuffer;
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _debugPipeline.Handle);

        _viewports[0] = new VkViewport
        {
            X = 0f,
            Y = 0f,
            Width = (float)ctx.RenderArea.Width,
            Height = (float)ctx.RenderArea.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        cmd.SetViewport(0, _viewports);
        _scissors[0] = new VkRect2D(ctx.RenderArea);
        cmd.SetScissor(0, _scissors);

        var pc = new DebugLinePushConstants
        {
            ViewProjMatrix = _currentCameraViewProj,
        };

        CommandBufferExtensions.PushConstants(
            cmd,
            _debugPipelineLayout,
            VkShaderStageFlags.VertexBit,
            new ByteSize(0),
            new ReadOnlySpan<DebugLinePushConstants>(&pc, 1)
        );

        _vertexBufferArray[0] = _currentDebugBuffer.Handle;
        _vertexOffsetArray[0] = new ByteSize64(0);
        cmd.BindVertexBuffers(0, _vertexBufferArray, _vertexOffsetArray);
        cmd.Draw((int)_currentDebugVertexCount, 1, 0, 0);
    }

    private static string ResolveShaderPath(string relativePath)
    {
        if (File.Exists(relativePath)) return relativePath;

        string fromBase = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(fromBase)) return fromBase;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return relativePath;
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;

        var device = _context.Device;

        _shadowPipeline?.Dispose();
        _skyboxPipeline?.Dispose();
        _pbrPipeline?.Dispose();
        _debugPipeline?.Dispose();

        if (_frameDescriptorPool.VkHandle != 0) device.DestroyDescriptorPool(_frameDescriptorPool, null);
        if (_frameDescriptorLayout.VkHandle != 0) device.DestroyDescriptorSetLayout(_frameDescriptorLayout, null);

        _lightEnvironmentBuffer?.Dispose();

        if (_ownsShaderCompiler)
        {
            _shaderCompiler.Dispose();
        }

        _disposed = true;
    }
}
