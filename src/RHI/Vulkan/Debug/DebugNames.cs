/* =================================================================================================
 * FILE: DebugNames.cs
 * LOCATION: src/RHI/Vulkan/Debug/DebugNames.cs
 * 
 * WHAT THIS FILE DOES:
 *   Wraps VK_EXT_debug_utils to assign readable human names and colors to Vulkan buffers, images,
 *   pipelines, queues, and command buffer regions for RenderDoc and GPU profilers.
 *   Safely falls back to no-op when the extension is not present or disabled.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Jatayu.RHI.Vulkan.Context;

namespace Jatayu.RHI.Vulkan.Debug;

/// <summary>
/// Helper for labeling Vulkan resources and command regions with VK_EXT_debug_utils.
/// </summary>
public static class DebugNames
{
    /// <summary>
    /// Assigns a human-readable name to any Vulkan handle (buffer, image, view, pipeline).
    /// </summary>
    public static unsafe void SetObjectName(
        VulkanContext context,
        ulong objectHandle,
        VkObjectType objectType,
        string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (objectHandle == 0 || string.IsNullOrEmpty(name)) return;

        var pfn = context.Device.PInvoke.vkSetDebugUtilsObjectNameEXT;
        if (Unsafe.As<VkSetDebugUtilsObjectNameEXT, nint>(ref pfn) == 0) return;

        var nameBytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* pName = nameBytes)
        {
            var info = new VkDebugUtilsObjectNameInfoEXT
            {
                ObjectType = objectType,
                ObjectHandle = (nint)objectHandle,
                ObjectName = pName
            };

            pfn.Invoke(context.Device.Handle, &info);
        }
    }

    /// <summary>
    /// Begins a colored debug region label in a command buffer for RenderDoc.
    /// </summary>
    public static unsafe void BeginLabel(
        VulkanContext context,
        CommandBuffer cmd,
        string name,
        Vector4 color = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(name)) return;

        var pfn = context.Device.PInvoke.vkCmdBeginDebugUtilsLabelEXT;
        if (Unsafe.As<VkCmdBeginDebugUtilsLabelEXT, nint>(ref pfn) == 0) return;

        var nameBytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* pName = nameBytes)
        {
            var label = new VkDebugUtilsLabelEXT
            {
                LabelName = pName,
                Color = new float4(color.X, color.Y, color.Z, color.W == 0 ? 1.0f : color.W)
            };

            pfn.Invoke(cmd.Handle, &label);
        }
    }

    /// <summary>
    /// Ends the current command buffer debug region.
    /// </summary>
    public static unsafe void EndLabel(VulkanContext context, CommandBuffer cmd)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pfn = context.Device.PInvoke.vkCmdEndDebugUtilsLabelEXT;
        if (Unsafe.As<VkCmdEndDebugUtilsLabelEXT, nint>(ref pfn) == 0) return;

        pfn.Invoke(cmd.Handle);
    }
}
