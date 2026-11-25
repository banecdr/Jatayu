#version 460 core

layout(location = 0) in vec3 vRayDir;
layout(location = 0) out vec4 outColor;

layout(std140, set = 0, binding = 0) uniform LightEnvironmentUBO {
    vec3 SunDirection;
    float SunIntensity;
    vec3 SunColor;
    uint CastsShadows;
    vec3 AmbientSkyColor;
    float AmbientIntensity;
    vec3 AmbientGroundColor;
    float ShadowBias;
    mat4 LightViewProj;
    vec4 CascadeSplits;
    mat4 CascadeViewProj0;
    mat4 CascadeViewProj1;
    mat4 CascadeViewProj2;
    mat4 CascadeViewProj3;
    vec4 ShadowParams;
    vec4 ClusterParams;
} uLight;

layout(set = 0, binding = 10) uniform samplerCube uPrefilterMap;

const vec3 ZenithColor = vec3(0.12, 0.32, 0.72);
const vec3 HorizonColor = vec3(0.70, 0.82, 0.95);
const vec3 GroundColor = vec3(0.18, 0.16, 0.14);

void main() {
    vec3 dir = normalize(vRayDir);

    if ((uLight.CastsShadows & 0x80000000u) != 0u) {
        vec3 skyColor = textureLod(uPrefilterMap, dir, 0.0).rgb;
        outColor = vec4(skyColor, 1.0);
        return;
    }

    float y = dir.y;
    vec3 skyColor;
    if (y >= 0.0) {
        // Sky hemisphere: blend horizon to zenith
        float t = pow(y, 0.6);
        skyColor = mix(HorizonColor, ZenithColor, t);

        // Sun disc
        float sunDot = max(dot(dir, normalize(-uLight.SunDirection)), 0.0);
        float sunDisc = pow(sunDot, 512.0) * 8.0;
        float sunGlow = pow(sunDot, 16.0) * 0.4;
        skyColor += (uLight.SunColor * sunDisc) + (HorizonColor * sunGlow);
    } else {
        // Ground hemisphere: blend horizon to ground
        float t = pow(-y, 0.7);
        skyColor = mix(HorizonColor, GroundColor, t);
    }

    outColor = vec4(skyColor, 1.0);
}
