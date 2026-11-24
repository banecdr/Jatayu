#version 460 core

// ==================================================================================================
// Jatayu Engine - Soft Particle Fragment Shader
// Shader: particle.frag
// Role: Evaluates soft radial particle falloff and additive / alpha blending.
// ==================================================================================================

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vTexCoord;
layout(location = 2) in float vViewZ;

layout(location = 0) out vec4 outAccumulation;
layout(location = 1) out float outRevealage;

void main() {
    // Radial soft billboard falloff from center (0.5, 0.5)
    vec2 delta = vTexCoord - vec2(0.5);
    float distSq = dot(delta, delta) * 4.0; // [0, 1] range inside circle
    if (distSq > 1.0) discard;

    float radialAlpha = 1.0 - distSq;
    radialAlpha = radialAlpha * radialAlpha; // Smooth quadratic falloff

    float finalAlpha = vColor.a * radialAlpha;
    if (finalAlpha < 0.005) discard;

    float depthWeight = clamp(10.0 / max(0.001, 1.0 + vViewZ * 0.02), 0.1, 10.0);
    float weight = clamp(finalAlpha * 8.0 + 0.01, 0.01, 1.0) * depthWeight;
    outAccumulation = vec4(vColor.rgb * finalAlpha * weight, finalAlpha * weight);
    outRevealage = finalAlpha;
}
