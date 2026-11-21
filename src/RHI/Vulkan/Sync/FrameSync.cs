/* =================================================================================================
 * FILE: FrameSync.cs
 * LOCATION: src/RHI/Vulkan/Sync/FrameSync.cs
 * 
 * WHAT THIS FILE DOES:
 *   Manages multi-frame synchronization primitives (Frames-in-Flight) in Vulkan.
 *   In a high-performance engine, we never want the CPU to halt and wait for the GPU to finish
 *   drawing before starting the next frame. FrameSync enables true CPU–GPU concurrency:
 *   while the GPU is rendering Frame 0 on the graphics card, the CPU is already recording
 *   and preparing instructions for Frame 1 in parallel!
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Frames-in-Flight Ring Buffer:
 *      Maintains parallel sets of semaphores and fences (typically 2 for double-buffered overlap,
 *      or 3 for triple-buffering). Tracks a CurrentFrame index (0, 1, 0, 1...).
 *
 *   2. Synchronization Roles:
 *      - ImageAvailableSemaphores (GPU-to-GPU):
 *        Signaled by the presentation engine when a swapchain image is ready for drawing.
 *      - RenderFinishedSemaphores (GPU-to-GPU):
 *        Signaled by the graphics queue when drawing commands for this frame complete.
 *      - InFlightFences (GPU-to-CPU):
 *        Controls CPU pacing. The CPU waits on this fence before recording commands into this
 *        slot's command buffer, ensuring the GPU has completely finished with this slot's resources.
 *
 *   3. Signaled Initial State:
 *      All fences are created with 'VkFenceCreateFlags.SignaledBit' so the first iteration of the
 *      engine loop does not block on frame 0.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Context: The parent VulkanContext providing GPU device handles.
 *   - MaxFramesInFlight: Number of frames that can be processed simultaneously (usually 2).
 *   - CurrentFrame: The index of the frame slot currently being recorded (0 to MaxFramesInFlight - 1).
 *   - CurrentImageAvailable: Semaphore signaled when the swapchain image is ready for this frame.
 *   - CurrentRenderFinished: Semaphore signaled when GPU rendering finishes for this frame.
 *   - CurrentFence: Fence that blocks the CPU until the previous GPU work in this slot finishes.
 *   - WaitForFrameFence: Halts the CPU until the GPU finishes processing this frame slot.
 *   - ResetFrameFence: Resets the fence to unsignaled state before submitting new work.
 *   - AdvanceFrame: Steps to the next frame slot (CurrentFrame = (CurrentFrame + 1) % MaxFramesInFlight).
 *
 * USAGE EXAMPLE:
 *   // 1. Initialize FrameSync for 2 frames in flight
 *   using var frameSync = new FrameSync(context, maxFramesInFlight: 2);
 *
 *   // 2. In master render loop:
 *   frameSync.WaitForFrameFence();
 *   frameSync.ResetFrameFence();
 *
 *   // 3. Acquire next image
 *   swapchain.AcquireNextImage(frameSync.CurrentImageAvailable, out uint imageIndex);
 *
 *   // 4. Record and submit commands using current semaphores & fence
 *   queue.Submit(
 *       [frameSync.CurrentImageAvailable],
 *       [VkPipelineStageFlags.ColorAttachmentOutputBit],
 *       [commandBuffers[frameSync.CurrentFrame]],
 *       [frameSync.CurrentRenderFinished],
 *       frameSync.CurrentFence
 *   );
 *
 *   // 5. Present and advance to next frame
 *   swapchain.Present(presentQueue, frameSync.CurrentRenderFinished, imageIndex);
 *   frameSync.AdvanceFrame();
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HelloBrutal.RHI.Vulkan.Context;

namespace HelloBrutal.RHI.Vulkan.Sync;

/// <summary>
/// Manages multi-frame synchronization primitives (Semaphores and Fences) for concurrent CPU/GPU execution.
/// </summary>
public sealed class FrameSync : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Gets the parent VulkanContext.
    /// </summary>
    public VulkanContext Context { get; }

    /// <summary>
    /// Gets the maximum number of frames that can be concurrently processed by CPU and GPU.
    /// </summary>
    public int MaxFramesInFlight { get; }

    /// <summary>
    /// Gets the active frame slot index (0 to MaxFramesInFlight - 1).
    /// </summary>
    public int CurrentFrame { get; private set; }

    /// <summary>
    /// Array of semaphores signaled when a swapchain image is available for drawing.
    /// </summary>
    public VkSemaphore[] ImageAvailableSemaphores { get; }

    /// <summary>
    /// Array of semaphores signaled when GPU rendering finishes.
    /// </summary>
    public VkSemaphore[] RenderFinishedSemaphores { get; }

    /// <summary>
    /// Array of fences used by the CPU to wait for the GPU to finish with this frame slot.
    /// </summary>
    public VkFence[] InFlightFences { get; }

    /// <summary>
    /// Gets the image available semaphore for the current frame slot.
    /// </summary>
    public VkSemaphore CurrentImageAvailable => ImageAvailableSemaphores[CurrentFrame];

    /// <summary>
    /// Gets the render finished semaphore for the current frame slot.
    /// </summary>
    public VkSemaphore CurrentRenderFinished => RenderFinishedSemaphores[CurrentFrame];

    /// <summary>
    /// Gets the in-flight fence for the current frame slot.
    /// </summary>
    public VkFence CurrentFence => InFlightFences[CurrentFrame];

    /// <summary>
    /// Initializes a new instance of the FrameSync manager.
    /// </summary>
    /// <param name="context">The active VulkanContext.</param>
    /// <param name="maxFramesInFlight">Number of frames in flight (defaults to 2 for double-buffered overlap).</param>
    public FrameSync(VulkanContext context, int maxFramesInFlight = 2)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        if (maxFramesInFlight < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFramesInFlight), "Must have at least 1 frame in flight.");

        MaxFramesInFlight = maxFramesInFlight;

        ImageAvailableSemaphores = new VkSemaphore[maxFramesInFlight];
        RenderFinishedSemaphores = new VkSemaphore[maxFramesInFlight];
        InFlightFences = new VkFence[maxFramesInFlight];

        var device = Context.Device;

        for (var i = 0; i < maxFramesInFlight; i++)
        {
            ImageAvailableSemaphores[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            RenderFinishedSemaphores[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);

            // Create fence initially signaled so frame 0 does not block
            InFlightFences[i] = device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
        }
    }

    /// <summary>
    /// Halts the CPU until the GPU finishes processing previous work associated with the current frame slot.
    /// </summary>
    /// <param name="timeoutNanoseconds">Timeout in nanoseconds (defaults to -1 for infinite wait).</param>
    public void WaitForFrameFence(nint timeoutNanoseconds = -1)
    {
        Context.Device.WaitForFence(CurrentFence, timeoutNanoseconds);
    }

    /// <summary>
    /// Resets the current frame's fence to an unsignaled state before submitting new commands.
    /// </summary>
    public void ResetFrameFence()
    {
        Context.Device.ResetFences([CurrentFence]);
    }

    /// <summary>
    /// Advances the frame slot index to the next slot in the ring buffer.
    /// Call this once at the end of each rendered frame.
    /// </summary>
    public void AdvanceFrame()
    {
        CurrentFrame = (CurrentFrame + 1) % MaxFramesInFlight;
    }

    /// <summary>
    /// Destroys all semaphores and fences.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            Context.WaitIdle();

            var device = Context.Device;

            for (var i = 0; i < MaxFramesInFlight; i++)
            {
                if (ImageAvailableSemaphores[i].VkHandle != 0)
                    device.DestroySemaphore(ImageAvailableSemaphores[i], null);

                if (RenderFinishedSemaphores[i].VkHandle != 0)
                    device.DestroySemaphore(RenderFinishedSemaphores[i], null);

                if (InFlightFences[i].VkHandle != 0)
                    device.DestroyFence(InFlightFences[i], null);
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
