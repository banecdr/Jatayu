#version 460 core

// ==================================================================================================
// Jatayu Engine - PBR Opaque Rendering
// Shader: pbr.vert
// Role: PBR vertex shader supporting GPU-driven multi-draw indirect from InstanceBuffer with velocity output (Sprint 7a).
// ==================================================================================================

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inTexCoord;

struct GpuInstanceData {
    mat4 CurrentTransform;
    mat4 PreviousTransform;
    vec3 BoundingCenter;
    float BoundingRadius;
    uint MeshId;
    uint MaterialId;
    uint Flags;
    float LodBias;
};

layout(std430, set = 0, binding = 3) readonly buffer InstanceBuffer {
    GpuInstanceData instances[];
};

layout(push_constant) uniform PbrPushConstants {
    mat4 modelMatrix;
    mat4 viewProjMatrix;
    mat4 lightViewProj;
    uint materialId;
    uint useInstanceBuffer; // 0 = push constants, 1 = instances[gl_InstanceIndex]
    uint padding0;
    uint padding1;
} pc;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vTangent;
layout(location = 3) out vec3 vBitangent;
layout(location = 4) out vec2 vTexCoord;
layout(location = 5) out vec4 vColor;
layout(location = 6) out vec4 vShadowCoord;
layout(location = 7) flat out uint vMaterialId;
layout(location = 8) out vec4 vCurrClipPos;
layout(location = 9) out vec4 vPrevClipPos;

void main() {
    mat4 model;
    uint matId;

    if (pc.useInstanceBuffer != 0u) {
        GpuInstanceData inst = instances[gl_InstanceIndex];
        model = inst.CurrentTransform;
        matId = inst.MaterialId;

        // Output previous-frame transform for continuous TAA motion vectors (D25)
        vec4 prevWorldPos = inst.PreviousTransform * vec4(inPosition, 1.0);
        vPrevClipPos = pc.viewProjMatrix * prevWorldPos;
    } else {
        model = pc.modelMatrix;
        matId = pc.materialId;
        vPrevClipPos = vec4(0.0);
    }

    vec4 worldPos = model * vec4(inPosition, 1.0);
    vec4 clipPos = pc.viewProjMatrix * worldPos;
    gl_Position = clipPos;
    vCurrClipPos = clipPos;

    mat3 normalMatrix = mat3(model);
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
    vMaterialId = matId;

    // Shadow coordinates in [0, 1] texture space for Vulkan
    vec4 shadowClip = pc.lightViewProj * worldPos;
    vShadowCoord = vec4(
        shadowClip.x * 0.5 + 0.5 * shadowClip.w,
        shadowClip.y * 0.5 + 0.5 * shadowClip.w,
        shadowClip.z,
        shadowClip.w
    );
}
