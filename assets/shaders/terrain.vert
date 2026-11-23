#version 450
/* =================================================================================================
 * FILE: terrain.vert
 * LOCATION: assets/shaders/terrain.vert
 * 
 * WHAT THIS FILE DOES:
 *   Continuous GPU Clipmap Terrain Vertex Shader.
 *   - Transforms clipmap concentric ring vertices centered around the camera.
 *   - Evaluates multi-frequency procedural elevation and central-difference normals.
 *   - Outputs camera-relative world positions and normal vectors to the fragment shader.
 * ================================================================================================= */

layout(location = 0) in vec3 inPosition; // Grid mesh position
layout(location = 1) in vec3 inNormal;   // Vertex normal
layout(location = 2) in vec2 inTexCoord; // Grid UV

layout(push_constant) uniform TerrainPushConstants
{
    mat4 ViewProjMatrix;
    vec4 CameraRelativeOrigin; // xyz = camera world position, w = geomorph factor
    vec4 TerrainParams;        // x = ring scale, y = debug ring colors flag, z = max height, w = ring index
} pc;

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec2 outTexCoord;
layout(location = 3) out float outMorphFactor;
layout(location = 4) out float outRingIndex;

float evaluateElevation(vec2 worldPos)
{
    float maxElev = 45.0;
    float h1 = sin(worldPos.x * 0.005) * cos(worldPos.y * 0.005) * maxElev;
    float h2 = sin(worldPos.x * 0.015 + 1.2) * sin(worldPos.y * 0.015 + 0.8) * (maxElev * 0.35);
    float h3 = cos(worldPos.x * 0.04 - 0.5) * sin(worldPos.y * 0.04 + 1.7) * (maxElev * 0.1);
    return h1 + h2 + h3;
}

vec3 evaluateNormal(vec2 worldPos)
{
    float eps = 1.0;
    float hL = evaluateElevation(worldPos - vec2(eps, 0.0));
    float hR = evaluateElevation(worldPos + vec2(eps, 0.0));
    float hD = evaluateElevation(worldPos - vec2(0.0, eps));
    float hU = evaluateElevation(worldPos + vec2(0.0, eps));
    return normalize(vec3(hL - hR, 2.0 * eps, hD - hU));
}

void main()
{
    float ringScale = pc.TerrainParams.x;
    float morphFactor = pc.CameraRelativeOrigin.w;
    float ringIndex = pc.TerrainParams.w;

    vec3 localPos = inPosition * ringScale;

    // Calculate absolute world coordinates on XZ plane
    vec2 worldXZ = localPos.xz + pc.CameraRelativeOrigin.xz;
    float elevation = evaluateElevation(worldXZ);

    // Camera-relative position (Y relative to camera Y)
    vec3 relWorldPos = vec3(localPos.x, elevation - pc.CameraRelativeOrigin.y, localPos.z);

    outWorldPos = relWorldPos;
    outNormal = evaluateNormal(worldXZ);
    outTexCoord = inTexCoord;
    outMorphFactor = morphFactor;
    outRingIndex = ringIndex;

    gl_Position = pc.ViewProjMatrix * vec4(relWorldPos, 1.0);
}
