/* =================================================================================================
 * FILE: RenderGraph.cs
 * LOCATION: src/Renderer/Graph/RenderGraph.cs
 * 
 * WHAT THIS FILE DOES:
 *   Modern Vulkan 1.3 frame render graph (DAG).
 *   - Passes declare read/write dependencies on virtual resource handles.
 *   - Compiles topological execution order and culls dead/unused passes.
 *   - Automates stage/access transitions and image layout barriers via BarrierBuilder (VK_KHR_synchronization2).
 *   - Coordinates Vulkan 1.3 Dynamic Rendering (vkCmdBeginRendering / vkCmdEndRendering) with zero
 *     legacy VkRenderPass or VkFramebuffer overhead.
 *   - Automatically integrates per-pass GPU timing queries via GpuTimer.
 *   - Reuses preallocated internal buffers for zero-allocation hot paths.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Profiling;

namespace Jatayu.Renderer.Graph;

/// <summary>
/// Frame render graph DAG coordinating resource synchronization, pass scheduling, and Dynamic Rendering.
/// </summary>
public sealed class RenderGraph : IDisposable
{
    private readonly List<RgTextureResource> _textures = new(32);
    private readonly List<RgBufferResource> _buffers = new(16);
    private readonly List<RenderPass> _passes = new(32);
    private readonly List<RenderPass> _compiledPasses = new(32);

    private readonly BarrierBuilder _barrierBuilder = new();
    private readonly RenderPassContext _passContext;

    private readonly VkRenderingAttachmentInfo[] _colorAttachmentBuffers = new VkRenderingAttachmentInfo[8];
    private bool _isCompiled;
    private bool _disposed;

    /// <summary>
    /// Gets the list of passes registered in the graph.
    /// </summary>
    public IReadOnlyList<RenderPass> Passes => _passes;

    /// <summary>
    /// Gets the compiled, topologically sorted active execution list.
    /// </summary>
    public IReadOnlyList<RenderPass> CompiledPasses => _compiledPasses;

    public RenderGraph()
    {
        _passContext = new RenderPassContext(this);
    }

    /// <summary>
    /// Imports an existing external texture (e.g. swapchain image or depth buffer) into the graph.
    /// </summary>
    public RgTextureHandle ImportTexture(
        string name,
        VkImage image,
        VkImageView imageView,
        VkFormat format,
        uint width,
        uint height,
        VkImageLayout initialLayout,
        VkPipelineStageFlags2 initialStage = VkPipelineStageFlags2.None,
        VkAccessFlags2 initialAccess = VkAccessFlags2.None)
    {
        var handle = new RgTextureHandle((uint)(_textures.Count + 1), 1);
        var aspect = RgTextureResource.DetermineAspect(format);

        var resource = new RgTextureResource
        {
            Handle = handle,
            Name = name,
            IsImported = true,
            Image = image,
            ImageView = imageView,
            Format = format,
            Width = width,
            Height = height,
            Aspect = aspect,
            InitialLayout = initialLayout,
            CurrentLayout = initialLayout,
            CurrentStage = initialStage,
            CurrentAccess = initialAccess,
        };

        _textures.Add(resource);
        return handle;
    }

    /// <summary>
    /// Adds a new render pass to the graph DAG with declarative setup and execution callbacks.
    /// </summary>
    public RenderPass AddPass(string name, Action<RenderPassBuilder> setup, Action<RenderPassContext> execute)
    {
        if (setup == null) throw new ArgumentNullException(nameof(setup));
        if (execute == null) throw new ArgumentNullException(nameof(execute));

        var pass = new RenderPass(name, _passes.Count)
        {
            ExecuteCallback = execute,
        };

        var builder = new RenderPassBuilder(pass);
        setup(builder);

        _passes.Add(pass);
        _isCompiled = false;
        return pass;
    }

    /// <summary>
    /// Compiles the render graph: analyzes data dependencies, culls unused passes, and topologically sorts execution order.
    /// </summary>
    public void Compile()
    {
        _compiledPasses.Clear();

        var passCount = _passes.Count;
        if (passCount == 0)
        {
            _isCompiled = true;
            return;
        }

        // Build adjacency list for topological sort
        // In-degree array: inDegree[i] is the number of prerequisites that pass i must wait for
        var inDegree = new int[passCount];
        var dependents = new List<int>[passCount];
        for (int i = 0; i < passCount; i++)
        {
            dependents[i] = [];
        }

        // Track last writer for each texture to determine write->read and write->write dependencies
        var lastWriter = new Dictionary<uint, int>();
        // Track all writers for each texture
        var writersByResource = new Dictionary<uint, List<int>>();
        for (int i = 0; i < passCount; i++)
        {
            var pass = _passes[i];
            foreach (var decl in pass.ResourceDeclarations)
            {
                if (!decl.TextureHandle.IsValid || !decl.IsWrite)
                    continue;

                var id = decl.TextureHandle.Id;
                if (!writersByResource.TryGetValue(id, out var list))
                {
                    list = [];
                    writersByResource[id] = list;
                }

                if (!list.Contains(i))
                    list.Add(i);
            }
        }

        // Build dependencies: writer -> reader and writer -> subsequent writer
        for (int i = 0; i < passCount; i++)
        {
            var pass = _passes[i];
            foreach (var decl in pass.ResourceDeclarations)
            {
                if (!decl.TextureHandle.IsValid)
                    continue;

                var id = decl.TextureHandle.Id;
                if (writersByResource.TryGetValue(id, out var writers))
                {
                    foreach (var w in writers)
                    {
                        if (w == i)
                            continue;

                        // If pass i reads, it depends on writer w (w must finish before i)
                        // If pass i also writes, establish ordering based on declaration order
                        if (!decl.IsWrite || w < i)
                        {
                            if (!dependents[w].Contains(i))
                            {
                                dependents[w].Add(i);
                            }
                        }
                    }
                }
            }
        }

        // Culling: identify active passes.
        // A pass is active if it has side effects (e.g. presentation) or produces resources consumed by active passes.
        var isActive = new bool[passCount];
        var queue = new Queue<int>();

        for (int i = 0; i < passCount; i++)
        {
            if (_passes[i].HasSideEffects)
            {
                isActive[i] = true;
                queue.Enqueue(i);
            }
        }

        // Backtrack from active passes to activate producers
        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            var currPass = _passes[curr];

            foreach (var decl in currPass.ResourceDeclarations)
            {
                if (decl.IsWrite || !decl.TextureHandle.IsValid)
                    continue;

                if (writersByResource.TryGetValue(decl.TextureHandle.Id, out var writers))
                {
                    foreach (var w in writers)
                    {
                        if (!isActive[w])
                        {
                            isActive[w] = true;
                            queue.Enqueue(w);
                        }
                    }
                }
            }
        }

        // Mark culled passes
        for (int i = 0; i < passCount; i++)
        {
            _passes[i].IsCulled = !isActive[i];
        }

        // Calculate in-degree for active passes only
        var activeInDegree = new int[passCount];
        for (int i = 0; i < passCount; i++)
        {
            if (!isActive[i])
                continue;

            foreach (var v in dependents[i])
            {
                if (isActive[v])
                {
                    activeInDegree[v]++;
                }
            }
        }

        // Topological Sort (Kahn's Algorithm) over active passes
        var readyQueue = new Queue<int>();
        for (int i = 0; i < passCount; i++)
        {
            if (isActive[i] && activeInDegree[i] == 0)
            {
                readyQueue.Enqueue(i);
            }
        }

        while (readyQueue.Count > 0)
        {
            var u = readyQueue.Dequeue();
            _compiledPasses.Add(_passes[u]);

            foreach (var v in dependents[u])
            {
                if (isActive[v])
                {
                    activeInDegree[v]--;
                    if (activeInDegree[v] == 0)
                    {
                        readyQueue.Enqueue(v);
                    }
                }
            }
        }

        _isCompiled = true;
    }

    /// <summary>
    /// Executes all compiled render passes in DAG order, automatically inserting sync2 barriers and managing Dynamic Rendering.
    /// </summary>
    public unsafe void Execute(CommandBuffer cmd, GpuTimer? gpuTimer = null)
    {
        if (!_isCompiled)
        {
            Compile();
        }

        _passContext.CommandBuffer = cmd;

        for (int p = 0; p < _compiledPasses.Count; p++)
        {
            var pass = _compiledPasses[p];
            if (pass.IsCulled)
                continue;

            // 1. Build and emit required synchronization2 barriers for this pass
            _barrierBuilder.Clear();
            foreach (var decl in pass.ResourceDeclarations)
            {
                if (!decl.TextureHandle.IsValid)
                    continue;

                var res = GetTextureResource(decl.TextureHandle);
                var layoutMismatch = decl.ExpectedLayout != VkImageLayout.Undefined && res.CurrentLayout != decl.ExpectedLayout;
                var stageMismatch = (res.CurrentAccess & decl.Access) != decl.Access || (res.CurrentStage & decl.Stage) != decl.Stage;

                if (layoutMismatch || stageMismatch)
                {
                    _barrierBuilder.AddImageBarrier(
                        res.Image,
                        res.Aspect,
                        res.CurrentLayout,
                        decl.ExpectedLayout,
                        res.CurrentStage == VkPipelineStageFlags2.None ? VkPipelineStageFlags2.TopOfPipeBit : res.CurrentStage,
                        res.CurrentAccess,
                        decl.Stage,
                        decl.Access
                    );

                    res.CurrentLayout = decl.ExpectedLayout;
                    res.CurrentStage = decl.Stage;
                    res.CurrentAccess = decl.Access;
                }
            }

            if (_barrierBuilder.HasBarriers)
            {
                _barrierBuilder.Execute(cmd);
            }

            // 2. Begin GPU timer query
            gpuTimer?.BeginPass(cmd, pass.Name);

            // 3. Execute pass
            if (pass.IsDynamicRenderingPass)
            {
                uint renderWidth = 0;
                uint renderHeight = 0;

                // Configure color attachments
                var colorCount = pass.ColorAttachments.Count;
                for (int c = 0; c < colorCount && c < _colorAttachmentBuffers.Length; c++)
                {
                    var att = pass.ColorAttachments[c];
                    var res = GetTextureResource(att.Handle);
                    renderWidth = res.Width;
                    renderHeight = res.Height;

                    _colorAttachmentBuffers[c] = new VkRenderingAttachmentInfo
                    {
                        ImageView = res.ImageView,
                        ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                        LoadOp = att.LoadOp,
                        StoreOp = att.StoreOp,
                        ClearValue = att.ClearValue,
                    };
                }

                // Configure depth attachment
                VkRenderingAttachmentInfo depthAttInfo = default;
                if (pass.DepthAttachment.HasAttachment)
                {
                    var res = GetTextureResource(pass.DepthAttachment.Handle);
                    if (renderWidth == 0)
                    {
                        renderWidth = res.Width;
                        renderHeight = res.Height;
                    }

                    depthAttInfo = new VkRenderingAttachmentInfo
                    {
                        ImageView = res.ImageView,
                        ImageLayout = VkImageLayout.DepthAttachmentOptimal,
                        LoadOp = pass.DepthAttachment.LoadOp,
                        StoreOp = pass.DepthAttachment.StoreOp,
                        ClearValue = new VkClearDepthStencilValue { Depth = pass.DepthAttachment.ClearDepth, Stencil = 0 },
                    };
                }

                _passContext.RenderArea = new VkExtent2D((int)renderWidth, (int)renderHeight);

                fixed (VkRenderingAttachmentInfo* pColors = _colorAttachmentBuffers)
                {
                    var renderingInfo = new VkRenderingInfo
                    {
                        RenderArea = new VkRect2D(new VkExtent2D((int)renderWidth, (int)renderHeight)),
                        LayerCount = 1,
                        ColorAttachmentCount = colorCount,
                        ColorAttachments = colorCount > 0 ? pColors : null,
                        DepthAttachment = pass.DepthAttachment.HasAttachment ? &depthAttInfo : null,
                    };

                    cmd.BeginRendering(in renderingInfo);
                    pass.ExecuteCallback?.Invoke(_passContext);
                    cmd.EndRendering();
                }
            }
            else
            {
                pass.ExecuteCallback?.Invoke(_passContext);
            }

            // 4. End GPU timer query
            gpuTimer?.EndPass(cmd, pass.Name);
        }
    }

    /// <summary>
    /// Resets graph passes and imported resource handles for the next frame.
    /// Retains internal capacity for zero GC allocation during frame execution.
    /// </summary>
    public void Reset()
    {
        _textures.Clear();
        _buffers.Clear();
        _passes.Clear();
        _compiledPasses.Clear();
        _barrierBuilder.Clear();
        _isCompiled = false;
    }

    internal VkImage GetNativeImage(RgTextureHandle handle) => GetTextureResource(handle).Image;
    internal VkImageView GetNativeImageView(RgTextureHandle handle) => GetTextureResource(handle).ImageView;
    internal uint GetTextureWidth(RgTextureHandle handle) => GetTextureResource(handle).Width;
    internal uint GetTextureHeight(RgTextureHandle handle) => GetTextureResource(handle).Height;

    private RgTextureResource GetTextureResource(RgTextureHandle handle)
    {
        var index = (int)handle.Id - 1;
        if (index < 0 || index >= _textures.Count)
            throw new ArgumentOutOfRangeException(nameof(handle), $"Invalid texture handle: {handle}");
        return _textures[index];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Reset();
        _disposed = true;
    }
}
