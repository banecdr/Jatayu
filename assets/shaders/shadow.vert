#version 460 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inTexCoord;

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

layout(push_constant) uniform ShadowPushConstants {
    mat4 modelMatrix;
    mat4 viewProjMatrix;
    mat4 lightViewProj;
    uint materialId;
    uint useInstanceBuffer;
    uint padding0;
    uint padding1;
} pc;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) flat out uint vMaterialId;

void main() {
    mat4 model;
    if (pc.useInstanceBuffer != 0u) {
        GpuInstanceData inst = instances[gl_InstanceIndex];
        model = inst.CurrentTransform;
        vMaterialId = inst.MaterialId;
    } else {
        model = pc.modelMatrix;
        vMaterialId = pc.materialId;
    }

    gl_Position = pc.viewProjMatrix * model * vec4(inPosition, 1.0);
    vTexCoord = inTexCoord;
}
