/* =================================================================================================
 * FILE: VramBudget.cs
 * LOCATION: src/RHI/Vulkan/Memory/VramBudget.cs
 * 
 * WHAT THIS FILE DOES:
 *   Queries real-time GPU physical memory budget and usage via VK_EXT_memory_budget.
 *   Provides telemetry for texture mip streaming, VRAM pressure bars, and allocation limits.
 * ================================================================================================= */

using System;
using Brutal.VulkanApi;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Memory;

/// <summary>
/// Telemetry record describing GPU physical memory budget and consumption.
/// </summary>
public readonly struct VramStats
{
    /// <summary>
    /// Total VRAM allocated by this process across all heaps in bytes.
    /// </summary>
    public ulong TotalAllocatedBytes { get; }

    /// <summary>
    /// Total budget available to this process from the OS / driver in bytes.
    /// </summary>
    public ulong TotalBudgetBytes { get; }

    /// <summary>
    /// Total physical device-local VRAM capacity on the GPU in bytes.
    /// </summary>
    public ulong TotalDeviceLocalCapacityBytes { get; }

    /// <summary>
    /// Percentage of budget currently consumed (0.0 to 1.0).
    /// </summary>
    public float BudgetUsagePercent => TotalBudgetBytes > 0 ? (float)TotalAllocatedBytes / TotalBudgetBytes : 0f;

    public VramStats(ulong totalAllocated, ulong totalBudget, ulong totalDeviceLocal)
    {
        TotalAllocatedBytes = totalAllocated;
        TotalBudgetBytes = totalBudget;
        TotalDeviceLocalCapacityBytes = totalDeviceLocal;
    }
}

/// <summary>
/// Monitors physical VRAM usage and streaming budget limits.
/// </summary>
public sealed class VramBudget
{
    private readonly VulkanContext _context;

    private VramStats _cachedStats;
    private long _lastQueryTimestamp;
    private static readonly long QueryIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 10; // 100ms throttle (10Hz)

    public VramBudget(VulkanContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Queries the current VRAM budget and usage stats from the Vulkan driver.
    /// Throttled to 10 Hz to prevent driver syscall stalls on high frame rate loops.
    /// </summary>
    public unsafe VramStats QueryStats()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastQueryTimestamp != 0 && now - _lastQueryTimestamp < QueryIntervalTicks)
        {
            return _cachedStats;
        }
        _lastQueryTimestamp = now;

        var budgetProps = new VkPhysicalDeviceMemoryBudgetPropertiesEXT();
        var memProps2 = new VkPhysicalDeviceMemoryProperties2
        {
            Next = &budgetProps
        };

        _context.Instance.PInvoke.vkGetPhysicalDeviceMemoryProperties2.Invoke(
            _context.PhysicalDevice.Handle,
            &memProps2
        );

        ulong totalUsage = 0;
        ulong totalBudget = 0;
        ulong totalDeviceLocal = 0;

        var memProps = memProps2.MemoryProperties;
        var heapCount = memProps.MemoryHeapCount;

        for (uint i = 0; i < heapCount; i++)
        {
            var heap = memProps.MemoryHeaps[(int)i];
            bool isDeviceLocal = (heap.Flags & VkMemoryHeapFlags.DeviceLocalBit) != 0;

            if (isDeviceLocal)
            {
                ulong heapSize = heap.Size.Value;
                ulong heapUsage = budgetProps.HeapUsage[(int)i].Value;
                ulong heapBudget = budgetProps.HeapBudget[(int)i].Value;

                totalDeviceLocal += heapSize;
                totalUsage += heapUsage;

                // Driver may report VK_WHOLE_SIZE (~0ULL) or 0 if unconstrained
                if (heapBudget == 0 || heapBudget > heapSize)
                {
                    heapBudget = heapSize;
                }
                totalBudget += heapBudget;
            }
        }

        // Fallback if budget extension returned zero or exceeded physical device memory
        if (totalBudget == 0 || totalBudget > totalDeviceLocal)
            totalBudget = totalDeviceLocal;

        _cachedStats = new VramStats(totalUsage, totalBudget, totalDeviceLocal);
        return _cachedStats;
    }
}
