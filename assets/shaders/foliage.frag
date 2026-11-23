#version 460 core

// ==================================================================================================
// Jatayu Engine - Procedural Foliage Rendering
// Shader: foliage.frag
// Role: Evaluates two-sided foliage lighting, subsurface scattering translucency, and dither fade.
// ==================================================================================================

#include "lod_dither.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vTexCoord;
layout(location = 3) in vec4 vColor;
layout(location = 4) in float vFadeAlpha;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform FoliagePushConstants {
    mat4 ViewProj;
    vec4 WindParams;
    vec4 CameraRelOrigin;
    float Time;
    float MaxDistance;
    float FadeRange;
    float Padding;
} pc;

const vec3 LightDir = normalize(vec3(-0.6, 0.8, -0.4));
const vec3 SunColor = vec3(1.0, 0.98, 0.92);
const vec3 AmbientSky = vec3(0.2, 0.35, 0.6);

void main() {
    // Distance dissolve using Bayer matrix stipple dither
    ApplyLODDither(gl_FragCoord.xy, vFadeAlpha);

    // Procedural grass blade / foliage color gradient
    vec3 baseColor = mix(vec3(0.08, 0.28, 0.04), vec3(0.22, 0.52, 0.08), vTexCoord.y);

    // Two-sided lighting with subsurface transmission through thin leaves
    vec3 N = normalize(vNormal);
    float NdotL = max(dot(N, LightDir), 0.0);
    float backLighting = max(dot(-N, LightDir), 0.0) * 0.35; // Subsurface scattering approximation
    float directTerm = NdotL + backLighting;

    vec3 ambient = AmbientSky * 0.4;
    vec3 finalColor = baseColor * (SunColor * directTerm + ambient);

    outColor = vec4(finalColor, 1.0);
}
