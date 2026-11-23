#version 460 core

// ==================================================================================================
// Jatayu Engine - Procedural Foliage Rendering
// Shader: foliage.vert
// Role: Renders GPU-instanced vegetation with procedural wind vertex oscillation and distance fade.
// ==================================================================================================

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in vec4 inColor; // r = flexibility/wind weight [0=root, 1=tip]

struct FoliageInstance {
    vec3 PositionOffset;
    uint PackedData; // byte rotY, byte scale, ushort tintAndPhase
};

layout(std430, set = 0, binding = 0) readonly buffer FoliageInstanceBuffer {
    FoliageInstance instances[];
};

layout(push_constant) uniform FoliagePushConstants {
    mat4 ViewProj;
    vec4 WindParams;         // X=WindDir.x, Y=WindDir.y, Z=Speed, W=Strength
    vec4 CameraRelOrigin;    // Camera-relative origin of the cell chunk
    float Time;
    float MaxDistance;
    float FadeRange;
    float Padding;
} pc;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vTexCoord;
layout(location = 3) out vec4 vColor;
layout(location = 4) out float vFadeAlpha;

const float PI = 3.14159265359;

void main() {
    FoliageInstance inst = instances[gl_InstanceIndex];

    // Unpack instance transform
    uint rotByte = inst.PackedData & 0xFFu;
    uint scaleByte = (inst.PackedData >> 8u) & 0xFFu;
    uint tintAndPhase = (inst.PackedData >> 16u) & 0xFFFFu;

    float rotY = (float(rotByte) / 255.0) * (2.0 * PI);
    float scale = 0.5 + (float(scaleByte) / 255.0) * 1.5;
    float windPhase = (float(tintAndPhase & 0xFFu) / 255.0) * (2.0 * PI);

    // Rotate and scale local vertex
    float cosR = cos(rotY);
    float sinR = sin(rotY);
    vec3 rotatedPos = vec3(
        (inPosition.x * cosR - inPosition.z * sinR) * scale,
        inPosition.y * scale,
        (inPosition.x * sinR + inPosition.z * cosR) * scale
    );

    vec3 rotatedNormal = normalize(vec3(
        inNormal.x * cosR - inNormal.z * sinR,
        inNormal.y,
        inNormal.x * sinR + inNormal.z * cosR
    ));

    // Camera-relative world position
    vec3 worldPos = pc.CameraRelOrigin.xyz + inst.PositionOffset + rotatedPos;

    // Procedural wind oscillation (affecting tips based on inColor.r flexibility)
    float flex = inColor.r;
    vec2 windDir = normalize(pc.WindParams.xy);
    float windSpeed = pc.WindParams.z;
    float windStrength = pc.WindParams.w;

    float wave = sin(pc.Time * windSpeed + windPhase + worldPos.x * 0.15 + worldPos.z * 0.15);
    float wave2 = cos(pc.Time * (windSpeed * 1.6) + windPhase * 1.3);
    vec3 windOffset = vec3(windDir.x, 0.0, windDir.y) * ((wave * 0.7 + wave2 * 0.3) * windStrength * flex);

    worldPos += windOffset;

    // Distance fade calculation
    float dist = length(worldPos);
    float fadeStart = pc.MaxDistance - pc.FadeRange;
    float alpha = clamp((pc.MaxDistance - dist) / pc.FadeRange, 0.0, 1.0);

    vWorldPos = worldPos;
    vNormal = rotatedNormal;
    vTexCoord = inTexCoord;
    vColor = inColor;
    vFadeAlpha = alpha;

    gl_Position = pc.ViewProj * vec4(worldPos, 1.0);
}
