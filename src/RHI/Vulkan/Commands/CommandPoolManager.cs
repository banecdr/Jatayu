/* =================================================================================================
 * FILE: CommandPoolManager.cs
 * LOCATION: src/RHI/Vulkan/Commands/CommandPoolManager.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages Vulkan command pools (VkCommandPool) and allocates primary and secondary command
 *   buffers for render frames and worker threads. Provides immediate one-shot command recording
 *   and execution helpers for synchronous GPU uploads (Sprint 3a Decision D14).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Allocates a VkCommandPool bound to a specific queue family (e.g. Graphics, Transfer).
 *   2. Allocates primary and secondary CommandBuffer handles via device.AllocateCommandBuffer.
 *   3. Exposes ExecuteImmediate() helper using a one-time submit command buffer with a fence wait.
 *   4. Supports resetting pools per-frame (ResetCommandBufferBit / ResetCommandPool).
 *   5. Implements IDisposable for graceful teardown of pools and allocated buffers.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Commands;

/// <summary>
/// Manages a Vulkan command pool and allocates primary/secondary command buffers.
/// </summary>
public sealed class CommandPoolManager : IDisposable
{
    private readonly VulkanContext _context;
    private VkCommandPool _pool;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying native Vulkan command pool handle.
    /// </summary>
    public VkCommandPool Handle => _pool;

    /// <summary>
    /// Gets the queue family index associated with this command pool.
    /// </summary>
    public int QueueFamilyIndex { get; }

    /// <summary>
    /// Initializes a new CommandPoolManager for the specified queue family.
    /// </summary>
    /// <param name="context">The active VulkanContext.</param>
    /// <param name="queueFamilyIndex">Hardware queue family index.</param>
    /// <param name="flags">Command pool creation flags (defaults to ResetCommandBufferBit).</param>
    public unsafe CommandPoolManager(
        VulkanContext context,
        int queueFamilyIndex,
        VkCommandPoolCreateFlags flags = VkCommandPoolCreateFlags.ResetCommandBufferBit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        QueueFamilyIndex = queueFamilyIndex;

        var createInfo = new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = queueFamilyIndex,
            Flags = flags,
        };

        _pool = _context.Device.CreateCommandPool(in createInfo, null);
    }

    /// <summary>
    /// Allocates a single primary or secondary command buffer from this pool.
    /// </summary>
    public CommandBuffer Allocate(VkCommandBufferLevel level = VkCommandBufferLevel.Primary)
    {
        return _context.Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
        {
            CommandPool = _pool,
            Level = level,
        });
    }

    /// <summary>
    /// Allocates an array of command buffers from this pool.
    /// </summary>
    public CommandBuffer[] AllocateMultiple(int count, VkCommandBufferLevel level = VkCommandBufferLevel.Primary)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");

        var buffers = new CommandBuffer[count];
        for (int i = 0; i < count; i++)
        {
            buffers[i] = Allocate(level);
        }
        return buffers;
    }

    /// <summary>
    /// Begins recording a transient, one-time-submit command buffer.
    /// </summary>
    public CommandBuffer BeginSingleTime()
    {
        var cmd = Allocate(VkCommandBufferLevel.Primary);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        return cmd;
    }

    /// <summary>
    /// Ends recording, submits the command buffer to the specified queue, waits for GPU completion via fence, and frees the buffer.
    /// Essential for synchronous one-shot GPU uploads (Decision D14).
    /// </summary>
    public unsafe void EndSingleTime(CommandBuffer cmd, Queue queue)
    {
        cmd.End();

        var fence = _context.Device.CreateFence(new VkFenceCreateInfo(), null);
        try
        {
            queue.Submit([], [], [cmd], [], fence);
            _context.Device.WaitForFence(fence, -1);
        }
        finally
        {
            _context.Device.DestroyFence(fence, null);
            var handle = cmd.Handle;
            _context.Device.PInvoke.vkFreeCommandBuffers.Invoke(_context.Device.Handle, _pool, 1, &handle);
        }
    }

    /// <summary>
    /// Records and synchronously executes a one-shot command on the given queue.
    /// </summary>
    /// <param name="queue">Queue to submit work to.</param>
    /// <param name="action">Delegate recording commands into the transient command buffer.</param>
    public void ExecuteImmediate(Queue queue, Action<CommandBuffer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(queue);

        var cmd = BeginSingleTime();
        try
        {
            action(cmd);
        }
        finally
        {
            EndSingleTime(cmd, queue);
        }
    }

    /// <summary>
    /// Resets all command buffers allocated from this pool.
    /// </summary>
    public void Reset(VkCommandPoolResetFlags flags = VkCommandPoolResetFlags.None)
    {
        _context.Device.ResetCommandPool(_pool, flags);
    }

    /// <summary>
    /// Disposes the native Vulkan command pool and frees its allocated resources.
    /// </summary>
    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            if (_pool.VkHandle != 0)
            {
                _context.Device.DestroyCommandPool(_pool, null);
                _pool = default;
            }
            _disposed = true;
        }
    }
}
