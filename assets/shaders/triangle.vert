/* =================================================================================================
 * FILE: triangle.vert
 * LOCATION: assets/shaders/triangle.vert
 * 
 * WHAT THIS SHADER DOES:
 *   This is the Vulkan Vertex Shader for rendering a 2D colored triangle on screen.
 *   It runs once per vertex (3 times per triangle) on the GPU's parallel vertex processing cores.
 *   It transforms input 2D coordinates into Normalized Device Coordinates (NDC) clip-space 
 *   and passes the vertex color down to the rasterizer for smooth interpolation.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Vertex Input Streams (From GpuBuffer):
 *      - Location 0 (position): Receives 2D X and Y coordinates (e.g. 0.0, -0.6).
 *      - Location 1 (inColor):  Receives 3D RGB color channels (e.g. 1.0, 0.2, 0.2).
 *
 *   2. Coordinate Transformation (gl_Position):
 *      In Vulkan, clip space coordinates range from -1.0 to +1.0 for X (left to right) and 
 *      -1.0 to +1.0 for Y (top to bottom). Z ranges from 0.0 (near plane) to 1.0 (far plane).
 *      We output a 4D homogeneous vector vec4(position.x, position.y, 0.0, 1.0).
 *
 *   3. Color Passing (outColor):
 *      The output color variable 'outColor' is written to Location 0. The GPU hardware rasterizer 
 *      automatically performs perspective-correct barycentric interpolation across the face 
 *      of the triangle before sending pixels to the fragment shader.
 *
 * PARAMETERS & ATTRIBUTES (IN PLAIN ENGLISH):
 *   - in vec2 position: The 2D (X, Y) coordinate of the vertex.
 *   - in vec3 inColor:  The RGB color components of the vertex (0.0 to 1.0).
 *   - out vec3 outColor: The color passed to the fragment shader.
 *   - gl_Position: Built-in Vulkan output representing the vertex's final 4D clip-space position.
 * ================================================================================================= */

#version 450

// Vertex Attributes from CPU Vertex Buffer (matches struct Vertex in C#)
layout(location = 0) in vec2 position;
layout(location = 1) in vec3 inColor;

// Output to Fragment Shader
layout(location = 0) out vec3 outColor;

void main()
{
    // Pass 2D position directly to Vulkan clip space (Z = 0.0, W = 1.0)
    gl_Position = vec4(position, 0.0, 1.0);

    // Forward color to rasterizer for smooth gradient interpolation
    outColor = inColor;
}
