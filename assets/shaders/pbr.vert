#version 460 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inTexCoord;

layout(push_constant) uniform PbrPushConstants {
    mat4 modelMatrix;
    mat4 viewProjMatrix;
    mat4 lightViewProj;
    uint materialId;
} pc;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vTangent;
layout(location = 3) out vec3 vBitangent;
layout(location = 4) out vec2 vTexCoord;
layout(location = 5) out vec4 vColor;
layout(location = 6) out vec4 vShadowCoord;
layout(location = 7) flat out uint vMaterialId;

void main() {
    vec4 worldPos = pc.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = pc.viewProjMatrix * worldPos;

    mat3 normalMatrix = mat3(pc.modelMatrix);
    vec3 N = normalize(normalMatrix * inNormal);
    vec3 up = abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
    vec3 T = normalize(cross(up, N));
    vec3 B = cross(N, T);

    vWorldPos = worldPos.xyz;
    vNormal = N;
    vTangent = T;
    vBitangent = B;
    vTexCoord = inTexCoord;
    vColor = vec4(inColor, 1.0);
    vMaterialId = pc.materialId;

    // Shadow coordinates in [0, 1] texture space for Vulkan
    vec4 shadowClip = pc.lightViewProj * worldPos;
    // Vulkan NDC [-1, 1] to UV [0, 1]
    vShadowCoord = vec4(
        shadowClip.x * 0.5 + 0.5 * shadowClip.w,
        shadowClip.y * 0.5 + 0.5 * shadowClip.w,
        shadowClip.z,
        shadowClip.w
    );
}
