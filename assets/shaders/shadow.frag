#version 460 core
#extension GL_EXT_nonuniform_qualifier : enable

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in uint vMaterialId;

struct GpuMaterialData {
    vec4 BaseColorFactor;
    float MetallicFactor;
    float RoughnessFactor;
    uint AlbedoIndex;
    uint NormalIndex;
    uint MetallicRoughnessIndex;
    uint OcclusionIndex;
    uint EmissiveIndex;
    float EmissiveIntensity;
    vec3 EmissiveFactor;
    uint Flags;
};

layout(std430, set = 0, binding = 2) readonly buffer MaterialStorage {
    GpuMaterialData materials[];
} uMaterialTable;

layout(set = 1, binding = 0) uniform sampler2D uTextures[];

void main() {
    GpuMaterialData material = uMaterialTable.materials[vMaterialId];
    if ((material.Flags & 2u) == 0u) {
        return;
    }

    float alpha = material.BaseColorFactor.a;
    if (material.AlbedoIndex != 0xFFFFFFFFu) {
        alpha *= texture(uTextures[nonuniformEXT(material.AlbedoIndex)], vTexCoord).a;
    }

    if (alpha < 0.5) {
        discard;
    }
}
