/* =================================================================================================
 * FILE: Vertex3D.cs
 * LOCATION: src/Renderer/Mesh/Vertex3D.cs
 * 
 * WHAT THIS FILE DOES:
 *   Defines the standard 3D vertex data structure used across the Jatayu 3D engine.
 *   Each vertex stores 3D position (X, Y, Z), vertex color (R, G, B), surface normal (X, Y, Z)
 *   for lighting calculations, and UV texture coordinates (U, V).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Interleaved Memory Layout:
 *      All vertex attributes are stored consecutively in a single struct. This is the most GPU cache-
 *      friendly format (interleaved vertex buffer), maximizing memory bandwidth during rasterization.
 *
 *   2. Automatic Vulkan Pipeline Reflection:
 *      BRUTAL's VertexInput.AddBindingWithAttributes<Vertex3D>() automatically detects these fields
 *      and binds them to GLSL shader input locations:
 *        - Location 0: vec3 inPosition (offset 0, format R32G32B32_SFLOAT)
 *        - Location 1: vec3 inColor    (offset 12, format R32G32B32_SFLOAT)
 *        - Location 2: vec3 inNormal   (offset 24, format R32G32B32_SFLOAT)
 *        - Location 3: vec2 inTexCoord (offset 36, format R32G32_SFLOAT)
 *      Total vertex stride = 44 bytes.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Position: 3D spatial coordinate in local object space.
 *   - Color: Per-vertex RGB tint (range 0.0 to 1.0).
 *   - Normal: Normalized 3D direction pointing perpendicularly outward from the surface.
 *   - TexCoord: 2D texture coordinates for UV mapping (range 0.0 to 1.0).
 *
 * USAGE EXAMPLES:
 *   var vertex = new Vertex3D(
 *       position: new float3(0f, 1f, 0f),
 *       color:    new float3(1f, 0f, 0f),
 *       normal:   new float3(0f, 1f, 0f),
 *       texCoord: new float2(0.5f, 1f)
 *   );
 * ================================================================================================= */

using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Jatayu.Renderer.Mesh;

/// <summary>
/// Standard 3D vertex struct containing Position, Color, Normal, and Texture Coordinates.
/// Interleaved layout mapped directly to GPU vertex buffer streams.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex3D
{
    /// <summary>
    /// 3D position in model space (X, Y, Z). Matches GLSL location = 0.
    /// </summary>
    public float3 Position;

    /// <summary>
    /// Per-vertex RGB color tint. Matches GLSL location = 1.
    /// </summary>
    public float3 Color;

    /// <summary>
    /// Surface normal vector for diffuse/specular lighting. Matches GLSL location = 2.
    /// </summary>
    public float3 Normal;

    /// <summary>
    /// 2D UV texture mapping coordinates. Matches GLSL location = 3.
    /// </summary>
    public float2 TexCoord;

    /// <summary>
    /// Initializes a new 3D vertex with all attributes.
    /// </summary>
    public Vertex3D(float3 position, float3 color, float3 normal, float2 texCoord)
    {
        Position = position;
        Color = color;
        Normal = normal;
        TexCoord = texCoord;
    }

    /// <summary>
    /// Initializes a 3D vertex with position and color, defaulting normal to Up and UVs to zero.
    /// </summary>
    public Vertex3D(float3 position, float3 color)
        : this(position, color, new float3(0f, 1f, 0f), new float2(0f, 0f))
    {
    }
}
