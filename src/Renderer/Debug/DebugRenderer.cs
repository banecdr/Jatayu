// ==================================================================================================
// Jatayu Engine - Diagnostics & Tooling
// Module: DebugRenderer.cs
// Role: Immediate-mode 3D wireframe debug renderer for lines, boxes, spheres, and bounds visualization.
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Jatayu.Renderer.Camera;
using Jatayu.RHI.Vulkan.Context;
using Jatayu.RHI.Vulkan.Memory;
using Jatayu.RHI.Vulkan.Pipelines;

namespace Jatayu.Renderer.Debug;

/// <summary>
/// Vertex structure for colored 3D debug lines.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 28)]
public struct DebugLineVertex
{
    public Vector3 Position;
    public Vector4 Color;

    public DebugLineVertex(Vector3 position, Vector4 color)
    {
        Position = position;
        Color = color;
    }
}

/// <summary>
/// Immediate-mode wireframe renderer for visualizing physics colliders, streaming boundaries, and light frustums.
/// </summary>
public sealed class DebugRenderer : IDisposable
{
    public const int MaxVertices = 65536;

    private readonly VulkanContext _context;
    private readonly GpuAllocator _allocator;

    private readonly DebugLineVertex[] _cpuVertices = new DebugLineVertex[MaxVertices];
    private GpuBuffer _vertexBuffer;
    private int _vertexCount;
    private bool _disposed;

    public int VertexCount => _vertexCount;

    public DebugRenderer(VulkanContext context, GpuAllocator allocator)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

        ulong bufferSize = (ulong)(Marshal.SizeOf<DebugLineVertex>() * MaxVertices);
        _vertexBuffer = _allocator.CreateBuffer(
            bufferSize,
            VkBufferUsageFlags.VertexBufferBit,
            VmaMemoryUsage.CpuToGpu
        );
    }

    /// <summary>
    /// Draws a 3D line segment between two camera-relative points.
    /// </summary>
    public void DrawLine(in Vector3 start, in Vector3 end, in Vector4 color)
    {
        if (_vertexCount + 2 > MaxVertices) return;

        _cpuVertices[_vertexCount++] = new DebugLineVertex(start, color);
        _cpuVertices[_vertexCount++] = new DebugLineVertex(end, color);
    }

    /// <summary>
    /// Draws a wireframe axis-aligned bounding box (AABB).
    /// </summary>
    public void DrawBox(in Vector3 min, in Vector3 max, in Vector4 color)
    {
        Vector3 p0 = new(min.X, min.Y, min.Z);
        Vector3 p1 = new(max.X, min.Y, min.Z);
        Vector3 p2 = new(max.X, max.Y, min.Z);
        Vector3 p3 = new(min.X, max.Y, min.Z);
        Vector3 p4 = new(min.X, min.Y, max.Z);
        Vector3 p5 = new(max.X, min.Y, max.Z);
        Vector3 p6 = new(max.X, max.Y, max.Z);
        Vector3 p7 = new(min.X, max.Y, max.Z);

        // Bottom face
        DrawLine(p0, p1, color);
        DrawLine(p1, p2, color);
        DrawLine(p2, p3, color);
        DrawLine(p3, p0, color);

        // Top face
        DrawLine(p4, p5, color);
        DrawLine(p5, p6, color);
        DrawLine(p6, p7, color);
        DrawLine(p7, p4, color);

        // Vertical pillars
        DrawLine(p0, p4, color);
        DrawLine(p1, p5, color);
        DrawLine(p2, p6, color);
        DrawLine(p3, p7, color);
    }

    /// <summary>
    /// Draws a wireframe sphere on the 3 principal axes (XY, XZ, YZ).
    /// </summary>
    public void DrawSphere(in Vector3 center, float radius, in Vector4 color, int segments = 24)
    {
        float angleStep = MathF.PI * 2.0f / segments;

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * angleStep;
            float a1 = (i + 1) * angleStep;

            float cos0 = MathF.Cos(a0) * radius;
            float sin0 = MathF.Sin(a0) * radius;
            float cos1 = MathF.Cos(a1) * radius;
            float sin1 = MathF.Sin(a1) * radius;

            // XZ circle
            DrawLine(center + new Vector3(cos0, 0, sin0), center + new Vector3(cos1, 0, sin1), color);

            // XY circle
            DrawLine(center + new Vector3(cos0, sin0, 0), center + new Vector3(cos1, sin1, 0), color);

            // YZ circle
            DrawLine(center + new Vector3(0, sin0, cos0), center + new Vector3(0, sin1, cos1), color);
        }
    }

    /// <summary>
    /// Flushes accumulated debug line vertices into the mapped GPU buffer.
    /// </summary>
    public void Flush(out ulong vertexCount, out GpuBuffer buffer)
    {
        vertexCount = (ulong)_vertexCount;
        buffer = _vertexBuffer;

        if (_vertexCount == 0) return;

        _vertexBuffer.SetData<DebugLineVertex>(_cpuVertices.AsSpan(0, _vertexCount));
    }

    /// <summary>
    /// Clears accumulated lines for the next frame.
    /// </summary>
    public void Clear()
    {
        _vertexCount = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _vertexBuffer.Dispose();
            _disposed = true;
        }
    }
}
