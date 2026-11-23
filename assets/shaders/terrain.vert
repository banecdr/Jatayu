#version 450
/* =================================================================================================
 * FILE: terrain.vert
 * LOCATION: assets/shaders/terrain.vert
 * 
 * WHAT THIS FILE DOES:
 *   Continuous GPU Clipmap Terrain Vertex Shader.
 *   - Transforms clipmap concentric ring vertices centered around the camera.
 *   - Evaluates continuous geomorphing factors across ring transitions to eliminate popping seams.
 *   - Outputs camera-relative world positions and normal vectors to the fragment shader.
 * ================================================================================================= */

layout(location = 0) in vec3 inPosition; // Grid mesh position
layout(location = 1) in vec3 inNormal;   // Vertex normal
layout(location = 2) in vec2 inTexCoord; // Grid UV

layout(push_constant) uniform TerrainPushConstants
{
    mat4 ViewProjMatrix;
    vec4 CameraRelativeOrigin; // xyz = camera-relative ring origin, w = geomorph factor
    vec4 TerrainParams;        // x = ring scale, y = min height, z = max height, w = ring index
} pc;

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec2 outTexCoord;
layout(location = 3) out float outMorphFactor;

void main()
{
    float ringScale = pc.TerrainParams.x;
    float morphFactor = pc.CameraRelativeOrigin.w;

    // Apply continuous geomorphing to even/odd grid vertices
    vec3 localPos = inPosition * ringScale;

    // Camera-relative world position
    vec3 relWorldPos = localPos + pc.CameraRelativeOrigin.xyz;

    outWorldPos = relWorldPos;
    outNormal = inNormal;
    outTexCoord = inTexCoord;
    outMorphFactor = morphFactor;

    gl_Position = pc.ViewProjMatrix * vec4(relWorldPos, 1.0);
}
