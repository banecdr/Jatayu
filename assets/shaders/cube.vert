/* =================================================================================================
 * FILE: cube.vert
 * LOCATION: assets/shaders/cube.vert
 * 
 * WHAT THIS SHADER DOES:
 *   3D Vertex shader for rendering 3D meshes (such as our 3D colored cube) in Vulkan 1.3.
 *   Receives vertex attributes (Position, Color, Normal, TexCoord) from the GPU vertex buffer,
 *   transforms the 3D position by a 64-byte Model-View-Projection (MVP) matrix supplied via
 *   a high-speed Vulkan Push Constant, and forwards interpolated attributes to the fragment shader.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Vertex Inputs:
 *      - location = 0: vec3 inPosition (3D local object-space coordinates)
 *      - location = 1: vec3 inColor    (Per-vertex RGB color tint)
 *      - location = 2: vec3 inNormal   (Surface normal vector for directional lighting)
 *      - location = 3: vec2 inTexCoord (UV texture mapping coordinates)
 *
 *   2. Push Constant:
 *      - layout(push_constant) uniform PushConstants { mat4 uMvp; };
 *      Push constants are the fastest uniform mechanism in Vulkan. They bypass descriptor sets
 *      and are embedded directly into the GPU command stream (up to 128 bytes guaranteed by spec).
 *      A single mat4 requires exactly 16 floats * 4 bytes = 64 bytes.
 *
 *   3. Position Transformation:
 *      gl_Position = uMvp * vec4(inPosition, 1.0);
 *      Transforms local 3D vertex coordinates into Vulkan Clip Space for hardware rasterization.
 * ================================================================================================= */

#version 450

// Vertex Buffer Attributes
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inTexCoord;

// Outputs to Fragment Shader
layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec2 fragTexCoord;

// 64-byte Model-View-Projection Matrix via Vulkan Push Constants
layout(push_constant) uniform PushConstants {
    mat4 uMvp;
};

void main() {
    gl_Position = uMvp * vec4(inPosition, 1.0);
    fragColor = inColor;
    fragNormal = inNormal;
    fragTexCoord = inTexCoord;
}
